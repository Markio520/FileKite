using FileAssistant.App.Features.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static async Task CheckScaledIsland(FrameworkElement root, float scale)
    {
        // A real WinUI XAML island can have independent DPI without modifying any monitor or user desktop setting.
        using var source = new DesktopWindowXamlSource();
        source.Initialize(Window.AppWindow.Id);
        var bridge = source.SiteBridge;
        bridge.OverrideScale = scale;
        var owner = new Grid { RequestedTheme = root.ActualTheme };
        source.Content = owner;
        bridge.MoveAndResize(new RectInt32(0, 0, 1510, 1000));
        bridge.Show();
        await Until(() => owner.IsLoaded && owner.XamlRoot is not null && Math.Abs(owner.XamlRoot.RasterizationScale - scale) < 0.01,
            "The private XAML host did not honor its requested rasterization scale.");
        var dialog = new SettingsDialog(owner, initialPage: "appearance");
        var opened = dialog.ShowAsync();
        try
        {
            await Until(() => SettingsDialog.IsOpen, "Scaled settings dialog did not open.");
            await Settle(dialog);
            var layout = Part<Grid>(dialog, "DialogLayout");
            Require(Math.Abs(layout.XamlRoot.RasterizationScale - scale) < 0.01, "Dialog and host rasterization scales differ.");
            var compact = Part<TextBox>(dialog, "CompactSearch").Visibility == Visibility.Visible;
            InspectPage(dialog, "appearance-scaled", "外观", "主导航栏样式", compact);
            await SavePng(layout, "appearance-scaled");
            bridge.MoveAndResize(new RectInt32(0, 0, 790, 820));
            await Settle(dialog);
            await Navigate(dialog, "general");
            InspectPage(dialog, "general-scaled-narrow", "常规", "语言与启动", true);
            await SavePng(layout, "general-scaled-narrow");
            evidence.Add(new { host = "DesktopWindowXamlSource", requestedScale = scale, actualScale = layout.XamlRoot.RasterizationScale,
                monitorScale = root.XamlRoot.RasterizationScale });
        }
        finally { dialog.Hide(); await opened; }
    }
}
