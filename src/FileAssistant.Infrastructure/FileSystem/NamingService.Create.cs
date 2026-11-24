using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.Infrastructure.FileSystem;

public sealed partial class NamingService
{
    private void ExecuteCreate(NamingPlan plan, NamingJournal journal, Dictionary<Guid, NamingItemResult> results,
        List<PathChange> changes, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        var completed = 0;
        foreach (var item in plan.Items.Where(x => x.CanRun))
        {
            if (token.IsCancellationRequested) break;
            var start = journal.Steps.Count;
            try
            {
                CopyFileSystem.CheckPath(item.Target);
                if (CopyFileSystem.Exists(item.Target)) throw new IOException("目标在预览后已被占用，未覆盖：" + item.Target);
                var parent = Path.GetDirectoryName(item.Target)!;
                if (!Directory.Exists(parent)) throw new IOException("上级目录不存在或创建失败。");
                foreach (var ancestor in plan.Items.Where(x => x.Action == NamingAction.Existing &&
                    item.Target.StartsWith(x.Target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    if (ancestor.ExistingStamp is not { } stamp || !NamingFileSystem.Matches(ancestor.Target, stamp))
                        throw new IOException("已有父目录在预览后发生变化，请重新预览。");
                // Never create children in a root that this task failed to create.
                var failedParent = plan.Items.Any(x => x.Id != item.Id && x.IsDirectory && x.CanRun &&
                    item.Target.StartsWith(x.Target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && results[x.Id].Outcome != NamingOutcome.Success);
                if (failedParent) throw new IOException("本批上级目录未成功创建，已停止此子目录。");
                var temporary = Path.Combine(parent, ".fa-create-" + Guid.NewGuid().ToString("N"));
                journal.Steps.Add(new("", temporary, item.IsDirectory, null, true)); journal.Remaining = journal.Steps.Count; store.Save(journal);
                if (item.IsDirectory)
                {
                    NamingFileSystem.CreateDirectory(temporary);
                    journal.Steps[^1] = journal.Steps[^1] with { Stamp = NamingFileSystem.Read(temporary, false, CancellationToken.None).Stamp }; store.Save(journal);
                }
                else
                {
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    journal.Steps[^1] = journal.Steps[^1] with { Stamp = NamingFileSystem.Stamp(output.SafeFileHandle) }; store.Save(journal);
                    if (item.TemplatePath is not null && item.CreationTemplate is null) throw new IOException("模板尚未检查，请重新预览。");
                    if ((item.CreationTemplate ?? plan.Template) is { } template)
                    {
                        using var input = new FileStream(template.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var stamp = NamingFileSystem.Stamp(input.SafeFileHandle) with { Hash = CopyFileSystem.Hash(input, token) };
                        if (!NamingFileSystem.SameContents(template.Stamp!, stamp)) throw new IOException("模板在预览后发生变化，请重新生成计划。");
                        input.Position = 0; var buffer = new byte[128 * 1024]; int read;
                        while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                    }
                    output.Flush(true);
                }
                var created = NamingFileSystem.Read(temporary, !item.IsDirectory, token).Stamp!;
                journal.Steps[^1] = journal.Steps[^1] with { Stamp = created }; store.Save(journal);
                token.ThrowIfCancellationRequested();
                store.Move(journal, temporary, item.Target, item.IsDirectory, created, changes, createdObject: true);
                results[item.Id] = new(item.IsDirectory ? item with { ExistingStamp = created } : item,
                    NamingOutcome.Success, "创建完成。", item.Target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
            {
                // A temporary created by this invocation has a known identity even if writing was cancelled.
                if (journal.Steps.Count > start && journal.Steps[start] is { } pending && pending.Create && CopyFileSystem.Exists(pending.To))
                {
                    try
                    {
                        var current = NamingFileSystem.Read(pending.To, !pending.Directory, CancellationToken.None).Stamp;
                        if (pending.Stamp is null || !NamingFileSystem.SameIdentity(pending.Stamp, current!)) throw new IOException("临时项身份无法确认，已保留。");
                        journal.Steps[start] = pending with { Stamp = current }; store.Save(journal);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { journal.Error = e.Message; }
                }
                var restored = Rollback(journal, start, changes, out var restoreError);
                results[item.Id] = new(item, token.IsCancellationRequested ? NamingOutcome.Cancelled : NamingOutcome.Failed,
                    CopyFileSystem.Friendly(error) + (restored ? "" : "；临时项保留，请使用恢复记录：" + restoreError));
                if (!restored || CopyFileSystem.Fatal(error)) break;
            }
            completed++; progress?.Report(new(completed, plan.ReadyCount, $"已创建 {results.Values.Count(x => x.Outcome == NamingOutcome.Success)}/{plan.ReadyCount} 项"));
        }
    }
}
