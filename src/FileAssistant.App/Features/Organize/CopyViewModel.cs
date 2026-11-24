using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Outputs;
using FileAssistant.App.Features.Tasks;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Organize;

public partial class CopyViewModel : ObservableObject
{
    private readonly ICopyService service;
    private readonly OutputsViewModel outputs;
    private readonly ProcessingJobsViewModel jobs;
    private CancellationTokenSource? cancellation;
    private int revision, lastTask;
    private string identity = "";
    private CopyReport? retryBase;
    private TaskCompletionSource? idle;
    private string outputTaskName = "";
    private OutputLocation defaultLocation = OutputLocation.Fixed;
    private AppSettings? savedDefaults;
    private bool hadInputs;
    private bool applyingDefaults, restoringRetry, customLayout, customDestination, verifyHash = true;
    private bool autoSwitchedToFolders;

    private OutputLocation EffectiveLocation => Destination.Length > 0 ? OutputLocation.Fixed : defaultLocation;

    public string DestinationPlaceholder => (retryBase is not null && Plan is { } plan ? plan.Request.Location : defaultLocation) switch
    {
        OutputLocation.Source => "各源文件所在目录",
        OutputLocation.Subfolder => "各源目录下的 " + BatchOutputDirectory.SubfolderName,
        _ => "选择或填写目标文件夹"
    };

    public void ApplyDefaults(AppSettings? previous, AppSettings current)
    {
        savedDefaults = current;
        var outputChanged = previous is null || defaultLocation != current.OutputLocation || current.OutputLocation == OutputLocation.Fixed && previous.OutputFolder != current.OutputFolder;
        var verificationChanged = verifyHash != current.VerifyCopyHash;
        defaultLocation = current.OutputLocation;
        verifyHash = current.VerifyCopyHash;
        applyingDefaults = true;
        try
        {
            if (!customLayout)
            {
                LayoutValue = (int)current.CopyLayout;
                if (current.CopyLayout == CopyLayout.Extension)
                    ClassifyBy = ClassifyPrimary.Extension;
            }
            if (!customDestination && outputChanged)
                Destination = defaultLocation == OutputLocation.Fixed ? current.OutputFolder : "";
        }
        finally { applyingDefaults = false; }
        if (outputChanged)
        {
            OnPropertyChanged(nameof(DestinationPlaceholder));
            if (!customDestination) Invalidate();
        }
        if (verificationChanged) Invalidate();
    }

    public Task WaitForIdleAsync() => idle?.Task ?? Task.CompletedTask;
    public WorkspaceViewModel Input { get; }

    public CopyViewModel(ICopyService service, WorkspaceViewModel input, OutputsViewModel outputs, ProcessingJobsViewModel jobs)
    {
        this.service = service;
        Input = input;
        this.outputs = outputs;
        this.jobs = jobs;
        identity = InputIdentity;
        hadInputs = input.SearchEntries.Count > 0;
        input.PropertyChanged += InputChanged;
        input.VisibleFilesRefreshed += (_, _) => UpdateFilePreviews();
    }

    public IReadOnlyList<SelectionOption> Layouts { get; } =
    [
        new(0, "照原样放"),
        new(1, "把子文件夹里的文件放到一起"),
        new(3, "自动分类建文件夹"),
        new(4, "只复制空文件夹结构")
    ];
    public IReadOnlyList<SelectionOption> Conflicts => WorkspaceSelectionOptions.Conflicts;

    // Top Operation: Copy (default) vs Move (explicit; never flipped silently by destination changes)
    [ObservableProperty] public partial CopyOperation Operation { get; set; } = CopyOperation.Copy;
    public bool IsMoveOperation => Operation == CopyOperation.Move && !IsLayoutSkeleton;
    public bool IsCopyOperation => !IsMoveOperation;
    public bool CanSelectMove => CanEdit && !IsLayoutSkeleton;
    public string MoveDisabledTip => IsLayoutSkeleton
        ? "只复制空文件夹结构不支持移动原文件"
        : "将文件移动到目标文件夹（同盘原子改名，跨盘校验哈希后移除原件，支持撤回）";

    [ObservableProperty] public partial string Destination { get; set; } = "";
    [ObservableProperty] public partial int LayoutValue { get; set; }
    [ObservableProperty] public partial int ConflictValue { get; set; }

