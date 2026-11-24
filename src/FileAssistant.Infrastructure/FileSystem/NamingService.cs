using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Infrastructure.FileSystem;

public sealed partial class NamingService(string recoveryDirectory) : INamingService
{
    private readonly NamingJournalStore store = new(recoveryDirectory);
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<NamingPlan> PlanAsync(NamingRequest request, CancellationToken token) => Task.Run(() =>
    {
        request = request with { Paths = request.Paths.ToArray() };
        if (request.Mode == NamingMode.Rename)
            return NamingPlanner.Rename(request, Scan(request, token), p => { token.ThrowIfCancellationRequested(); return CopyFileSystem.Exists(p); });
        CopyFileSystem.CheckPath(request.Destination);
        if (!Directory.Exists(request.Destination)) throw new IOException("请先选择一个已经存在的保存目录。");
        NamingSource? template = null;
        if (request.Entries is null && request.Kind == CreationKind.Template)
        {
            template = NamingFileSystem.Read(request.Template, true, token);
            if (template.IsDirectory || template.Stamp!.Length == 0) throw new IOException("请选择非空的有效模板文件；模板将按原内容复制。");
        }
        var plan = NamingPlanner.Create(request, p => { token.ThrowIfCancellationRequested(); return CopyFileSystem.Exists(p); }, template, Directory.Exists);
        var templates = new Dictionary<string, NamingSource>(StringComparer.OrdinalIgnoreCase);
        return plan with { Items = plan.Items.Select(item =>
        {
            if (item.Action == NamingAction.Existing)
                return item with { ExistingStamp = NamingFileSystem.Read(item.Target, false, token).Stamp };
            if (!item.CanRun || item.TemplatePath is not { } path) return item;
            try
            {
                if (!templates.TryGetValue(path, out var source))
                {
                    source = NamingFileSystem.Read(path, true, token);
                    if (source.IsDirectory || source.Stamp!.Length == 0) throw new IOException("请选择非空的有效模板文件。");
                    templates.Add(path, source);
                }
                return item with { CreationTemplate = source };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { return item with { Action = NamingAction.Invalid, Message = "模板无法使用：" + error.Message }; }
        }).ToArray() };
    }, token);

    private static IReadOnlyList<NamingSource> Scan(NamingRequest request, CancellationToken token)
    {
        var result = new List<NamingSource>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traversed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(path)) return;
            try { result.Add(NamingFileSystem.Read(path, false, token)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { result.Add(new(path, false, null, CopyFileSystem.Friendly(e))); }
        }
        void Walk(string directory)
        {
            if (!traversed.Add(directory)) return;
            try
            {
                CopyFileSystem.CheckPath(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var attr = File.GetAttributes(path);
                    var isDirectory = (attr & FileAttributes.Directory) != 0;
                    if (!isDirectory || request.RenameFolders) Add(path);
                    if (isDirectory && request.IncludeSubfolders && (attr & FileAttributes.ReparsePoint) == 0) Walk(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { result.Add(new(directory, true, null, "目录扫描未完成：" + e.Message)); }
        }
        foreach (var raw in request.Paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = CopyPlanner.Normalize(raw); CopyFileSystem.CheckPath(path);
                var directory = Directory.Exists(path);
                if (!directory || request.RenameFolders) Add(path);
                if (directory && request.FolderContents) Walk(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { result.Add(new(raw, false, null, e.Message)); }
        }
        return result;
    }

    public async Task<NamingReport> ExecuteAsync(NamingPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await Task.Run(() => Execute(plan, progress, token), CancellationToken.None); }
        finally { gate.Release(); }
    }
    private NamingReport Execute(NamingPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        progress = new NamingProgress(progress);
        if (plan.InvalidCount > 0) throw new IOException("计划仍有需调整项，请先处理冲突或选择跳过。");
        var journal = new NamingJournal { Title = plan.Request.Mode == NamingMode.Rename ? "批量改名" : "批量创建" };
        var changes = new List<PathChange>(); var results = plan.Items.ToDictionary(x => x.Id,
            x => new NamingItemResult(x, x.CanRun ? NamingOutcome.Cancelled : NamingOutcome.Skipped, x.CanRun ? "尚未执行。" : x.Message ?? "已跳过。"));
        store.Save(journal);
        if (plan.Request.Mode == NamingMode.Create) ExecuteCreate(plan, journal, results, changes, progress, token);
        else ExecuteRename(plan, journal, results, changes, progress, token);
        var outputs = new List<ProducedFile>();
        foreach (var item in plan.Items)
        {
            var result = results[item.Id];
            var actual = item.Source is null ? item.Target : NamingRulesEngine.MapPath(item.Source.Path, changes);
            results[item.Id] = result with { ActualPath = actual };
            if (result.Outcome == NamingOutcome.Success && item.Source is null)
            {
                if (item.IsDirectory) outputs.Add(new(actual, 0, Directory.GetLastWriteTimeUtc(actual), [], true));
                else { var info = new FileInfo(actual); var source = item.CreationTemplate ?? plan.Template; outputs.Add(new(actual, info.Length, info.LastWriteTimeUtc, source is null ? [] : [source.Path])); }
            }
        }
        return new(plan, plan.Items.Select(x => results[x.Id]).ToArray(), changes.ToArray(), outputs, token.IsCancellationRequested, journal.Id);
    }
    private void ExecuteRename(NamingPlan plan, NamingJournal journal, Dictionary<Guid, NamingItemResult> results,
        List<PathChange> changes, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        var groups = plan.Items.Where(x => x.CanRun).GroupBy(x => Path.GetDirectoryName(x.Source!.Path)!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Key.Length).ToArray();
        var completed = 0;
        foreach (var group in groups)
        {
            if (token.IsCancellationRequested) break;
            var items = group.ToArray(); var start = journal.Steps.Count;
            try
            {
                foreach (var item in items)
                {
                    var expected = item.Source!;
                    if (!NamingFileSystem.Matches(expected.Path, expected.Stamp!, !item.IsDirectory)) throw new IOException("源文件在预览后已变化，请重新检查：" + expected.Path);
                }
                var staged = new Dictionary<Guid, string>();
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    var temporary = Path.Combine(group.Key, ".fa-rename-" + Guid.NewGuid().ToString("N"));
                    store.Move(journal, item.Source!.Path, temporary, item.IsDirectory, item.Source.Stamp!, changes); staged.Add(item.Id, temporary);
                    progress?.Report(new(completed, plan.ReadyCount, $"正在准备目录内改名 {staged.Count}/{items.Length}"));
                }
                var committed = 0;
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    store.Move(journal, staged[item.Id], item.Target, item.IsDirectory, item.Source!.Stamp!, changes);
                    progress?.Report(new(completed + ++committed, plan.ReadyCount, $"正在改名 {completed + committed}/{plan.ReadyCount}"));
                }
                foreach (var item in items) results[item.Id] = new(item, NamingOutcome.Success, "改名完成。");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
            {
                var message = error is OperationCanceledException ? "已取消，此目录内改名已回退。" : CopyFileSystem.Friendly(error);
                var restored = Rollback(journal, start, changes, out var restoreError);
                if (!restored) message += "；回退未完成，请使用恢复记录：" + restoreError;
                foreach (var item in items) results[item.Id] = new(item, token.IsCancellationRequested ? NamingOutcome.Cancelled : NamingOutcome.Failed, message);
                if (!restored) break;
            }
            completed += items.Length; progress?.Report(new(completed, plan.ReadyCount, $"已处理 {completed}/{plan.ReadyCount} 项"));
        }
    }
    private bool Rollback(NamingJournal journal, int start, List<PathChange> changes, out string? error)
    {
        try
        {
            while (journal.Remaining > start)
            {
                store.UndoStep(journal.Steps[journal.Remaining - 1], changes, []);
                journal.Remaining--; store.Save(journal);
            }
            journal.Steps.RemoveRange(start, journal.Steps.Count - start); store.Save(journal); error = null; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { journal.Error = error = e.Message; store.Save(journal); return false; }
    }

    public async Task PruneRecoveryAsync(DateTimeOffset before, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await Task.Run(() => store.Prune(before, token), token); }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<NamingRecovery>> RecoveriesAsync(CancellationToken token) => Task.Run(store.List, token);
    public async Task<NamingRestoreResult> RestoreAsync(string id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                var journal = store.Read(id); journal.Restoring = true; journal.Error = null; store.Save(journal);
                var changes = new List<PathChange>(); var removed = new List<string>();
                try
                {
                    while (journal.Remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var index = journal.Remaining - 1; var step = journal.Steps[index];
                        var replay = journal.UndoIndex == index;
                        if (!step.Create && !replay && step.Stamp is { } stamp &&
                            !NamingFileSystem.Matches(step.From, stamp) && NamingFileSystem.Matches(step.To, stamp))
                        { journal.UndoIndex = index; store.Save(journal); }
                        store.UndoStep(step, changes, removed, replay);
                        journal.Remaining--; journal.UndoIndex = null;
                        if (journal.Remaining == 0) journal.Restoring = false;
                        store.Save(journal);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
                { journal.Error = e is OperationCanceledException ? "恢复已暂停，可继续。" : e.Message; store.Save(journal); }
                // Deleted creations retain their public names in saved references, not staging names.
                foreach (var temporary in removed.ToArray())
                {
                    var commit = journal.Steps.LastOrDefault(x => x.CreatedObject && x.From == temporary);
                    if (commit is null) continue;
                    changes.RemoveAll(x => x.Before == commit.To && x.After == temporary);
                    changes.Add(new(temporary, commit.To, commit.Directory)); // Also repairs references from an interrupted earlier restore.
                    removed.Remove(temporary); removed.Add(commit.To);
                }
                return new NamingRestoreResult(changes, removed, journal.Remaining == 0 ? "本次操作已撤销。" : "恢复未完成：" + journal.Error);
            }, CancellationToken.None);
        }
        finally { gate.Release(); }
    }
    public Task<NamingPlan> RetryAsync(NamingReport report, CancellationToken token) => Task.Run(() =>
    {
        if (report.RecoveryId is { } id && store.Read(id).Error is not null) throw new IOException("上次回退未完成，请先处理恢复记录。");
        var items = report.Items.Where(x => x.Retryable).Select(x =>
        {
            token.ThrowIfCancellationRequested(); var item = x.Item;
            if (item.Source is null) return item;
            var current = NamingRulesEngine.MapPath(item.Source.Path, report.Changes);
            return item with { Source = NamingFileSystem.Read(current, false, token), Target = Path.Combine(Path.GetDirectoryName(current)!, Path.GetFileName(item.Target)), FinalTarget = null };
        }).ToArray();
        if (report.Plan.Request.Mode == NamingMode.Rename)
            return NamingPlanner.ResolveRename(report.Plan.Request, items, CopyFileSystem.Exists);
        var reused = report.Items.Where(x => x.Item.IsDirectory && (x.Outcome == NamingOutcome.Success || x.Item.Action == NamingAction.Existing)
            && items.Any(item => item.Target.StartsWith(x.Item.Target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Select(x =>
            {
                var item = x.Item; var stamp = NamingFileSystem.Read(item.Target, false, token).Stamp;
                if (item.ExistingStamp is { } expected && (stamp is null || !NamingFileSystem.SameIdentity(expected, stamp))) throw new IOException("上级目录已改变，请重新预览。");
                return item with { Action = NamingAction.Existing, ExistingStamp = stamp, Message = "沿用已有文件夹。" };
            });
        return new NamingPlan(report.Plan.Request, reused.Concat(items.Select(item => CopyFileSystem.Exists(item.Target)
            ? item with { Action = NamingAction.Invalid, Message = "目标现在已存在，请调整输入后重新预览。" } : item)).ToArray(), report.Plan.Template);
    }, token);
}
