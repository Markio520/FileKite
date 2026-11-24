using FileAssistant.Core.Abstractions;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.Workspace;

public sealed partial class FileInspector : UserControl
{
    private readonly IFileInteraction fileInteraction = new WindowsFileInteraction();

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WorkspaceViewModel), typeof(FileInspector), new PropertyMetadata(null));

    public WorkspaceViewModel ViewModel
    {
        get => (WorkspaceViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public FileInspector() => InitializeComponent();

    public void ShowParameters(bool show, UIElement? operationContent = null, bool hideSingleTabHeader = false)
    {
        CustomParameters.Content = operationContent;
        CustomParameters.Visibility = operationContent is null ? Visibility.Collapsed : Visibility.Visible;
        // Pivot still renders a collapsed item's header; remove the unavailable tab instead.
        if (!show) InspectorTabs.Items.Remove(ParametersTab);
        else if (!InspectorTabs.Items.Contains(ParametersTab)) InspectorTabs.Items.Add(ParametersTab);
        InspectorTabs.SelectedIndex = show ? 1 : 0;
        var single = !show && hideSingleTabHeader;
        if (single)
        {
            DetailsTab.Content = null;
            SingleFilePresenter.Content = DetailsContent;
        }
        else
        {
            SingleFilePresenter.Content = null;
            DetailsTab.Content = DetailsContent;
        }
        InspectorTabs.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        SingleFilePresenter.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedFile is { IsCollectionFolder: true, FolderId: { } folderId })
        {
            ViewModel.EnterFolder(folderId);
            return;
        }
        if (ViewModel?.PreviewFile is not { } file) return;
        try { fileInteraction.Open(file); }
        catch (FileInteractionException error) { ViewModel.InputFeedback = error.Message; }
    }

    private void RevealPreview_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.PreviewFile is not { } file) return;
        try { fileInteraction.Reveal(file); }
        catch (FileInteractionException error) { ViewModel.InputFeedback = error.Message; }
    }

    private void CopySelectedPaths_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var paths = ViewModel.SelectedRows.Count > 0
            ? string.Join(Environment.NewLine, ViewModel.SelectedRows.Select(row => row.FullPath))
            : ViewModel.PreviewPath;
        if (string.IsNullOrWhiteSpace(paths)) return;
        try
        {
            fileInteraction.CopyText(paths);
            ViewModel.InputFeedback = "已复制完整路径。";
        }
        catch (FileInteractionException error) { ViewModel.InputFeedback = error.Message; }
    }
}
