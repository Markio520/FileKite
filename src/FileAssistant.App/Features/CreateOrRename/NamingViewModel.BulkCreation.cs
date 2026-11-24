using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    private DirectoryDraft folderLocation = null!;
    private IReadOnlyList<DirectoryDraft> creationParents = [];
    private string creationNames = "", creationInputError = "";
    private bool changingCreationSelection, reconcilingNames;
    private readonly Dictionary<string, string> directoryInputs = [];
    public IReadOnlyList<DirectoryDraft> CreationTreeRoots => [folderLocation];
    public IReadOnlyList<DirectoryDraft> CreationParents => creationParents;
    public bool ShowCreationEditor => IsCreate && (!ShowResults || EditingResults);
    public bool ShowCreationLines => ShowCreationEditor && !CreateNumbered;
    public bool HasUnaddedDirectoryInput => IsTreeCreation && (CreateNumbered || Custom || !string.IsNullOrWhiteSpace(creationNames));
    public bool CanAddDirectoryBatch => CanEdit && IsTreeCreation && !string.IsNullOrWhiteSpace(Destination) && HasUnaddedDirectoryInput;
    public bool CanRemovePendingContent => CanEdit && SelectedCreationContent?.Draft is { IsExisting: false };
    public int PendingDirectoryCount => DirectoryEntries().Count(x => !x.Node.IsExisting && !x.Node.IsMissing);
    public int PendingCreationCount => DraftEntries().Count(x => !x.Node.IsExisting && !x.Node.IsMissing);
    public bool HasCreationConflicts => IsCreate && Plan is { } plan && plan.Items.Any(x => x.Action is NamingAction.Invalid or NamingAction.Skipped or NamingAction.Numbered);
    public string CreationHeading => creationParents.Count > 1 ? $"创建到：选中的 {creationParents.Count} 个文件夹"
        : creationParents.FirstOrDefault() is { IsDestination: false } parent ? $"创建到：{parent.Name}"
        : string.IsNullOrWhiteSpace(Destination) ? "尚未选择创建位置" : $"创建到：{CreationRootName}";
    public string CreationTargetPaths => string.IsNullOrWhiteSpace(Destination) ? "点击右上角“添加文件夹”"
        : string.Join("\n", creationParents.Select(parent => CreationContext(parent)?.Path ?? Destination));
    private string CreationRootName
    {
        get
        {
            var path = Path.TrimEndingDirectorySeparator(Destination.Trim());
            var name = Path.GetFileName(path);
            return name.Length > 0 ? name : path;
        }
    }
    public string CreationNamesLabel => IsDirectoryCreation ? "文件夹名称或路径（每行一个）" : "文件名称或路径（每行一个）";
    public string CreationInputHint => IsDirectoryCreation ? "多层目录可写 1/1/2，换行再写 1/1/3；相同父目录自动合并。" : "没有扩展名时自动补全；可写 素材/说明.txt，自动补齐上级目录。";
    public string NumberedModeLabel => CreateNumbered || Custom ? "按行填写名称" : "按编号生成";
    public string CreationEstimate => $"待创建：{PendingDirectoryCount:N0} 个文件夹 · {PendingCreationCount - PendingDirectoryCount:N0} 个文件";
    public string CreationNames
    {
        get => creationNames;
        set
        {
            if (!CanEdit) return;
            if (creationNames == value) return;
            creationNames = value; creationInputError = "";
            directoryInputs[CreationInputKey] = value;
            NotifyCreationEditor(); OnPropertyChanged(nameof(NextStep));
            RequestReadyCreationPreview();
        }
    }
    private string CreationInputKey => string.Join("|", creationParents.Select(x => x.Id).Order());
    private void InitializeCreationDraft()
    {
        folderLocation = new("保存位置", Directories) { IsDestination = true, IsExisting = true };
        creationParents = [folderLocation]; folderLocation.IsTarget = true;
        browsedCreationDirectory = folderLocation; folderLocation.IsCurrent = true;
    }
    public void SelectCreationParents(IEnumerable<DirectoryDraft> selection, bool browse = true)
    {
        if (changingCreationSelection || !CanEdit) return;
        var root = folderLocation;
        var valid = DirectoryEntries().Select(x => x.Node).ToHashSet();
        var parents = selection.Where(x => ReferenceEquals(x, root) || valid.Contains(x)).Distinct().ToList();
        if (parents.Count > 1) parents.Remove(root);
        if (parents.Count == 0) parents.Add(CurrentCreationDirectory.IsDestination || valid.Contains(CurrentCreationDirectory) ? CurrentCreationDirectory : root);
        if (creationParents.Count == parents.Count && creationParents.ToHashSet().SetEquals(parents))
        { foreach (var parent in parents) parent.IsTarget = true; if (browse) SetCreationBrowser(parents[0]); return; }
        changingCreationSelection = true;
        try
        {
            directoryInputs[CreationInputKey] = creationNames;
            foreach (var old in creationParents) old.IsTarget = false;
            creationParents = parents;
            foreach (var parent in parents) parent.IsTarget = true;
            SelectedDirectory = parents.FirstOrDefault(x => !x.IsDestination);
            LoadCreationInput();
        }
        finally { changingCreationSelection = false; }
        if (browse) SetCreationBrowser(parents[0]);
        NotifyCreationEditor(); OnPropertyChanged(nameof(NextStep));
        RequestReadyCreationPreview();
    }
    private void RequestReadyCreationPreview()
    {
        if (IsTreeCreation && Plan is null && CanBuild) PlanInvalidated?.Invoke(this, EventArgs.Empty);
    }
    private void LoadCreationInput()
    {
        creationInputError = "";
        creationNames = directoryInputs.GetValueOrDefault(CreationInputKey) ?? "";
    }
    private DirectoryDraft MakeDraft(string name, bool isDirectory = true, string template = "")
    {
        var node = new DirectoryDraft(name) { IsDirectory = isDirectory, TemplatePath = template };
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DirectoryDraft.Name) && !reconcilingNames) Invalidate();
        };
        return node;
    }
    private IReadOnlyList<string[]> DirectoryBatchPaths(DirectoryDraft parent)
    {
        var lines = creationNames.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        IEnumerable<string> names = lines;
        if (CreateNumbered || Custom)
        {
            var count = Custom && lines.Length > 0 ? lines.Length : Number(Count, "创建数量");
            if (count is < 1 or > 100000) throw new ArgumentException("每批创建数量为 1–100000。");
            var rules = SimpleRules();
            names = Enumerable.Range(0, count).Select(index => NamingRulesEngine.Generate(Custom && lines.Length > 0 ? lines[index] : "新建", parent.Name, DateTimeOffset.Now, index, rules));
        }
        var paths = new List<string[]>();
        foreach (var name in names)
        {
            var parts = name.Replace('/', '\\').Split('\\').Select(x => x.Trim()).ToArray();
            if (parts.Length > 128) throw new ArgumentException("目录层级不能超过 128 层。");
            if (!IsDirectoryCreation && Path.GetExtension(parts[^1]).Length == 0) parts[^1] += NormalizedExtension();
            foreach (var part in parts) NamingRulesEngine.ValidateName(part);
            paths.Add(parts);
            if (paths.Count > 100000) throw new ArgumentException("每批最多添加 100000 行。");
        }
        if (paths.Count == 0) throw new ArgumentException("请填写名称，每行一个。");
        return paths;
    }
    [RelayCommand(CanExecute = nameof(CanAddDirectoryBatch))]
    private void AddDirectoryBatch()
    {
        try
        {
            // Validate the entire batch before changing the shared draft.
            var batches = creationParents.Select(parent => (Parent: parent, Paths: DirectoryBatchPaths(parent))).ToArray();
            var additions = new List<(DirectoryDraft Parent, DirectoryDraft Node)>();
            var pendingCount = PendingCreationCount;
            var indexes = new Dictionary<DirectoryDraft, Dictionary<string, DirectoryDraft>>();
            foreach (var batch in batches)
                foreach (var parts in batch.Paths)
                {
                    var parent = batch.Parent;
                    for (var index = 0; index < parts.Length; index++)
                    {
                        var part = parts[index];
                        var directory = index < parts.Length - 1 || IsDirectoryCreation;
                        var template = directory ? "" : CreationTemplateFor(part);
                        if (!indexes.TryGetValue(parent, out var children)) indexes[parent] = children = parent.Children.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
                        if (!children.TryGetValue(part, out var node))
                        {
                            node = MakeDraft(part, directory, template); children.Add(part, node); additions.Add((parent, node));
                            if (additions.Count + pendingCount > 100000) throw new ArgumentException("最多安排 100000 个待创建项目。");
                        }
                        else if (node.IsDirectory != directory || !directory && (!string.Equals(node.TemplatePath, template, StringComparison.OrdinalIgnoreCase) || node.IsExisting))
                            throw new ArgumentException("此名称已被其他项目使用：" + part + "。请修改本批名称。");
                        parent = node;
                    }
                }
            foreach (var (parent, node) in additions) parent.Children.Add(node);
            creationNames = ""; directoryInputs[CreationInputKey] = ""; creationInputError = "";
            CreateMethod = 3; EditingResults = true;
            Invalidate(); Feedback = additions.Count == 0 ? "这些项目已在待创建清单中。" : $"已添加 {additions.Count} 项到目录树，尚未写入磁盘。";
            var firstVisible = additions.FirstOrDefault(x => ReferenceEquals(x.Parent, CurrentCreationDirectory)).Node;
            if (firstVisible is not null) SelectedCreationContent = CreationContents.FirstOrDefault(x => ReferenceEquals(x.Draft, firstVisible));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { creationInputError = error.Message; Feedback = error.Message; }
        NotifyCreationEditor();
    }
    [RelayCommand(CanExecute = nameof(CanRemovePendingContent))]
    private void RemovePendingContent()
    {
        if (SelectedCreationContent?.Draft is { } node) RemoveCreationDraft(node);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ToggleCreationNumbering() => CreateMethod = CreateNumbered || Custom ? 3 : 0;
    [RelayCommand(CanExecute = nameof(CanEdit))] private void UseAdvancedCreation() { CreateMethod = 2; PanelValue = 0; }
    private void ResetCreationContext()
    {
        if (folderLocation is null) return;
        IsChoosingCreationTargets = false;
        directoryInputs[CreationInputKey] = creationNames;
        changingCreationSelection = true;
        try
        {
            foreach (var old in creationParents) old.IsTarget = false;
            creationParents = [folderLocation]; creationParents[0].IsTarget = true;
            SelectedDirectory = null; LoadCreationInput();
        }
        finally { changingCreationSelection = false; }
        SetCreationBrowser(creationParents[0]);
        OnPropertyChanged(nameof(CreationTreeRoots)); NotifyCreationEditor();
    }
    partial void OnDestinationChanged(string value)
    {
        if (folderLocation is null) return;
        // Keep only the pending structure when changing the disk location.
        bool Retain(DirectoryDraft node)
        {
            foreach (var child in node.Children.ToArray()) if (!Retain(child)) node.Children.Remove(child);
            if ((node.IsExisting || node.IsMissing) && node.Children.Count == 0) return false;
            node.IsExisting = false; node.IsMissing = false; node.HasLoadedContents = false; return true;
        }
        foreach (var node in Directories.ToArray()) if (!Retain(node)) Directories.Remove(node);
        folderLocation.IsMissing = false; folderLocation.HasLoadedContents = false;
        directoryInputs.Clear(); creationNames = ""; ResetCreationContext();
    }
    private void NotifyCreationEditor()
    {
        if (folderLocation is null) return;
        folderLocation.Name = string.IsNullOrWhiteSpace(Destination) ? "保存位置" : Destination;
        folderLocation.Preview = folderLocation.IsMissing ? "保存位置已不存在" : "保存位置";
        foreach (var name in new[] { nameof(CreationNames), nameof(CreationHeading), nameof(CreationTargetPaths), nameof(CreationNamesLabel), nameof(CreationInputHint), nameof(NumberedModeLabel),
            nameof(ShowCreationEditor), nameof(ShowCreationLines), nameof(HasCreationConflicts), nameof(CreationEstimate), nameof(CanAddDirectoryBatch), nameof(CanRemovePendingContent), nameof(HasUnaddedDirectoryInput), nameof(CanBuild), nameof(CanRun) }) OnPropertyChanged(name);
        ToggleCreationNumberingCommand.NotifyCanExecuteChanged(); UseAdvancedCreationCommand.NotifyCanExecuteChanged();
        AddDirectoryBatchCommand.NotifyCanExecuteChanged(); RemovePendingContentCommand.NotifyCanExecuteChanged(); BuildPlanCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged();
        NotifyCreationFiles(); NotifyCreationTargets();
    }
}
