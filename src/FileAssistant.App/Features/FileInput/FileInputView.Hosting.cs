using Microsoft.UI.Xaml;

namespace FileAssistant.App.Features.FileInput;

public sealed partial class FileInputView
{
    // Optional operation slots keep collection navigation and input commands in one shared workspace.
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(nameof(HeaderContent), typeof(UIElement), typeof(FileInputView), new PropertyMetadata(null, HostingChanged));
    public static readonly DependencyProperty AlternateHeaderActionsProperty = DependencyProperty.Register(nameof(AlternateHeaderActions), typeof(UIElement), typeof(FileInputView), new PropertyMetadata(null, HostingChanged));
    public static readonly DependencyProperty AlternateContentProperty = DependencyProperty.Register(nameof(AlternateContent), typeof(UIElement), typeof(FileInputView), new PropertyMetadata(null, HostingChanged));
    public static readonly DependencyProperty ShowAlternateContentProperty = DependencyProperty.Register(nameof(ShowAlternateContent), typeof(bool), typeof(FileInputView), new PropertyMetadata(false, HostingChanged));
    public static readonly DependencyProperty InputEnabledProperty = DependencyProperty.Register(nameof(InputEnabled), typeof(bool), typeof(FileInputView), new PropertyMetadata(true));
    public static readonly DependencyProperty OperationTitleProperty = DependencyProperty.Register(nameof(OperationTitle), typeof(string), typeof(FileInputView), new PropertyMetadata("", HostingChanged));
    public static readonly DependencyProperty AlternateFolderContentProperty = DependencyProperty.Register(nameof(AlternateFolderContent), typeof(UIElement), typeof(FileInputView), new PropertyMetadata(null, HostingChanged));
    public static readonly DependencyProperty ShowAlternateFoldersProperty = DependencyProperty.Register(nameof(ShowAlternateFolders), typeof(bool), typeof(FileInputView), new PropertyMetadata(false, HostingChanged));
    public static readonly DependencyProperty PathBarActionsProperty = DependencyProperty.Register(nameof(PathBarActions), typeof(UIElement), typeof(FileInputView), new PropertyMetadata(null, HostingChanged));
    public static readonly DependencyProperty CustomBodyContentProperty = DependencyProperty.Register(nameof(CustomBodyContent), typeof(UIElement), typeof(FileInputView), new PropertyMetadata(null, HostingChanged));
    public static readonly DependencyProperty ShowCustomBodyProperty = DependencyProperty.Register(nameof(ShowCustomBody), typeof(bool), typeof(FileInputView), new PropertyMetadata(false, HostingChanged));
    public UIElement? HeaderContent { get => (UIElement?)GetValue(HeaderContentProperty); set => SetValue(HeaderContentProperty, value); }
    public UIElement? AlternateHeaderActions { get => (UIElement?)GetValue(AlternateHeaderActionsProperty); set => SetValue(AlternateHeaderActionsProperty, value); }
    public UIElement? AlternateContent { get => (UIElement?)GetValue(AlternateContentProperty); set => SetValue(AlternateContentProperty, value); }
    public bool ShowAlternateContent { get => (bool)GetValue(ShowAlternateContentProperty); set => SetValue(ShowAlternateContentProperty, value); }
    public bool InputEnabled { get => (bool)GetValue(InputEnabledProperty); set => SetValue(InputEnabledProperty, value); }
    public string OperationTitle { get => (string)GetValue(OperationTitleProperty); set => SetValue(OperationTitleProperty, value); }
    public UIElement? AlternateFolderContent { get => (UIElement?)GetValue(AlternateFolderContentProperty); set => SetValue(AlternateFolderContentProperty, value); }
    public bool ShowAlternateFolders { get => (bool)GetValue(ShowAlternateFoldersProperty); set => SetValue(ShowAlternateFoldersProperty, value); }
    public UIElement? PathBarActions { get => (UIElement?)GetValue(PathBarActionsProperty); set => SetValue(PathBarActionsProperty, value); }
    public UIElement? CustomBodyContent { get => (UIElement?)GetValue(CustomBodyContentProperty); set => SetValue(CustomBodyContentProperty, value); }
    public bool ShowCustomBody { get => (bool)GetValue(ShowCustomBodyProperty); set => SetValue(ShowCustomBodyProperty, value); }
    private bool HasFolderSidebar => !ShowAlternateContent || ShowAlternateFolders && AlternateFolderContent is not null;
    private bool AcceptsFileInput => InputEnabled && !ShowAlternateContent;
    private static void HostingChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var view = (FileInputView)owner; view.UpdateHosting(); view.UpdateInspector();
    }
    private void UpdateHosting()
    {
        if (HeaderPresenter is null) return;
        HeaderPresenter.Content = HeaderContent;
        HeaderPresenter.Visibility = HeaderContent is null ? Visibility.Collapsed : Visibility.Visible;
        FilterSearchHost.Visibility = HeaderActionsHost.Visibility = FileSurfaceHost.Visibility = ShowAlternateContent ? Visibility.Collapsed : Visibility.Visible;
        AlternateHeaderActionsPresenter.Content = AlternateHeaderActions;
        AlternateHeaderActionsPresenter.Visibility = ShowAlternateContent && AlternateHeaderActions is not null ? Visibility.Visible : Visibility.Collapsed;
        AlternatePresenter.Content = AlternateContent;
        AlternatePresenter.Visibility = ShowAlternateContent ? Visibility.Visible : Visibility.Collapsed;
        AlternateFolderPresenter.Content = AlternateFolderContent;
        AlternateFolderPresenter.Visibility = ShowAlternateContent && HasFolderSidebar ? Visibility.Visible : Visibility.Collapsed;
        FolderTreeArea.Visibility = ShowAlternateContent ? Visibility.Collapsed : Visibility.Visible;
        NewFolderHeaderButton.Visibility = ShowAlternateContent ? Visibility.Collapsed : Visibility.Visible;
        Controls.WorkspaceLocalization.SetText(FolderSidebarCaption, ShowAlternateContent ? "创建位置" : "分组目录");
        PathBarActionsPresenter.Content = PathBarActions;
        PathBarActionsPresenter.Visibility = PathBarActions is null ? Visibility.Collapsed : Visibility.Visible;
        var customBodyVisible = !ShowAlternateContent && ShowCustomBody && CustomBodyContent is not null;
        ViewModeButtonsHost.Visibility = customBodyVisible ? Visibility.Collapsed : Visibility.Visible;
        CustomBodyPresenter.Content = CustomBodyContent;
        CustomBodyPresenter.Visibility = customBodyVisible ? Visibility.Visible : Visibility.Collapsed;
        FilesViewport.Visibility = customBodyVisible ? Visibility.Collapsed : Visibility.Visible;
        UpdateFoldersWidth();
        UpdateToolbarLayout();
    }
}
