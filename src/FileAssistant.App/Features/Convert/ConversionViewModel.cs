using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Outputs;
using FileAssistant.App.Features.Tasks;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Convert;

public partial class ConversionViewModel : ObservableObject
{
    private readonly IFileProcessingService service;
    private readonly OutputsViewModel outputs;
    private readonly ProcessingJobsViewModel jobs;
    private CancellationTokenSource? cancellation;
    private string inputIdentity = string.Empty;
    private int optionsRevision;
    private OutputLocation defaultLocation = OutputLocation.Fixed;
    private AppSettings? savedDefaults;
    private bool hadInputs;
    private bool applyingDefaults, customOutput, customQuality, customEncoding, customConflict, preferHardware;
    private OutputLocation EffectiveLocation => OutputDirectory.Length > 0 ? OutputLocation.Fixed : defaultLocation;
    public string OutputPlaceholder => defaultLocation switch
    {
        OutputLocation.Source => "各源文件所在目录",
        OutputLocation.Subfolder => "各源目录下的 " + BatchOutputDirectory.SubfolderName,
        _ => "选择或填写输出文件夹"
    };

    public void ApplyDefaults(AppSettings? previous, AppSettings current)
    {
        savedDefaults = current;
        var outputChanged = previous is null || defaultLocation != current.OutputLocation || current.OutputLocation == OutputLocation.Fixed && previous.OutputFolder != current.OutputFolder;
        var hardwareChanged = preferHardware != current.PreferHardwareEncoding;
        preferHardware = current.PreferHardwareEncoding;
        defaultLocation = current.OutputLocation;
        applyingDefaults = true;
        try
        {
            if (!customQuality) Quality = Math.Clamp(current.ImageQuality, 50, 100);
            if (!customEncoding) EncodingValue = (int)current.TextEncoding;
            if (!customConflict) ConflictPolicyValue = (int)current.ConversionConflict;
            if (!customOutput && outputChanged)
                OutputDirectory = defaultLocation == OutputLocation.Fixed ? current.OutputFolder : "";
        }
        finally { applyingDefaults = false; }
        if (outputChanged)
        {
            OnPropertyChanged(nameof(OutputPlaceholder));
            OnPropertyChanged(nameof(CanBuildPlan));
            BuildPlanCommand.NotifyCanExecuteChanged();
            if (!customOutput) Invalidate();
        }
        if (hardwareChanged) Invalidate();
    }
    private HashSet<string> availableEngines = [];
    public WorkspaceViewModel Input { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFormats), nameof(CanSelectFormat), nameof(CanBuildPlan), nameof(CanRun), nameof(FormatButtonContent))]
    [NotifyCanExecuteChangedFor(nameof(BuildPlanCommand), nameof(RunCommand))]
    public partial IReadOnlyList<SelectionOption> Formats { get; set; } = [];
    public ConversionViewModel(IFileProcessingService service, WorkspaceViewModel input, OutputsViewModel outputs, ProcessingJobsViewModel jobs)
    {
        this.service = service; Input = input; this.outputs = outputs; this.jobs = jobs;
        hadInputs = input.SearchEntries.Count > 0;
        input.PropertyChanged += InputChanged;
        input.VisibleFilesRefreshed += (_, _) => UpdateFilePreviews();
        RefreshCapabilities();
    }
    // A stable ConversionTarget value, never the position in the filtered menu.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBuildPlan), nameof(CanRun), nameof(ShowQuality), nameof(ShowPdfDpi), nameof(ConversionNote))]
    [NotifyCanExecuteChangedFor(nameof(BuildPlanCommand), nameof(RunCommand))]
    public partial int TargetFormatValue { get; set; } = -1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFormatError))] public partial string FormatFeedback { get; set; } = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FormatButtonContent))] public partial bool HasMissingEngine { get; set; }
    [ObservableProperty] public partial double Quality { get; set; } = 90;
    [ObservableProperty] public partial double PdfDpi { get; set; } = 150;
    [ObservableProperty] public partial string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FileAssistant输出");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRun), nameof(PlanSummary)), NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial ConversionPlan? Plan { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanSelectFormat), nameof(CanBuildPlan), nameof(CanRun)), NotifyCanExecuteChangedFor(nameof(BuildPlanCommand), nameof(RunCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial ProcessingJob? ActiveJob { get; set; }
    [ObservableProperty] public partial bool MoreSettingsOpen { get; set; }
    private string runFailure = "";
    private string runningScope = "";
    public bool ShowResultPanel => !IsBusy && (showReportPreviews || runFailure.Length > 0);
    public bool ShowSettings => !IsExecuting && !ShowResultPanel;
    public bool ShowMoreSettings => false;
    public bool IsChecking => IsBusy && !IsExecuting;
    public bool CanChangeInput => !IsExecuting;
    public string PanelTitle => IsExecuting ? "正在转换" : ShowResultPanel ? "本次处理结果" : "格式转换";
    public string ResultSummary => showReportPreviews && Report is { } report ? report.Summary : runFailure;
    public IReadOnlyList<SelectionOption> ConflictPolicies { get; } = [new(0, "自动编号"), new(1, "跳过同名"), new(2, "覆盖同名文件")];
    [ObservableProperty] public partial int ConflictPolicyValue { get; set; }
    public string FooterScope => Plan is { } plan ? $"{plan.ReadyCount:N0} 项可转换" : $"{ConversionEntries.Count:N0} 项待检查";
    public string ProductExtension => HasFormats && Enum.IsDefined(Target) ? ConversionCapabilities.Extension(Target) : "";
    public string ProductName => Plan?.Items.FirstOrDefault(item => item.CanRun)?.OutputPath is { } path
        ? Path.GetFileNameWithoutExtension(path) : "";
    public string PlanCount => PreviewDetails.Count > 0 ? $"{PreviewDetails.Count:N0} 项" : "待检查";
    public string ExecutionScope => IsExecuting ? runningScope : ScopeSummary +
        (Plan is { } plan ? $"\n预计生成 {plan.ReadyCount:N0} 个文件" : "");
    private const string ReadyFeedback = "计划已就绪。";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatusMessage))] public partial string Feedback { get; set; } = string.Empty;
    public bool HasStatusMessage => Feedback.Length > 0 && Feedback != ReadyFeedback;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOutputs)), NotifyCanExecuteChangedFor(nameof(ShowOutputsCommand))] public partial ConversionReport? Report { get; set; }
    public bool CanEdit => !IsBusy;
    public bool HasFormats => Formats.Count > 0;
    public bool CanSelectFormat => CanEdit && HasFormats;
    public bool CanBuildPlan => CanSelectFormat && !Input.IsLoading && (EffectiveLocation != OutputLocation.Fixed || !string.IsNullOrWhiteSpace(OutputDirectory)) && Formats.Any(option => option.Value == TargetFormatValue);
    public bool CanRun => ShowSettings && CanBuildPlan && Plan?.ReadyCount > 0;
    public bool HasFormatError => FormatFeedback.Length > 0;
    public string? FormatButtonContent => HasFormats ? IsWebPPreset ? "WebP 网页轻量" : null : HasMissingEngine ? "请先安装所需引擎" : ConversionEntries.Count == 0 ? "请先添加文件" : "无可用输出格式";
    public bool ShowQuality => TargetFormatValue is (int)ConversionTarget.Jpeg or (int)ConversionTarget.Avif || TargetFormatValue == (int)ConversionTarget.WebP && !ConversionEntries.Any(file => ConversionCapabilities.IsAnimation(file.FullPath, ConversionTarget.WebP));
    public bool ShowPdfDpi => HasFormats && Target != ConversionTarget.Txt && ConversionEntries.Any(file => file.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase));
    public string ConversionNote => ConversionEntries.Any(file => ConversionCapabilities.IsDocument(file.FullPath))
        ? "每份文档单独输出；字体和复杂排版可能影响分页。输出 TXT 按所选编码保存纯文本。启动文档引擎可能需要较长时间，可随时取消。"
        : ConversionEntries.Any(file => ConversionCapabilities.IsSpreadsheet(file.FullPath))
            ? "工作簿互转保留全部表；CSV/TSV 每表一个文件，0 表示全部，其他数字指定表序号。CSV/TSV 使用所选编码；TSV 固定 Tab，CSV 使用所选分隔符。可选公式或计算结果；导入等号开头内容默认作为文本，数字按英语区域规则识别。CSV/TSV 不保留样式、图表与多表结构；PDF 遵循打印区域和分页设置。"
        : ConversionEntries.Any(file => ConversionCapabilities.IsPresentation(file.FullPath))
            ? "每份演示单独输出。PDF 是静态页面，不保留动画、转场、交互和音视频播放。缺少字体会替换；复杂图表、版式和分页请核对。"
        : ConversionEntries.Any(file => ConversionCapabilities.IsVideo(file.FullPath))
            ? "音轨从 1 开始，计划检查数量并显示所选轨道；音频目标仅提取所选音轨。视频保留首个视频流和所选音轨，字幕/其他轨道不输出。MP4/MKV/MOV 为 H.264 + AAC；WebM 为 VP9 + Opus（48 kHz）。重新编码不保证文件一定变小。"
        : ConversionEntries.Any(file => ConversionCapabilities.IsAudio(file.FullPath)) ? "按序号选择音轨。WAV 为 16 位 PCM；OGG 使用固定质量 5。有损音源转 FLAC/WAV 不会恢复音质。重新编码不保证变小。"
        : Target == ConversionTarget.Txt ? "按 PDF 页序及所选编码提取文本，以换页符分隔；尽力恢复阅读顺序与换行，复杂多栏可能需调整。扫描/空白页明确标记，全部无有效文字时失败；不含 OCR。"
        : Target == ConversionTarget.Ico ? "生成包含 16、24、32、48、64、128、256 像素的 ICO；等比缩放，空白补透明。"
        : Target == ConversionTarget.Pdf ? "按列表顺序合并为一个 PDF；TIFF 默认逐页加入，其他多帧图片使用指定帧（默认第 1 帧）。"
        : "GIF ↔ WebP 保留全部帧、时长与循环，动态 WebP 使用无损编码；GIF 仅支持 256 色和二值透明。TIFF 默认逐页导出，静态单图使用指定帧/页。JPG/BMP 透明处铺白。尺寸为 0 时保留原尺寸；压缩不保证一定变小。";
    public string ScopeSummary => ConversionEntries.Count == 0 ? Input.SelectedFolderIds.Count > 0 ? "所选文件夹为空，请重新选择。" : "当前文件夹没有文件，请先添加要转换的文件。"
        : Input.SelectionCount > 0 ? Input.SelectedFolderIds.Count > 0 || Input.VisibleSelectedCount != Input.SelectionCount
            ? Input.SelectedScopeSummary : $"仅转换已选 {ConversionEntries.Count:N0} 项。"
        : Input.CurrentFolderId != Guid.Empty ? $"“{Input.CurrentFolderName}”及子文件夹 · 全部 {ConversionEntries.Count:N0} 项"
        : $"未选择文件，将转换输入列表全部 {ConversionEntries.Count:N0} 项。";
    private bool CanShowOutputs => Report?.Outputs.Count > 0;
    public bool HasOutputs => CanShowOutputs;
    public IReadOnlyList<SearchFile> OutputFiles =>
        Report?.Outputs.Select(output => new SearchFile(output.FullPath, output.IsDirectory, output.IsDirectory ? null : output.Size, output.Modified)).ToArray() ?? [];
    public string PlanSummary => showReportPreviews && Report is { } report ? report.Summary : Plan is null ? "转换后的名称将显示在文件下方。" : $"将生成 {Plan.ReadyCount} 个文件 · {Plan.RejectedCount} 项不可处理" + (Plan.SkippedCount > 0 ? $" · {Plan.SkippedCount} 项同名跳过" : "");
    public event EventHandler<int>? OutputsRequested;
    public event EventHandler? EngineManagementRequested;
    public event EventHandler? PlanInvalidated;
    private int lastTask;
    private IReadOnlyList<SearchFile> ConversionEntries => Input.EffectiveEntries;
    public string InputIdentity => $"{Input.CurrentFolderId:N}:{Input.CollectionRevision}:" + string.Join('\0', ConversionEntries.Select(file => file.FullPath));
    private void InputChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(WorkspaceViewModel.CollectionRevision))
        {
            var hasInputs = Input.SearchEntries.Count > 0;
            if (hadInputs && !hasInputs && savedDefaults is { } defaults)
            {
                customOutput = customQuality = customEncoding = customConflict = false;
                ApplyDefaults(null, defaults);
            }
            hadInputs = hasInputs;
        }
        if (args.PropertyName == nameof(WorkspaceViewModel.VisibleSelectedCount))
        {
            NotifyScope();
            return;
        }
        if (args.PropertyName == nameof(WorkspaceViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(CanBuildPlan)); OnPropertyChanged(nameof(CanRun));
            BuildPlanCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged();
            if (!Input.IsLoading && Plan is null) PlanInvalidated?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (args.PropertyName is not (nameof(WorkspaceViewModel.BatchSummary) or nameof(WorkspaceViewModel.EffectiveEntries) or nameof(WorkspaceViewModel.CollectionRevision))) return;
        RefreshInput();
    }
    private void RefreshInput(bool force = false)
    {
        NotifyScope();
        var identity = InputIdentity;
        if (!force && identity == inputIdentity) return;
        inputIdentity = identity;
        Invalidate();
        var entries = ConversionEntries;
        IReadOnlyList<ConversionTarget> targets = entries.Any(file => file.IsFolder) ? [] : ConversionCapabilities.GetCommonTargets(entries.Select(file => file.FullPath), (ConversionMode)ModeValue);
        var requiredEngine = entries.Any(file => ConversionCapabilities.IsOffice(file.FullPath)) ? "libreoffice"
            : entries.Any(file => ConversionCapabilities.IsAudio(file.FullPath) || ConversionCapabilities.IsVideo(file.FullPath)) ? "ffmpeg" : null;
        HasMissingEngine = targets.Count > 0 && requiredEngine is not null && !availableEngines.Contains(requiredEngine);
        if (HasMissingEngine) targets = [];
        Formats = targets.Select(target => new SelectionOption((int)target, target switch
        {
            ConversionTarget.Jpeg => "JPG / JPEG", ConversionTarget.WebP => "WebP", ConversionTarget.WebM => "WebM",
            ConversionTarget.Pdf => entries.All(file => ConversionCapabilities.IsImage(file.FullPath)) ? "PDF · 图片合并" : "PDF", _ => target.ToString().ToUpperInvariant()
        })).ToArray();
        if (!Formats.Any(option => option.Value == TargetFormatValue)) TargetFormatValue = Formats.FirstOrDefault()?.Value ?? -1;
        FormatFeedback = string.Empty;
        if (HasMissingEngine) FormatFeedback = requiredEngine == "libreoffice" ? "文档转换需要 LibreOffice，请前往引擎管理按需下载。文件将在本机转换。" : "音视频引擎缺失，请前往引擎管理下载或修复。";
        else if (entries.Any(file => file.IsFolder)) FormatFeedback = "当前不直接转换磁盘文件夹，请选择其中的文件；软件文件夹会自动展开已收集的文件引用。";
        else if (entries.Count > 0 && !HasFormats)
        {
            var unsupported = entries.Where(file => ConversionCapabilities.GetTargets(file.FullPath).Count == 0)
                .Select(file => file.Extension.Length == 0 ? "无扩展名文件" : file.Extension.ToUpperInvariant()).Distinct().ToArray();
            var kinds = entries.GroupBy(file => ConversionCapabilities.IsImage(file.FullPath) ? "图片" : ConversionCapabilities.IsDocument(file.FullPath) ? "文本文档"
                : ConversionCapabilities.IsSpreadsheet(file.FullPath) ? "表格" : ConversionCapabilities.IsPresentation(file.FullPath) ? "演示文稿"
                : ConversionCapabilities.IsAudio(file.FullPath) ? "音频" : ConversionCapabilities.IsVideo(file.FullPath) ? "视频" : file.Extension.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "其他文件").ToArray();
            var scope = string.Join("、", kinds.Select(group => $"{group.Count():N0} 项{group.Key}"));
            FormatFeedback = $"当前包含 {scope}，没有共同的输出格式，请按类型分开选择。";
            if (unsupported.Length > 0) FormatFeedback += $" 当前尚未接入：{string.Join("、", unsupported)}。";
            if (ShowProcessing) FormatFeedback += " 压缩/兼容模式只支持同一输出格式的图片或音视频；文档请使用格式转换。";
        }
        OnPropertyChanged(nameof(ShowPdfDpi));
        OnPropertyChanged(nameof(ConversionNote));
        OnPropertyChanged(nameof(ShowQuality));
        NotifyOptions();
        OnPropertyChanged(nameof(ProductExtension));
    }
    public void RefreshCapabilities()
    {
        var current = new HashSet<string>(StringComparer.Ordinal);
        if (service.IsEngineAvailable("ffmpeg")) current.Add("ffmpeg");
        if (service.IsEngineAvailable("libreoffice")) current.Add("libreoffice");
        var changed = !availableEngines.SetEquals(current);
        availableEngines = current;
        RefreshInput(force: changed);
    }
    [RelayCommand] private void OpenEngineManagement() => EngineManagementRequested?.Invoke(this, EventArgs.Empty);
    private void Invalidate()
    {
        optionsRevision++;
        if (IsExecuting)
        {
            PlanInvalidated?.Invoke(this, EventArgs.Empty);
            return;
        }
        showReportPreviews = false; runFailure = ""; Plan = null; RebuildFilePreviews();
        NotifyPanel();
        OnPropertyChanged(nameof(PlanSummary)); PlanInvalidated?.Invoke(this, EventArgs.Empty);
    }
    private void NotifyScope()
    {
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(ExecutionScope));
        OnPropertyChanged(nameof(FooterScope));
        OnPropertyChanged(nameof(ProductName));
        OnPropertyChanged(nameof(ProductExtension));
    }
    private void NotifyPanel()
    {
        foreach (var property in new[] { nameof(ShowSettings), nameof(ShowMoreSettings), nameof(ShowResultPanel),
            nameof(IsChecking), nameof(CanChangeInput), nameof(PanelTitle), nameof(ResultSummary), nameof(CanRun) })
            OnPropertyChanged(property);
        RunCommand.NotifyCanExecuteChanged();
        NotifyScope();
    }
    partial void OnIsBusyChanged(bool value) => NotifyPanel();
    partial void OnIsExecutingChanged(bool value) => NotifyPanel();
    partial void OnMoreSettingsOpenChanged(bool value) => NotifyPanel();
    [RelayCommand] private void OpenMoreSettings() => MoreSettingsOpen = true;
    [RelayCommand] private void CloseMoreSettings() => MoreSettingsOpen = false;
    [RelayCommand] private void ContinueEditing() { if (!IsBusy) Invalidate(); }
    partial void OnTargetFormatValueChanged(int value) { Invalidate(); NotifyOptions(); OnPropertyChanged(nameof(FormatButtonContent)); }
    partial void OnQualityChanged(double value) { if (!applyingDefaults) customQuality = true; Invalidate(); NotifyOptions(); OnPropertyChanged(nameof(FormatButtonContent)); }
    partial void OnPdfDpiChanged(double value) => Invalidate();
    partial void OnConflictPolicyValueChanged(int value) { if (!applyingDefaults) customConflict = true; Invalidate(); }
    partial void OnOutputDirectoryChanged(string value)
    {
        if (!applyingDefaults) customOutput = value.Length > 0 || defaultLocation == OutputLocation.Fixed;
        Invalidate();
        OnPropertyChanged(nameof(CanBuildPlan));
        BuildPlanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBuildPlan))]
    private async Task BuildPlanAsync()
    {
        if (!CanBuildPlan) { Feedback = HasFormatError ? FormatFeedback : "请先添加文件，并选择这批文件共同支持的输出格式。"; return; }
        var entries = ConversionEntries;
        var identity = InputIdentity;
        var revision = optionsRevision;
        var quality = ShowQuality ? Quality : 90;
        var pdfDpi = ShowPdfDpi ? PdfDpi : 150;
        if (!double.IsFinite(quality) || !double.IsFinite(pdfDpi)) { Feedback = "请填写有效的质量和 PDF 分辨率。"; return; }
        IsBusy = true; showReportPreviews = false; Plan = null; RebuildFilePreviews();
        OnPropertyChanged(nameof(PlanSummary)); Feedback = "正在检查文件和输出路径…";
        using var current = new CancellationTokenSource(); cancellation = current;
        try
        {
            var settings = ReadSettings();
            var result = await service.CreatePlanAsync(new(entries.Select(file => file.FullPath).ToArray(),
                OutputDirectory.Trim(), (ConversionTarget)TargetFormatValue, (int)quality, (int)pdfDpi, settings)
            {
                Location = EffectiveLocation, Conflict = (ConversionCollision)ConflictPolicyValue,
                SkipConflictingOutputs = ConflictPolicyValue == (int)ConversionCollision.Skip
            }, current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (identity != InputIdentity || revision != optionsRevision) { Feedback = "输入文件或参数已变化，正在更新计划。"; return; }
            Plan = result;
            Feedback = result.ReadyCount == 0 && result.SkippedCount == result.Items.Count && result.SkippedCount > 0 ? "所有输出均已按同名策略跳过。"
                : result.ReadyCount == 0 ? "没有可转换的文件，请查看计划中的原因并重新添加原文件。"
                : result.RejectedCount > 0 ? "部分文件不可处理，开始转换将只处理计划中的可用文件。"
                : result.SkippedCount > 0 ? $"计划已就绪，{result.SkippedCount:N0} 项同名输出将跳过。" : ReadyFeedback;
        }
        catch (OperationCanceledException) { Feedback = "已取消预览计划。"; }
        catch (FileProcessingException error) { Feedback = error.Message; }
        catch (Exception) { Feedback = "无法生成转换计划，请检查输入文件和输出路径。"; }
        finally { if (ReferenceEquals(cancellation,current)) cancellation = null; IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!CanRun || Plan is not { } plan) return;
        runningScope = ScopeSummary + $"\n预计生成 {plan.ReadyCount:N0} 个文件";
        IsExecuting = true; IsBusy = true; Report = null; runFailure = "";
        using var current = new CancellationTokenSource(); cancellation = current;
        var job = jobs.Start(current.Cancel); lastTask = job.Id; ActiveJob = job;
        Feedback = "正在转换，原文件保留…";
        try
        {
            var report = await service.ConvertAsync(plan, new Progress<ProcessingProgress>(job.Update), current.Token);
            Report = report; outputs.Add(job.Id, job.Name, report.Outputs); job.Complete(report); Feedback = report.Summary;
        }
        catch (OperationCanceledException) { job.Fail("已取消。"); runFailure = Feedback = "已取消转换。"; }
        catch (Exception error) { job.Fail(error.Message); runFailure = Feedback = "转换未完成：" + error.Message; }
        finally { if (ReferenceEquals(cancellation,current)) cancellation = null; Plan = null; IsExecuting = false; IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(IsBusy))] private void Cancel() => cancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanShowOutputs))] private void ShowOutputs() => OutputsRequested?.Invoke(this, lastTask);
}
