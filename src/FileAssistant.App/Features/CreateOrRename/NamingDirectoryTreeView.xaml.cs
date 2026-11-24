using System.Collections.Specialized;
using System.ComponentModel;
using FileAssistant.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed partial class NamingDirectoryTreeView : UserControl, IDropTargetHandler
{
    internal static DirectoryDraft? ActiveTreeDrag;

    private readonly Dictionary<Guid, TreeViewNode> draftNodes = [];
    private readonly HashSet<DirectoryDraft> subscribedDrafts = [];
    private readonly DropTargetHighlight dropHighlight = new();
    private DirectoryDraft? draggedNode;
    private bool updatingTree;
    private bool treeUpdateQueued;
    private Guid? shownDirectoryId;

    public NamingViewModel ViewModel => App.Naming;

    public NamingDirectoryTreeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.OutlineReloadRequested += OnOutlineReloadRequested;
        UpdateTreeNavigation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.OutlineReloadRequested -= OnOutlineReloadRequested;
        foreach (var draft in subscribedDrafts)
            draft.FolderChildren.CollectionChanged -= OnFolderChildrenChanged;
        subscribedDrafts.Clear();
        draggedNode = null;
        ActiveTreeDrag = null;
        dropHighlight.Clear();
    }

    private void OnOutlineReloadRequested(object? sender, EventArgs e) => QueueTreeUpdate();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NamingViewModel.CurrentCreationDirectory)
            or nameof(NamingViewModel.CreationTreeRoots)
            or nameof(NamingViewModel.Destination)
            or nameof(NamingViewModel.ModeValue))
        {
            QueueTreeUpdate();
        }
    }

    private void OnFolderChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueTreeUpdate();

    private void QueueTreeUpdate()
    {
        if (treeUpdateQueued) return;
        treeUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            treeUpdateQueued = false;
            if (IsLoaded) UpdateTreeNavigation();
        })) treeUpdateQueued = false;
    }

    private void UpdateTreeNavigation()
    {
        var roots = ViewModel.CreationTreeRoots;
        if (roots.Count == 0) return;

        var reachable = new List<DirectoryDraft>();
        void Collect(DirectoryDraft folder)
        {
            reachable.Add(folder);
            foreach (var child in folder.FolderChildren) Collect(child);
        }
        foreach (var r in roots) Collect(r);

        var reachableIds = reachable.Select(x => x.Id).ToHashSet();

        updatingTree = true;
        try
        {
            foreach (var oldDraft in subscribedDrafts.Where(d => !reachableIds.Contains(d.Id)).ToArray())
            {
                oldDraft.FolderChildren.CollectionChanged -= OnFolderChildrenChanged;
                subscribedDrafts.Remove(oldDraft);
            }

            foreach (var deadId in draftNodes.Keys.Where(id => !reachableIds.Contains(id)).ToArray())
            {
                var deadNode = draftNodes[deadId];
                deadNode.Parent?.Children.Remove(deadNode);
                draftNodes.Remove(deadId);
            }

            foreach (var folder in reachable)
            {
                if (subscribedDrafts.Add(folder))
                    folder.FolderChildren.CollectionChanged += OnFolderChildrenChanged;

                if (!draftNodes.TryGetValue(folder.Id, out var node))
                    draftNodes[folder.Id] = node = new TreeViewNode { Content = folder, IsExpanded = true };
                else if (!ReferenceEquals(node.Content, folder))
                    node.Content = folder;
            }

            var rootDraft = roots[0];
            var rootNode = draftNodes[rootDraft.Id];
            if (CreationTree.RootNodes.Count == 0)
            {
                CreationTree.RootNodes.Add(rootNode);
                rootNode.IsExpanded = true;
            }
            else if (!ReferenceEquals(CreationTree.RootNodes[0], rootNode))
            {
                CreationTree.RootNodes.Clear();
                CreationTree.RootNodes.Add(rootNode);
                rootNode.IsExpanded = true;
            }

            // Detach moved nodes from old parents before inserting into new parents
            foreach (var parentDraft in reachable)
            {
                var parentNode = draftNodes[parentDraft.Id];
                foreach (var childDraft in parentDraft.FolderChildren)
                {
                    var childNode = draftNodes[childDraft.Id];
                    if (childNode.Parent is not null && !ReferenceEquals(childNode.Parent, parentNode))
                        childNode.Parent.Children.Remove(childNode);
                }
            }

            // Sync each parent's Children in exact order while preserving IsExpanded
            foreach (var parentDraft in reachable)
            {
                var parentNode = draftNodes[parentDraft.Id];
                var hadChildren = parentNode.Children.Count > 0;
                var desiredNodes = parentDraft.FolderChildren.Select(c => draftNodes[c.Id]).ToArray();
                var desiredSet = desiredNodes.ToHashSet();

                for (var i = parentNode.Children.Count - 1; i >= 0; i--)
                {
                    if (!desiredSet.Contains(parentNode.Children[i]))
                        parentNode.Children.RemoveAt(i);
                }

                for (var i = 0; i < desiredNodes.Length; i++)
                {
                    var desired = desiredNodes[i];
                    var existingIdx = parentNode.Children.IndexOf(desired);
                    if (existingIdx < 0)
                        parentNode.Children.Insert(i, desired);
                    else if (existingIdx != i)
                    {
                        parentNode.Children.RemoveAt(existingIdx);
                        parentNode.Children.Insert(i, desired);
                    }
                }

                // Automatically expand a folder the first time children are placed inside it
                if (!hadChildren && parentNode.Children.Count > 0)
                    parentNode.IsExpanded = true;
            }

            var currentDraft = ViewModel.CurrentCreationDirectory;
            if (draftNodes.TryGetValue(currentDraft.Id, out var currentNode))
            {
                if (shownDirectoryId != currentDraft.Id)
                {
                    for (var p = currentNode.Parent; p is not null; p = p.Parent)
                        p.IsExpanded = true;
                }
                if (!ReferenceEquals(CreationTree.SelectedNode, currentNode))
                    CreationTree.SelectedNode = currentNode;
                shownDirectoryId = currentDraft.Id;
            }
        }
        finally
        {
            updatingTree = false;
        }
    }

    private void CreationTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (updatingTree || sender.SelectedNode?.Content is not DirectoryDraft folder) return;
        if (!ReferenceEquals(folder, ViewModel.CurrentCreationDirectory))
            ViewModel.BrowseCreationDirectory(folder);
        QueueTreeUpdate();
    }

    private void Target_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: DirectoryDraft parent } box) return;
        var selected = ViewModel.CreationParents.Where(x => !ReferenceEquals(x, parent)).ToList();
        if (box.IsChecked == true)
        {
            if (parent.IsDestination) selected.Clear();
            selected.Add(parent);
        }
        ViewModel.SelectCreationParents(selected, browse: false);
    }

    private TreeViewItem? FolderContainer(DependencyObject? source)
    {
        for (; source is not null && !ReferenceEquals(source, CreationTreeArea); source = VisualTreeHelper.GetParent(source))
            if (source is TreeViewItem item) return item;
        return null;
    }

    private TreeViewItem? DropDraftContainer(DependencyObject? source, DragEventArgs e)
    {
        if (FolderContainer(source) is { } direct) return direct;
        var point = e.GetPosition(CreationTreeArea);
        if (point.X < 0 || point.X >= CreationTreeArea.ActualWidth) return null;
        foreach (var node in draftNodes.Values)
        {
            if (CreationTree.ContainerFromNode(node) is not TreeViewItem { IsLoaded: true } item) continue;
            var bounds = item.TransformToVisual(CreationTreeArea).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (point.Y >= bounds.Top - item.Margin.Top && point.Y < bounds.Bottom + item.Margin.Bottom) return item;
        }
        return null;
    }

    private DirectoryDraft? ResolveDropTarget(TreeViewItem? container)
    {
        if (container is not null && CreationTree.NodeFromContainer(container)?.Content is DirectoryDraft folder)
            return folder;
        return ViewModel.CreationTreeRoots.FirstOrDefault();
    }

    private void CreationTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
    {
        var node = e.Items.OfType<TreeViewNode>().Select(n => n.Content).OfType<DirectoryDraft>().FirstOrDefault()
            ?? e.Items.OfType<DirectoryDraft>().FirstOrDefault();
        if (node is null || !node.CanDragDraft)
        {
            e.Cancel = true;
            return;
        }
        draggedNode = node;
        ActiveTreeDrag = node;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(node.Name);
    }

    private void CreationTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs e)
    {
        draggedNode = null;
        ActiveTreeDrag = null;
        dropHighlight.Clear();
    }

    void IDropTargetHandler.TargetDragOver(DependencyObject source, DragEventArgs args) => HandleTreeDragOver(source, args);
    void IDropTargetHandler.TargetDrop(DependencyObject source, DragEventArgs args) => HandleTreeDrop(source, args);
    void IDropTargetHandler.TargetDragLeave(DependencyObject source, DragEventArgs args) => dropHighlight.Clear();

    private void TreeArea_DragOver(object sender, DragEventArgs e) => HandleTreeDragOver(sender as DependencyObject, e);
    private void TreeArea_Drop(object sender, DragEventArgs e) => HandleTreeDrop(sender as DependencyObject, e);
    private void TreeArea_DragLeave(object sender, DragEventArgs e) => dropHighlight.Clear();

    private void HandleTreeDragOver(DependencyObject? source, DragEventArgs e)
    {
        var container = DropDraftContainer(source, e);
        var target = ResolveDropTarget(container);
        if (target is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            dropHighlight.Clear();
            return;
        }

        // Case 1: Dragging outline rows from Center Preview onto a Left Tree folder
        if (draggedNode is null && NamingCreationPreviewView.ActiveExternalDrag is { DraggedLines.Count: > 0 } external)
        {
            if (ViewModel.CanDropOutlineLinesIntoDirectory(external.DraggedLines, external.RemainingCurrentLines, target))
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                e.DragUIOverride.Caption = $"放入「{(target.IsDestination ? "根目录" : target.Name)}」（{external.DraggedLines.Count} 项）";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = false;
                dropHighlight.Show(container ?? (DependencyObject)CreationTreeArea);
                return;
            }
            e.AcceptedOperation = DataPackageOperation.None;
            dropHighlight.Clear();
            return;
        }

        // Case 2: Dragging a pending folder within the Left Tree onto another folder (or empty area -> root)
        var moving = draggedNode ?? ActiveTreeDrag;
        if (moving is not null && ViewModel.CanMoveCreationDraft(moving, target))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"移动到「{(target.IsDestination ? "根目录" : target.Name)}」";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            dropHighlight.Show(container ?? (DependencyObject)CreationTreeArea);
            return;
        }

        e.AcceptedOperation = DataPackageOperation.None;
        dropHighlight.Clear();
    }

    private void HandleTreeDrop(DependencyObject? source, DragEventArgs e)
    {
        var container = DropDraftContainer(source, e);
        var target = ResolveDropTarget(container);
        dropHighlight.Clear();
        if (target is null) return;

        if (draggedNode is null && NamingCreationPreviewView.ActiveExternalDrag is { } external)
        {
            NamingCreationPreviewView.ActiveExternalDrag = null;
            if (ViewModel.MoveOutlineLinesToDirectory(external.DraggedLines, external.RemainingCurrentLines, target)
                && draftNodes.TryGetValue(target.Id, out var targetNode))
            {
                targetNode.IsExpanded = true;
            }
            QueueTreeUpdate();
            return;
        }

        var moving = draggedNode ?? ActiveTreeDrag;
        draggedNode = null;
        ActiveTreeDrag = null;
        if (moving is null) return;

        if (ViewModel.MoveCreationDraft(moving, target)
            && draftNodes.TryGetValue(target.Id, out var parentNode))
        {
            parentNode.IsExpanded = true;
        }
        QueueTreeUpdate();
    }

    private void Tree_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var hasPos = e.TryGetPosition(CreationTreeArea, out _);
        var source = hasPos ? e.OriginalSource : FocusManager.GetFocusedElement(XamlRoot);
        var container = FolderContainer(source as DependencyObject);
        var node = (container is not null ? CreationTree.NodeFromContainer(container)?.Content as DirectoryDraft : null)
            ?? NamingCreationMenu.TargetAt<DirectoryDraft>(source)
            ?? (!hasPos ? CreationTree.SelectedNode?.Content as DirectoryDraft : null);
        NamingCreationMenu.Show(CreationTreeArea, e, node is null ? null : ViewModel.CreationContext(node), ViewModel.CreationTreeRoots[0]);
    }
}
