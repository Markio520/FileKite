using System.ComponentModel;
using FileAssistant.Core.Features.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileAssistant.App.Features.Search;

public sealed partial class SearchView
{
    private const int FolderPageSize = 200;
    private CancellationTokenSource navigationCancellation = new();
    private int locationVersion;
    private int shownVisibilityRevision = -1;
    private readonly Dictionary<TreeViewNode, IReadOnlyList<SearchFile>> locationFolders = [];
    private readonly Dictionary<TreeViewNode, Task> locationLoads = [];
    private string? parentLocation;
    private TreeViewNode? locationAnchor, pinnedLocationRoot;

    private void SearchState_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (active && e.PropertyName == nameof(SearchViewModel.LocationVisibilityRevision)) RefreshLocations();
        if (e.PropertyName is nameof(SearchViewModel.ScopeIndex) or nameof(SearchViewModel.Folder)) QueueLocationSelection();
        if (e.PropertyName is nameof(SearchViewModel.ScopeIndex) or nameof(SearchViewModel.Folder) or
            nameof(SearchViewModel.IsBusy) or nameof(SearchViewModel.Total) or
            nameof(SearchViewModel.SizeIndex) or nameof(SearchViewModel.DateIndex) or nameof(SearchViewModel.MatchPath)) UpdateSearchChrome();
    }

    private void UpdateSearchChrome()
    {
        if (RefreshButton is null) return;
        RefreshButton.Visibility = ViewModel.IsBusy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        var activeFilters = (ViewModel.SizeIndex > 0 ? 1 : 0) + (ViewModel.DateIndex > 0 ? 1 : 0) + (ViewModel.MatchPath ? 1 : 0);
        Controls.WorkspaceLocalization.SetContent(FiltersButton, activeFilters > 0 ? $"筛选 · {activeFilters}" : "筛选");
        var crumbs = LocationTrail(ViewModel.IsFolderScope ? ViewModel.Folder : "");
        parentLocation = crumbs.Length > 1 ? crumbs[^2].Path : null;
        ParentLocation.IsEnabled = parentLocation is not null;
        LocationCrumbs.ItemsSource = crumbs;
        LocationCrumbs.Visibility = crumbs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScopeCaption.Visibility = crumbs.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Controls.WorkspaceLocalization.SetText(VisibleResultCount, $"{ViewModel.Total:N0} 项");
    }

    private static SearchLocationCrumb[] LocationTrail(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return [];
        try
        {
            var result = new List<SearchLocationCrumb>();
            for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                result.Add(new(SearchViewModel.LocationName(current), current));
            result.Reverse();
            return result.ToArray();
        }
        catch (ArgumentException) { return []; }
        catch (NotSupportedException) { return []; }
    }

    private void TaskToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1100;
        var narrow = e.NewSize.Width < 700;
        TaskToolbar.ColumnSpacing = narrow ? 4 : 8;
        if (QueryBox.Parent is Grid field) field.MinWidth = narrow ? 80 : 160;
        KindBox.MinWidth = narrow ? 72 : 104;
        PresetsButton.Visibility = HelpButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MorePresets_Click(object sender, RoutedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => PresetsButton.Flyout.ShowAt(MoreButton));
    private void MoreHelp_Click(object sender, RoutedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => HelpButton.Flyout.ShowAt(MoreButton));
    private void ParentLocation_Click(object sender, RoutedEventArgs e)
    { if (parentLocation is not null) ViewModel.UseFolder(parentLocation); }
    private void LocationCrumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    { if (e.Item is SearchLocationCrumb crumb) ViewModel.UseFolder(crumb.Path); }

    private void StartLocationNavigation()
    {
        if (shownVisibilityRevision != ViewModel.LocationVisibilityRevision)
        {
            shownVisibilityRevision = ViewModel.LocationVisibilityRevision;
            StopLocationNavigation();
            locationFolders.Clear(); locationLoads.Clear(); LocationsTree.RootNodes.Clear();
            locationAnchor = pinnedLocationRoot = null;
        }
        if (navigationCancellation.IsCancellationRequested)
        {
            navigationCancellation.Dispose(); navigationCancellation = new();
            locationLoads.Clear();
        }
        if (LocationsTree.RootNodes.Count == 0)
        {
            LocationsTree.RootNodes.Add(new() { Content = new SearchLocationNode("全部索引", "", "\uE774", Scope: SearchScope.Everything) });
            LocationsTree.RootNodes.Add(new() { Content = new SearchLocationNode("文件暂存区", "", "\uE8B7", Scope: SearchScope.Batch) });
            try
            {
                foreach (var root in ViewModel.GetLocationRoots()) LocationsTree.RootNodes.Add(LocationNode(root.FullPath, root: true));
                NavigationMessage.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                NavigationMessage.Text = "无法列出磁盘。" + error.Message;
                NavigationMessage.Visibility = Visibility.Visible;
            }
        }
        UpdateSearchChrome(); QueueLocationSelection();
    }
    private void StopLocationNavigation() { ++locationVersion; navigationCancellation.Cancel(); }
    private void RefreshLocations_Click(object sender, RoutedEventArgs e) => RefreshLocations();
    private void RefreshLocations()
    {
        shownVisibilityRevision = -1;
        StartLocationNavigation();
    }

    private void QueueLocationSelection()
    {
        var version = ++locationVersion;
        if (!active) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!active || version != locationVersion) return;
            var trail = LocationTrail(ViewModel.IsFolderScope ? ViewModel.Folder : "");
            if (trail.Length == 0)
            {
                LocationsTree.SelectedNode = LocationsTree.RootNodes.FirstOrDefault(node =>
                    node.Content is SearchLocationNode item && item.Scope != SearchScope.Folder && (int)item.Scope == ViewModel.ScopeIndex);
                return;
            }
            var anchorIndex = locationAnchor?.Content is SearchLocationNode anchor
                ? Array.FindIndex(trail, crumb => SamePath(crumb.Path, anchor.Path)) : -1;
            if (anchorIndex < 0)
            {
                // A chosen deep directory is a useful navigation root. Do not unfold
                // every ancestor from the drive and push its name out of the pane.
                locationAnchor = LocationsTree.RootNodes.FirstOrDefault(item => SameLocation(item, trail[^1].Path));
                if (locationAnchor is null)
                {
                    if (pinnedLocationRoot is not null) LocationsTree.RootNodes.Remove(pinnedLocationRoot);
                    pinnedLocationRoot = locationAnchor = LocationNode(trail[^1].Path);
                    LocationsTree.RootNodes.Insert(2, pinnedLocationRoot);
                }
                anchorIndex = trail.Length - 1;
            }
            var node = locationAnchor!;
            for (var i = anchorIndex + 1; i < trail.Length; i++)
            {
                await LoadLocationAsync(node);
                if (!active || version != locationVersion) return;
                var next = node.Children.FirstOrDefault(item => SameLocation(item, trail[i].Path));
                if (next is null && locationFolders.TryGetValue(node, out var children) && children.Any(item => SamePath(item.FullPath, trail[i].Path)))
                {
                    next = LocationNode(trail[i].Path);
                    node.Children.Insert(0, next);
                }
                if (next is null) break;
                node.IsExpanded = true; node = next;
            }
            if (version != locationVersion) return;
            // Synchronizing selection never changes the search scope; only user invocation does.
            if (!SamePath(((SearchLocationNode)node.Content).Path, trail[^1].Path)) { LocationsTree.SelectedNode = null; return; }
            node.IsExpanded = true;
            await LoadLocationAsync(node);
            // TreeView rebuilds its flat list after expansion; restore selection once
            // that work is committed, including when returning during a pending read.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!active || version != locationVersion) return;
                LocationsTree.SelectedNode = node;
                (LocationsTree.ContainerFromNode(node) as TreeViewItem)?.StartBringIntoView();
            });
        });
    }
    private static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
    private static bool SameLocation(TreeViewNode node, string path) => node.Content is SearchLocationNode { IsAction: false, Scope: SearchScope.Folder } item && SamePath(item.Path, path);
    private static TreeViewNode LocationNode(string path, bool root = false) => new()
    {
        Content = new SearchLocationNode(SearchViewModel.LocationName(path), path, root ? "\uEDA2" : "\uE8B7"),
        HasUnrealizedChildren = true
    };
    private async void Locations_Expanding(TreeView sender, TreeViewExpandingEventArgs e) => await LoadLocationAsync(e.Node);
    private async void Locations_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        if (e.InvokedItem is not TreeViewNode { Content: SearchLocationNode item } node) return;
        if (item.Scope != SearchScope.Folder) { ViewModel.ScopeIndex = (int)item.Scope; return; }
        if (!item.IsAction) { ViewModel.UseFolder(item.Path); return; }
        if (node.Parent is not { } parent) return;
        if (locationFolders.ContainsKey(parent)) AppendLocationPage(parent);
        else await LoadLocationAsync(parent);
    }

    private Task LoadLocationAsync(TreeViewNode node)
    {
        if (locationFolders.ContainsKey(node)) return Task.CompletedTask;
        if (locationLoads.TryGetValue(node, out var pending)) return pending;
        var task = ReadLocationAsync(node, navigationCancellation.Token);
        locationLoads[node] = task;
        _ = RemoveLoadAsync(node, task);
        return task;
    }
    private async Task RemoveLoadAsync(TreeViewNode node, Task task)
    {
        await task;
        if (locationLoads.TryGetValue(node, out var current) && ReferenceEquals(current, task)) locationLoads.Remove(node);
    }
    private async Task ReadLocationAsync(TreeViewNode node, CancellationToken token)
    {
        if (node.Content is not SearchLocationNode { Scope: SearchScope.Folder } item) return;
        try
        {
            node.Children.Clear();
            node.Children.Add(new() { Content = new SearchLocationNode("正在读取…", item.Path, "\uE895", true) });
            var children = await ViewModel.ReadLocationFoldersAsync(item.Path, token);
            token.ThrowIfCancellationRequested();
            NavigationMessage.Visibility = Visibility.Collapsed;
            locationFolders[node] = children;
            node.Children.Clear(); node.HasUnrealizedChildren = false;
            AppendLocationPage(node);
        }
        catch (OperationCanceledException)
        {
            // An earlier page visit must not clear a newer read of this same node.
            if (token == navigationCancellation.Token) { node.Children.Clear(); node.HasUnrealizedChildren = true; }
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            node.HasUnrealizedChildren = false; node.Children.Clear();
            node.Children.Add(new() { Content = new SearchLocationNode("无法读取 · 点击重试", item.Path, "\uE946", true) });
            NavigationMessage.Text = "无法读取目录。" + error.Message;
            ToolTipService.SetToolTip(NavigationMessage, NavigationMessage.Text);
            NavigationMessage.Visibility = Visibility.Visible;
        }
    }
    private void AppendLocationPage(TreeViewNode node)
    {
        foreach (var action in node.Children.Where(child => child.Content is SearchLocationNode { IsAction: true }).ToArray()) node.Children.Remove(action);
        var shown = node.Children.Select(child => ((SearchLocationNode)child.Content).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = locationFolders[node].Where(item => !shown.Contains(item.FullPath)).ToArray();
        foreach (var folder in remaining.Take(FolderPageSize)) node.Children.Add(LocationNode(folder.FullPath));
        if (remaining.Length > FolderPageSize)
            node.Children.Add(new() { Content = new SearchLocationNode($"显示更多目录 · 还有 {remaining.Length - FolderPageSize:N0} 项", ((SearchLocationNode)node.Content).Path, "\uE712", true) });
    }
}

public sealed record SearchLocationNode(string Name, string Path, string Glyph, bool IsAction = false, SearchScope Scope = SearchScope.Folder);
public sealed record SearchLocationCrumb(string Name, string Path);
