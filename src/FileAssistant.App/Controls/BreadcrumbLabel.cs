using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Controls;

/// <summary>The native breadcrumb item handles navigation, overflow and keyboard focus.</summary>
public sealed class BreadcrumbLabel : Grid
{
    public BreadcrumbLabel()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Padding = new Thickness(4, 2, 4, 2);
        CornerRadius = new CornerRadius(5);
    }
}
