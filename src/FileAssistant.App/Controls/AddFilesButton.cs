using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileAssistant.App.Controls;

/// <summary>Shared file/folder entry point; the owning feature supplies the import operation.</summary>
public sealed class AddFilesButton : Button
{
    public static readonly DependencyProperty FolderOnlyProperty = DependencyProperty.Register(nameof(FolderOnly), typeof(bool), typeof(AddFilesButton),
        new PropertyMetadata(false, (owner, _) => ((AddFilesButton)owner).UpdateMode()));
    public bool FolderOnly { get => (bool)GetValue(FolderOnlyProperty); set => SetValue(FolderOnlyProperty, value); }
    private readonly TextBlock labelText = new();
    private readonly MenuFlyout addMenu;
    public event RoutedEventHandler? FilesRequested;
    public event RoutedEventHandler? FolderRequested;

    public AddFilesButton()
    {
        Style = (Style)Application.Current.Resources["WorkspaceToolbarButtonStyle"];
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 14 });
        label.Children.Add(labelText);
        Content = label;
        var menu = addMenu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["ActionDropDownPresenterStyle"]
        };
        var files = new MenuFlyoutItem { Style = (Style)Application.Current.Resources["ActionDropDownItemStyle"] };
        var folder = new MenuFlyoutItem { Style = files.Style };
        WorkspaceLocalization.SetText(files, "添加文件…");
        WorkspaceLocalization.SetText(folder, "添加文件夹…");
        files.Click += (_, args) => FilesRequested?.Invoke(this, args);
        folder.Click += (_, args) => FolderRequested?.Invoke(this, args);
        menu.Items.Add(files); menu.Items.Add(folder);
        Click += (_, args) => { if (FolderOnly) FolderRequested?.Invoke(this, args); };
        UpdateMode();
    }
    private void UpdateMode()
    {
        WorkspaceLocalization.SetText(labelText, FolderOnly ? "添加文件夹" : "添加文件");
        WorkspaceLocalization.SetName(this, FolderOnly ? "添加文件夹" : "添加文件或文件夹");
        WorkspaceLocalization.SetTip(this, FolderOnly ? "添加目标文件夹" : "添加文件或文件夹");
        Flyout = FolderOnly ? null : addMenu;
    }
}