    public bool IsLayoutPreserve => LayoutValue == 0;
    public bool IsLayoutFlat => LayoutValue == 1;
    public bool IsLayoutByExtension => LayoutValue == 2;
    public bool IsLayoutClassify => LayoutValue is 2 or 3;
    public bool IsLayoutSkeleton => LayoutValue == 4;
    public bool CanSelectSkeleton => CanEdit && Operation != CopyOperation.Move;
    public bool ShowConflictRow => !IsLayoutSkeleton;
    public string SkeletonSubtitle => Operation == CopyOperation.Move
        ? "仅在「复制」模式下可用（不拷文件）"
        : "只把多级目录树空壳复制过去，不拷里面的文件";

    // Option 2 (Flat / Gather from subfolders) sub-options
    [ObservableProperty] public partial GatherFilter GatherFilter { get; set; } = GatherFilter.All;
    [ObservableProperty] public partial bool GatherPrefixSourceFolder { get; set; } = true;
    public bool IsGatherAll => GatherFilter == GatherFilter.All;
    public bool IsGatherDocs => GatherFilter == GatherFilter.DocumentsAndSheets;
    public bool IsGatherImages => GatherFilter == GatherFilter.Images;

    // Option 3 (Auto classify into folders) sub-options
    [ObservableProperty] public partial ClassifyPrimary ClassifyBy { get; set; } = ClassifyPrimary.FileType;
    [ObservableProperty] public partial ClassifyDateGranularity SubDateLayer { get; set; } = ClassifyDateGranularity.None;
    [ObservableProperty] public partial ClassifyDateGranularity DateGranularity { get; set; } = ClassifyDateGranularity.YearMonth;
    [ObservableProperty] public partial bool PreferMediaDate { get; set; } = true;
    [ObservableProperty] public partial NameMatchRule NameMatchRule { get; set; } = NameMatchRule.ExistingFolderFirst;

    private ClassifyPrimary EffectiveClassifyBy => LayoutValue == (int)CopyLayout.Extension ? ClassifyPrimary.Extension : ClassifyBy;
    public bool IsClassifyByType => EffectiveClassifyBy == ClassifyPrimary.FileType;
    public bool IsClassifyByDate => EffectiveClassifyBy == ClassifyPrimary.Date;
    public bool IsClassifyByNameMatch => EffectiveClassifyBy == ClassifyPrimary.NameMatch;
    public bool IsClassifyByExtension => EffectiveClassifyBy == ClassifyPrimary.Extension;
    public bool IsSubDateNone => SubDateLayer == ClassifyDateGranularity.None;
    public bool IsSubDateYearMonth => SubDateLayer == ClassifyDateGranularity.YearMonth;
    public bool IsSubDateYear => SubDateLayer == ClassifyDateGranularity.Year;
    public bool IsDateYearMonth => DateGranularity == ClassifyDateGranularity.YearMonth;
    public bool IsDateYear => DateGranularity == ClassifyDateGranularity.Year;
    public bool IsNameMatchExisting => NameMatchRule == NameMatchRule.ExistingFolderFirst;
    public bool IsNameMatchPrefix => NameMatchRule == NameMatchRule.PrefixAlways;
    public bool ShowSubDateLayer => IsLayoutClassify && EffectiveClassifyBy != ClassifyPrimary.Date;
    public bool ShowDateGranularity => IsLayoutClassify && EffectiveClassifyBy == ClassifyPrimary.Date;
    public bool ShowNameMatchRule => IsLayoutClassify && EffectiveClassifyBy == ClassifyPrimary.NameMatch;
    public bool ShowPreferMediaDate => IsLayoutClassify && (EffectiveClassifyBy == ClassifyPrimary.Date || SubDateLayer != ClassifyDateGranularity.None);

    // Option 4 (Skeleton empty folder tree) sub-options
    [ObservableProperty] public partial bool ReplaceSkeletonText { get; set; }
    [ObservableProperty] public partial string SkeletonFind { get; set; } = "";
    [ObservableProperty] public partial string SkeletonReplace { get; set; } = "";

    // Center View Switcher: File List (default) vs Target Folders
    [ObservableProperty] public partial bool ShowTargetFoldersView { get; set; }
    public bool ShowFileListView => !ShowTargetFoldersView;
    public bool ShowTargetFoldersStage => ShowTargetFoldersView && !Input.ShowEmpty && !Input.IsLoading && !Input.HasError;

