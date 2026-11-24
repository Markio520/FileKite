using FileAssistant.App.Controls;
using FileAssistant.App.Features.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FileAssistant.App.Features.FileInput;

public sealed partial class FileInputView : IDropTargetHandler
{
    void IDropTargetHandler.TargetDragOver(DependencyObject source, DragEventArgs args) => FolderTree_DragOver(source, args);
    void IDropTargetHandler.TargetDrop(DependencyObject source, DragEventArgs args) => FolderTree_Drop(source, args);
    void IDropTargetHandler.TargetDragLeave(DependencyObject source, DragEventArgs args) => ClearDropHighlight();

    private readonly Dictionary<Guid, TreeViewNode> folderNodes = [];
    private bool updatingFolderTree;
    private bool folderUpdateQueued;
    private Guid? shownFolderId;

    private void ResetFolderNavigation()
    {
        updatingFolderTree = true;
        try { FolderTree.RootNodes.Clear(); folderNodes.Clear(); shownFolderId = null; }
        finally { updatingFolderTree = false; }
    }

    private void QueueFolderNavigationUpdate()
    {
        if (folderUpdateQueued) return;
        folderUpdateQueued = true;
        // Navigation changes the input list during TreeView's selection event.
        // Reconcile after that event so native keyboard focus and selection finish first.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            folderUpdateQueued = false;
            if (IsLoaded && displayModel is not null) UpdateFolderNavigation();
        })) folderUpdateQueued = false;
    }

    private void UpdateFolderNavigation()
    {
        var folders = ViewModel.FolderNavigation;
        var ids = folders.Select(folder => folder.Id).ToHashSet();
        updatingFolderTree = true;
        try
        {
            foreach (var id in folderNodes.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var node = folderNodes[id];
                node.Parent?.Children.Remove(node);
                folderNodes.Remove(id);
            }
            foreach (var folder in folders)
            {
                if (!folderNodes.TryGetValue(folder.Id, out var node))
                    folderNodes[folder.Id] = node = new TreeViewNode { IsExpanded = true };
                if (!Equals(node.Content, folder)) node.Content = folder;
            }
            var root = folderNodes[Guid.Empty];
            if (FolderTree.RootNodes.Count == 0) FolderTree.RootNodes.Add(root);
            // Detach old relationships first; undo can reverse a parent/child move.
            foreach (var folder in folders.Where(folder => folder.Id != Guid.Empty))
            {
                var node = folderNodes[folder.Id];
                if (!ReferenceEquals(node.Parent, folderNodes[folder.ParentId])) node.Parent?.Children.Remove(node);
            }
            foreach (var folder in folders.Where(folder => folder.Id != Guid.Empty))
            {
                var node = folderNodes[folder.Id];
                var parent = folderNodes[folder.ParentId];
                if (ReferenceEquals(node.Parent, parent)) continue;
                parent.Children.Add(node);
            }
            var current = folderNodes[ViewModel.CurrentFolderId];
            if (shownFolderId != ViewModel.CurrentFolderId)
                for (var parent = current.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
            if (!ReferenceEquals(FolderTree.SelectedNode, current)) FolderTree.SelectedNode = current;
            shownFolderId = ViewModel.CurrentFolderId;
        }
        finally { updatingFolderTree = false; }
    }

    private void FolderTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs e)
    {
        if (updatingFolderTree || sender.SelectedNode?.Content is not FolderNavigationItem folder) return;
        ViewModel.EnterFolder(folder.Id);
        QueueFolderNavigationUpdate();
    }

    private TreeViewItem? FolderContainer(DependencyObject? source)
    {
        for (; source is not null && source != FolderTreeArea; source = VisualTreeHelper.GetParent(source))
            if (source is TreeViewItem item) return item;
        return null;
    }

    private Guid DropFolderId(TreeViewItem? container) =>
        (container is not null ? FolderTree.NodeFromContainer(container)?.Content as FolderNavigationItem : null)?.Id ?? Guid.Empty;

    private TreeViewItem? DropFolderContainer(DragEventArgs e)
    {
        // Include the row margins so the narrow gaps between rows do not briefly
        // switch the destination to the root. Space below the last row stays root.
        var point = e.GetPosition(FolderTreeArea);
        if (point.X < 0 || point.X >= FolderTreeArea.ActualWidth) return null;
        foreach (var node in folderNodes.Values)
        {
            if (FolderTree.ContainerFromNode(node) is not TreeViewItem { IsLoaded: true } item) continue;
            var bounds = item.TransformToVisual(FolderTreeArea).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (point.Y >= bounds.Top - item.Margin.Top && point.Y < bounds.Bottom + item.Margin.Bottom) return item;
        }
        return null;
    }

    private void FolderTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs e)
    {
        var ids = e.Items.OfType<TreeViewNode>().Select(node => node.Content).OfType<FolderNavigationItem>().Select(folder => folder.Id).ToArray();
        e.Cancel = ids.Contains(Guid.Empty) || !StartInputDrag(ViewModel.GetFolderItems(ids), e.Data);
    }

    private void FolderTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs e)
    { currentDrag = null; ClearDropHighlight(); }

    private void FolderTree_DragOver(object sender, DragEventArgs e)
    {
        var container = DropFolderContainer(e);
        if (!CollectionDragOver(e, DropFolderId(container))) { ClearDropHighlight(); return; }
        dropHighlight.Show(container ?? (DependencyObject)FolderTreeArea);
    }

    private async void FolderTree_Drop(object sender, DragEventArgs e)
    {
        var destination = DropFolderId(DropFolderContainer(e));
        await CollectionDropAsync(e, destination);
        if (folderNodes.TryGetValue(destination, out var parent)) parent.IsExpanded = true;
    }

    private void FolderTree_DragLeave(object sender, DragEventArgs e) => ClearDropHighlight();

    private void FolderTree_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var hasPosition = e.TryGetPosition(FolderTreeArea, out var position);
        var source = hasPosition ? e.OriginalSource : FocusManager.GetFocusedElement(XamlRoot);
        var container = FolderContainer(source as DependencyObject);
        var node = container is not null ? FolderTree.NodeFromContainer(container) : hasPosition ? null : FolderTree.SelectedNode;
        var parentId = (node?.Content as FolderNavigationItem)?.Id ?? Guid.Empty;
        var menu = FolderMenu(parentId);
        if (hasPosition) menu.ShowAt(FolderTreeArea, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(container ?? (FrameworkElement)FolderTree);
    }

    private MenuFlyout FolderMenu(Guid folder)
    {
        var model = ViewModel;
        var resources = Application.Current.Resources;
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)resources["ActionDropDownPresenterStyle"] };
        void Add(string label, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = label, Style = (Style)resources["ActionDropDownItemStyle"], IsEnabled = model.CanReceiveFiles && InputEnabled };
            item.Click += async (_, _) =>
            {
                if (ReferenceEquals(model, ViewModel) && InputEnabled && model.CanReceiveFiles && model.HasFolder(folder)) await action();
            };
            menu.Items.Add(item);
        }

        Add(folder == Guid.Empty ? "新建文件夹…" : "新建子文件夹…", () => FolderDialogAsync(async () =>
        {
            await FileCollectionDialogs.RenameOrCreateAsync(model, this, parent: folder);
            if (folderNodes.TryGetValue(folder, out var parent)) parent.IsExpanded = true;
        }));
        Add("添加文件…", () => ChooseAsync(FileInputSources.PickFilesAsync, folder));
        Add("添加文件夹…", () => ChooseAsync(FileInputSources.PickFolderAsync, folder));
        if (folder != Guid.Empty)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("重命名…", () => FolderDialogAsync(() => FileCollectionDialogs.RenameOrCreateAsync(model, this, folder)));
            Add("移动到…", () => FolderDialogAsync(() => FileCollectionDialogs.MoveAsync(model, model.GetFolderItems([folder]), this)));
            Add("解散分组，内容移到上一级…", () => DissolveFolderAsync(folder));
            Add("从列表移除…", () => RemoveItemsAsync(model.GetFolderItems([folder])));
        }
        return menu;
    }
}
