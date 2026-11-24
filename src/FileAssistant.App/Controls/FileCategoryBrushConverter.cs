using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Controls;

public sealed class FileCategoryBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var tone = value is int n ? n : 0;
        var key = tone switch
        {
            1 => "WorkspaceCategoryFolderBrush",
            2 => "WorkspaceCategoryImageBrush",
            3 => "WorkspaceCategoryDocumentBrush",
            4 => "WorkspaceCategoryMediaBrush",
            5 => "WorkspaceCategoryArchiveBrush",
            _ => "WorkspaceSecondaryBrush"
        };
        return Application.Current.Resources[key] as Brush ?? new SolidColorBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => 0;
}
