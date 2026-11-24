using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    // Panel navigation never changes a pending plan or runs a file operation.
    [ObservableProperty] public partial int PanelValue { get; set; }
    [ObservableProperty] public partial int MoreSection { get; set; }
    [ObservableProperty] public partial bool EditingResults { get; set; }
    [ObservableProperty] public partial int NumberPosition { get; set; } = 1;
    [ObservableProperty] public partial DirectoryDraft? SelectedDirectory { get; set; }
    public ObservableCollection<DirectoryDraft> Directories { get; } = [];
    public IReadOnlyList<SelectionOption> NumberPositions { get; } = [new(0, "名称前面"), new(1, "名称后面")];
    public IReadOnlyList<SelectionOption> MoreSections => new[]
    {
        ShowNumbering ? new SelectionOption(2, "编号") : null,
        new SelectionOption(0, "同名处理"),
        HasFolderRenameTargets ? new SelectionOption(5, "文件夹范围") : null,
        CanUseAdvancedCreation ? new SelectionOption(1, "高级格式") : null,
        Custom ? new SelectionOption(4, "组合处理") : null
    }.OfType<SelectionOption>().ToArray();
    public bool HasFolderRenameTargets => IsRename && !Manual && Input.EffectiveEntries.Any(x => x.IsFolder);
    public bool CanUseAdvancedCreation => IsCreate && !Custom;
    public bool HasMoreSectionChoice => MoreSections.Count > 1;
    public bool HasAdditionalMoreSections => HasFolderRenameTargets || CanUseAdvancedCreation || Custom;
    public bool NumberOnly => IsRename && RenameAction == 6;
    public bool IsTreeCreation => IsCreate;
    public bool ShowMainSettings => !HasProgress && PanelValue == 0 && (!ShowResults || EditingResults);
    public bool ShowResultPanel => !HasProgress && PanelValue == 0 && ShowResults && !EditingResults;
    public bool ShowMoreSettings => !HasProgress && PanelValue == 1;
    public bool ShowHistory => !HasProgress && PanelValue == 2;
    public bool ShowMoreConflicts => MoreSection == 0;
    public bool ShowMoreScope => MoreSection == 5 && HasFolderRenameTargets;
    public bool ShowMoreCreation => MoreSection == 1 && CanUseAdvancedCreation;
    public bool ShowMoreNumbering => MoreSection == 2 && ShowNumbering;
    public bool ShowMoreCombination => MoreSection == 4 && Custom;
    public bool NoRecoveries => Recoveries.Count == 0;
    public bool ShowPanelBack => !HasProgress && PanelValue != 0;
    public bool HasSelectedDirectory => SelectedDirectory is not null && CanEdit;
    public bool ShowTreeEditor => IsTreeCreation && (!ShowResults || EditingResults);
    public bool ShowActionFooter => ShowMainSettings || ShowResultPanel || IsBusy;
    public bool ShowSharedSubdirectories => IsDirectoryCreation && !IsTreeCreation;
    public bool NoDirectories => Directories.Count == 0;
    public string AddDirectoryLabel => SelectedDirectory is null ? "添加文件夹" : "添加同级";
    private string DirectoryValidationMessage
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (node, path) in DirectoryEntries())
            {
                try { NamingRulesEngine.ValidateName(node.Name); }
                catch (ArgumentException error) { return $"文件夹“{node.Name}”：{error.Message}"; }
                if (!seen.Add(path)) return "同一层级存在重名文件夹：" + path;
            }
            return "";
        }
    }
    public string PanelTitle => HasProgress ? (IsRename ? "正在改名" : "正在创建") : ShowMoreSettings ? "更多设置"
        : ShowHistory ? "操作记录" : ShowResultPanel ? "本次处理结果" : OptionsTitle;
    public string ExecutionScope
    {
        get
        {
            if (IsCreate)
            {
                if (Plan is { } creationPlan) return $"待创建 {creationPlan.ReadyCount:N0} 项 · {CreationLocation}";
                return CreationLocation;
            }
            if (Manual)
                return Plan is { } manualPlan ? $"待改名 {manualPlan.ReadyCount:N0} 项 · 已填写新名称的文件" : "处理已填写新名称的文件";
            var scope = Input.SelectionCount > 0 ? Input.SelectedScopeSummary : $"当前文件夹及子文件夹 · {Input.EffectiveEntries.Count:N0} 项输入";
            return Plan is { } plan ? scope + $"\n待改名 {plan.ReadyCount:N0} 项" : scope;
        }
    }
    // The whole draft is executed, even when the browser is showing a child directory.
    public string ExecutionScopeText => !IsCreate ? ExecutionScope : string.IsNullOrWhiteSpace(Destination) ? CreationLocation
        : (Plan is { } plan ? $"待创建 {plan.ReadyCount:N0} 项 · " : "创建范围：") + CreationRootName;
    public string SettingsSummary => string.Join(" · ", new[]
    {
        ShowNumbering && Step != "1" ? $"每次递增 {Step}" : "",
        IsRename && ShowNumbering && OrderValue != 0 ? Orders.FirstOrDefault(x => x.Value == OrderValue)?.Label ?? "" : "",
        IsRename && ShowNumbering && PerDirectory ? "每个目录重新编号" : "",
        HasFolderRenameTargets && FolderContents ? IncludeSubfolders ? "包含所有子目录内容" : "包含文件夹内的内容" : "",
        ConflictValue != 0 ? Conflicts.FirstOrDefault(x => x.Value == ConflictValue)?.Label ?? "" : ""
    }.Where(x => x.Length > 0));
    public string CompactSettingsSummary => HasSettingsSummary ? SettingsSummary
        : IsRename && ShowNumbering ? "按输入顺序 · 同名停止" : "同名停止";
    public string NumberingTuningSummary
    {
        get
        {
            var parts = new List<string> { $"步长 {(string.IsNullOrWhiteSpace(Step) ? "1" : Step)}" };
            if (IsRename)
            {
                parts.Add(Orders.FirstOrDefault(x => x.Value == OrderValue)?.Label ?? "按输入顺序");
                if (PerDirectory) parts.Add("按目录重编");
            }
            return string.Join(" · ", parts);
        }
    }
    public string FooterCount => IsCreate ? $"{Plan?.ReadyCount ?? PendingCreationCount:N0} 项待创建"
        : $"{Plan?.ReadyCount ?? (Manual ? CustomNameCount : Input.EffectiveEntries.Count):N0} 项待改名";
    public string HistorySummary => Recoveries.Count > 0 ? $"{Recoveries.Count:N0} 条可恢复" : "撤销与恢复";
    public bool HasActionMessage => IsImporting || IsSelectingTargets || IsScanning || !string.IsNullOrWhiteSpace(Feedback) || !string.IsNullOrWhiteSpace(MissingInput);
    public bool ShowDefaultSettingsSummary => !HasSettingsSummary;
    public bool HasSettingsSummary => SettingsSummary.Length > 0;
    public string CreationLocation => string.IsNullOrWhiteSpace(Destination) ? "先点击右上角“添加文件夹”" : Destination;
    public string TreeSummary => $"目录结构 · {DirectoryEntries().Count():N0} 个文件夹";
    public string SelectedDirectoryPath => SelectedDirectory is null ? "选择文件夹后，可修改名称或添加子文件夹。"
        : DirectoryEntries().FirstOrDefault(x => ReferenceEquals(x.Node, SelectedDirectory)).Path ?? "";
    [RelayCommand] private void OpenMore() { MoreSection = MoreSections[0].Value; PanelValue = 1; }
    [RelayCommand] private async Task OpenHistoryAsync() { PanelValue = 2; await RefreshRecoveriesAsync(); }
    [RelayCommand] private void BackToSettings() => PanelValue = 0;
    [RelayCommand] private void ContinueEditing() { EditingResults = true; PanelValue = 0; }
    partial void OnPanelValueChanged(int value) => NotifyInteraction();
    partial void OnMoreSectionChanged(int value) => NotifyInteraction();
    partial void OnEditingResultsChanged(bool value) { UpdateNamePreviews(); NotifyInteraction(); }
    partial void OnSelectedDirectoryChanged(DirectoryDraft? value)
    {
        OnPropertyChanged(nameof(HasSelectedDirectory)); OnPropertyChanged(nameof(SelectedDirectoryPath));
        OnPropertyChanged(nameof(AddDirectoryLabel));
        if (!changingCreationSelection) SelectCreationParents(value is null ? [] : [value]);
        SelectDirectoryPreview();
        AddChildDirectoryCommand.NotifyCanExecuteChanged(); RemoveDirectoryCommand.NotifyCanExecuteChanged();
    }
    partial void OnModeValueChanged(int value) { PanelValue = 0; EditingResults = false; ResetCreationContext(); }
    partial void OnKindValueChanged(int value) { creationInputError = ""; NotifyCreationEditor(); }
    partial void OnCreateMethodChanged(int value) { creationInputError = ""; NotifyCreationEditor(); RequestReadyCreationPreview(); }
    partial void OnNamesChanged(string value) => OnPropertyChanged(nameof(CreationNames));

    private IEnumerable<(DirectoryDraft Node, string Path)> DirectoryEntries() => DraftEntries().Where(x => x.Node.IsDirectory);
    private IEnumerable<(DirectoryDraft Node, string Path)> DraftEntries()
    {
        IEnumerable<(DirectoryDraft, string)> Walk(IEnumerable<DirectoryDraft> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                var path = parent.Length == 0 ? node.Name : parent + "\\" + node.Name;
                yield return (node, path);
                foreach (var child in Walk(node.Children, path)) yield return child;
            }
        }
        return Walk(Directories, "");
    }
    private ObservableCollection<DirectoryDraft>? Siblings(DirectoryDraft selected)
    {
        if (Directories.Contains(selected)) return Directories;
        return DirectoryEntries().Select(x => x.Node.Children).FirstOrDefault(x => x.Contains(selected));
    }
    private DirectoryDraft NewDirectory(ObservableCollection<DirectoryDraft> siblings)
    {
        var name = "新建文件夹"; var index = 2;
        while (siblings.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"新建文件夹 {index++}";
        var node = MakeDraft(name);
        siblings.Add(node); SelectedDirectory = node; Invalidate();
        return node;
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddDirectory() => NewDirectory(SelectedDirectory is { } selected ? Siblings(selected) ?? Directories : Directories);
    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private void AddChildDirectory() { if (SelectedDirectory is { } selected) NewDirectory(selected.Children); }
    [RelayCommand(CanExecute = nameof(HasSelectedDirectory))]
    private void RemoveDirectory()
    {
        if (SelectedDirectory is not { IsExisting: false } selected || Siblings(selected) is not { } siblings) return;
        var parent = DirectoryEntries().FirstOrDefault(x => ReferenceEquals(x.Node.Children, siblings)).Node;
        siblings.Remove(selected); SelectedDirectory = siblings.FirstOrDefault() ?? parent; Invalidate();
    }
    private void UpdateNamePreviews()
    {
        var rows = Rows.Where(x => x.SourcePath is not null).GroupBy(x => x.SourcePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        if (Report is { } report && Plan is null)
            foreach (var result in report.Items.Where(x => x.ActualPath is not null && x.Item.Source is not null))
                if (rows.TryGetValue(result.Item.Source!.Path, out var row)) rows[result.ActualPath!] = row;
        foreach (var file in Input.VisibleFiles)
        {
            UpdateCustomEditor(file);
            rows.TryGetValue(file.FullPath, out var row);
            var isError = IsRename && (row?.NeedsAttention == true || file.ReferenceError is not null);
            var previewText = isError
                ? (file.ReferenceError is not null ? "无法改名（原文件已失效）" : "无法改名（需调整）")
                : IsRename && row is not null ? "→ " + row.NewName : Manual && file.Source is not null ? "→ " + file.Name : "";
            var status = isError
                ? (row?.Status ?? file.ReferenceError ?? "原文件不可访问")
                : row?.Status ?? (Manual ? "未修改" : "");
            file.NamePreview.ApplyDiffPreview(file.Name, previewText, status, row?.Details ?? file.ReferenceError ?? "", isError);
        }
    }
    private void SelectDirectoryPreview()
    {
        if (!IsTreeCreation || SelectedDirectory is null) return;
        var entries = DirectoryEntries().OrderBy(x => x.Path.Count(c => c == '\\')).ToArray();
        var index = Array.FindIndex(entries, x => ReferenceEquals(x.Node, SelectedDirectory));
        if (index >= 0 && index < Rows.Count) SelectedRow = Rows[index];
    }
    private void NotifyInteraction()
    {
        if (!MoreSections.Any(x => x.Value == MoreSection)) { MoreSection = MoreSections[0].Value; return; }
        foreach (var name in new[] { nameof(NumberOnly), nameof(IsTreeCreation), nameof(ShowMainSettings), nameof(ShowResultPanel), nameof(ShowMoreSettings),
            nameof(ShowHistory), nameof(ShowPanelBack), nameof(PanelTitle), nameof(SettingsSummary), nameof(HasSettingsSummary), nameof(HasSelectedDirectory), nameof(ShowTreeEditor),
            nameof(ShowSharedSubdirectories), nameof(CreationLocation), nameof(TreeSummary), nameof(SelectedDirectoryPath), nameof(CreationMethods),
            nameof(MoreSections), nameof(HasFolderRenameTargets), nameof(CanUseAdvancedCreation), nameof(HasMoreSectionChoice), nameof(HasAdditionalMoreSections),
            nameof(ShowMoreConflicts), nameof(ShowMoreScope), nameof(ShowMoreCreation), nameof(ShowMoreNumbering), nameof(ShowMoreCombination), nameof(NoRecoveries), nameof(HistorySummary), nameof(NoDirectories), nameof(AddDirectoryLabel), nameof(ShowActionFooter), nameof(ExecutionScope), nameof(ExecutionScopeText), nameof(CompactSettingsSummary), nameof(NumberingTuningSummary), nameof(ShowDefaultSettingsSummary), nameof(FooterCount), nameof(HasActionMessage) }) OnPropertyChanged(name);
        NotifyCreationEditor();
        AddDirectoryCommand.NotifyCanExecuteChanged(); AddChildDirectoryCommand.NotifyCanExecuteChanged(); RemoveDirectoryCommand.NotifyCanExecuteChanged();
    }
}
