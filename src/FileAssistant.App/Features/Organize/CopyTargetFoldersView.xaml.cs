using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.Organize;

public sealed partial class CopyTargetFoldersView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(CopyViewModel), typeof(CopyTargetFoldersView), new PropertyMetadata(null));

    public CopyViewModel ViewModel
    {
        get => (CopyViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public CopyTargetFoldersView()
    {
        InitializeComponent();
    }

    private void GroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CopyTargetFolderGroup group } && group.HasChildren)
        {
            group.ToggleExpanded();
            ViewModel?.RecordGroupExpansion(group);
        }
    }
}
