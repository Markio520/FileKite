using System;
using System.Linq;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace FileAssistant.App.Resources;

internal static class WorkspaceMaterials
{
    private static bool SystemUsesLight()
    {
        try
        {
            var bg = new UISettings().GetColorValue(UIColorType.Background);
            return bg.R + bg.G + bg.B > 382;
        }
        catch
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Light;
        }
    }

    public static bool IsActiveLightTheme(AppSettings s) =>
        s.Mode == AppearanceMode.Light || (s.Mode == AppearanceMode.System && SystemUsesLight());

    public static void Apply()
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        ResourceDictionary Resource(string name) => resources.First(d => d.Source?.OriginalString.EndsWith("/" + name + ".xaml", StringComparison.OrdinalIgnoreCase) == true);
        var xamlControls = resources.OfType<XamlControlsResources>().FirstOrDefault();
        var palette = Resource("ThemePalette");
        var theme = Resource("WorkspaceTheme");
        var materials = Resource("WorkspaceMaterials");
        var s = App.Preferences.Current;
        var selected = (WorkspacePalette)palette["Palette_" + s.Palette];
        var activeLight = IsActiveLightTheme(s);

        Color activeText = default, activeSecondary = default, activeAccent = default, activeOnAccent = default;
        Color activeRowHover = default, activeNavSelected = default, activeRowSelected = default, activeSelectionHover = default;

        foreach (var name in new[] { "Light", "Dark" })
        {
            var light = name == "Light";
            var p = selected.Light == light ? selected : (WorkspacePalette)palette[light ? "Palette_sand" : "Palette_charcoal"];
            var isDefaultPalette = (light && p == palette["Palette_sand"]) || (!light && p == palette["Palette_charcoal"]);
            var colors = (ResourceDictionary)palette.ThemeDictionaries[name];
            var surfaces = (ResourceDictionary)materials.ThemeDictionaries[name];
            var brushes = (ResourceDictionary)theme.ThemeDictionaries[name];

            var borderSolid = isDefaultPalette
                ? (light ? Rgb(0xDE, 0xDF, 0xD5) : Rgb(0x34, 0x38, 0x42))
                : Mix(p.Window, p.Text, light ? .13 : .11);
            var divider = isDefaultPalette
                ? (light ? Rgb(0xDA, 0xDA, 0xD1) : Rgb(0x3A, 0x3E, 0x46))
                : Mix(p.Card, p.Text, light ? .12 : .11);
            var subtleSolid = isDefaultPalette
                ? (light ? Rgb(0xF3, 0xF3, 0xEE) : Rgb(0x19, 0x1C, 0x22))
                : Mix(p.Window, p.Rail, .55);
            var opField = isDefaultPalette
                ? (light ? Rgb(0xE6, 0xE5, 0xDD) : Rgb(0x28, 0x2C, 0x33))
                : Mix(p.Card, p.Text, light ? .06 : .045);
            var opFieldHover = isDefaultPalette
                ? (light ? Rgb(0xEC, 0xEB, 0xE4) : Rgb(0x32, 0x37, 0x40))
                : Mix(p.Card, p.Text, light ? .04 : .085);

            var rowHover = isDefaultPalette
                ? (light ? Rgb(0xEF, 0xEF, 0xE7) : Rgb(0x26, 0x2A, 0x33))
                : (light ? Mix(p.Card, p.Rail, .60) : Mix(p.Card, p.Text, .065));
            var selectionHover = isDefaultPalette
                ? (light ? Rgb(0xDC, 0xE0, 0xD3) : Rgb(0x3E, 0x43, 0x50))
                : (light ? Mix(p.Rail, p.Accent, .13) : Mix(p.Card, p.Text, .14));
            var navSelected = isDefaultPalette
                ? (light ? Rgb(0xE1, 0xE3, 0xD8) : Rgb(0x2E, 0x33, 0x3E))
                : (light ? Mix(p.Rail, p.Accent, .07) : Mix(Mix(p.Card, p.Text, .085), p.Accent, .05));
            var rowSelected = isDefaultPalette
                ? (light ? Rgb(0xE4, 0xE6, 0xDC) : Rgb(0x33, 0x37, 0x42))
                : (light ? Mix(p.Rail, p.Accent, .08) : Mix(Mix(p.Card, p.Text, .105), p.Accent, .06));

            if (light == activeLight)
            {
                activeText = p.Text;
                activeSecondary = p.Secondary;
                activeAccent = p.Accent;
                activeOnAccent = p.OnAccent;
                activeRowHover = rowHover;
                activeNavSelected = navSelected;
                activeRowSelected = rowSelected;
                activeSelectionHover = selectionHover;
            }

            var accentText = isDefaultPalette
                ? (light ? Rgb(0x29, 0x2E, 0x25) : Rgb(0xEC, 0x70, 0x68))
                : (light ? p.Accent : Mix(p.Accent, Rgb(0xFF, 0xFF, 0xFF), .24));
            var accentTertiary = isDefaultPalette
                ? (light ? Rgb(0x1D, 0x22, 0x1A) : Rgb(0xA6, 0x3B, 0x35))
                : Mix(p.Accent, p.Rail, .18);

            var bgSolid = isDefaultPalette ? (light ? Rgb(0xEE, 0xEE, 0xE8) : Rgb(0x16, 0x18, 0x1D)) : p.Window;
            var surfaceSolid = isDefaultPalette ? (light ? Rgb(0xFD, 0xFD, 0xF9) : Rgb(0x1D, 0x20, 0x27)) : p.Card;
            var popupTint = isDefaultPalette ? (light ? Rgb(0xF3, 0xF3, 0xEE) : Rgb(0x2E, 0x36, 0x42)) : Mix(p.Window, p.Card, .65);
            var dialogFallback = isDefaultPalette ? (light ? Rgb(0xF5, 0xF5, 0xF0) : Rgb(0x24, 0x2C, 0x36)) : Mix(p.Window, p.Card, light ? .5 : .6);

            var windowOverlay = !s.Mica ? bgSolid
                : isDefaultPalette ? (light ? Color.FromArgb(0xEA, 0xF2, 0xF1, 0xEC) : Color.FromArgb(0x66, 0x16, 0x18, 0x1D))
                : WithAlpha(p.Window, light ? (byte)0xEA : (byte)0x68);
            var glassContent = !s.Mica ? surfaceSolid
                : isDefaultPalette ? (light ? Color.FromArgb(0xF5, 0xFC, 0xFB, 0xF7) : Color.FromArgb(0xEC, 0x1B, 0x1F, 0x26))
                : WithAlpha(Mix(p.Window, p.Card, .35), light ? (byte)0xF5 : (byte)0xEC);
            var glassPane = !s.Mica ? p.Rail
                : isDefaultPalette ? (light ? Color.FromArgb(0xDC, 0xF2, 0xF1, 0xEA) : Color.FromArgb(0x9A, 0x22, 0x28, 0x32))
                : WithAlpha(Mix(p.Rail, p.Card, .45), light ? (byte)0xDC : (byte)0x9A);
            var glassChrome = !s.Mica ? p.Rail
                : isDefaultPalette ? (light ? Color.FromArgb(0x70, 0xFA, 0xFA, 0xF6) : Color.FromArgb(0x34, 0x38, 0x43, 0x50))
                : WithAlpha(p.Card, light ? (byte)0x70 : (byte)0x38);
            var glassInset = !s.Mica ? subtleSolid
                : isDefaultPalette ? (light ? Color.FromArgb(0xB8, 0xEC, 0xEC, 0xE5) : Color.FromArgb(0x98, 0x19, 0x1C, 0x22))
                : WithAlpha(subtleSolid, light ? (byte)0xC4 : (byte)0x98);
            var glassCard = !s.Mica ? p.Card
                : isDefaultPalette ? (light ? Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF))
                : (light ? Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF) : WithAlpha(Mix(p.Card, Rgb(0xFF, 0xFF, 0xFF), .12), 0xB8));
            var glassControl = !s.Mica ? opField
                : isDefaultPalette ? (light ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x3E, 0x4A, 0x54, 0x62))
                : (light ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : WithAlpha(Mix(p.Card, p.Text, .14), 0x62));
            var glassField = !s.Mica ? (light ? p.Card : subtleSolid)
                : isDefaultPalette ? (light ? Color.FromArgb(0xFF, 0xFA, 0xFA, 0xF6) : Color.FromArgb(0xF0, 0x23, 0x29, 0x32))
                : WithAlpha(light ? p.Card : Mix(p.Window, p.Card, .55), light ? (byte)0xFF : (byte)0xF0);
            var glassBorder = !s.Mica ? borderSolid
                : isDefaultPalette ? (light ? Color.FromArgb(0x2E, 0x88, 0x8C, 0x82) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                : (light ? WithAlpha(p.Secondary, 0x32) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

            Set(p.Text, ["AppTextColor"], ["WorkspaceTextBrush", "WorkspaceNavigationTextBrush", "TextFillColorPrimaryBrush", "OpChoiceTextBrush"]);
            Set(p.Secondary, ["AppSecondaryTextColor"], ["WorkspaceSecondaryBrush", "TextFillColorSecondaryBrush"]);
            Set(p.Accent, ["AppAccentColor", "SystemAccentColor"], ["SelectionIndicatorBrush", "WorkspaceMarqueeFillBrush", "WorkspaceAccentBrush", "AccentFillColorDefaultBrush", "AccentButtonBackground", "NavigationViewSelectionIndicatorForeground"]);
            Set(accentText, ["AppAccentTextColor"], ["AccentTextFillColorPrimaryBrush"]);
            Set(p.Hover, ["AppAccentSecondaryColor", "SystemAccentColorLight1"], ["AccentFillColorSecondaryBrush", "AccentButtonBackgroundPointerOver"]);
            Set(accentTertiary, ["AppAccentTertiaryColor", "SystemAccentColorDark1"], ["AccentFillColorTertiaryBrush", "AccentButtonBackgroundPressed"]);
            Set(p.OnAccent, ["AppOnAccentColor"], ["WorkspaceOnAccentBrush", "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed"]);

            Set(navSelected, ["AppNavigationColor"], ["WorkspaceNavigationSelectedBrush", "OpChoiceBrush"]);
            Set(rowSelected, ["AppAccentSubtleColor"], ["WorkspaceAccentSubtleBrush"]);
            Set(WithAlpha(p.Accent, light ? (byte)26 : (byte)44), ["AppAccentSoftColor"], ["WorkspaceAccentSoftBrush"]);
            Set(selectionHover, ["AppSelectionHoverColor"], ["WorkspaceHoverBrush", "WorkspaceControlHoverBrush"]);
            Set(opField, ["OpFieldColor"], ["OpSoftBrush"]);
            Set(opFieldHover, ["OpFieldHoverColor"], ["OpSoftHoverBrush"]);
            Set(divider, ["OpDividerColor"], ["OpDividerBrush"]);

            Set(bgSolid, ["AppBackgroundColor"], []);
            Set(windowOverlay, ["AppWindowOverlayColor"], ["WorkspaceBackgroundBrush"]);
            Set(surfaceSolid, ["AppSurfaceColor"], ["OpCardBrush"]);
            Set(glassContent, ["AppGlassContentColor"], ["WorkspaceSurfaceBrush"]);
            Set(popupTint, ["AppGlassPopupColor"], []);
            Set(dialogFallback, ["AppPopupFallbackColor"], ["WorkspaceDialogBrush"]);
            Set(glassPane, ["AppGlassPaneColor"], ["WorkspacePaneBrush"]);
            Set(glassChrome, ["AppGlassChromeColor"], ["WorkspaceChromeBrush"]);
            Set(glassInset, ["AppSubtleColor", "AppGlassInsetColor"], ["WorkspaceSubtleBrush"]);
            Set(glassCard, ["AppGlassCardColor"], ["WorkspaceCardBrush"]);
            Set(glassControl, ["AppGlassControlColor"], ["WorkspaceControlBrush"]);
            Set(glassField, ["AppGlassFieldColor"], ["WorkspaceFieldBrush"]);
            Set(borderSolid, ["AppBorderColor"], []);
            Set(glassBorder, ["AppGlassBorderColor"], ["WorkspaceBorderBrush"]);

            var popup = (AcrylicBrush)surfaces["WorkspacePopupBrush"];
            popup.TintColor = popupTint;
            popup.FallbackColor = dialogFallback;
            popup.TintOpacity = .74;
            popup.AlwaysUseFallback = !s.Mica;

            void Set(Color value, string[] tokens, string[] keys)
            {
                foreach (var token in tokens) colors[token] = value;
                foreach (var key in keys)
                {
                    if (brushes.TryGetValue(key, out var themeBrush) && themeBrush is SolidColorBrush b) b.Color = value;
                    if (surfaces.TryGetValue(key, out var material) && material is SolidColorBrush surface) surface.Color = value;
                }
            }
        }

        // WinUI 3's native C++ ListViewItemPresenter resolves {ThemeResource ListViewItem*} at container creation
        // BEFORE the ListViewItem enters WindowRoot (using Application.Current.RequestedTheme instead of WindowRoot.RequestedTheme).
        // Keeping ListViewItem* & ComboBoxItem* in both theme dictionaries synchronized to the active app theme prevents Dark charcoal bleed in Light mode.
        foreach (var name in new[] { "Light", "Dark" })
        {
            var brushes = (ResourceDictionary)theme.ThemeDictionaries[name];
            ResourceDictionary? xamlDict = null;
            if (xamlControls?.ThemeDictionaries.TryGetValue(name, out var xd) == true)
                xamlDict = xd as ResourceDictionary;

            void SyncItemBrush(string key, Color color)
            {
                if (brushes.TryGetValue(key, out var tb) && tb is SolidColorBrush sb) sb.Color = color;
                if (xamlDict is not null && xamlDict.TryGetValue(key, out var xb) && xb is SolidColorBrush xsb)
                {
                    try { xsb.Color = color; } catch { }
                }
            }

            SyncItemBrush("ListViewItemBackgroundPointerOver", activeRowHover);
            SyncItemBrush("ListViewItemBackgroundPressed", activeNavSelected);
            SyncItemBrush("ListViewItemBackgroundSelected", activeRowSelected);
            SyncItemBrush("ListViewItemBackgroundSelectedPointerOver", activeSelectionHover);
            SyncItemBrush("ListViewItemBackgroundSelectedPressed", activeSelectionHover);
            SyncItemBrush("ListViewItemForegroundPointerOver", activeText);
            SyncItemBrush("ListViewItemForegroundSelected", activeText);
            SyncItemBrush("ListViewItemForegroundSelectedPointerOver", activeText);
            SyncItemBrush("ListViewItemSelectionIndicatorBrush", activeAccent);
            SyncItemBrush("ListViewItemSelectionIndicatorPointerOverBrush", activeAccent);
            SyncItemBrush("ListViewItemSelectionIndicatorPressedBrush", activeAccent);
            SyncItemBrush("ListViewItemCheckBrush", activeOnAccent);
            SyncItemBrush("ListViewItemCheckPressedBrush", activeOnAccent);
            SyncItemBrush("ListViewItemCheckBoxSelectedBrush", activeAccent);

            SyncItemBrush("ComboBoxItemBackgroundPointerOver", activeRowHover);
            SyncItemBrush("ComboBoxItemBackgroundPressed", activeNavSelected);
            SyncItemBrush("ComboBoxItemBackgroundSelected", activeRowSelected);
            SyncItemBrush("ComboBoxItemBackgroundSelectedPointerOver", activeSelectionHover);
            SyncItemBrush("ComboBoxItemBackgroundSelectedPressed", activeSelectionHover);
            SyncItemBrush("ComboBoxItemForegroundSelected", activeText);

            // Prevent WinUI C++ controls created before entering WindowRoot from caching Dark white foregrounds in Light mode
            foreach (var fgKey in new[]
            {
                "TextFillColorPrimaryBrush", "SystemControlPageTextBaseHighBrush", "SystemControlForegroundBaseHighBrush",
                "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
                "ToggleButtonForeground", "ToggleButtonForegroundPointerOver", "ToggleButtonForegroundPressed",
                "DropDownButtonForeground", "DropDownButtonForegroundPointerOver", "DropDownButtonForegroundPressed",
                "ComboBoxForeground", "ComboBoxForegroundPointerOver",
                "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused",
                "BreadcrumbBarNormalForegroundBrush", "BreadcrumbBarHoverForegroundBrush",
                "BreadcrumbBarPressedForegroundBrush", "BreadcrumbBarCurrentNormalForegroundBrush",
                "ExpanderHeaderForeground", "ExpanderChevronForegroundPointerOver", "ExpanderChevronForegroundPressed"
            })
            {
                SyncItemBrush(fgKey, activeText);
            }
            foreach (var secKey in new[] { "TextFillColorSecondaryBrush", "SystemControlForegroundBaseMediumBrush", "ExpanderChevronForeground" })
            {
                SyncItemBrush(secKey, activeSecondary);
            }
        }
    }

    public static Brush ThemeBrush(ElementTheme theme, string key)
    {
        var dictKey = theme == ElementTheme.Light ? "Light" : "Dark";
        foreach (var dict in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (dict.ThemeDictionaries.TryGetValue(dictKey, out var themed) &&
                themed is ResourceDictionary td &&
                td.TryGetValue(key, out var val) &&
                val is Brush brush)
                return brush;
        }
        return (Brush)Application.Current.Resources[key];
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color Mix(Color a, Color b, double amount)
    {
        byte Blend(byte from, byte to) => (byte)Math.Round(from + (to - from) * amount);
        return Color.FromArgb(255, Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
    }
}
