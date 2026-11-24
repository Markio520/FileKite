using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FileAssistant.App.Features.Settings;

internal static class AppIconAssets
{
    internal static string Ico(string id) => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcons", AppIconCatalog.Normalize(id) + ".ico");

    internal static void SetImage(Image image, string id)
    {
        var uri = new Uri("ms-appx:///Assets/AppIcons/" + AppIconCatalog.Normalize(id) + ".png");
        if (image.Source is not BitmapImage bitmap || bitmap.UriSource != uri)
            image.Source = new BitmapImage(uri);
    }
}
