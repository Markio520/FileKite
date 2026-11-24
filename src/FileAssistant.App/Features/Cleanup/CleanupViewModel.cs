using FileAssistant.App.Features.Tasks;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Cleanup;

public sealed class CleanupViewModel(ICleanupService service, ProcessingJobsViewModel jobs)
{
    private CancellationTokenSource? cancellation;
    private Task active = Task.CompletedTask;
    private readonly List<(PathChange[] Changes, HashSet<string> Removed)> pendingPaths = [];
    public event EventHandler? Changed;
    public event EventHandler? FilesRequested;
    public event EventHandler<IReadOnlyList<string>>? FilesRemoved;
    public event EventHandler? SoftwareChanged;
    public InstalledSoftware[] Software { get; private set; } = [];
    public InstalledSoftware? SelectedSoftware { get; private set; }
    public string[] Paths { get; private set; } = [];
    public CleanupPlan? Plan { get; private set; }
    public CleanupResidue[] Residues { get; private set; } = [];
    public CleanupLock[] Locks { get; private set; } = [];
    public CleanupResult[] Results { get; private set; } = [];
    public string[] Warnings { get; private set; } = [];
    public string Message { get; private set; } = "选择要卸载的软件，或切换到文件清理。";
    public string ProgressText { get; private set; } = "";
    public bool IsBusy => cancellation is not null;
    public bool IsCancelling => cancellation?.IsCancellationRequested == true;
    public bool HasLoaded { get; private set; }
    public bool FilesMode { get; private set; }
    public bool ShowResidues { get; private set; }
    public bool Elevated { get; private set; }
    public CleanupMethod Method { get; private set; }
    public bool BackupRegistry { get; private set; } = true;
    private bool customMethod, newFileBatch;
    private AppSettings? savedDefaults;
    public void ApplyDefaults(AppSettings? previous, AppSettings current)
    {
        savedDefaults = current;
        var next = current.PermanentDelete ? CleanupMethod.Permanent : CleanupMethod.Recycle;
        var changed = !customMethod && Method != next;
        if (changed)
        {
            Method = next;
            if (!IsBusy) Plan = null;
        }
        if (changed || BackupRegistry != current.BackupRegistry)
        {
            BackupRegistry = current.BackupRegistry;
            Notify();
        }
    }
    public bool SelectedIsInstalled => SelectedSoftware is { } selected && Software.Any(item => item.Id == selected.Id);
    public bool CanUninstall => !IsBusy && SelectedIsInstalled && SelectedSoftware?.CanUninstall == true;
    public bool CanScanResidue => !IsBusy && SelectedSoftware is { IsProtected: false } && !SelectedIsInstalled;
    public bool CanRunFiles => !IsBusy && Plan?.Roots.Count > 0;

