using FileAssistant.App.Controls;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.FileCollections;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace FileAssistant.App.Features.FileInput;

public sealed class FileInputTransferEventArgs(IReadOnlyList<SearchFile> files) : EventArgs
{
    public IReadOnlyList<SearchFile> Files { get; } = files;
    public FileCollectionBundle Bundle { get; private init; } = FileCollectionBundle.FromFiles(files);
    public FileInputTransferEventArgs(FileCollectionBundle bundle) : this(bundle.Files) { Bundle = bundle; }
}

public sealed partial class FileInputView : UserControl
{
    private bool choosing;
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WorkspaceViewModel), typeof(FileInputView),
        new PropertyMetadata(null, (owner, _) => { var view = (FileInputView)owner; view.Bindings.Update(); view.UpdateInspector(); view.AttachDisplayModel(); }));
    public WorkspaceViewModel ViewModel
    {
        get => (WorkspaceViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
    public static readonly DependencyProperty OperationContentProperty = DependencyProperty.Register(nameof(OperationContent), typeof(UIElement),
        typeof(FileInputView), new PropertyMetadata(null, (owner, _) => ((FileInputView)owner).UpdateInspector()));
    public UIElement? OperationContent { get => (UIElement?)GetValue(OperationContentProperty); set => SetValue(OperationContentProperty, value); }
    public event EventHandler? SearchRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForCopyRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForConversionRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForNamingRequested;

    public FileInputView()
    {
        InitializeComponent();
        // Item containers handle selection gestures; navigation must also receive handled double taps.
        FileList.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(Files_DoubleTapped), handledEventsToo: true);
        Loaded += (_, _) => { App.PaneWidths.Reset += ResetWidths; UpdateHosting(); UpdateInspector(); AttachDisplayModel(); SetFileView(showTiles); };
        Unloaded += (_, _) => { App.PaneWidths.Reset -= ResetWidths; DetachDisplayModel(); };
        KeyDown += Input_KeyDown;
    }

    private void UpdateInspector()
    {
        if (!IsLoaded || ViewModel is null) return;
        var custom = OperationContent is not null;
        Inspector.ShowParameters(ViewModel.IsFunctionInput, hideSingleTabHeader: true);
        Inspector.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        OperationPresenter.Content = OperationContent;
        OperationPresenter.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        Controls.WorkspaceLocalization.SetText(InspectorTitle, OperationTitle.Length > 0 ? OperationTitle : ViewModel.Purpose == WorkspaceInputPurpose.Conversion ? "转换设置" : ViewModel.IsFunctionInput ? "复制设置" : "文件信息");
    }

    private void ResetWidths(object? sender, EventArgs e)
    {
        preferredFoldersWidth = null;
        UpdateFoldersWidth();
        ColumnWidths.Reset(FilesViewport.ActualWidth);
    }

    public void FocusQuery() => FilterSearch.FocusQuery();
    private FileInputColumnWidths ColumnWidths => (FileInputColumnWidths)Resources["FileColumns"];
    private void FilesViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ColumnWidths.FitViewport(e.NewSize.Width);
        if (!showTiles) TableContent.MinWidth = ColumnWidths.MinimumTableWidth;
        UpdateTileWidth();
    }
    private void FilesHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!showTiles) FilesHeaderRow.Height = new GridLength(e.NewSize.Height);
    }
    private void Column_ResizeRequested(object? sender, double delta)
    {
        ColumnWidths.Resize((string)((FrameworkElement)sender!).Tag, delta);
        if (!showTiles) TableContent.MinWidth = ColumnWidths.MinimumTableWidth;
    }

    private async void PickFiles_Click(object sender, RoutedEventArgs e) => await ChooseAsync(FileInputSources.PickFilesAsync);
    private async void PickFolder_Click(object sender, RoutedEventArgs e) => await ChooseAsync(FileInputSources.PickFolderAsync);
    private async Task ChooseAsync(Func<Task<IReadOnlyList<string>>> pick, Guid? folder = null)
    {
        if (choosing || !AcceptsFileInput || !ViewModel.CanReceiveFiles) return;
        choosing = true;
        var target = ViewModel;
        var destination = folder ?? target.CurrentFolderId;
        try
        {
            var paths = await pick();
            if (!AcceptsFileInput || !target.CanReceiveFiles) return;
            if (paths.Count > 0) await target.ImportPathsAsync(paths, destination);
        }
        catch (Exception) { target.InputFeedback = "无法打开文件选择器，请重试，或直接拖入文件。"; }
        finally { choosing = false; }
    }

    private async Task PasteAsync()
    {
        if (choosing || !AcceptsFileInput || !ViewModel.CanReceiveFiles) return;
        var target = ViewModel;
        var destination = target.CurrentFolderId;
        try
        {
            var paths = await FileInputSources.ReadAsync(Clipboard.GetContent());
            if (!AcceptsFileInput || !target.CanReceiveFiles) return;
            if (paths.Count == 0) target.InputFeedback = "剪贴板中没有可添加的文件。请复制文件，或每行一个完整文件路径。";
            else await target.ImportPathsAsync(paths, destination);
        }
        catch (Exception) { target.InputFeedback = "无法读取剪贴板，请重新复制文件后重试。"; }
    }

    private async void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!AcceptsFileInput || e.Key is not (VirtualKey.V or VirtualKey.Z) || Visibility != Visibility.Visible ||
            !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) return;
        // Text inputs keep their ordinary paste and undo behavior.
        if (EditingText()) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Z) { if (!choosing && ViewModel.CanUndo) ViewModel.UndoCommand.Execute(null); }
        else await PasteAsync();
    }

    private bool EditingText()
    {
        if (XamlRoot is null) return false;
        for (var focus = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focus is not null; focus = VisualTreeHelper.GetParent(focus))
            if (focus is TextBox or RichEditBox or PasswordBox) return true;
        return false;
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
        SetEmptyDropFan(true);
        CollectionDragOver(e, ViewModel.CurrentFolderId);
    }

    private void Files_DragLeave(object sender, DragEventArgs e) => SetEmptyDropFan(false);

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        SetEmptyDropFan(false);
        await CollectionDropAsync(e, ViewModel.CurrentFolderId);
    }

    private void EmptyState_PointerEntered(object sender, PointerRoutedEventArgs e) => SetEmptyDropFan(true);
    private void EmptyState_PointerExited(object sender, PointerRoutedEventArgs e) => SetEmptyDropFan(false);

    private void SetEmptyDropFan(bool fanned)
    {
        if (EmptyStateCard is null) return;
        WorkspaceMotion.AnimateWidth(DispatcherQueue, EmptyFanLeftRotate.Angle, fanned ? -21 : -11, 220, a =>
        {
            var t = Math.Clamp((Math.Abs(a) - 11.0) / 10.0, 0, 1);
            EmptyFanLeftRotate.Angle = -11 - 10 * t;
            EmptyFanLeftTranslate.X = -15 - 11 * t;
            EmptyFanLeftTranslate.Y = 4 - 6 * t;
            EmptyFanRightRotate.Angle = 11 + 10 * t;
            EmptyFanRightTranslate.X = 15 + 11 * t;
            EmptyFanRightTranslate.Y = 4 - 6 * t;
            EmptyFanCenterTranslate.Y = -5 * t;
        });
        WorkspaceMotion.AnimateScale(EmptyFanCenter, fanned ? 1.07f : 1f, 200);
        if (Application.Current.Resources.TryGetValue(fanned ? "WorkspaceAccentBrush" : "WorkspaceBorderBrush", out var b) && b is Brush br)
            EmptyStateCard.BorderBrush = br;
    }

    private async void QuickLook_Click(object sender, RoutedEventArgs e) => await OpenQuickLookAsync();

    internal async Task OpenQuickLookAsync(bool compare = false)
    {
        var format = (Core.Engines.ConversionTarget)App.Conversion.TargetFormatValue;
        (Core.Engines.ConversionTarget Target, int Quality)? conversion = compare &&
            ViewModel.Purpose == WorkspaceInputPurpose.Conversion && App.Conversion.ShowQuality
            ? (format, (int)App.Conversion.Quality) : null;
        var selected = FileList.SelectedItems.OfType<WorkspaceFile>().Where(f => !f.IsCollectionFolder).Select(f => f.Source).OfType<SearchFile>().ToArray();
        if (selected.Length > 0)
        {
            await Preview.QuickLookDialog.ShowAsync(this, selected, 0, compare, conversion);
            return;
        }
        var allVisible = ViewModel.VisibleFiles.Where(f => !f.IsCollectionFolder).Select(f => f.Source).OfType<SearchFile>().ToArray();
        if (allVisible.Length > 0) await Preview.QuickLookDialog.ShowAsync(this, allVisible, 0, compare, conversion);
    }

    private void SendSelected_Click(object sender, RoutedEventArgs e) => SendToCopy(ViewModel.ExportSelection());
    private void SendAll_Click(object sender, RoutedEventArgs e) => SendToCopy(ViewModel.ExportCurrentFolder());
    private void ConvertSelected_Click(object sender, RoutedEventArgs e) => SendToConversion(ViewModel.ExportSelection());
    private void ConvertAll_Click(object sender, RoutedEventArgs e) => SendToConversion(ViewModel.ExportCurrentFolder());
    private void RenameSelected_Click(object sender, RoutedEventArgs e) => SendToNaming(ViewModel.ExportSelection());
    private void RenameAll_Click(object sender, RoutedEventArgs e) => SendToNaming(ViewModel.ExportCurrentFolder());
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => FileList.SelectedItems.Clear();
    private void SendToConversion(FileCollectionBundle bundle)
    { if (bundle.HasItems && ViewModel.CanReceiveFiles) FilesForConversionRequested?.Invoke(this, new(bundle)); }
    private void SendToCopy(FileCollectionBundle bundle)
    {
        if (bundle.HasItems && ViewModel.CanReceiveFiles) FilesForCopyRequested?.Invoke(this, new(bundle));
    }
    private void SendToNaming(FileCollectionBundle bundle)
    {
        if (bundle.HasItems && ViewModel.CanReceiveFiles) FilesForNamingRequested?.Invoke(this, new(bundle));
    }

    private async void AddToStaging_Click(object sender, RoutedEventArgs e)
    {
        if (choosing || !ViewModel.CanExportSelection) return;
        choosing = true;
        try
        {
            var report = await FileCollectionDialogs.ImportAsync(App.Workspace, ViewModel.ExportSelection(), this);
            if (report is not null) { ViewModel.ImportReport = report; ViewModel.InputFeedback = $"加入暂存区：{report.Summary}"; }
        }
        catch (Exception) { ViewModel.InputFeedback = "无法打开目标文件夹选择窗口，请稍后重试。"; }
        finally { choosing = false; }
    }

    private async void ImportFromStaging_Click(object sender, RoutedEventArgs e)
    {
        if (choosing || !AcceptsFileInput || !ViewModel.CanReceiveFiles) return;
        choosing = true;
        try
        {
            await FileCollectionDialogs.ImportFromAsync(App.Workspace, ViewModel, this);
        }
        catch (Exception ex) { ViewModel.InputFeedback = "暂时无法打开导入窗口：" + ex.Message; }
        finally { choosing = false; }
    }

    private async void QuickImportFromStaging_Requested(object? sender, StagingQuickImportEventArgs e)
    {
        if (choosing || !AcceptsFileInput || !ViewModel.CanReceiveFiles || !e.Bundle.HasItems) return;
        choosing = true;
        try
        {
            var report = await ViewModel.ImportBundleAsync(e.Bundle, ViewModel.CurrentFolderId, preserveFolders: true);
            if (!string.IsNullOrWhiteSpace(report.Summary))
                ViewModel.InputFeedback = $"从「{e.SourceLabel}」快捷导入：{report.Summary}";
        }
        catch (Exception) { ViewModel.InputFeedback = "快捷导入失败，请稍后重试。"; }
        finally { choosing = false; }
    }

    private async void ImportDetails_Click(object sender, RoutedEventArgs e)
    {
        if (choosing || !ViewModel.HasFooterMessage) return;
        choosing = true;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            foreach (var message in new[] { ViewModel.InputFeedback, ViewModel.PersistenceMessage, ViewModel.UndoMessage }.Where(message => !string.IsNullOrWhiteSpace(message)))
                content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (ViewModel.HasImportDetails)
                content.Children.Add(new ListView { ItemsSource = ViewModel.ImportReport.Items, SelectionMode = ListViewSelectionMode.None,
                    MaxHeight = 300, ItemTemplate = (DataTemplate)Resources["ImportResultTemplate"] });
            await DialogDismiss.Attach(new ContentDialog { Title = "操作明细", XamlRoot = XamlRoot, RequestedTheme = ActualTheme, CloseButtonText = "关闭",
                Content = new ScrollViewer { Content = content, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }).ShowAsync();
        }
        catch (Exception) { ViewModel.InputFeedback = "暂时无法打开明细，请关闭其他弹窗后重试。"; }
        finally { choosing = false; }
    }

    private void FindFiles_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke(this, EventArgs.Empty);
    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<WorkspaceSortField>(tag, out var field))
            ViewModel?.ToggleSort(field);
    }
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarqueeSelection.GetIsSelecting(FileList)) return;
        UpdateFileSelection(e.AddedItems.OfType<WorkspaceFile>().LastOrDefault());
    }
    private void Files_MarqueeCommitted(object? sender, EventArgs e) => UpdateFileSelection(FileList.SelectedItems.OfType<WorkspaceFile>().LastOrDefault());
    private void UpdateFileSelection(WorkspaceFile? current = null)
    {
        if (switchingView || ViewModel is null || ViewModel.IsRefreshingFiles) return;
        ViewModel.SetVisibleSelection(FileList.SelectedItems.OfType<WorkspaceFile>().ToArray(),
            current ?? FileList.SelectedItems.OfType<WorkspaceFile>().FirstOrDefault());
    }
    private async void Remove_Click(object sender, RoutedEventArgs e) => await RemoveSelectionAsync();
    private async void Remove_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (EditingText()) return; args.Handled = true; await RemoveSelectionAsync(); }
    private void SelectAll_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (EditingText()) return; FileList.SelectAll(); args.Handled = true; }
    private Task RemoveSelectionAsync() => RemoveItemsAsync(FileList.SelectedItems.OfType<WorkspaceFile>().ToArray());

    private async Task RemoveItemsAsync(IReadOnlyList<WorkspaceFile> items)
    {
        if (!ViewModel.CanEditList || choosing || items.Count == 0) return;
        choosing = true;
        try
        {
            if (App.Preferences.Current.ConfirmNonemptyGroup && items.Any(item => item.FolderId is { } id && ViewModel.FolderHasContents(id)))
            {
                var count = ViewModel.ExportItems(items).Files.Count;
                var dialog = FileCollectionDialogs.Dialog(this, "从列表移除文件夹及内容？",
                    new TextBlock { Text = $"将移出所选文件夹及其中 {count:N0} 项。原文件和其他功能中的列表保留，可撤销本次操作。", TextWrapping = TextWrapping.Wrap }, "从列表移除");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            ViewModel.RemoveFromList(items);
        }
        catch (Exception) { ViewModel.InputFeedback = "无法移出这些项目，请刷新列表后重试。"; }
        finally { choosing = false; }
    }
}
