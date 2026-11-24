using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Features.SpaceAnalysis;

public sealed partial class SpaceAnalysisView
{
    private const int TreeBatch = 180;
    private const string FolderWidthKey = "SpaceAnalysis.Folders";
    private const double DefaultFolderWidth = 194;
    private const double MinimumFolderWidth = 172;
    private const double MaximumFolderWidth = 430;
    private readonly Dictionary<int, TreeViewNode> folderNodes = [];
    private readonly Dictionary<int, FolderBranch> folderBranches = [];
    private bool changingTree;

    private sealed class FolderBranch(int[] children)
    {
        public int[] Children { get; } = children;
        public int Loaded { get; set; }
    }

    private void RebuildTree(SpaceSnapshot? snapshot)
    {
        changingTree = true;
        try
        {
            Folders.RootNodes.Clear();
            folderNodes.Clear();
            folderBranches.Clear();
            if (snapshot is null) return;
            var root = CreateFolderNode(snapshot, 0);
            Folders.RootNodes.Add(root);
            LoadChildren(snapshot, 0);
            root.IsExpanded = true;
        }
        finally { changingTree = false; }
    }

    private TreeViewNode CreateFolderNode(SpaceSnapshot snapshot, int id)
    {
        var entry = snapshot.Entries[id];
        var share = Percentage(entry.TotalBytes(model.LogicalSize), snapshot.Entries[Math.Max(0, entry.Parent)].TotalBytes(model.LogicalSize));
        bool physicalVolume = !model.LogicalSize && id == 0 && snapshot.IsWholeVolume;
        var size = physicalVolume ? SpaceLabels.Size(snapshot.Volume!.UsedBytes) : model.LogicalSize ? SpaceLabels.Logical(entry) : SpaceLabels.Allocated(entry);
        var label = physicalVolume ? $"{entry.Name} · 磁盘已用 {size}" :
            $"{entry.Name} · {size}{(entry.UnknownAllocation ? "" : $" · 占已统计空间 {share:0.#}%")}{(entry.IsShared ? " · 含共享数据" : "")}";
        if (model.ProgramLabel(id) is { Length: > 0 } program) label += " · " + program;
        var node = new TreeViewNode
        {
            Content = new SpaceFolderRow(id, entry.Name, size, share, label),
            HasUnrealizedChildren = snapshot.HasFolders(id)
        };
        folderNodes.Add(id, node);
        return node;
    }

    private void LoadChildren(SpaceSnapshot snapshot, int id)
    {
        var node = folderNodes[id];
        if (!folderBranches.TryGetValue(id, out var branch))
        {
            var ordered = snapshot.Children(id).Where(child => snapshot.Entries[child].IsDirectory)
                .OrderByDescending(child => snapshot.Entries[child].TotalBytes(model.LogicalSize))
                .ThenBy(child => snapshot.Entries[child].Name, StringComparer.OrdinalIgnoreCase).ToArray();
            folderBranches[id] = branch = new(ordered);
        }
        var limit = Math.Min(branch.Loaded + TreeBatch, branch.Children.Length);
        if (node.Children.LastOrDefault()?.Content is SpaceFolderRow { More: true })
            node.Children.RemoveAt(node.Children.Count - 1);
        for (var i = branch.Loaded; i < limit; i++)
        {
            var child = branch.Children[i];
            if (!folderNodes.ContainsKey(child)) node.Children.Add(CreateFolderNode(snapshot, child));
        }
        branch.Loaded = limit;
        if (limit < branch.Children.Length)
        {
            var label = $"显示更多目录（剩 {branch.Children.Length - limit:N0}）";
            node.Children.Add(new() { Content = new SpaceFolderRow(id, label, "", 0, label, More: true) });
        }
        node.HasUnrealizedChildren = false;
    }

    private void ShowFolder(SpaceSnapshot snapshot, int id)
    {
        var trail = new Stack<int>();
        for (var i = id; i >= 0; i = snapshot.Entries[i].Parent) trail.Push(i);
        Crumbs.ItemsSource = trail.Select(i => new SpaceCrumb(i, snapshot.Entries[i].Name)).ToArray();
        ToolTipService.SetToolTip(Crumbs, snapshot.PathOf(id));

        changingTree = true;
        try
        {
            foreach (var part in trail)
            {
                if (part == 0) continue;
                var parent = snapshot.Entries[part].Parent;
                var node = folderNodes[parent];
                if (!folderBranches.ContainsKey(parent)) LoadChildren(snapshot, parent);
                if (!folderNodes.ContainsKey(part))
                {
                    // A chart/list can link to a directory beyond the first batch.
                    var index = node.Children.LastOrDefault()?.Content is SpaceFolderRow { More: true } ? node.Children.Count - 1 : node.Children.Count;
                    node.Children.Insert(index, CreateFolderNode(snapshot, part));
                }
                node.IsExpanded = true;
            }
            if (!ReferenceEquals(Folders.SelectedNode, folderNodes[id])) Folders.SelectedNode = folderNodes[id];
        }
        finally { changingTree = false; }
    }

    private void Folders_Expanding(TreeView sender, TreeViewExpandingEventArgs e)
    {
        if (!changingTree && shownSnapshot is { } snapshot && e.Node.Content is SpaceFolderRow { More: false } row &&
            !folderBranches.ContainsKey(row.Id))
            LoadChildren(snapshot, row.Id);
    }

    private void Folders_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs e)
    {
        if (!changingTree && sender.SelectedNode?.Content is SpaceFolderRow { More: false } row &&
            (model.Folder != row.Id || model.View != SpaceView.Directory))
            model.Browse(row.Id);
    }