    [ObservableProperty] public partial CopyPlan? Plan { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDecision), nameof(CanApproveOverwrite), nameof(SelectedPlanPaths),
        nameof(SelectedPlanSource), nameof(SelectedPlanTarget), nameof(SelectedPlanNote))]
    public partial CopyPlanItem? SelectedPlanItem { get; set; }
    public IReadOnlyList<CopyPlanItem> PlanItems => Plan?.Items ?? [];
    public IReadOnlyList<CopyPlanItem> PendingItems => PlanItems.Where(item => item.NeedsDecision).ToArray();
    public bool HasPendingItems => Plan?.PendingCount > 0;
    public bool HasPlan => Plan is not null;
    public string SelectedPlanPaths => SelectedPlanItem?.Paths ?? "";
    public string SelectedPlanSource => SelectedPlanItem?.Source.Path ?? "";
    public string SelectedPlanTarget => SelectedPlanItem?.Target ?? "";
    public string SelectedPlanNote => SelectedPlanItem?.Message ?? "";
    [ObservableProperty] public partial CopyReport? Report { get; set; }
    [ObservableProperty] public partial IReadOnlyList<CopyRecovery> Recoveries { get; set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RestoreActionText), nameof(DiscardActionText), nameof(CanActOnSelectedRecovery))]
    public partial CopyRecovery? SelectedRecovery { get; set; }

    private const string InitialFeedback = "选择目标目录后，自动扫描并预览整理计划。";
    private const string ReadyFeedback = "计划已就绪，请核对来源和目标路径。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultFeedback), nameof(HasPlanNotice), nameof(EmptyTargetFoldersMessage))]
    public partial string Feedback { get; set; } = InitialFeedback;
    public bool HasPlanNotice => Feedback.Length > 0 && Feedback != InitialFeedback && Feedback != ReadyFeedback;
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial bool IsResolving { get; set; }
    [ObservableProperty] public partial ProcessingJob? ActiveJob { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PanelTitle), nameof(ShowSettings), nameof(ShowProgress),
        nameof(ShowResults), nameof(ShowRecovery))]
    private partial CopyPanel Panel { get; set; }
    [ObservableProperty] public partial string RunError { get; set; } = "";
    private enum CopyPanel { Settings, Progress, Results, Recovery }
    private CopyPanel panelBeforeRecovery;
    public bool ShowSettings => Panel == CopyPanel.Settings;
    public bool ShowProgress => Panel == CopyPanel.Progress;
    public bool ShowResults => Panel == CopyPanel.Results;
    public bool ShowRecovery => Panel == CopyPanel.Recovery;
    public string PanelTitle => Panel switch
    {
        CopyPanel.Progress => IsMoveOperation ? "正在移动" : "正在复制",
        CopyPanel.Results => "本次处理结果",
        CopyPanel.Recovery => "撤回移动与覆盖恢复",
        _ => "复制与整理"
    };
    public bool IsBusy => IsScanning || IsRunning || IsResolving;
    public bool CanCancel => IsScanning || IsRunning;
    public bool CanEdit => !IsRunning && !IsResolving;
    public bool CanChangePanel => !IsBusy;
    public bool CanBuild => ShowSettings && !IsBusy && !Input.IsLoading && !Input.HasSamples && Input.EffectiveEntries.Count > 0 && (EffectiveLocation != OutputLocation.Fixed || !string.IsNullOrWhiteSpace(Destination));
    public bool CanRun => ShowSettings && !IsBusy && Plan is { PendingCount: 0, ReadyCount: > 0 };
    public bool CanRetry => !IsBusy && Report?.Items.Any(x => x.Retryable) == true;
    public bool HasOutputs => Report?.Items.Any(item => item.Output is not null) == true;
    public IReadOnlyList<SearchFile> OutputFiles =>
        Report?.Outputs.Select(output => new SearchFile(output.FullPath, output.IsDirectory, output.IsDirectory ? null : output.Size, output.Modified)).ToArray() ?? [];
    public bool HasRecoveries => Recoveries.Count > 0;
    public bool NoRecoveries => !HasRecoveries;
    public bool CanRecover => !IsBusy && HasRecoveries;
    public bool CanActOnSelectedRecovery => CanRecover && SelectedRecovery is not null;
    public string RestoreActionText => SelectedRecovery?.ActionLabel ?? "恢复 / 撤回所选记录";
    public string DiscardActionText => SelectedRecovery?.IsMoveUndo == true ? "移除此条移动记录…" : "删除所选备份…";
    public string RecoveryDisclosureLabel => Recoveries.Count > 0 ? $"撤回移动与覆盖恢复记录 ({Recoveries.Count})" : "撤回移动与覆盖恢复记录";
    public bool HasResults => Report is not null || RunError.Length > 0;
    public IReadOnlyList<CopyItemResult> ResultItems => Report?.Items ?? [];
    public string ResultSummary => Report?.Summary ?? "处理未完成";
    public string ResultFeedback => Feedback == ResultSummary ? "" : Feedback;
    public bool HasResultItems => ResultItems.Count > 0;
    public bool HasDecision => !IsBusy && SelectedPlanItem?.NeedsDecision == true;
    public bool CanApproveOverwrite => HasDecision && SelectedPlanItem?.CanOverwrite == true;
    public string InputIdentity => $"{Input.CurrentFolderId}:{Input.CollectionRevision}:" + string.Join('\0', Input.EffectiveEntries.Select(x => x.FullPath));

    public int HiddenSelectedCount => Math.Max(0, Input.SelectionCount - Input.VisibleSelectedCount);
    public bool HasHiddenSelectionWarning => ShowSettings && Input.SelectionCount > 0 && HiddenSelectedCount > 0;
    public string HiddenSelectionWarningText => $"已勾选 {Input.SelectionCount:N0} 项（含 {HiddenSelectedCount:N0} 项被筛选隐藏，仍会一并处理）";
    public string PrimaryActionText => IsLayoutSkeleton ? "复制空目录结构" : IsMoveOperation ? "开始移动" : "开始复制";
    public string CancelActionText => IsMoveOperation ? "取消移动" : "取消复制";

    public string FooterScope => Plan is { } plan
        ? plan.IsSkeleton ? $"待新建 {plan.ReadyCount:N0} 个空文件夹" : $"待{(plan.IsMove ? "移动" : "复制")} {plan.ReadyCount:N0} 项"
        : $"待检查 {Input.EffectiveEntries.Count:N0} 项";

    public string ScopeSummary
    {
        get
        {
            if (Plan is { } fixedPlan && (IsRunning || retryBase is not null))
                return (IsRunning ? "本次固定计划" : "仅重试未完成项") + $"\n{CopyScope(fixedPlan)}";
            var scope = Input.SelectionCount > 0 ? Input.SelectedScopeSummary
                : Input.EffectiveEntries.Count == 0 ? "请先添加文件或文件夹" : $"当前文件夹及子文件夹 · {Input.EffectiveEntries.Count:N0} 项输入";
            return Plan is { } plan ? scope + "\n" + CopyScope(plan) : scope;
        }
    }

    private static string CopyScope(CopyPlan plan)
    {
        if (plan.IsSkeleton)
            return $"待新建 {plan.ReadyCount:N0} 个空目录（不拷贝内部文件）";
        var verb = plan.IsMove ? "待移动" : "待复制";
        var files = plan.Items.Count(item => item.CanRun && !item.Source.IsDirectory);
        var folders = plan.ReadyCount - files;
        var scope = $"{verb} {files:N0} 个文件 · {WorkspaceFile.FormatSize(plan.CopyBytes)}";
        if (folders > 0) scope += $" · 保留 {folders:N0} 个目录";
        if (plan.PendingCount > 0) scope += $" · {plan.PendingCount:N0} 项待确认";
        return scope;
    }

    public string PlanSummary => Plan is null ? "尚无可执行计划" :
        $"待处理 {Plan.ReadyCount:N0} · 待确认 {Plan.PendingCount:N0} · 跳过 {Plan.Items.Count(x => x.Action == CopyAction.Skip):N0} · 不可处理 {Plan.Items.Count(x => x.Action == CopyAction.Rejected):N0}";

    public event EventHandler? PlanInvalidated;
    public event EventHandler<int>? OutputsRequested;
    public event EventHandler<IReadOnlyList<PathChange>>? PathsChanged;

    public void SetOperation(CopyOperation operation)
    {
        if (!CanEdit) return;
        if (operation == CopyOperation.Move && IsLayoutSkeleton) return;
        Operation = operation;
    }

    public void SelectGatherFilter(GatherFilter filter)
    {
        if (!CanEdit) return;
        GatherFilter = filter;
    }

    public void SelectClassifyBy(ClassifyPrimary primary)
    {
        if (!CanEdit) return;
        if (LayoutValue == (int)CopyLayout.Extension) LayoutValue = (int)CopyLayout.Classify;
        ClassifyBy = primary;
    }

    public void SelectSubDateLayer(ClassifyDateGranularity layer)
    {
        if (!CanEdit) return;
        if (LayoutValue == (int)CopyLayout.Extension) LayoutValue = (int)CopyLayout.Classify;
        SubDateLayer = layer;
    }

    public void SelectDateGranularity(ClassifyDateGranularity granularity)
    {
        if (!CanEdit) return;
        DateGranularity = granularity;
    }

    public void SelectNameMatchRule(NameMatchRule rule)
    {
        if (!CanEdit) return;
        NameMatchRule = rule;
    }

    private void InputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.CollectionRevision))
        {
            var hasInputs = Input.SearchEntries.Count > 0;
            if (hadInputs && !hasInputs && savedDefaults is { } defaults)
            {
                customDestination = customLayout = false;
                ApplyDefaults(null, defaults);
            }
            hadInputs = hasInputs;
        }
        if (e.PropertyName is nameof(WorkspaceViewModel.EffectiveEntries) or nameof(WorkspaceViewModel.CollectionRevision) or nameof(WorkspaceViewModel.IsLoading) or nameof(WorkspaceViewModel.BatchSummary))
        {
            var next = InputIdentity;
            if (identity != next) { identity = next; Invalidate(); }
            NotifyState();
        }
        if (e.PropertyName is nameof(WorkspaceViewModel.VisibleSelectedCount) or nameof(WorkspaceViewModel.SelectionCount))
        {
            OnPropertyChanged(nameof(ScopeSummary));
            OnPropertyChanged(nameof(HiddenSelectedCount));
            OnPropertyChanged(nameof(HasHiddenSelectionWarning));
            OnPropertyChanged(nameof(HiddenSelectionWarningText));
        }
        if (e.PropertyName is nameof(WorkspaceViewModel.ShowEmpty) or nameof(WorkspaceViewModel.IsLoading) or nameof(WorkspaceViewModel.HasError))
            OnPropertyChanged(nameof(ShowTargetFoldersStage));
    }

    private void Invalidate()
    {
        if (restoringRetry) return;
        revision++;
        if (IsScanning) cancellation?.Cancel();
        if (!IsRunning) { Plan = null; retryBase = null; }
        PlanInvalidated?.Invoke(this, EventArgs.Empty);
        NotifyState();
    }

    partial void OnOperationChanged(CopyOperation value)
    {
        NotifyOptionFlags();
        Invalidate();
    }

    partial void OnDestinationChanged(string value)
    {
        if (!applyingDefaults) customDestination = value.Length > 0 || defaultLocation == OutputLocation.Fixed;
        OnPropertyChanged(nameof(EmptyTargetFoldersMessage));
        Invalidate();
    }

    partial void OnLayoutValueChanged(int value)
    {
        if (value == (int)CopyLayout.Skeleton && Operation == CopyOperation.Move && !restoringRetry)
        {
            // Skeleton is disabled in Move mode; never silently mutate Operation, revert layout if forced.
            LayoutValue = (int)CopyLayout.Preserve;
            return;
        }
        if (!applyingDefaults) customLayout = true;
        if (value == (int)CopyLayout.Skeleton)
        {
            if (!ShowTargetFoldersView) { ShowTargetFoldersView = true; autoSwitchedToFolders = true; }
        }
        else if (autoSwitchedToFolders)
        {
            ShowTargetFoldersView = false;
            autoSwitchedToFolders = false;
        }
        NotifyOptionFlags();
        Invalidate();
    }

    partial void OnConflictValueChanged(int value) => Invalidate();
    partial void OnGatherFilterChanged(GatherFilter value) { NotifyOptionFlags(); Invalidate(); }
    partial void OnGatherPrefixSourceFolderChanged(bool value) => Invalidate();
    partial void OnClassifyByChanged(ClassifyPrimary value) { NotifyOptionFlags(); Invalidate(); }
    partial void OnSubDateLayerChanged(ClassifyDateGranularity value) { NotifyOptionFlags(); Invalidate(); }
    partial void OnDateGranularityChanged(ClassifyDateGranularity value) { NotifyOptionFlags(); Invalidate(); }
    partial void OnPreferMediaDateChanged(bool value) => Invalidate();
    partial void OnNameMatchRuleChanged(NameMatchRule value) { NotifyOptionFlags(); Invalidate(); }
    partial void OnReplaceSkeletonTextChanged(bool value) => Invalidate();
    partial void OnSkeletonFindChanged(string value) { if (ReplaceSkeletonText) Invalidate(); }
    partial void OnSkeletonReplaceChanged(string value) { if (ReplaceSkeletonText) Invalidate(); }

    partial void OnShowTargetFoldersViewChanged(bool value)
    {
        if (!value) autoSwitchedToFolders = false;
        OnPropertyChanged(nameof(ShowFileListView));
        OnPropertyChanged(nameof(ShowTargetFoldersStage));
    }

    private void NotifyOptionFlags()
    {
        foreach (var prop in new[]
        {
            nameof(IsMoveOperation), nameof(IsCopyOperation), nameof(CanSelectMove), nameof(MoveDisabledTip),
            nameof(IsLayoutPreserve), nameof(IsLayoutFlat), nameof(IsLayoutByExtension), nameof(IsLayoutClassify), nameof(IsLayoutSkeleton),
            nameof(CanSelectSkeleton), nameof(ShowConflictRow), nameof(SkeletonSubtitle),
            nameof(IsGatherAll), nameof(IsGatherDocs), nameof(IsGatherImages),
            nameof(IsClassifyByType), nameof(IsClassifyByDate), nameof(IsClassifyByNameMatch), nameof(IsClassifyByExtension),
            nameof(IsSubDateNone), nameof(IsSubDateYearMonth), nameof(IsSubDateYear),
            nameof(IsDateYearMonth), nameof(IsDateYear),
            nameof(IsNameMatchExisting), nameof(IsNameMatchPrefix),
            nameof(ShowSubDateLayer), nameof(ShowDateGranularity), nameof(ShowNameMatchRule), nameof(ShowPreferMediaDate),
            nameof(PrimaryActionText), nameof(CancelActionText), nameof(PanelTitle), nameof(EmptyTargetFoldersMessage)
        })
            OnPropertyChanged(prop);
    }

    partial void OnPlanChanged(CopyPlan? value)
    {
        OnPropertyChanged(nameof(DestinationPlaceholder));
        OnPropertyChanged(nameof(PlanItems));
        OnPropertyChanged(nameof(PendingItems));
        OnPropertyChanged(nameof(HasPendingItems));
        OnPropertyChanged(nameof(HasPlan));
        SelectedPlanItem = value?.Items.FirstOrDefault(x => x.NeedsDecision) ?? value?.Items.FirstOrDefault();
        RebuildFilePreviews();
        NotifyState();
    }

    partial void OnReportChanged(CopyReport? value)
    {
        OnPropertyChanged(nameof(ResultItems));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(ResultFeedback));
        OnPropertyChanged(nameof(HasResultItems));
        RebuildFilePreviews();
        NotifyState();
    }

    partial void OnPanelChanged(CopyPanel value)
    {
        OnPropertyChanged(nameof(HasHiddenSelectionWarning));
        RebuildFilePreviews();
        NotifyState();
    }

    partial void OnRunErrorChanged(string value) => OnPropertyChanged(nameof(HasResults));
    partial void OnIsScanningChanged(bool value) { OnPropertyChanged(nameof(EmptyTargetFoldersMessage)); NotifyState(); }
    partial void OnIsRunningChanged(bool value) => NotifyState();
    partial void OnIsResolvingChanged(bool value) => NotifyState();

    partial void OnRecoveriesChanged(IReadOnlyList<CopyRecovery> value)
    {
        SelectedRecovery = value.FirstOrDefault();
        OnPropertyChanged(nameof(HasRecoveries));
        OnPropertyChanged(nameof(NoRecoveries));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(CanActOnSelectedRecovery));
        OnPropertyChanged(nameof(RecoveryDisclosureLabel));
    }

    private void NotifyState()
    {
        if (IsBusy) idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        else { idle?.TrySetResult(); idle = null; }
        foreach (var property in new[]
        {
            nameof(IsBusy), nameof(CanEdit), nameof(CanChangePanel), nameof(CanBuild), nameof(CanRun), nameof(CanRetry),
            nameof(CanSelectMove), nameof(CanSelectSkeleton),
            nameof(HasOutputs), nameof(HasResults), nameof(ScopeSummary), nameof(FooterScope), nameof(PlanSummary),
            nameof(HiddenSelectedCount), nameof(HasHiddenSelectionWarning), nameof(HiddenSelectionWarningText)
        })
            OnPropertyChanged(property);
        BuildPlanCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasDecision));
        OnPropertyChanged(nameof(CanApproveOverwrite));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRecover));
        OnPropertyChanged(nameof(CanActOnSelectedRecovery));
        CancelCommand.NotifyCanExecuteChanged();
        ShowOutputsCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private CopyRequest CreateCurrentRequest() =>
        new(Input.EffectiveEntries.Select(x => x.FullPath).ToArray(), Destination.Trim(), (CopyLayout)LayoutValue, (CopyConflictPolicy)ConflictValue)
        {
            Operation = IsLayoutSkeleton ? CopyOperation.Copy : Operation,
            Location = EffectiveLocation,
            VerifyHash = verifyHash,
            GatherFilter = GatherFilter,
            GatherPrefixSourceFolder = GatherPrefixSourceFolder,
            ClassifyBy = EffectiveClassifyBy,
            SubDateLayer = SubDateLayer,
            DateGranularity = DateGranularity,
            PreferMediaDate = PreferMediaDate,
            NameMatchRule = NameMatchRule,
            ReplaceSkeletonText = ReplaceSkeletonText,
            SkeletonFind = SkeletonFind,
            SkeletonReplace = SkeletonReplace
        };

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildPlanAsync()
    {
        if (!CanBuild) return;
        var currentRevision = revision;
        var request = CreateCurrentRequest();
        retryBase = null;
        Plan = null;
        IsScanning = true;
        using var current = new CancellationTokenSource();
        cancellation = current;
        Feedback = "正在扫描文件和目标目录…";
        try
        {
            var plan = await service.PlanAsync(request, new Progress<ProcessingProgress>(p => { if (IsScanning && revision == currentRevision) Feedback = p.Message; }), current.Token);
            if (current.IsCancellationRequested || revision != currentRevision) return;
            Plan = plan;
            var verb = plan.IsSkeleton ? "创建空目录" : plan.IsMove ? "移动" : "复制";
            Feedback = plan.PendingCount > 0
                ? $"请逐项处理待确认冲突，然后开始{verb}。"
                : plan.ReadyCount > 0
                    ? ReadyFeedback
                    : plan.IsSkeleton
                        ? "当前输入没有可复制的子文件夹层级，请添加文件夹。"
                        : $"没有待{verb}项，请查看跳过及不可处理原因。";
        }
        catch (OperationCanceledException) { Feedback = revision == currentRevision ? "扫描已取消，未生成可执行计划。" : "输入或规则已变化，正在更新计划。"; }
        catch (Exception error) { Feedback = "无法生成计划：" + error.Message; }
        finally { if (ReferenceEquals(cancellation, current)) cancellation = null; IsScanning = false; }
    }

    public async Task DecideAsync(Guid id, CopyDecision decision)
    {
        if (IsBusy || Plan is not { } plan) return;
        var currentRevision = revision;
        IsResolving = true;
        try
        {
            var updated = await service.DecideAsync(plan, id, decision, CancellationToken.None);
            if (revision != currentRevision) return;
            Plan = updated;
            Feedback = updated.PendingCount > 0 ? "请继续核对下一项同名冲突。" : "同名冲突已处理，请核对去向。";
        }
        catch (Exception error) { Feedback = error.Message; }
        finally { IsResolving = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!CanRun || Plan is not { } plan) return;
        IsRunning = true;
        RunError = "";
        Panel = CopyPanel.Progress;
        using var current = new CancellationTokenSource();
        cancellation = current;
        var baseReport = retryBase;
        var jobTitle = plan.IsMove ? "批量移动" : "批量复制";
        var verb = plan.IsMove ? "移动" : "复制";
        var job = jobs.Start(current.Cancel, jobTitle);
        ActiveJob = job;
        if (baseReport is null)
        {
            Report = null;
            lastTask = job.Id;
            outputTaskName = job.Name;
        }
        Feedback = $"正在{verb}，输入与规则已固定为本次计划。";
        try
        {
            using var slot = await jobs.Slots.EnterAsync(current.Token);
            var result = await service.ExecuteAsync(plan, new Progress<ProcessingProgress>(job.Update), current.Token);
            outputs.Add(lastTask, outputTaskName, result.Outputs);
            if (result.Changes.Count > 0)
                PathsChanged?.Invoke(this, result.Changes);
            Report = baseReport is null ? result : result with
            {
                Plan = baseReport.Plan with { Items = baseReport.Plan.Items.Select(x => result.Items.FirstOrDefault(n => n.Item.Id == x.Id)?.Item ?? x).ToArray() },
                Items = baseReport.Items.Select(x => result.Items.FirstOrDefault(n => n.Item.Id == x.Item.Id) ?? x).ToArray()
            };
            job.Complete(result.Summary, result.Items.Select(x => $"{x.Item.Source.Path}\n→ {x.Item.Target}\n{x.Message}"), result.Cancelled || result.StopReason is not null,
                result.Items.Where(item => item.Outcome == CopyOutcome.Success).Select(item => plan.Request.DirectoryFor(item.Item.Source)));
            Feedback = result.Summary;
        }
        catch (OperationCanceledException) { job.Fail("已取消。"); RunError = Feedback = $"已取消{verb}。"; }
        catch (Exception error) { job.Fail(error.Message); RunError = Feedback = $"{verb}未完成：" + error.Message; }
        finally
        {
            if (ReferenceEquals(cancellation, current)) cancellation = null;
            Panel = CopyPanel.Results;
            Plan = null;
            retryBase = null;
            IsRunning = false;
            await RefreshRecoveriesAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (!CanRetry || Report is not { } report) return;
        var currentRevision = revision;
        IsScanning = true;
        Plan = null;
        Feedback = "正在重新检查未完成项…";
        using var current = new CancellationTokenSource();
        cancellation = current;
        try
        {
            var plan = await service.RetryPlanAsync(report, current.Token);
            if (current.IsCancellationRequested || revision != currentRevision) return;
            applyingDefaults = restoringRetry = true;
            try
            {
                Operation = plan.Request.Operation;
                Destination = plan.Request.Destination;
                LayoutValue = (int)plan.Request.Layout;
                ConflictValue = (int)plan.Request.Conflict;
                GatherFilter = plan.Request.GatherFilter;
                GatherPrefixSourceFolder = plan.Request.GatherPrefixSourceFolder;
                ClassifyBy = plan.Request.ClassifyBy;
                SubDateLayer = plan.Request.SubDateLayer;
                DateGranularity = plan.Request.DateGranularity;
                PreferMediaDate = plan.Request.PreferMediaDate;
                NameMatchRule = plan.Request.NameMatchRule;
                ReplaceSkeletonText = plan.Request.ReplaceSkeletonText;
                SkeletonFind = plan.Request.SkeletonFind;
                SkeletonReplace = plan.Request.SkeletonReplace;
            }
            finally { applyingDefaults = restoringRetry = false; }
            NotifyOptionFlags();
            retryBase = report;
            Plan = plan;
            Panel = CopyPanel.Settings;
            var verb = plan.IsMove ? "移动" : "复制";
            Feedback = $"已重新检查未完成项，请核对计划后点击开始{verb}。成功与主动跳过的项目不会重做。";
        }
        catch (OperationCanceledException) { Feedback = "已取消重试计划。"; }
        catch (Exception error) { Feedback = error.Message; }
        finally { if (ReferenceEquals(cancellation, current)) cancellation = null; IsScanning = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        cancellation?.Cancel();
        Feedback = "正在取消，等待当前读写结束并清理临时文件…";
    }

    [RelayCommand(CanExecute = nameof(HasOutputs))]
    private void ShowOutputs() => OutputsRequested?.Invoke(this, lastTask);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ClearSelection() => Input.SetSelection([]);

    [RelayCommand]
    private void ContinueEditing()
    {
        if (IsBusy) return;
        Panel = CopyPanel.Settings;
        if (Plan is null) PlanInvalidated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ShowLastResult()
    {
        if (!IsBusy && HasResults) Panel = CopyPanel.Results;
    }

    [RelayCommand]
    private async Task OpenRecoveryAsync()
    {
        if (IsBusy || ShowRecovery) return;
        panelBeforeRecovery = Panel;
        Panel = CopyPanel.Recovery;
        await RefreshRecoveriesAsync();
    }

    [RelayCommand]
    private void CloseRecovery()
    {
        if (IsBusy) return;
        if (panelBeforeRecovery == CopyPanel.Results) Panel = CopyPanel.Results;
        else ContinueEditing();
    }

    [RelayCommand]
    private async Task RefreshRecoveriesAsync()
    {
        try { Recoveries = await service.RecoveriesAsync(CancellationToken.None); }
        catch (Exception error) { Feedback = "无法读取恢复记录：" + error.Message; }
    }

    public async Task RecoverAsync(Guid id, bool discard)
    {
        if (IsBusy) return;
        IsResolving = true;
        try
        {
            if (discard)
            {
                await service.DiscardBackupAsync(id, CancellationToken.None);
                Feedback = "已移除所选备份或撤回记录，当前目标文件保留。";
            }
            else
            {
                var changes = await service.RestoreWithChangesAsync(id, CancellationToken.None);
                if (changes.Count > 0)
                {
                    PathsChanged?.Invoke(this, changes);
                    Feedback = $"已撤回移动，{changes.Count:N0} 个文件已回到原位置。";
                }
                else
                {
                    Feedback = "已恢复旧目标文件。";
                }
            }
            await RefreshRecoveriesAsync();
            Invalidate();
        }
        catch (Exception error) { Feedback = "操作未完成：" + error.Message; }
        finally { IsResolving = false; }
    }
}
