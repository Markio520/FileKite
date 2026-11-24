using FileAssistant.App.Controls;
using FileAssistant.App.Features.FileInput;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Search;

public sealed partial class SearchView : UserControl
{
    private bool active;
    private bool choosingFolder;
    private object? savedResults;
    private double savedVerticalOffset;
    private double savedHorizontalOffset;
    private int lastSearchSelCount;
    private SearchResultItem[]? indexSelection;
    private SearchResultItem? indexDetail;
    private double indexOffset;
    public SearchViewModel ViewModel => App.Search;
    public event EventHandler<FileInputTransferEventArgs>? FilesForConversionRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForCopyRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForNamingRequested;

    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += SearchState_Changed;
            ViewModel.IndexRowsChanging += SaveIndexSelection;
            ViewModel.IndexRowsChanged += RestoreIndexSelection;
            UpdateSearchChrome();
            if (active) StartLocationNavigation();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= SearchState_Changed;
            ViewModel.IndexRowsChanging -= SaveIndexSelection;
            ViewModel.IndexRowsChanged -= RestoreIndexSelection;
            StopLocationNavigation();
        };
    }
    public void Activate()
    {
        active = true;
        ViewModel.Activate();
        StartLocationNavigation();
        QueryBox.Focus(FocusState.Programmatic);
        // Restore after the collapsed page has been measured, only for the same result collection.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!active || !ReferenceEquals(savedResults, ViewModel.Results)) return;
            ResultsList.UpdateLayout();
            FindScrollViewer(ResultsList)?.ChangeView(null, savedVerticalOffset, null, true);
            ResultsViewport.ChangeView(savedHorizontalOffset, null, null, true);
        });
    }
    public void Deactivate()
    {
        if (!active) return;
        active = false;
        savedResults = ViewModel.Results;
        savedVerticalOffset = FindScrollViewer(ResultsList)?.VerticalOffset ?? 0;
        savedHorizontalOffset = ResultsViewport.HorizontalOffset;
        ViewModel.Deactivate();
        StopLocationNavigation();
    }
    public void FocusQuery() => QueryBox.Focus(FocusState.Programmatic);
    private void ToggleDetails_Click(object sender, RoutedEventArgs e) => ViewModel.ShowDetails = !ViewModel.ShowDetails;
    private void ToggleSources_Click(object sender, RoutedEventArgs e) => SourcesHost.IsPaneOpen = !SourcesHost.IsPaneOpen;

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (choosingFolder) return;
        choosingFolder = true;
        AddFolderButton.IsEnabled = false;
        try
        {
            var paths = await FileInputSources.PickFolderAsync();
            if (paths.Count > 0) ViewModel.UseFolder(paths[0]);
        }
        catch (Exception) { ViewModel.Feedback = "无法打开文件夹选择器，请重试，或从左侧目录导航选择文件夹。"; }
        finally
        {
            choosingFolder = false;
            AddFolderButton.IsEnabled = true;
            if (active) AddFolderButton.Focus(FocusState.Programmatic);
        }
    }
    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (indexSelection is not null || MarqueeSelection.GetIsSelecting(ResultsList)) return;
        UpdateResultSelection(e.AddedItems.OfType<SearchResultItem>().LastOrDefault());
    }

    private void SaveIndexSelection()
    {
        indexSelection = ResultsList.SelectedItems.OfType<SearchResultItem>().ToArray();
        indexDetail = ViewModel.SelectedItem;
        indexOffset = FindScrollViewer(ResultsList)?.VerticalOffset ?? 0;
    }

    private void RestoreIndexSelection()
    {
        if (indexSelection is null) return;
        try
        {
            if (indexSelection.Length > 0 || ResultsList.SelectedItems.Count > 0)
            {
                // WinUI can drop selected items on an ObservableCollection.Move.
                var wanted = indexSelection.ToHashSet();
                wanted.IntersectWith(ViewModel.Results);
                var selected = ResultsList.SelectedItems.OfType<SearchResultItem>().ToHashSet();
                foreach (var row in selected)
                    if (!wanted.Contains(row)) ResultsList.SelectedItems.Remove(row);
                foreach (var row in wanted)
                    if (!selected.Contains(row)) ResultsList.SelectedItems.Add(row);
                var detail = indexDetail is not null && wanted.Contains(indexDetail) ? indexDetail : wanted.FirstOrDefault();
                if (ViewModel.SelectedItem != detail || ViewModel.SelectionCount != wanted.Count || lastSearchSelCount != wanted.Count)
                    UpdateResultSelection(detail);
            }
            var scroll = FindScrollViewer(ResultsList);
            if (scroll is not null && Math.Abs(scroll.VerticalOffset - indexOffset) > 0.5)
                scroll.ChangeView(null, indexOffset, null, true);
        }
        finally { indexSelection = null; indexDetail = null; }
    }
    private void Results_MarqueeCommitted(object? sender, EventArgs e) => UpdateResultSelection(ResultsList.SelectedItems.OfType<SearchResultItem>().LastOrDefault());
    private void UpdateResultSelection(SearchResultItem? current = null)
    {
        var prev = lastSearchSelCount;
        ViewModel.SelectionCount = ResultsList.SelectedItems.Count;
        ViewModel.SelectedItem = current ?? ResultsList.SelectedItems.OfType<SearchResultItem>().FirstOrDefault();
        lastSearchSelCount = ViewModel.SelectionCount;
        WorkspaceMotion.AnimateSpringIsland(SearchFloatingIsland, ViewModel.HasSelection);
        if (lastSearchSelCount > 0 && lastSearchSelCount != prev)
            WorkspaceMotion.PlayPopBounce(SearchIslandSummaryText, 1.14f);
    }

    private async void QuickLookSearch_Click(object sender, RoutedEventArgs e) => await OpenSearchQuickLookAsync();

    public async Task RunShortcutAsync(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.QuickLook when ViewModel.CanUseResults:
            case ShortcutAction.Compare when ViewModel.CanUseResults:
                await OpenSearchQuickLookAsync(action == ShortcutAction.Compare);
                break;
            case ShortcutAction.Refresh:
                RefreshLocations();
                if (ViewModel.RefreshCommand.CanExecute(null)) await ViewModel.RefreshCommand.ExecuteAsync(null);
                break;
            case ShortcutAction.Filter:
                FocusQuery();
                break;
        }
    }

    private async Task OpenSearchQuickLookAsync(bool compare = false)
    {
        var selected = SelectedSearchFiles().Where(f => !f.IsFolder).ToArray();
        if (selected.Length > 0) await Preview.QuickLookDialog.ShowAsync(this, selected, 0, compare: compare);
    }
    private async void Add_Click(object sender, RoutedEventArgs e) => await ViewModel.AddSelectionAsync(ResultsList.SelectedItems.OfType<SearchResultItem>().ToArray());
    private SearchFile[] SelectedSearchFiles() => ResultsList.SelectedItems.OfType<SearchResultItem>().Select(item => item.File).ToArray();
    private void ConvertSelection_Click(object sender, RoutedEventArgs e)
    { if (SelectedSearchFiles() is { Length: > 0 } files) FilesForConversionRequested?.Invoke(this, new(files)); }
    private void RenameSelection_Click(object sender, RoutedEventArgs e)
    { if (SelectedSearchFiles() is { Length: > 0 } files) FilesForNamingRequested?.Invoke(this, new(files)); }
    private void CopySelectionToOrganize_Click(object sender, RoutedEventArgs e)
    { if (SelectedSearchFiles() is { Length: > 0 } files) FilesForCopyRequested?.Invoke(this, new(files)); }
    private void CopyPathSelection_Click(object sender, RoutedEventArgs e)
        => ViewModel.CopySelection(ResultsList.SelectedItems.OfType<SearchResultItem>().ToArray(), false);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ResultsList.SelectedItems.Clear();
    private void OpenSelected_Click(object sender, RoutedEventArgs e) { if (ViewModel.SelectedItem is { } item) ViewModel.OpenFile(item); }
    private void RevealSelected_Click(object sender, RoutedEventArgs e) { if (ViewModel.SelectedItem is { } item) ViewModel.OpenLocation(item); }
    private void ResultsHeader_SizeChanged(object sender, SizeChangedEventArgs e) => ResultsHeaderRow.Height = new GridLength(e.NewSize.Height);
    private void ResultsViewport_SizeChanged(object sender, SizeChangedEventArgs e) => ViewModel.ColumnWidths.FitViewport(e.NewSize.Width);
    private void Column_ResizeRequested(object? sender, double delta)
        => ViewModel.ColumnWidths.Resize((string)((FrameworkElement)sender!).Tag, delta);

    private ListViewItem? ResultContainer(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null && element != ResultsList)
        {
            if (element is ListViewItem container) return container;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void Results_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (!ViewModel.CanUseResults) return;
        var pointer = e.TryGetPosition(ResultsList, out var position);
        var container = ResultContainer(pointer ? e.OriginalSource : FocusManager.GetFocusedElement(XamlRoot));
        var target = container?.Content as SearchResultItem ?? (pointer ? null : ViewModel.SelectedItem);
        if (target is null || !ViewModel.Results.Contains(target)) return;
        if (!ResultsList.SelectedItems.Contains(target))
        {
            ResultsList.SelectedItems.Clear();
            ResultsList.SelectedItems.Add(target);
        }
        var selection = ResultsList.SelectedItems.OfType<SearchResultItem>().ToArray();
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Item(target.File.IsFolder ? "打开文件夹" : "打开文件", "\uE8E5", () => ViewModel.OpenFile(target), selection.Length == 1));
        menu.Items.Add(Item("打开所在文件夹", "\uED25", () => ViewModel.OpenLocation(target), selection.Length == 1));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("复制完整路径", "\uE8C8", () => ViewModel.CopySelection(selection, false)));
        menu.Items.Add(Item("复制文件名", "\uE8A5", () => ViewModel.CopySelection(selection, true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("加入暂存区", "\uE710", async () => await ViewModel.AddSelectionAsync(selection), ViewModel.CanAdd));
        menu.Items.Add(Item("用于格式转换", "\uE8AB", () => FilesForConversionRequested?.Invoke(this, new(selection.Select(item => item.File).ToArray()))));
        menu.Items.Add(Item("用于批量改名", "\uE70F", () => FilesForNamingRequested?.Invoke(this, new(selection.Select(item => item.File).ToArray()))));
        menu.Items.Add(Item("用于复制与整理", "\uE8C8", () => FilesForCopyRequested?.Invoke(this, new(selection.Select(item => item.File).ToArray()))));
        menu.Items.Add(Item("加入清理清单", "\uE74D", () => App.Cleanup.AddPaths(selection.Select(item => item.File.FullPath), navigate: true), !App.Cleanup.IsBusy));
        if (pointer) menu.ShowAt(ResultsList, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(container ?? (FrameworkElement)ResultsList);
        e.Handled = true;
    }

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!ViewModel.CanUseResults || ResultContainer(e.OriginalSource)?.Content is not SearchResultItem item) return;
        e.Handled = true;
        if (item.File.IsFolder) ViewModel.OpenFile(item);
        else await FileActivation.OpenAsync(this, item.File);
    }
    private void Open_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (ResultsList.SelectedItems.Count == 1 && ViewModel.SelectedItem is { } item) ViewModel.OpenFile(item);
        e.Handled = true;
    }
    private void SortHeader_Click(object sender, RoutedEventArgs e) => ViewModel.SortBy((SearchSort)int.Parse((string)((Button)sender).Tag));
    private void ClearDate_Click(object sender, RoutedEventArgs e)
    {
        if ((string)((Button)sender).Tag == "From") ViewModel.CustomDateFrom = null;
        else ViewModel.CustomDateThrough = null;
    }
    private void SelectAll_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { ResultsList.SelectAll(); args.Handled = true; }
    private void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { if (ViewModel.RefreshCommand.CanExecute(null)) ViewModel.RefreshCommand.Execute(null); RefreshLocations(); args.Handled = true; }
    private void Query_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) => ViewModel.SetInputComposition(true);
    private void Query_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) => ViewModel.SetInputComposition(false);
}
