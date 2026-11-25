using FileAssistant.App.Features.Workspace;
using FileAssistant.App.Resources;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static async Task CheckSelectionColors(FrameworkElement root, WorkspacePage workspace, ContentControl bar)
    {
        var chip = Elements(bar).OfType<Button>().Single(b => Equals(b.Content, "转格式"));
        var chipRoot = Elements(chip).OfType<Border>().Single(b => b.Name == "ChipRoot");
        var chipText = Elements(chip).OfType<ContentPresenter>().Single(c => c.Name == "ChipContent");
        var summary = ((StackPanel)bar.Content).Children.OfType<TextBlock>().First();
        var opening = workspace.OpenSettingsAsync(initialPage: "appearance");
        var dialog = await Opened(workspace, opening);
        var surfaces = new HashSet<Color>();
        var selected = new Dictionary<ElementTheme, Color>();

        foreach (var id in new[] { "sand", "frost", "linen", "studio", "charcoal", "ocean", "forest", "espresso" })
        {
            var palette = (WorkspacePalette)Application.Current.Resources["Palette_" + id];
            var theme = palette.Light ? ElementTheme.Light : ElementTheme.Dark;
            if (root.ActualTheme != theme)
            {
                InvokeButton(WithName<ToggleButton>(dialog, palette.Light ? "外观模式 · 浅色" : "外观模式 · 深色"));
                await Until(() => root.ActualTheme == theme, "Appearance mode did not switch.");
                await Settle(dialog);
            }
            InvokeButton(WithId<ToggleButton>(dialog, "Settings.Palette." + id));
            await Until(() => Preferences.Current.Palette == id && ColorOf(summary.Foreground) == palette.Text,
                "The visible selection summary did not adopt palette " + id);
            await Settle(bar);
            var surface = ColorOf(bar.Background);
            var normal = ColorOf(chipRoot.Background);
            Require(surfaces.Add(surface) && bar.Visibility == Visibility.Visible, "Selection bar failed to recolor in place: " + id);
            Require(ColorOf(chipText.Foreground) == palette.Text, "Wrong chip foreground: " + id);
            Require(VisualStateManager.GoToState(chip, "PointerOver", false), "Missing chip hover state.");
            var hover = ColorOf(chipRoot.Background);
            var minimumContrast = Math.Min(Contrast(palette.Text, surface, palette.Window),
                Math.Min(Contrast(palette.Text, normal, palette.Window), Contrast(ColorOf(chipText.Foreground), hover, palette.Window)));
            Require(normal != hover && minimumContrast >= 4.5, $"Unreadable or indistinguishable chip states: {id}, {minimumContrast:F2}");
            Require(VisualStateManager.GoToState(chip, "Normal", false), "Missing chip normal state.");
            evidence.Add(new { palette = id, surface = surface.ToString(), text = palette.Text.ToString(),
                chip = normal.ToString(), hover = hover.ToString(), border = ColorOf(bar.BorderBrush).ToString(), minimumContrast });
            if (id is "sand" or "charcoal") selected[theme] = surface;
            if (id is "linen" or "ocean") await SavePng(bar, "selection-" + id, minimumHeight: 24);
        }

        var mica = WithName<ToggleSwitch>(dialog, "云母半透明背景材质 (Mica)");
        mica.IsOn = false;
        await Until(() => !Preferences.Current.Mica && ColorOf(bar.Background).A == 255, "Solid material did not reach the selection bar.");
        mica.IsOn = true;
        await Until(() => Preferences.Current.Mica && ColorOf(bar.Background).A < 255, "Mica did not reach the selection bar.");
        // Put both modes on their default palettes before following the actual system mode.
        InvokeButton(WithId<ToggleButton>(dialog, "Settings.Palette.charcoal"));
        await Until(() => Preferences.Current.Palette == "charcoal", "Charcoal was not restored.");
        InvokeButton(WithName<ToggleButton>(dialog, "外观模式 · 跟随系统"));
        await Until(() => Preferences.Current.Mode == AppearanceMode.System && ColorOf(bar.Background) == selected[root.ActualTheme],
            "Selection color does not follow the effective system theme.");
        dialog.Hide();
        await opening;
        await Settle(root);
        SelectionButtonAtCenter(root, chip);
    }

    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    private static double Contrast(Color foreground, Color fill, Color backdrop)
    {
        static double Linear(double channel) => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        static double Luminance(double r, double g, double b) =>
            .2126 * Linear(r / 255) + .7152 * Linear(g / 255) + .0722 * Linear(b / 255);
        var alpha = fill.A / 255d;
        var bg = Luminance(fill.R * alpha + backdrop.R * (1 - alpha), fill.G * alpha + backdrop.G * (1 - alpha),
            fill.B * alpha + backdrop.B * (1 - alpha));
        var fg = Luminance(foreground.R, foreground.G, foreground.B);
        return (Math.Max(bg, fg) + .05) / (Math.Min(bg, fg) + .05);
    }

    private static async Task CheckOutputsNavigation(FrameworkElement root, WorkspacePage workspace)
    {
        var outputs = (ToggleButton)workspace.FindName("LeftOutputsNav");
        var files = (ToggleButton)workspace.FindName("LeftWorkspaceNav");
        var pill = (Border)workspace.FindName("LeftSlidingPill");
        var badge = (Border)workspace.FindName("LeftOutputsBadge");
        Require(ReferenceEquals(outputs.Style, files.Style) && outputs.IsChecked == true, "Outputs does not use the active primary navigation style.");
        bool Aligned()
        {
            var a = Bounds(outputs, workspace);
            var b = Bounds(pill, workspace);
            return pill.Opacity == 1 && Math.Abs(a.Y + a.Height / 2 - b.Y - b.Height / 2) < 1 && Math.Abs(a.X - b.X) < 1;
        }
        await Until(Aligned, "The accent indicator missed the pinned outputs row.");
        var collapse = (Button)workspace.FindName("RailCollapseButton");
        Require(Elements(outputs).Contains(badge) && ((TextBlock)badge.Child).Text == "3", "Output count is missing.");
        InvokeButton(collapse);
        await Until(() => !Elements(outputs).Contains(badge) && Aligned(), "Compact outputs row lost its indicator or still shows its count.");
        await ResizeWindow(root, 1250, 650);
        await Until(Aligned, "Resizing moved the footer away from its indicator.");
        InvokeButton(collapse);
        await Until(() => Elements(outputs).Contains(badge) && Aligned(), "Reopening the rail did not restore the badge and indicator.");
        await SavePng((FrameworkElement)workspace.FindName("LeftNavigationRail"), "outputs-navigation", minimumWidth: 46);
        await ResizeWindow(root, 1460, 820);
        evidence.Add(new { check = "pinned outputs shares the primary navigation style, indicator and compact behavior", count = 3 });
    }
}