    public void SetMode(bool files)
    {
        if (IsBusy) return;
        if (FilesMode != files)
        {
            FilesMode = files;
            Message = files
                ? Paths.Length == 0 ? "拖入文件或文件夹开始。" : $"清单中有 {Paths.Length:N0} 个目标，请核对选择和处理方式。"
                : ShowResidues
                    ? Residues.Length == 0 ? "本次候选残留已处理完，或未发现可清理的残留。" : $"保留 {Residues.Length:N0} 项候选残留，请逐项核对后选择。"
                    : SoftwareSelectionHint;
        }
        Notify();
    }
    public void BackToSoftware() { if (IsBusy) return; ShowResidues = false; Notify(); }
    public void ClearSoftwareSelection() { if (IsBusy) return; SelectedSoftware = null; Notify(); }
    public void SelectSoftware(InstalledSoftware? software)
    {
        if (IsBusy || software is null || SelectedSoftware?.Id == software.Id) return;
        if (SelectedSoftware is not null) ResetBatchDefaults();
        SelectedSoftware = software; Residues = []; Results = []; Warnings = []; ShowResidues = false;
        Message = SoftwareSelectionHint;
        Notify();
    }
    public string SoftwareSelectionHint => SelectedSoftware is not { } software ? "选择要卸载的软件。"
        : software.IsProtected ? "此软件受保护，不能在这里卸载。"
        : software.CanUninstallQuietly ? "双击软件，确认后在后台卸载。"
        : software.CanUninstall ? "此软件只提供卸载向导，可在右侧手动打开。" : "未找到可用的卸载程序。";
    public void SetOptions(CleanupMethod method, bool elevated)
    {
        if (IsBusy || Method == method && Elevated == elevated) return;
        if (Method != method) { customMethod = true; newFileBatch = false; }
        Method = method; Elevated = elevated; Plan = null; Locks = []; Message = !FilesMode && ShowResidues ? "处理方式已变化，请核对勾选的残留后再执行。" : "处理方式已更新，点击执行后确认目标。"; Notify();
    }
    public void AddPaths(IEnumerable<string> paths, bool navigate = false)
    {
        if (IsBusy) { Message = "当前操作结束后再加入文件。"; Notify(); return; }
        var combined = Paths.Concat(paths.Select(path => path.Trim().Trim('"')).Where(path => path.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (combined.Length > 500) { Message = "一次最多加入 500 个目标，请分批添加。"; Notify(); return; }
        if (newFileBatch && combined.Length > 0) { ResetBatchDefaults(); newFileBatch = false; }
        Paths = combined; Plan = null; Locks = []; Results = []; Warnings = []; FilesMode = true;
        Message = Paths.Length == 0 ? "拖入文件或文件夹开始。" : $"已加入 {Paths.Length:N0} 个目标，选择处理方式后即可执行。";
        Notify(); if (navigate) FilesRequested?.Invoke(this, EventArgs.Empty);
    }
    public void ApplyPathChanges(IReadOnlyList<PathChange> changes, IReadOnlyList<string>? removed = null)
    {
        if (Paths.Length == 0 || changes.Count == 0 && removed is not { Count: > 0 }) return;
        var deleted = (removed ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = Paths;
        foreach (var pending in pendingPaths) current = Remap(current, pending.Changes, pending.Removed);
        var mapped = Remap(current, changes, deleted);
        var roots = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Renaming or removing a child also invalidates a prepared recursive deletion of its parent.
        if (current.SequenceEqual(mapped, StringComparer.Ordinal) &&
            !changes.Any(change => FileCollection.IsRemoved(change.Before, roots) || FileCollection.IsRemoved(change.After, roots)) &&
            !deleted.Any(path => FileCollection.IsRemoved(path, roots))) return;
        Plan = null; Locks = [];
        if (IsBusy)
        {
            pendingPaths.Add((changes.ToArray(), deleted));
            return;
        }
        Paths = mapped;
        if (Paths.Length == 0) newFileBatch = true;
        if (FilesMode) Message = PathChangeHint;
        Notify();
    }

    private static string[] Remap(string[] paths, IReadOnlyList<PathChange> changes, IReadOnlySet<string> deleted) =>
        paths.Select(path => NamingRulesEngine.MapPath(path, changes)).Where(path => !FileCollection.IsRemoved(path, deleted))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private string PathChangeHint => Paths.Length == 0 ? "清理目标已移出清单。" : $"清理目标的路径已变化，请重新核对 {Paths.Length:N0} 个目标。";

    private void ResetBatchDefaults()
    {
        if (savedDefaults is null) return;
        customMethod = false;
        Method = savedDefaults.PermanentDelete ? CleanupMethod.Permanent : CleanupMethod.Recycle;
    }
    public void RemovePaths(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        var remove = paths.ToHashSet(StringComparer.OrdinalIgnoreCase); Paths = Paths.Where(path => !remove.Contains(path)).ToArray();
        if (Paths.Length == 0) newFileBatch = true;
        Plan = null; Locks = []; Results = []; Warnings = []; Message = "已移出清单，磁盘文件未改变。"; Notify();
    }
    public Task LoadSoftwareAsync() => Start(new(CleanupOperation.ListSoftware), false, "读取软件", response =>
    {
        Software = response.Software; HasLoaded = true;
        if (SelectedSoftware is { } selected && Software.FirstOrDefault(item => item.Id == selected.Id) is { } fresh)
        { SelectedSoftware = fresh; Residues = []; ShowResidues = false; }
        Message = $"{Software.Length:N0} 个已登记软件 · 大小来自安装登记，可能不完整。";
    });
    public Task UninstallAsync(bool quiet = false)
    {
        if (!CanUninstall || (quiet && SelectedSoftware?.CanUninstallQuietly != true)) return active;
        return Start(new(CleanupOperation.UninstallSoftware) { Software = SelectedSoftware, QuietUninstall = quiet }, false, "卸载软件", response =>
        {
            SoftwareChanged?.Invoke(this, EventArgs.Empty);
            if (response.Results.Any(item => item.Status == CleanupStatus.Succeeded))
                Software = Software.Where(item => item.Id != SelectedSoftware!.Id).ToArray();
        });
    }
    public Task ScanResidueAsync()
    {
        if (!CanScanResidue) return active;
        return Start(new(CleanupOperation.ScanResidue) { Software = SelectedSoftware }, false, "检查残留", response =>
        {
            Residues = response.Residues; ShowResidues = true;
            Message = Residues.Length == 0 ? "未发现可供清理的残留；可查看详情了解跳过的范围。" : $"发现 {Residues.Length:N0} 项候选残留。逐项核对后选择，个人数据也可能包含在内。";
        });
    }
    public Task RemoveResidueAsync(CleanupResidue[] selected)
    {
        if (selected.Length == 0 || selected.Any(item => !Residues.Contains(item))) return active;
        return Start(new(CleanupOperation.RemoveResidue) { Residues = selected, Method = Method, BackupRegistry = BackupRegistry }, Elevated, "清理残留", response =>
        {
            var removed = response.Results.Where(result => result.Status == CleanupStatus.Succeeded && result.ItemId is not null).Select(result => result.ItemId).ToHashSet();
            Residues = Residues.Where(item => !removed.Contains(item.Id)).ToArray();
            ShowResidues = true;
            if (Residues.Length == 0) Message += "已处理完本次候选。";
            else Message += "未勾选及未完成的项目已保留；内容变化或部分删除后请重新检查残留。";
        });
    }
    public Task PlanAsync(IReadOnlyList<string>? targets = null)
    {
        if (IsBusy || Paths.Length == 0) return active;
        Plan = null;
        var requested = targets is null ? Paths : targets.Where(path => Paths.Contains(path, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) { Notify(); return Task.CompletedTask; }
        return Start(new(CleanupOperation.PlanFiles) { Paths = requested }, Elevated, "正在准备处理目标", response =>
        {
            Plan = response.Plan; Locks = [];
            Message = Plan is null ? "无法准备目标，请重试。" : $"可处理 {Plan.Roots.Count:N0} 个目标 · {Plan.Issues.Count:N0} 个无法处理，可查看详情。";
        });
    }
    public Task RunFilesAsync()
    {
        if (!CanRunFiles) return active;
        var plan = Plan; Plan = null;
        return Start(new(CleanupOperation.RunFiles) { Plan = plan, Method = Method }, Elevated, Method == CleanupMethod.Recycle ? "移入回收站" : "永久删除", response =>
        {
            Locks = [];
            var completed = response.Results.Where(item => item.Status == CleanupStatus.Succeeded).Select(item => item.Path).ToArray();
            Paths = Paths.Where(path => !completed.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (Paths.Length == 0) newFileBatch = true;
        });
    }
    public Task CloseLocksAsync(CleanupLock[] selected, bool terminate)
    {
        if (selected.Length == 0 || selected.Any(item => !item.CanClose || !Locks.Contains(item))) return active;
        Plan = null;
        return Start(new(CleanupOperation.CloseLocks) { Paths = Paths, Locks = selected, Terminate = terminate }, Elevated,
            terminate ? "结束占用进程" : "关闭占用程序", _ => { Locks = []; });
    }
    public void Feedback(string message) { Message = message; Notify(); }
    public void Cancel() { cancellation?.Cancel(); Notify(); }
    public async Task StopAsync() { Cancel(); await active; }
    private Task Start(CleanupRequest request, bool elevated, string name, Action<CleanupResponse> apply)
    {
        if (IsBusy) return active;
        return active = RunAsync(request, elevated, name, apply);
    }
    private async Task RunAsync(CleanupRequest request, bool elevated, string name, Action<CleanupResponse> apply)
    {
        using var source = new CancellationTokenSource(); cancellation = source;
        Results = []; Warnings = []; ProgressText = name; Message = "";
        var batch = request.Operation is CleanupOperation.UninstallSoftware or CleanupOperation.RemoveResidue or CleanupOperation.RunFiles;
        var job = jobs.Start(Cancel, name, batch); Notify();
        try
        {
            var progress = new Progress<CleanupProgress>(value =>
            {
                if (!ReferenceEquals(cancellation, source)) return;
                ProgressText = value.Stage + (value.Total > 0 ? $" · {value.Completed}/{value.Total}" : "");
                job.Update(new(value.Completed, value.Total, ProgressText)); Notify();
            });
            using var slot = await jobs.Slots.EnterAsync(source.Token);
            if (pendingPaths.Count > 0 && request.Operation is CleanupOperation.PlanFiles or CleanupOperation.CloseLocks)
            { job.Complete("路径已变化，请重新检查。", [], false); return; }
            var response = await service.RunAsync(request, elevated, progress, source.Token);
            if (pendingPaths.Count > 0 && request.Operation == CleanupOperation.PlanFiles)
            { job.Complete("已忽略旧路径的检查结果。", [], false); return; }
            Results = response.Results; Warnings = response.Warnings;
            if (response.Error.Length > 0) { Message = response.Error; job.Fail(Message); return; }
            var removed = Results.SelectMany(item => item.RemovedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (removed.Length > 0) FilesRemoved?.Invoke(this, removed);
            Message = response.Cancelled ? "操作已停止，请核对实际结果。" : Results.Length > 0
                ? $"完成 {Results.Count(item => item.Status == CleanupStatus.Succeeded)} 项 · 需查看 {Results.Count(item => item.Status != CleanupStatus.Succeeded)} 项。" : "操作完成。";
            if (!response.Cancelled) apply(response);
            var failedPaths = request.Operation == CleanupOperation.PlanFiles
                ? response.Plan?.Issues.Select(item => item.Path).ToArray() ?? []
                : request.Operation == CleanupOperation.RunFiles
                    ? Results.Where(item => item.Status is CleanupStatus.Failed or CleanupStatus.Partial).Select(item => item.Path)
                        .Concat(request.Plan?.Issues.Select(item => item.Path) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
            if (request.Operation == CleanupOperation.RunFiles && request.Plan is { Issues.Count: > 0 } reviewed)
            {
                Warnings = [.. Warnings, .. reviewed.Issues.Select(item => item.Path + "：" + item.Message)];
                Message += $"另有 {reviewed.Issues.Count} 项未执行，已保留。";
            }
            if (!response.Cancelled && !source.IsCancellationRequested && failedPaths.Length > 0 && pendingPaths.Count == 0)
            {
                ProgressText = "正在查找无法处理的原因…"; Notify();
                try
                {
                    var diagnosis = await service.RunAsync(new(CleanupOperation.FindLocks) { Paths = failedPaths }, elevated, progress, source.Token);
                    if (pendingPaths.Count == 0)
                    {
                        Locks = diagnosis.Locks;
                        Warnings = Warnings.Concat(diagnosis.Warnings).Concat(diagnosis.Error.Length > 0 ? new[] { diagnosis.Error } : []).ToArray();
                        if (Locks.Length > 0) Message += $"有 {Locks.Length} 个程序正在占用，保存工作后可关闭并重试。";
                    }
                }
                catch (OperationCanceledException) { Message += "已停止查找占用，处理结果已保留。"; }
                catch (Exception error) { Warnings = [.. Warnings, "无法查询占用：" + error.Message]; }
            }
            job.Complete(Message, Results.Select(item => item.Path + "：" + item.Message).Concat(Warnings), response.Cancelled || Results.Any(item => item.Status == CleanupStatus.Cancelled));
        }
        catch (OperationCanceledException) { Message = "操作已停止，请核对实际结果。"; job.Complete(Message, [], true); }
        catch (Exception error) { Message = error.Message; job.Fail(Message); }
        finally
        {
            if (request.Operation == CleanupOperation.PlanFiles && source.IsCancellationRequested) Plan = null;
            if (pendingPaths.Count > 0)
            {
                foreach (var pending in pendingPaths) Paths = Remap(Paths, pending.Changes, pending.Removed);
                pendingPaths.Clear();
                if (Paths.Length == 0) newFileBatch = true;
                Plan = null; Locks = [];
                if (FilesMode) Message = request.Operation == CleanupOperation.PlanFiles ? PathChangeHint : (Message + " " + PathChangeHint).Trim();
            }
            cancellation = null; ProgressText = ""; Notify();
        }
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
