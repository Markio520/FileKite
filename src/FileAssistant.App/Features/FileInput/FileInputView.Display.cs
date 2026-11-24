using System.ComponentModel;
using FileAssistant.App.Features.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.FileInput;

public sealed partial class FileInputView
{
    private bool showTiles;
    private bool foldersOpen;
    private bool? userFoldersOpen;
    private bool switchingView;
    private WorkspaceViewModel? displayModel;
    private long cascadeUntilTicks;
    private int lastVisibleCount;
    private int lastIslandSelectionCount;

    private bool EffectiveFoldersOpen => HasFolderSidebar && (userFoldersOpen ?? (ShowAlternateContent
        ? wasCompact != true
        : ViewModel?.HasCustomFolders == true && wasCompact != true));

    private void AttachDisplayModel()
    {
        if (!IsLoaded || ReferenceEquals(displayModel, ViewModel)) return;
        DetachDisplayModel();
        displayModel = ViewModel;
        if (displayModel is null) return;
        userFoldersOpen = null;
        LoadPaneWidths(displayModel.Purpose);
        displayModel.PropertyChanged += DisplayModelChanged;
        displayModel.VisibleFilesRefreshed += RestoreVisibleSelection;
        lastVisibleCount = displayModel.VisibleFiles.Count;
        lastIslandSelectionCount = displayModel.SelectionCount;
        Controls.WorkspaceMotion.AnimateSpringIsland(FloatingActionIsland, displayModel.ShowFloatingIsland);
        ResetFolderNavigation();
        UpdateFolderNavigation();
        UpdateFoldersWidth();
    }

    private void DetachDisplayModel()
    {
        if (displayModel is not null) { displayModel.PropertyChanged -= DisplayModelChanged; displayModel.VisibleFilesRefreshed -= RestoreVisibleSelection; }
        displayModel = null;
    }

    private void DisplayModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedRows)) RestoreVisibleSelection(sender, EventArgs.Empty);
        if (e.PropertyName == nameof(WorkspaceViewModel.ShowFloatingIsland))
            Controls.WorkspaceMotion.AnimateSpringIsland(FloatingActionIsland, ViewModel.ShowFloatingIsland);
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectionCount))
        {
            var next = ViewModel.SelectionCount;
            if (next > 0 && next != lastIslandSelectionCount) Controls.WorkspaceMotion.PlayPopBounce(IslandSummaryText, 1.14f);
            lastIslandSelectionCount = next;
        }
        if (e.PropertyName == nameof(WorkspaceViewModel.CollectionRevision) ||
            e.PropertyName == nameof(WorkspaceViewModel.BatchSummary) && ViewModel.HasSamples)
        {
            var count = ViewModel.VisibleFiles.Count;
            if (count > lastVisibleCount) cascadeUntilTicks = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 2;
            lastVisibleCount = count;
            QueueFolderNavigationUpdate();
            UpdateFoldersWidth();
        }
    }

    private void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is null) return;
        if (showTiles) Controls.WorkspaceMotion.AttachHoverLift(args.ItemContainer, -3f);
        if (System.Diagnostics.Stopwatch.GetTimestamp() < cascadeUntilTicks && args.ItemIndex < 12)
            Controls.WorkspaceMotion.PlayCascadeItemEntrance(args.ItemContainer, args.ItemIndex);
    }

    private void RestoreVisibleSelection(object? sender, EventArgs e)
    {
        if (switchingView || ViewModel.IsRefreshingFiles) return;
        switchingView = true;
        try
        {
            var desired = ViewModel.VisibleFiles.Where(ViewModel.IsRowSelected).ToHashSet();
            var current = FileList.SelectedItems.OfType<WorkspaceFile>().ToHashSet();
            foreach (var old in FileList.SelectedItems.OfType<WorkspaceFile>().ToArray())
                if (!desired.Contains(old)) FileList.SelectedItems.Remove(old);
            foreach (var row in desired) if (!current.Contains(row)) FileList.SelectedItems.Add(row);
        }
        finally { switchingView = false; }
    }

    private void ToggleFolderDrawer_Click(object sender, RoutedEventArgs e) => SetFoldersOpen(!EffectiveFoldersOpen, explicitChoice: true);
    private void CollapseFolders_Click(object sender, RoutedEventArgs e) => SetFoldersOpen(false, explicitChoice: true);
    private void ExpandFolders_Click(object sender, RoutedEventArgs e) => SetFoldersOpen(true, explicitChoice: true);

    private void DrawerToggle_DragOver(object sender, DragEventArgs e)
    {
        if (!EffectiveFoldersOpen && CanDrop(e.DataView, Guid.Empty)) SetFoldersOpen(true, explicitChoice: true);
        FolderTree_DragOver(sender, e);
    }

    private void SetFoldersOpen(bool open, bool explicitChoice = false)
    {
        if (explicitChoice) userFoldersOpen = open;
        foldersOpen = EffectiveFoldersOpen;
        UpdateFoldersWidth();
    }

    private void CollapseInspector_Click(object sender, RoutedEventArgs e) => DetailsHost.IsPaneOpen = false;
    private void ExpandInspector_Click(object sender, RoutedEventArgs e) => DetailsHost.IsPaneOpen = true;
    private void ListMode_Click(object sender, RoutedEventArgs e) => SetFileView(false);
    public void ShowListView() => SetFileView(false);
    private void TileMode_Click(object sender, RoutedEventArgs e) => SetFileView(true);

    private void SetFileView(bool tiles)
    {
        var template = (DataTemplate)Resources[tiles ? "FileTileTemplate" : "FileTableTemplate"];
        ListModeButton.IsChecked = !tiles;
        TileModeButton.IsChecked = tiles;
        if (showTiles == tiles && ReferenceEquals(FileList.ItemTemplate, template)) return;
        var selection = FileList.SelectedItems.OfType<WorkspaceFile>().ToArray();
        switchingView = true;
        try
        {
            showTiles = tiles;
            FilesHeader.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
            FilesHeaderRow.Height = tiles ? new GridLength(0) : GridLength.Auto;
            FilesViewport.HorizontalScrollMode = tiles ? ScrollMode.Disabled : ScrollMode.Enabled;
            FilesViewport.HorizontalScrollBarVisibility = tiles ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            TableContent.MinWidth = tiles ? 0 : ColumnWidths.MinimumTableWidth;
            FileList.ItemTemplate = template;
            FileList.ItemsPanel = (ItemsPanelTemplate)Resources[tiles ? "FileTilePanel" : "FileTablePanel"];
            FileList.ItemContainerStyle = (Style)Application.Current.Resources[tiles ? "WorkspaceTileItemStyle" : "WorkspaceListItemStyle"];
            foreach (var item in selection)
                if (FileList.Items.Contains(item) && !FileList.SelectedItems.Contains(item)) FileList.SelectedItems.Add(item);
        }
        finally { switchingView = false; }
        DispatcherQueue.TryEnqueue(UpdateTileWidth);
    }

    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileWidth();
    private void UpdateTileWidth()
    {
        if (!showTiles || FileList.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        var width = Math.Max(1, FileList.ActualWidth - 18);
        var columns = Math.Max(1, (int)(width / 184));
        panel.ItemWidth = Math.Floor(width / columns);
        panel.ItemHeight = ViewModel.Purpose == WorkspaceInputPurpose.Rename ? ViewModel.VisibleFiles.Any(x => x.NamePreview.CanEditName) ? 280 : 234
            : ViewModel.Purpose is WorkspaceInputPurpose.Copy or WorkspaceInputPurpose.Conversion ? 234 : 192;
    }
}
