using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.Outputs;

public partial class OutputsViewModel(IFileInteraction interaction) : ObservableObject
{
    private readonly List<OutputEntry> entries = [];
    private IReadOnlyList<OutputEntry> selection = [];
    public ObservableCollection<OutputEntry> VisibleFiles { get; } = [];
    public ObservableCollection<SelectionOption> Tasks { get; } = [new(0, "全部任务")];
    public WorkspaceViewModel Inspector { get; } = new();
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial int TaskId { get; set; }
    [ObservableProperty] public partial string Feedback { get; set; } = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] public partial OutputEntry? Selected { get; set; }
    public bool HasSelection => Selected is not null;
    public bool HasFiles => entries.Count > 0;
    public int TotalCount => entries.Count;
    public bool IsEmpty => VisibleFiles.Count == 0;
    public string Summary => $"显示 {VisibleFiles.Count} / {entries.Count} 项输出";
    public string SelectionSummary => selection.Count == 0 ? "选择输出文件以查看位置，或将结果继续流转到下一道工序" : $"已选 {selection.Count} 项 · 可直接打开或流转至改名/转换/复制";
    public string IslandSummary => $"已选 {selection.Count:N0} 项产物";
    public IReadOnlyList<SearchFile> SelectedFiles => selection.Select(entry => entry.File).ToArray();
    public IReadOnlyList<SearchFile> VisibleOutputFiles => VisibleFiles.Select(entry => entry.File).ToArray();

    public void RefreshDisplay()
    {
        foreach (var row in VisibleFiles) row.RefreshDisplay();
        Inspector.RefreshDisplay();
    }

    public void ApplyPathChanges(IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.PathChange> changes, IReadOnlyList<string>? removed = null)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i]; var result = entry.Result;
            entries[i] = new(entry.TaskId, entry.TaskName, result with
            {
                FullPath = Core.Features.CreateOrRename.NamingRulesEngine.MapPath(result.FullPath, changes),
                SourcePaths = result.SourcePaths.Select(x => Core.Features.CreateOrRename.NamingRulesEngine.MapPath(x, changes)).ToArray()
            });
        }
        if (removed is { Count: > 0 })
        {
            var deleted = removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries.RemoveAll(x => Core.Features.FileCollections.FileCollection.IsRemoved(x.FullPath, deleted));
        }
        Refresh();
    }

    public void Add(int taskId, string name, IReadOnlyList<ProducedFile> outputs)
    {
        if (outputs.Count == 0) return;
        if (!Tasks.Any(option => option.Value == taskId)) Tasks.Add(new(taskId, name));
        entries.AddRange(outputs.Select(output => new OutputEntry(taskId, name, output)));
        Refresh();
    }
    public void SetSelection(IReadOnlyList<OutputEntry> items)
    {
        selection = items; Selected = items.LastOrDefault();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(IslandSummary));
        Inspector.SelectedFile = Selected is null ? null : WorkspaceFile.FromSource(Selected.File);
    }
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnTaskIdChanged(int value) => Refresh();
    private void Refresh()
    {
        SetSelection([]); VisibleFiles.Clear();
        foreach (var entry in entries.Where(entry => (TaskId == 0 || entry.TaskId == TaskId) &&
            (SearchText.Length == 0 || entry.FullPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)))) VisibleFiles.Add(entry);
        OnPropertyChanged(nameof(HasFiles)); OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(VisibleOutputFiles));
    }
    [RelayCommand] private void Open() => Interact(() => { if (Selected is not null) interaction.Open(Selected.File); });
    [RelayCommand] private void Reveal() => Interact(() => { if (Selected is not null) interaction.Reveal(Selected.File); });
    [RelayCommand] private void CopyPath() => Interact(() => { if (selection.Count > 0) interaction.CopyText(string.Join(Environment.NewLine, selection.Select(item => item.FullPath))); });
    private void Interact(Action action) { try { action(); } catch (FileInteractionException error) { Feedback = error.Message; } }
    [RelayCommand] private void ClearRecords()
    { entries.Clear(); TaskId = 0; Tasks.Clear(); Tasks.Add(new(0, "全部任务")); Refresh(); Feedback = "已清空输出记录，磁盘文件保留。"; }
}
