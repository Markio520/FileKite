using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Outputs;
using FileAssistant.App.Features.Tasks;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Settings;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel : ObservableObject
{
    private readonly INamingService service;
    private readonly OutputsViewModel outputs;
    private readonly ProcessingJobsViewModel jobs;
    private CancellationTokenSource? cancellation;
    private int revision, outputTask;
    private string inputIdentity = "";
    private bool applyingDefaultConflict, customConflict, hadRenameInputs;
    private AppSettings? savedDefaults;
    public void ApplyDefaults(AppSettings? previous, AppSettings current)
    {
        savedDefaults = current;
        if (customConflict || ConflictValue == (int)current.NamingConflict) return;
        applyingDefaultConflict = true;
        try { ConflictValue = (int)current.NamingConflict; }
        finally { applyingDefaultConflict = false; }
    }
    partial void OnConflictValueChanged(int value)
    {
        if (!applyingDefaultConflict) customConflict = true;
    }
    private TaskCompletionSource? idle;
    public WorkspaceViewModel Input { get; }
    public Task WaitForIdleAsync() => idle?.Task ?? Task.CompletedTask;
    public NamingViewModel(INamingService service, WorkspaceViewModel input, OutputsViewModel outputs, ProcessingJobsViewModel jobs, IDirectoryContentReader? directoryReader = null, IFileInteraction? fileInteraction = null)
    {
        this.service = service; Input = input; this.outputs = outputs; this.jobs = jobs;
        this.directoryReader = directoryReader;
        this.fileInteraction = fileInteraction;
        InitializeCreationDraft();
        RefreshRuleSample();
        input.VisibleFilesRefreshed += (_, _) => { UpdateNamePreviews(); NotifyRenameTabs(); };
        hadRenameInputs = input.SearchEntries.Count > 0;
        input.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.CollectionRevision))
            {
                var hasInputs = input.SearchEntries.Count > 0;
                if (IsRename && hadRenameInputs && !hasInputs && savedDefaults is { } defaults)
                {
                    customConflict = false;
                    ApplyDefaults(null, defaults);
                }
                hadRenameInputs = hasInputs;
            }
            if (e.PropertyName == nameof(WorkspaceViewModel.InputFeedback) && IsRename) Feedback = input.InputFeedback;
            if (e.PropertyName is nameof(WorkspaceViewModel.EffectiveEntries) or nameof(WorkspaceViewModel.CollectionRevision))
            {
                var next = input.CollectionRevision + ":" + string.Join('\0', input.EffectiveEntries.Select(x => x.FullPath));
                if (next != inputIdentity) { inputIdentity = next; PruneCustomNames(); if (IsRename) Invalidate(); }
            }
            if (e.PropertyName is nameof(WorkspaceViewModel.IsLoading) or nameof(WorkspaceViewModel.LoadingMessage) or nameof(WorkspaceViewModel.VisibleSelectedCount)) Notify();
            if (e.PropertyName == nameof(WorkspaceViewModel.IsLoading) && IsRename && !input.IsLoading && !IsExecuting && Plan is null)
                PlanInvalidated?.Invoke(this, EventArgs.Empty);
        };
    }
    [ObservableProperty] public partial int ModeValue { get; set; }
    [ObservableProperty] public partial string RenamePattern { get; set; } = "{name}";
    [ObservableProperty] public partial string CreatePattern { get; set; } = "{name}_{n}";
    [ObservableProperty] public partial string Prefix { get; set; } = "";
    [ObservableProperty] public partial string Suffix { get; set; } = "";
    [ObservableProperty] public partial string Find { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial string DeleteText { get; set; } = "";
    [ObservableProperty] public partial string RemoveAt { get; set; } = "0";
    [ObservableProperty] public partial string RemoveCount { get; set; } = "0";
    [ObservableProperty] public partial string Start { get; set; } = "1";
    [ObservableProperty] public partial string Step { get; set; } = "1";
    [ObservableProperty] public partial string Digits { get; set; } = "3";
    [ObservableProperty] public partial int CaseValue { get; set; }
    [ObservableProperty] public partial int OrderValue { get; set; }
    [ObservableProperty] public partial int ConflictValue { get; set; }
    [ObservableProperty] public partial bool PerDirectory { get; set; }
    [ObservableProperty] public partial bool RenameFolders { get; set; } = true;
    [ObservableProperty] public partial bool FolderContents { get; set; }
    [ObservableProperty] public partial bool IncludeSubfolders { get; set; }
    [ObservableProperty] public partial string Destination { get; set; } = "";
    [ObservableProperty] public partial string Names { get; set; } = "";
    [ObservableProperty] public partial string Count { get; set; } = "10";
    [ObservableProperty] public partial int KindValue { get; set; }
    [ObservableProperty] public partial string Subdirectories { get; set; } = "";
    [ObservableProperty] public partial string Template { get; set; } = "";
    [ObservableProperty] public partial NamingPlan? Plan { get; set; }
    [ObservableProperty] public partial NamingReport? Report { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial string Feedback { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<NamingRecovery> Recoveries { get; set; } = [];
    [ObservableProperty] public partial NamingPlanItem? SelectedItem { get; set; }
    [ObservableProperty] public partial NamingRecovery? SelectedRecovery { get; set; }
    [ObservableProperty] public partial ProcessingJob? ActiveJob { get; set; }
    public bool HasProgress => IsBusy && ActiveJob is not null;
    public bool IsScanning => IsBusy && ActiveJob is null;
    public IReadOnlyList<SelectionOption> Modes { get; } = [new(0, "批量改名"), new(1, "批量创建")];
    public IReadOnlyList<SelectionOption> Conflicts { get; } = [new(0, "有冲突时停止"), new(1, "跳过冲突项"), new(2, "自动编号保留")];
    public IReadOnlyList<SelectionOption> Kinds { get; } = [new(0, "文件夹"), new(1, "文件")];
    public IReadOnlyList<SelectionOption> Orders { get; } = [new(0, "输入顺序"), new(1, "名称升序"), new(2, "修改日期升序")];
    public IReadOnlyList<SelectionOption> Cases { get; } = [new(0, "保持大小写"), new(1, "全部小写"), new(2, "全部大写")];
    public bool IsRename => ModeValue == 0;
    public bool IsCreate => !IsRename;
    public bool IsDirectoryCreation => IsCreate && KindValue == 0;
    public bool IsTemplateCreation => IsFileCreation && FileContentValue == 1;
    public bool CanEdit => !IsExecuting && !IsSelectingTargets;
    public bool CanBuild => !IsBusy && !IsSelectingTargets && (!IsRename || !Input.IsLoading) && MissingInput.Length == 0;
    public bool CanRun => !IsBusy && !IsSelectingTargets && !HasMissingCreationParents && !HasUnaddedDirectoryInput && (!IsRename || !Input.IsLoading) && Plan is { ReadyCount: > 0, InvalidCount: 0 } plan && plan.Request.Mode == (NamingMode)ModeValue;
    public bool CanRetry => !IsBusy && Report?.Plan.Request.Mode == (NamingMode)ModeValue && Report.Items.Any(x => x.Retryable);
    public bool CanRestore => !IsBusy && SelectedRecovery is { Remaining: > 0 };
    public bool HasResults => Report is not null;
    public bool HasOutputs => Report?.Outputs.Count > 0;
    public IReadOnlyList<SearchFile> OutputFiles =>
        Report?.Outputs.Select(output => new SearchFile(output.FullPath, output.IsDirectory, output.IsDirectory ? null : output.Size, output.Modified)).ToArray() ?? [];
    public bool HasPlan => PlanItems.Count > 0;
    public bool IsPlanEmpty => !HasPlan;
    public string RunLabel => Plan is { ReadyCount: > 0 } plan ? $"{(IsRename ? "改名" : "创建全部")} {plan.ReadyCount:N0} 项" : IsRename ? "确认改名" : "创建全部";
    public string Scope => IsCreate ? "新建到所选保存目录" : Input.SelectionCount > 0
        ? $"仅处理已勾选的 {Input.SelectionCount} 项 · 更改范围可打开“更多”" : Input.EffectiveEntries.Count == 0 ? "也可以把文件直接拖到下方" : $"处理 {Input.CurrentFolderName} 中的 {Input.EffectiveEntries.Count:N0} 项";
    public string Summary => Plan is null ? Report?.Summary ?? (Input.EffectiveEntries.Count > 0 && IsRename ? $"已加入 {Input.EffectiveEntries.Count:N0} 项" : "名称预览") : $"待处理 {Plan.ReadyCount} · 需调整 {Plan.InvalidCount} · 跳过 {Plan.Items.Count(x => x.Action == NamingAction.Skipped)}";
    public string SelectedDetails => SelectedItem?.Details ?? "选择一项查看完整路径与原因。";
    public IReadOnlyList<NamingPlanItem> PlanItems => Plan?.Items ?? [];
    public IReadOnlyList<NamingItemResult> Results => Report?.Items ?? [];
    public event EventHandler? PlanInvalidated;
    public event EventHandler<IReadOnlyList<PathChange>>? PathsChanged;
    public event EventHandler<IReadOnlyList<string>>? FilesRemoved;
    public event EventHandler<int>? OutputsRequested;
    private static int Number(string text, string name) => int.TryParse(text, out var n) ? n : throw new ArgumentException(name + "请输入整数。");
    private NamingRequest Request() => new((NamingMode)ModeValue, Manual ? CustomNamesForRequest().Keys.ToArray() : IsRename ? Input.EffectiveEntries.Select(x => x.FullPath).ToArray() : [],
        IsTreeCreation ? new() : SimpleRules(), (NamingConflict)ConflictValue, Manual || RenameFolders, !Manual && FolderContents, !Manual && IncludeSubfolders, Destination.Trim(),
        !IsTreeCreation && IsCreate && CreateMethod is 1 or 2 ? Names : "", !IsTreeCreation && IsCreate && CreateMethod is not (1 or 3) ? Number(Count, "创建数量") : 1,
        (CreationKind)KindValue, ShowSharedSubdirectories ? Subdirectories : "", Template.Trim(),
        ReuseExistingDirectories: IsTreeCreation,
        Entries: IsTreeCreation ? DraftEntries().Where(x => !x.Node.IsExisting).Select(x => new CreationEntry(x.Path, x.Node.IsDirectory, x.Node.TemplatePath)).ToArray() : null,
        CustomNames: Manual ? CustomNamesForRequest() : null);
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        SyncRenameTabFromModelChange(e.PropertyName);
        // Form changes configure the next addition; the queued structure stays immutable.
        if (IsCreate && e.PropertyName is nameof(CreatePattern) or nameof(Prefix) or nameof(Suffix) or nameof(Find) or nameof(Replacement)
            or nameof(DeleteText) or nameof(RemoveAt) or nameof(RemoveCount) or nameof(Start) or nameof(Step) or nameof(Digits)
            or nameof(CaseValue) or nameof(OrderValue) or nameof(PerDirectory) or nameof(Names) or nameof(Count) or nameof(KindValue)
            or nameof(Subdirectories) or nameof(Template) or nameof(CreateBaseName) or nameof(CreateMethod) or nameof(SeparatorValue) or nameof(NumberPosition))
        { Notify(); return; }
        if (e.PropertyName is nameof(ModeValue) or nameof(RenamePattern) or nameof(CreatePattern) or nameof(Prefix) or nameof(Suffix) or nameof(Find) or nameof(Replacement)
            or nameof(DeleteText) or nameof(RemoveAt) or nameof(RemoveCount) or nameof(Start) or nameof(Step) or nameof(Digits) or nameof(CaseValue) or nameof(OrderValue)
            or nameof(ConflictValue) or nameof(PerDirectory) or nameof(RenameFolders) or nameof(FolderContents) or nameof(IncludeSubfolders)
            or nameof(Destination) or nameof(Names) or nameof(Count) or nameof(KindValue) or nameof(Subdirectories) or nameof(Template)
            or nameof(RenameAction) or nameof(RenameBaseName) or nameof(CreateBaseName) or nameof(CreateMethod) or nameof(SeparatorValue) or nameof(DeleteMethod) or nameof(NumberPosition)) Invalidate();
    }
    private void Invalidate()
    {
        revision++; if (IsBusy && !IsExecuting) cancellation?.Cancel();
        if (!IsExecuting) { Plan = null; Report = null; Feedback = ""; }
        RefreshRows(); Notify(); if (!IsExecuting) PlanInvalidated?.Invoke(this, EventArgs.Empty);
    }
    partial void OnPlanChanged(NamingPlan? value) { OnPropertyChanged(nameof(PlanItems)); SelectedItem = value?.Items.FirstOrDefault(); RefreshRows(); Notify(); }
    partial void OnSelectedItemChanged(NamingPlanItem? value) => OnPropertyChanged(nameof(SelectedDetails));
    partial void OnSelectedRecoveryChanged(NamingRecovery? value) { OnPropertyChanged(nameof(CanRestore)); RestoreCommand.NotifyCanExecuteChanged(); }
    partial void OnReportChanged(NamingReport? value) { if (value is not null) EditingResults = false; OnPropertyChanged(nameof(Results)); RefreshRows(); Notify(); }
    partial void OnIsBusyChanged(bool value)
    {
        if (value) idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        else { idle?.TrySetResult(); idle = null; }
        Notify();
    }
    partial void OnIsExecutingChanged(bool value) { UpdateNamePreviews(); Notify(); }
    private void Notify()
    {
        foreach (var name in new[] { nameof(IsRename), nameof(IsCreate), nameof(IsDirectoryCreation), nameof(IsTemplateCreation), nameof(CanEdit), nameof(CanBuild), nameof(CanRun), nameof(CanRetry), nameof(CanRestore), nameof(HasResults), nameof(HasOutputs), nameof(OutputFiles), nameof(HasPlan), nameof(IsPlanEmpty), nameof(RunLabel), nameof(Scope), nameof(Summary), nameof(HasProgress), nameof(IsScanning) }) OnPropertyChanged(name);
        NotifySimple();
        NotifyRenameTabs();
        BuildPlanCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildPlanAsync()
    {
        if (!CanBuild) return; var version = revision;
        using var current = new CancellationTokenSource(); cancellation = current; ActiveJob = null; IsBusy = true; Plan = null;
        try
        {
            var plan = await service.PlanAsync(Request(), current.Token);
            if (version == revision && !current.IsCancellationRequested) { Plan = plan; Feedback = plan.InvalidCount > 0 ? "有名称需要调整，点击对应行查看原因。" : plan.ReadyCount == 0 ? "名称没有变化，或所有项目均已跳过。请调整右侧设置。" : ""; }
        }
        catch (OperationCanceledException) { Feedback = "预览已取消，原文件未改变。"; }
        catch (Exception e) { if (version == revision) Feedback = e.Message; }
        finally { cancellation = null; IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!CanRun || Plan is not { } plan) return;
        using var current = new CancellationTokenSource(); cancellation = current; IsExecuting = true; IsBusy = true;
        var job = jobs.Start(current.Cancel, IsRename ? "批量改名" : "批量创建"); ActiveJob = job; Notify(); outputTask = job.Id;
        try
        {
            using var slot = await jobs.Slots.EnterAsync(current.Token);
            var result = await service.ExecuteAsync(plan, new Progress<ProcessingProgress>(job.Update), current.Token);
            foreach (var item in result.Items.Where(x => x.Outcome == NamingOutcome.Success && x.Item.Source is not null)) customNames.Remove(item.Item.Source!.Path);
            Report = result; PromoteCreatedDirectories(result); PathsChanged?.Invoke(this, result.Changes); outputs.Add(job.Id, job.Name, result.Outputs);
            var succeeded = result.Items.Where(item => item.Outcome == NamingOutcome.Success);
            var folders = plan.Request.Mode == NamingMode.Create
                ? succeeded.Take(1).Select(_ => plan.Request.Destination)
                : succeeded.Select(item => Path.GetDirectoryName(item.ActualPath ?? item.Item.DisplayPath)!);
            job.Complete(result.Summary, result.Items.Select(x => x.Description), result.Cancelled || result.Items.Any(x => x.Retryable), folders);
            Feedback = result.Summary;
        }
        catch (OperationCanceledException) { job.Fail("已取消。"); Feedback = "已取消执行。"; }
        catch (Exception e) { job.Fail(e.Message); Feedback = "执行未完成：" + e.Message; }
        finally { Plan = null; cancellation = null; IsExecuting = false; IsBusy = false; await RefreshRecoveriesAsync(); }
    }
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (!CanRetry || Report is not { } report) return; ActiveJob = null; IsBusy = true;
        using var current = new CancellationTokenSource(); cancellation = current; var version = revision;
        try { var plan = await service.RetryAsync(report, current.Token); if (version == revision) { Plan = plan; Feedback = "仅包含未完成项，保留原编号；请核对后开始。"; } }
        catch (Exception e) { Feedback = e.Message; }
        finally { cancellation = null; IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (!CanRestore || SelectedRecovery is not { } recovery) return;
        using var current = new CancellationTokenSource(); cancellation = current; ActiveJob = null; IsExecuting = true; IsBusy = true;
        try { var result = await service.RestoreAsync(recovery.Id, current.Token); PathsChanged?.Invoke(this, result.Changes); FilesRemoved?.Invoke(this, result.Removed); ApplyRemovedDirectories(result.Removed); Plan = null; Report = null; Feedback = result.Message; }
        catch (Exception e) { Feedback = e.Message; }
        finally { cancellation = null; IsExecuting = false; IsBusy = false; await RefreshRecoveriesAsync(); }
    }
    [RelayCommand] private async Task RefreshRecoveriesAsync()
    { try { Recoveries = await service.RecoveriesAsync(CancellationToken.None); SelectedRecovery = Recoveries.FirstOrDefault(); NotifySimple(); } catch (Exception e) { Feedback = "恢复记录读取失败：" + e.Message; } }
    [RelayCommand(CanExecute = nameof(IsBusy))] private void Cancel() => cancellation?.Cancel();
    [RelayCommand] private void ShowOutputs() => OutputsRequested?.Invoke(this, outputTask);
}
