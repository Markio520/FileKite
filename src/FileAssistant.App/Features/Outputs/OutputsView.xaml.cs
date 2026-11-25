using FileAssistant.App.Controls;
using FileAssistant.App.Features.FileInput;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FileAssistant.App.Features.Outputs;

public sealed partial class OutputsView : UserControl
{
    public OutputsViewModel ViewModel => App.Outputs;
    public event EventHandler<FileInputTransferEventArgs>? FilesForConversionRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForCopyRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForNamingRequested;
    public OutputsView() { InitializeComponent(); Loaded += (_, _) => Inspector.ShowParameters(false, hideSingleTabHeader: true); }
    public void FocusQuery() => Query.Focus(FocusState.Programmatic);
    private void ToggleDetails_Click(object sender, RoutedEventArgs e) => DetailsHost.IsPaneOpen = !DetailsHost.IsPaneOpen;
    private void ToggleSources_Click(object sender, RoutedEventArgs e) => SourcesHost.IsPaneOpen = !SourcesHost.IsPaneOpen;
    private void Query_TextChanged(object sender, TextChangedEventArgs e) => ViewModel.SearchText = Query.Text;
    private void SourceTask_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListView)sender;
        if (list.SelectedItem is SelectionOption source) ViewModel.TaskId = source.Value;
        else if (list.Items.Count > 0) list.SelectedIndex = 0;
    }

    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!MarqueeSelection.GetIsSelecting(Files)) UpdateFileSelection(); }
    private void Files_MarqueeCommitted(object? sender, EventArgs e) => UpdateFileSelection();
    private void UpdateFileSelection() => ViewModel.SetSelection(Files.SelectedItems.Cast<OutputEntry>().ToArray());
    private async void Files_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Files;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is not ListViewItem { Content: OutputEntry entry }) continue;
            e.Handled = true;
            await FileActivation.OpenAsync(this, entry.File);
            break;
        }
    }
    private async void AddToStaging_Click(object sender, RoutedEventArgs e)
    {
        var report = await FileCollectionDialogs.ImportAsync(App.Workspace, Core.Features.FileCollections.FileCollectionBundle.FromFiles(ViewModel.SelectedFiles), this);
        if (report is not null) ViewModel.Feedback = report.Summary;
    }
    private void Convert_Click(object sender, RoutedEventArgs e) => FilesForConversionRequested?.Invoke(this, new(ViewModel.SelectedFiles));
    private void Copy_Click(object sender, RoutedEventArgs e) => FilesForCopyRequested?.Invoke(this, new(ViewModel.SelectedFiles));
    private void Rename_Click(object sender, RoutedEventArgs e) => FilesForNamingRequested?.Invoke(this, new(ViewModel.SelectedFiles));
    private void ConvertAll_Click(object sender, RoutedEventArgs e) { if (ViewModel.VisibleOutputFiles.Count > 0) FilesForConversionRequested?.Invoke(this, new(ViewModel.VisibleOutputFiles)); }
    private void CopyAll_Click(object sender, RoutedEventArgs e) { if (ViewModel.VisibleOutputFiles.Count > 0) FilesForCopyRequested?.Invoke(this, new(ViewModel.VisibleOutputFiles)); }
    private void RenameAll_Click(object sender, RoutedEventArgs e) { if (ViewModel.VisibleOutputFiles.Count > 0) FilesForNamingRequested?.Invoke(this, new(ViewModel.VisibleOutputFiles)); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Files.SelectedItems.Clear();
}
