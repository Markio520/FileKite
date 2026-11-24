using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed record CreationBreadcrumb(DirectoryDraft Directory, string Name)
{
    public override string ToString() => Name;
}

public sealed record CreationContentRow(string Name, string Path, bool IsDirectory, string Status, string Details,
    bool IsExisting = false, DirectoryDraft? Draft = null)
{
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string Kind => IsDirectory ? "文件夹" : System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() + " 文件";
}

public partial class NamingViewModel
{
    private readonly IDirectoryContentReader? directoryReader;
    private DirectoryDraft? browsedCreationDirectory;
    private Dictionary<DirectoryDraft, NamingPlanItem> directoryItems = [];
    private readonly Dictionary<Guid, NamingItemResult> creationOutcomes = [];
    private IReadOnlyList<SearchFile> diskContents = [];
    private string browserPath = "";
    private CancellationTokenSource? contentsCancellation;
    private int contentsRevision;
    [ObservableProperty] public partial IReadOnlyList<CreationContentRow> CreationContents { get; set; } = [];
    [ObservableProperty] public partial CreationContentRow? SelectedCreationContent { get; set; }
    [ObservableProperty] public partial bool IsReadingCreationContents { get; set; }
    [ObservableProperty] public partial string CreationContentsNotice { get; set; } = "";
    public DirectoryDraft CurrentCreationDirectory => browsedCreationDirectory ?? folderLocation;
    public string CurrentCreationPath => browserPath;
    public IReadOnlyList<CreationBreadcrumb> CreationBreadcrumbs { get; private set; } = [];
    public bool HasCreationContents => CreationContents.Count > 0;
    public bool EmptyCreationContents => !HasCreationContents && !IsReadingCreationContents;
    public bool HasCreationContentsNotice => CreationContentsNotice.Length > 0;
    public string CreationEmptyMessage => browserPath.Length == 0 ? "点击右上角“添加文件夹”选择创建位置" : HasCreationContentsNotice ? "暂时无法显示已有内容" : "此文件夹内暂无内容，可在右侧添加名称或路径。";
    public string CreationContentsSummary => $"当前目录 {CreationContents.Count:N0} 项 · 已有 {CreationContents.Count(x => x.IsExisting):N0} 项";
    public string CreationContentDetails => SelectedCreationContent?.Details ?? CurrentCreationPath;
    public bool CanBrowseCreationParent => !CurrentCreationDirectory.IsDestination && CanEdit;
    public bool HasMissingCreationParents => IsCreate && (folderLocation.IsMissing || DraftEntries().Any(x => x.Node.IsMissing));
    private void InvalidateCreationPlan()
    {
        revision++; if (IsBusy && !IsExecuting) cancellation?.Cancel();
        Plan = null; RefreshRows(); Notify(); PlanInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void BrowseCreationDirectory(DirectoryDraft directory)
    {
        if (!CanEdit || !directory.IsDirectory) return;
        if (ShowResults) { EditingResults = true; PanelValue = 0; }
        if (IsChoosingCreationTargets || creationParents.Count > 1) SetCreationBrowser(directory);
        else SelectCreationParents([directory]);
    }
    private void SetCreationBrowser(DirectoryDraft directory)
    {
        if (browsedCreationDirectory is { } old) old.IsCurrent = false;
        browsedCreationDirectory = directory; directory.IsCurrent = true;
        RefreshCreationContents();
    }
    [RelayCommand(CanExecute = nameof(CanBrowseCreationParent))]
    private void BrowseCreationParent()
    {
        if (CreationBreadcrumbs.Count > 1)
            BrowseCreationDirectory(CreationBreadcrumbs[^2].Directory);
    }
    private static bool SamePath(string first, string second) => string.Equals(first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string FullCreationPath(string destination, string relative = "")
    {
        try
        {
            if (!Path.IsPathFullyQualified(destination) || destination.StartsWith(@"\\.\", StringComparison.Ordinal)) return "";
            if (relative.Length > 0) foreach (var part in relative.Split('\\')) NamingRulesEngine.ValidateName(part);
            return Path.GetFullPath(Path.Combine(destination.Trim(), relative));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return ""; }
    }
    private Dictionary<DirectoryDraft, NamingPlanItem> MapDirectoryItems(IReadOnlyList<(DirectoryDraft Node, string Path)> entries)
    {
        if (!IsTreeCreation || Plan is null && Report is null) return [];
        var nodes = entries.ToDictionary(x => x.Path, x => x.Node, StringComparer.OrdinalIgnoreCase);
        var valid = entries.Select(x => x.Node).ToHashSet();
        var mapped = directoryItems.Where(x => valid.Contains(x.Key)).ToDictionary();
        foreach (var item in (Report?.Plan.Items ?? []).Concat(Plan?.Items ?? []))
        {
            if (nodes.TryGetValue(item.RequestedRelativePath ?? item.RelativeTarget ?? "", out var node) && !node.IsMissing) mapped[node] = item;
        }
        return mapped.Where(x => !x.Key.IsMissing).ToDictionary();
    }
    private CreationContentRow PlannedContent(NamingPlanItem item, DirectoryDraft? draft, IReadOnlySet<Guid> plannedIds)
    {
        if (item.Action == NamingAction.Existing) return new(Path.GetFileName(item.DisplayPath), item.DisplayPath, true, "已有", item.Details, true, draft);
        var result = creationOutcomes.GetValueOrDefault(item.Id);
        if (result is not null && !plannedIds.Contains(item.Id))
        {
            var succeeded = result.Outcome == NamingOutcome.Success;
            var path = succeeded ? result.ActualPath ?? item.DisplayPath : item.DisplayPath;
            return new(Path.GetFileName(path), path, item.IsDirectory,
                succeeded ? "已创建" : result.Outcome == NamingOutcome.Skipped ? "已跳过" : result.Outcome == NamingOutcome.Cancelled ? "已取消" : "未创建",
                path + "\n" + result.Message, succeeded, draft);
        }
        return new(Path.GetFileName(item.DisplayPath), item.DisplayPath, item.IsDirectory, item.Status, item.Details, Draft: draft);
    }
    private void RefreshCreationContents()
    {
        if (folderLocation is null) return;
        var entries = DraftEntries().ToArray();
        var current = CurrentCreationDirectory;
        var currentIndex = current.IsDestination ? -1 : Array.FindIndex(entries, entry => ReferenceEquals(entry.Node, current));
        if (browsedCreationDirectory is null || !current.IsDestination && currentIndex < 0)
        { if (browsedCreationDirectory is { } old) old.IsCurrent = false; browsedCreationDirectory = folderLocation; folderLocation.IsCurrent = true; }
        directoryItems = MapDirectoryItems(entries);
        var relative = currentIndex < 0 ? "" : entries[currentIndex].Path;
        var path = directoryItems.TryGetValue(CurrentCreationDirectory, out var selected) && !CurrentCreationDirectory.IsExisting ? selected.DisplayPath : FullCreationPath(Destination, relative);
        if (!SamePath(path, browserPath))
        {
            contentsRevision++; contentsCancellation?.Cancel(); diskContents = []; CreationContentsNotice = ""; IsReadingCreationContents = false;
            browserPath = path; OnPropertyChanged(nameof(CurrentCreationPath));
        }
        if (Report is null) creationOutcomes.Clear();
        else foreach (var result in Report.Items) creationOutcomes[result.Item.Id] = result;
        var plannedIds = Plan?.Items.Select(x => x.Id).ToHashSet() ?? [];
        var rows = new List<CreationContentRow>();
        if (IsTreeCreation)
        {
            foreach (var (node, _) in entries)
                node.Preview = node.IsMissing ? "已删除 · 含未创建内容" : directoryItems.TryGetValue(node, out var item) ? PlannedContent(item, node, plannedIds).Status : node.IsExisting ? "已有" : "待创建";
            foreach (var child in CurrentCreationDirectory.Children)
            {
                if (directoryItems.TryGetValue(child, out var item)) rows.Add(PlannedContent(item, child, plannedIds));
                else
                {
                    var childPath = FullCreationPath(browserPath, child.Name);
                    rows.Add(new(child.Name, childPath, child.IsDirectory, child.Preview,
                        child.IsMissing ? childPath + "\n此目录已删除，仅保留尚未创建的内容；不会自动重建。" : childPath.Length > 0 ? childPath : "请检查保存位置和文件夹名称。", child.IsExisting, child));
                }
            }
        }
        var displayed = rows.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.InsertRange(0, diskContents.Where(x => !displayed.Contains(x.FullPath)).Select(x =>
            new CreationContentRow(Path.GetFileName(x.FullPath), x.FullPath, x.IsFolder, "已有", x.FullPath, true)));
        var previous = SelectedCreationContent;
        CreationContents = rows;
        SelectedCreationContent = rows.FirstOrDefault(x => previous is not null && SamePath(x.Path, previous.Path) && x.IsExisting == previous.IsExisting);
        UpdateCreationBreadcrumbs(entries, currentIndex);
        foreach (var name in new[] { nameof(CurrentCreationDirectory), nameof(HasCreationContents), nameof(EmptyCreationContents), nameof(CreationEmptyMessage), nameof(CreationContentsSummary), nameof(CreationContentDetails), nameof(CanBrowseCreationParent), nameof(CreationEstimate), nameof(ExistingCreationContents), nameof(HasExistingCreationContents), nameof(ExistingCreationSummary) }) OnPropertyChanged(name);
        BrowseCreationParentCommand.NotifyCanExecuteChanged();
    }

    private void UpdateCreationBreadcrumbs(IReadOnlyList<(DirectoryDraft Node, string Path)> entries, int currentIndex)
    {
        var trail = new Stack<CreationBreadcrumb>();
        if (currentIndex >= 0)
        {
            var current = entries[currentIndex].Node;
            trail.Push(new(current, current.Name));
            // Draft entries visit parents before children. Walk back through that order to find the ancestors.
            for (var i = currentIndex - 1; i >= 0; i--)
            {
                var node = entries[i].Node;
                if (node.FolderChildren.Contains(trail.Peek().Directory))
                    trail.Push(new(node, node.Name));
            }
        }

        var destination = Destination.Trim();
        var rootName = Path.GetFileName(destination.TrimEnd('\\', '/'));
        if (rootName.Length == 0) rootName = destination.Length == 0 ? "保存位置" : destination;
        trail.Push(new(folderLocation, rootName));
        if (CreationBreadcrumbs.SequenceEqual(trail)) return;
        CreationBreadcrumbs = trail.ToArray();
        OnPropertyChanged(nameof(CreationBreadcrumbs));
    }

    private bool RetainUncreatedBranch(DirectoryDraft node)
    {
        foreach (var child in node.Children.ToArray())
            if (!RetainUncreatedBranch(child)) node.Children.Remove(child);
        if (!node.IsExisting && !node.IsMissing) return true;
        var hasInput = directoryInputs.Any(x => x.Value.Trim().Length > 0 && x.Key.Split('|').Contains(node.Id.ToString()));
        if (node.Children.Count == 0 && !hasInput) return false;
        node.IsExisting = false; node.IsMissing = true; node.HasLoadedContents = false;
        return true;
    }
    private void ReconcileCreationNavigation(string previousPath)
    {
        var entries = DirectoryEntries().ToArray();
        var valid = entries.Select(x => x.Node).ToHashSet(); valid.Add(folderLocation);
        if (!valid.Contains(CurrentCreationDirectory) || CurrentCreationDirectory.IsMissing)
        {
            var ancestor = entries.Where(x => x.Node.IsExisting && !x.Node.IsMissing && previousPath.StartsWith(FullCreationPath(Destination, x.Path).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Path.Length).FirstOrDefault().Node;
            SetCreationBrowser(ancestor ?? folderLocation);
        }
        SelectCreationParents(creationParents.Where(x => valid.Contains(x) && !x.IsMissing).ToArray(), browse: false);
        NotifyCreationEditor();
    }
    private void MarkCreationDirectoryMissing(DirectoryDraft directory)
    {
        if (directory.IsDestination)
        {
            foreach (var child in directory.Children.ToArray()) if (!RetainUncreatedBranch(child)) directory.Children.Remove(child);
            directory.IsMissing = true; directory.HasLoadedContents = false;
        }
        else if (!RetainUncreatedBranch(directory)) Siblings(directory)?.Remove(directory);
    }
    private bool SyncExistingDirectories(DirectoryDraft parent, IReadOnlyList<SearchFile> contents)
    {
        if (!IsTreeCreation) return false;
        parent.HasLoadedContents = true;
        var invalidate = parent.IsMissing;
        if (parent.IsMissing) parent.IsExisting = true;
        parent.IsMissing = false;
        var existing = contents.ToDictionary(x => Path.GetFileName(x.FullPath), StringComparer.OrdinalIgnoreCase);
        var childrenByName = parent.Children.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var node in parent.Children.Where(x => x.IsExisting).ToArray())
            if (!existing.TryGetValue(node.Name, out var file) || file.IsFolder != node.IsDirectory)
            {
                if (!RetainUncreatedBranch(node)) { parent.Children.Remove(node); childrenByName.Remove(node.Name); }
                invalidate = true;
            }
        foreach (var file in existing.Values.Where(x => x.IsFolder))
        {
            var name = Path.GetFileName(file.FullPath);
            var node = childrenByName.GetValueOrDefault(name);
            if (node is null) { node = MakeDraft(name); parent.Children.Add(node); }
            else if (!node.IsDirectory) continue;
            else if (!node.IsExisting) invalidate = true;
            if (node.IsMissing) invalidate = true;
            node.IsExisting = true; node.IsMissing = false; node.IsLink = file.IsReparsePoint; node.Preview = "已有";
        }
        return invalidate;
    }
    private void PromoteCreatedDirectories(NamingReport report)
    {
        if (!IsTreeCreation) return;
        var nodes = DraftEntries().ToDictionary(x => x.Path, x => x.Node, StringComparer.OrdinalIgnoreCase);
        reconcilingNames = true;
        try
        {
            foreach (var result in report.Items.Where(x => x.Outcome == NamingOutcome.Success || x.Item.Action == NamingAction.Existing))
            {
                var key = result.Item.RequestedRelativePath ?? result.Item.RelativeTarget ?? "";
                var node = nodes.GetValueOrDefault(key) ?? directoryItems.FirstOrDefault(x => x.Value.Id == result.Item.Id).Key;
                if (node is null) continue;
                node.Name = Path.GetFileName(result.ActualPath ?? result.Item.DisplayPath); node.IsExisting = true;
            }
        }
        finally { reconcilingNames = false; }
        RefreshCreationContents();
    }
    private void ApplyRemovedDirectories(IReadOnlyList<string> removed)
    {
        foreach (var entry in DraftEntries().ToArray())
            if (removed.Any(path => SamePath(path, FullCreationPath(Destination, entry.Path)))) Siblings(entry.Node)?.Remove(entry.Node);
        SelectCreationParents(creationParents.ToArray());
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ReloadCreationContentsAsync()
    {
        if (IsExecuting || IsSelectingTargets) return;
        contentsCancellation?.Cancel();
        var version = ++contentsRevision; var path = CurrentCreationPath;
        if (!IsCreate || directoryReader is null || path.Length == 0) { IsReadingCreationContents = false; return; }
        using var current = new CancellationTokenSource(); contentsCancellation = current;
        var directory = CurrentCreationDirectory;
        var loaded = DirectoryEntries().Where(x => x.Node.IsExisting && x.Node.HasLoadedContents && !x.Node.IsLink && !ReferenceEquals(x.Node, directory))
            .Select(x => (x.Node, Path: FullCreationPath(Destination, x.Path)))
            .Where(x => x.Path.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
        var reloadParent = false;
        IsReadingCreationContents = true; CreationContentsNotice = "";
        try
        {
            var contents = await directoryReader.ReadAsync(path, current.Token);
            if (version == contentsRevision && !current.IsCancellationRequested)
            {
                diskContents = contents;
                var changed = SyncExistingDirectories(directory, contents);
                foreach (var item in loaded)
                {
                    if (!item.Node.IsExisting || item.Node.IsMissing || !DirectoryEntries().Any(x => ReferenceEquals(x.Node, item.Node))) continue;
                    try
                    {
                        var children = await directoryReader.ReadAsync(item.Path, current.Token);
                        if (version != contentsRevision || current.IsCancellationRequested) return;
                        changed |= SyncExistingDirectories(item.Node, children);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        if (version != contentsRevision || current.IsCancellationRequested) return;
                        MarkCreationDirectoryMissing(item.Node);
                        changed = true;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { if (version == contentsRevision) CreationContentsNotice = "部分子目录未刷新：" + error.Message; }
                }
                if (changed)
                {
                    ReconcileCreationNavigation(path);
                    InvalidateCreationPlan();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (DirectoryNotFoundException)
        {
            if (version == contentsRevision)
            {
                diskContents = [];
                if (directory.IsExisting)
                {
                    MarkCreationDirectoryMissing(directory);
                    ReconcileCreationNavigation(path);
                    InvalidateCreationPlan();
                    reloadParent = !SamePath(path, CurrentCreationPath);
                    CreationContentsNotice = directory.IsDestination ? "保存位置已不存在，请在右上角重新选择文件夹。" : "此文件夹已删除，已返回上一级。";
                }
            }
        }
        catch (Exception error)
        {
            if (version == contentsRevision) { diskContents = []; CreationContentsNotice = "无法读取此文件夹，可检查路径或权限后刷新：" + error.Message; }
        }
        finally
        {
            if (version == contentsRevision) { IsReadingCreationContents = false; RefreshCreationContents(); }
            if (ReferenceEquals(contentsCancellation, current)) contentsCancellation = null;
        }
        if (reloadParent) await ReloadCreationContentsAsync();
    }
    partial void OnSelectedCreationContentChanged(CreationContentRow? value)
    {
        OnPropertyChanged(nameof(CreationContentDetails)); OnPropertyChanged(nameof(CanRemovePendingContent));
        RemovePendingContentCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsReadingCreationContentsChanged(bool value) => OnPropertyChanged(nameof(EmptyCreationContents));
    partial void OnCreationContentsNoticeChanged(string value)
    { OnPropertyChanged(nameof(HasCreationContentsNotice)); OnPropertyChanged(nameof(CreationEmptyMessage)); }
}