    private void Folders_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        if (changingTree || model.IsBusy || shownSnapshot is not { } snapshot ||
            e.InvokedItem is not TreeViewNode { Content: SpaceFolderRow row }) return;
        if (!row.More)
        {
            if (model.Folder != row.Id || model.View != SpaceView.Directory) model.Browse(row.Id);
            return;
        }
        changingTree = true;
        try
        {
            LoadChildren(snapshot, row.Id);
            Folders.SelectedNode = folderNodes[model.Folder];
        }
        finally { changingTree = false; }
    }

    private void Folders_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (model.IsBusy || shownSnapshot is not { } snapshot) return;
        var source = ContextTarget(Folders, e);
        var container = FindContainer<TreeViewItem>(source, Folders);
        var node = container is not null ? Folders.NodeFromContainer(container) :
            e.TryGetPosition(Folders, out _) ? null : Folders.SelectedNode;
        if (node?.Content is SpaceFolderRow { More: false } row)
            ShowActions(Folders, e, snapshot, row.Id, [row.Id]);
    }

    private void Parent_Click(object sender, RoutedEventArgs e)
    {
        if (shownSnapshot is not { } snapshot || model.Folder == 0) return;
        var parent = snapshot.Entries[model.Folder].Parent;
        model.Browse(parent, model.Folder);
    }
    private void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is SpaceCrumb crumb) model.Browse(crumb.Id);
    }

    private bool? lastFoldersOpen;
    private void ToggleFolders_Click(object sender, RoutedEventArgs e)
    {
        foldersOpen = !foldersOpen;
        UpdateFolderWidth();
    }

    private bool? wasCompact;
    private void MainArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActualWidth > 0)
        {
            var compact = ActualWidth < 1050;
            if (wasCompact != compact)
            {
                wasCompact = compact;
                foldersOpen = !compact;
            }
        }
        UpdateFolderWidth();
    }
    private double AvailableFolderWidth => Math.Clamp(MainArea.ActualWidth - 350, MinimumFolderWidth, MaximumFolderWidth);
    private double CurrentFolderWidth => Math.Clamp(preferredFolderWidth ?? DefaultFolderWidth, MinimumFolderWidth, AvailableFolderWidth);

    private void Folders_Resize(object? sender, double delta)
    {
        if (!foldersOpen || !double.IsFinite(delta)) return;
        var current = CurrentFolderWidth;
        var width = Math.Clamp(current + delta, MinimumFolderWidth, AvailableFolderWidth);
        if (width != current) SetPreferredFolderWidth(width);
    }

    private void Folders_ResetRequested(object? sender, EventArgs e) => SetPreferredFolderWidth(null);

    private void SetPreferredFolderWidth(double? width)
    {
        preferredFolderWidth = width;
        UpdateFolderWidth();
        App.PaneWidths.SetWidth(FolderWidthKey, width);
    }

    private void UpdateFolderWidth()
    {
        if (FolderColumn is null || FolderGapColumn is null || CenterSurface is null || MainArea is null) return;
        var open = foldersOpen;
        var toggled = lastFoldersOpen is not null && lastFoldersOpen != open;
        lastFoldersOpen = open;
        DetailsPane.MinimumContentWidth = 360 + (open ? CurrentFolderWidth : 48);

        if (toggled && IsLoaded && MainArea.ActualWidth > 0)
        {
            Controls.WorkspaceMotion.AnimateSidePaneToggle(
                this,
                FolderColumn,
                FolderGapColumn,
                FoldersPane,
                FoldersCollapsed,
                FolderResize,
                CenterSurface,
                opening: open,
                expandedWidth: CurrentFolderWidth,
                collapsedWidth: 48,
                applyRestingGeometry: () => ApplyFolderRestingGeometry(open));
            return;
        }

        Controls.WorkspaceMotion.CancelWidthAnimation(this);
        FoldersPane.Opacity = 1;
        ApplyFolderRestingGeometry(open);
    }

    private void ApplyFolderRestingGeometry(bool open)
    {
        FoldersPane.Visibility = FolderResize.Visibility = Show(open);
        FoldersCollapsed.Visibility = Show(!open);

        if (open)
        {
            FolderGapColumn.Width = new GridLength(Controls.ResizeHandle.HitTargetThickness);
            FolderColumn.Width = new GridLength(Math.Max(0, CurrentFolderWidth - Controls.ResizeHandle.HitTargetOverlap));
            Grid.SetColumn(FoldersPane, 0);
            Grid.SetColumnSpan(FoldersPane, 2);
            FoldersPane.Margin = new Thickness(0, 0, Controls.ResizeHandle.HitTargetOverlap, 0);
            Grid.SetColumn(CenterSurface, 1);
            Grid.SetColumnSpan(CenterSurface, 2);
            CenterSurface.Margin = new Thickness(Controls.ResizeHandle.HitTargetOverlap, 0, 0, 0);
        }
        else
        {
            FolderGapColumn.Width = new GridLength(0);
            FolderColumn.Width = new GridLength(48);
            Grid.SetColumn(FoldersPane, 0);
            Grid.SetColumnSpan(FoldersPane, 1);
            FoldersPane.Margin = new Thickness(0);
            Grid.SetColumn(CenterSurface, 2);
            Grid.SetColumnSpan(CenterSurface, 1);
            CenterSurface.Margin = new Thickness(0);
        }
    }

    private object? ContextTarget(FrameworkElement owner, ContextRequestedEventArgs e)
        => e.TryGetPosition(owner, out _) ? e.OriginalSource : FocusManager.GetFocusedElement(XamlRoot);

    private static T? FindContainer<T>(object? source, DependencyObject stop) where T : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null && current != stop; current = VisualTreeHelper.GetParent(current))
            if (current is T item) return item;
        return null;
    }
}
