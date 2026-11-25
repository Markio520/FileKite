using FileAssistant.Core.Settings;
using FileAssistant.App.Resources;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    internal sealed record Palette(string Id, string Zh, string En, bool Light);
    private static readonly Palette[] Palettes =
    [
        new("charcoal", "炭黑微光", "Charcoal Glow", false), new("ocean", "深海松石", "Deep Ocean", false),
        new("forest", "暗夜苔原", "Night Forest", false), new("espresso", "暖咖暗室", "Warm Espresso", false),
        new("sand", "沙白画廊", "Sand Gallery", true), new("frost", "云杉雾白", "Spruce Mist", true),
        new("linen", "暖杏羊皮", "Apricot Linen", true), new("studio", "极简纯白", "Studio White", true)
    ];

    internal static Palette EffectivePaletteForMode(AppSettings s, bool light) =>
        Palettes.FirstOrDefault(p => p.Id == s.Palette && p.Light == light) ?? Palettes[light ? 4 : 0];

    internal static string FormatModeMenuLabel(AppSettings s, bool light)
    {
        var preset = EffectivePaletteForMode(s, light);
        return light
            ? InterfaceText.Get($"浅色（{preset.Zh}）", $"Light ({preset.En})", s.Language)
            : InterfaceText.Get($"深色（{preset.Zh}）", $"Dark ({preset.En})", s.Language);
    }

    private void BuildAppearance()
    {
        AddSection("色彩与主题", "Color & themes", "配色 颜色 色彩 深色 浅色 黑色 白色 系统 palette dark light", cards =>
        {
            var mode = T("外观模式", "Color mode");
            Row(cards, mode, Segments(mode, s => s.Mode, SetColorMode,
            [
                (AppearanceMode.System, T("跟随系统", "System")), (AppearanceMode.Light, T("浅色", "Light")),
                (AppearanceMode.Dark, T("深色", "Dark"))
            ]), glyph: "\uE706");

            Border BuildPaletteGroupCard(bool lightGroup, string zhTitle, string enTitle)
            {
                var box = new StackPanel { Spacing = 14 };
                var current = Label("", "SettingCaption");
                Bind(s =>
                {
                    var palette = EffectivePalette(s);
                    current.Text = palette.Light == lightGroup
                        ? string.Format(T("当前：{0}", "Current: {0}"), T(palette.Zh, palette.En))
                        : "";
                });
                box.Children.Add(Pair(Label(T(zhTitle, enTitle), "SettingCaption"), current, 320));
                var gallery = new SettingsTilePanel { MaximumColumns = 4, MinimumTileWidth = 91, Gap = 12, RowGap = 14 };
                foreach (var palette in Palettes.Where(p => p.Light == lightGroup))
                    gallery.Children.Add(PaletteButton(palette));
                box.Children.Add(gallery);
                var groupCard = Card(box);
                groupCard.Padding = new(16, 14, 16, 16);
                return groupCard;
            }

            var lightCard = BuildPaletteGroupCard(true, "浅色主题方案（点击即时切换配色）", "Light theme presets · select to apply");
            var darkCard = BuildPaletteGroupCard(false, "深色主题方案（点击即时切换配色）", "Dark theme presets · select to apply");
            Bind(s =>
            {
                lightCard.Visibility = Visible(s.Mode is AppearanceMode.Light or AppearanceMode.System);
                darkCard.Visibility = Visible(s.Mode is AppearanceMode.Dark or AppearanceMode.System);
            });
            cards.Children.Add(lightCard);
            cards.Children.Add(darkCard);
        });
        AddSection("应用图标", "App icon", "图标 logo 纸鸢 外观 托盘 任务栏 icon tray taskbar 圖示 紙鳶 系統匣 工作列", cards =>
        {
            var gallery = new SettingsTilePanel { MaximumColumns = 5, MinimumTileWidth = 88, Gap = 7, RowGap = 10 };
            foreach (var choice in AppIconCatalog.Choices) gallery.Children.Add(AppIconButton(choice));
            var group = new StackPanel { Spacing = 12 };
            group.Children.Add(gallery);
            group.Children.Add(Label(T("切换后立即更新标题栏、任务栏、系统托盘及快捷方式图标（可执行文件默认图标为原生钴蓝）。",
                "Switching updates the title bar, taskbar, system tray, and existing shortcuts. The executable uses Classic Cobalt."), "SettingCaption"));
            var card = Card(group);
            card.Padding = new(12);
            cards.Children.Add(card);
        });
        AddSection("主导航栏样式", "Main navigation", "导航 侧栏 顶栏 布局 左侧分组 单行融合 sidebar top bar layout", cards =>
        {
            var layouts = new SettingsTilePanel { MaximumColumns = 2, MinimumTileWidth = 174, Gap = 8, RowGap = 5 };
            layouts.Children.Add(NavigationCard(false, T("左侧分组导航", "Grouped side navigation"), T("纵向展示更多文件行", "Keep more file rows in view")));
            layouts.Children.Add(NavigationCard(true, T("单行融合顶栏", "Single-row top navigation"), T("融入标题栏释放横向宽度", "Free up horizontal space")));
            cards.Children.Add(layouts);
        });
        AddSection("窗口材质与显示细节", "Window material & display", "透明 行高 动效 分栏 宽度 重置 间距 file extensions transitions column width",
            cards =>
        {
            SwitchRow(cards, T("云母半透明背景材质 (Mica)", "Mica window material"), s => s.Mica, (s, v) => s with { Mica = v },
                T("可在远程桌面或低功耗设备上关闭透明效果", "Turn off transparency on a remote desktop or a low-power device."), "\uE91B");
            var density = T("文件列表行距密度", "File list density");
            Row(cards, density, Segments(density, s => s.Density, v => Save(s => s with { Density = v }),
            [
                (ListDensity.Compact, T("紧凑", "Compact")), (ListDensity.Standard, T("标准", "Standard")), (ListDensity.Spacious, T("宽松", "Spacious"))
            ]), glyph: "\uE8FD");
            SwitchRow(cards, T("在列表中始终显示完整文件扩展名", "Always show file extensions"), s => s.ShowExtensions,
                (s, v) => s with { ShowExtensions = v }, glyph: "\uE8A5");
            SwitchRow(cards, T("界面平滑过渡动效", "Animate interface transitions"), s => s.Animations,
                (s, v) => s with { Animations = v }, glyph: "\uE768");
            Row(cards, T("各模块拖拽后的左右分栏宽度", "Custom workspace pane widths"),
                ActionButton(T("恢复默认列宽", "Restore default widths"), async () =>
                {
                    await SettingsOperations.ResetPaneWidthsAsync();
                    Notify(T("已恢复默认列宽", "Default pane widths restored"), "", InfoBarSeverity.Success);
                }), glyph: "\uE91B");
        });
    }

    private ToggleButton AppIconButton(AppIconChoice choice)
    {
        var image = new Image { Width = 44, Height = 44 };
        AppIconAssets.SetImage(image, choice.Id);
        var preview = new Grid { Width = 52, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
        preview.Children.Add(new Border
        {
            CornerRadius = new(10), Width = 44, Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = image
        });
        var check = new Border
        {
            Width = 16, Height = 16, CornerRadius = new(8), Style = LocalStyle("SettingIconCheck"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 9.5, Style = LocalStyle("SettingIconCheckGlyph") }
        };
        preview.Children.Add(check);
        var primary = T(choice.Chinese, choice.English);
        var name = Label(primary, "SettingLabel");
        var alternate = Label(primary == choice.English ? choice.Chinese : choice.English, "SettingCaption");
        name.FontSize = 12.5;
        name.TextAlignment = alternate.TextAlignment = TextAlignment.Center;
        alternate.FontSize = 10.5;
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(preview);
        content.Children.Add(name);
        content.Children.Add(alternate);
        var card = new Border { CornerRadius = new(7), Padding = new(6, 8, 6, 8), BorderThickness = new(1), Child = content };
        var button = new ToggleButton { Style = LocalStyle("SettingSwatch"), Content = card };
        NameControl(button, primary + " · " + alternate.Text, "Settings.AppIcon." + choice.Id);
        Bind(s =>
        {
            var selected = s.AppIcon == choice.Id;
            button.IsChecked = selected;
            check.Visibility = Visible(selected);
            name.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            if (selected)
            {
                card.ClearValue(Border.BackgroundProperty);
                card.ClearValue(Border.BorderBrushProperty);
                card.Style = LocalStyle("SettingInset");
            }
            else
            {
                card.Style = null;
                card.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                card.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        });
        button.Click += async (_, _) => { button.IsChecked = true; await Save(s => s with { AppIcon = choice.Id }); };
        return button;
    }

    private static bool SystemUsesLight()
    {
        var background = new UISettings().GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B > 382;
    }
    private static Palette EffectivePalette(AppSettings s)
    {
        var light = s.Mode == AppearanceMode.Light || (s.Mode == AppearanceMode.System && SystemUsesLight());
        var stored = Palettes.FirstOrDefault(p => p.Id == s.Palette && p.Light == light);
        return stored ?? Palettes[light ? 4 : 0];
    }
    private Task SetColorMode(AppearanceMode mode) => Save(s =>
    {
        var next = s with { Mode = mode };
        return next with { Palette = EffectivePalette(next).Id };
    });

    private FrameworkElement PaletteButton(Palette preset)
    {
        var colors = (WorkspacePalette)Application.Current.Resources["Palette_" + preset.Id];
        var accent = new SolidColorBrush(colors.Accent);
        var name = T(preset.Zh, preset.En);
        var preview = new Grid();
        preview.ColumnDefinitions.Add(new() { Width = new(28) });
        preview.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var rail = new StackPanel { Spacing = 4, Padding = new(5, 7, 3, 3) };
        rail.Children.Add(MiniBar(13, 3, accent));
        var line = ColorBrush(preset.Light ? 0 : 0xFFFFFF, 45);
        rail.Children.Add(MiniBar(9, 3, line));
        rail.Children.Add(MiniBar(11, 3, line));
        Place(preview, new Border
        {
            Background = new SolidColorBrush(colors.Rail), BorderThickness = new(0, 0, 1, 0), BorderBrush = line,
            CornerRadius = new(5, 0, 0, 5), Child = rail
        });
        var main = new Grid { Padding = new(7, 6, 7, 6), RowSpacing = 4 };
        main.RowDefinitions.Add(new() { Height = new(10) });
        main.RowDefinitions.Add(new() { Height = new(10) });
        main.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var cardBrush = new SolidColorBrush(colors.Card);
        Place(main, new Border { Background = cardBrush, BorderBrush = line, BorderThickness = new(0.7), CornerRadius = new(2.5) });
        Place(main, new Border { Background = cardBrush, BorderBrush = line, BorderThickness = new(0.7), CornerRadius = new(2.5), Margin = new(0, 0, 12, 0) }, row: 1);
        var chip = MiniBar(19, 5, accent);
        chip.HorizontalAlignment = HorizontalAlignment.Right; chip.VerticalAlignment = VerticalAlignment.Bottom;
        Place(main, chip, row: 2);
        Place(preview, main, 1);
        var check = new Border
        {
            Width = 15, Height = 15, CornerRadius = new(8), Margin = new(0, 3, 3, 0), Background = accent,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon { Glyph = "\uE73E", FontSize = 9, Foreground = new SolidColorBrush(colors.OnAccent) }
        };
        Place(preview, check, 1);
        var frame = new Border
        {
            Height = 56, CornerRadius = new(7), Background = new SolidColorBrush(colors.Window), Child = preview, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var label = Label(name, "SettingCaption");
        label.TextAlignment = TextAlignment.Center;
        var caption = Leading(new Border { Width = 6.5, Height = 6.5, CornerRadius = new(4), Background = accent }, label);
        caption.ColumnSpacing = 5; caption.HorizontalAlignment = HorizontalAlignment.Center;
        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(frame); content.Children.Add(caption);
        var button = new ToggleButton { Style = LocalStyle("SettingSwatch"), Content = content };
        NameControl(button, name, "Settings.Palette." + preset.Id);
        Bind(s =>
        {
            var selected = EffectivePalette(s).Id == preset.Id;
            button.IsChecked = selected;
            frame.BorderBrush = selected ? accent : line;
            frame.BorderThickness = new(selected ? 2 : 1);
            check.Visibility = Visible(selected);
            label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        });
        button.Click += async (_, _) =>
        {
            button.IsChecked = true;
            await Save(s => s with { Mode = preset.Light ? AppearanceMode.Light : AppearanceMode.Dark, Palette = preset.Id });
        };
        return button;
    }

    private static Border MiniBar(double width, double height, Brush brush) => new()
    {
        Width = width, Height = height, CornerRadius = new(2), Background = brush, HorizontalAlignment = HorizontalAlignment.Left
    };

    private FrameworkElement NavigationCard(bool top, string title, string desc)
    {
        var diagram = new Grid { RowSpacing = 3, ColumnSpacing = 3 };
        diagram.RowDefinitions.Add(new() { Height = top ? new(6) : new(1, GridUnitType.Star) });
        if (top) diagram.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        diagram.ColumnDefinitions.Add(new() { Width = top ? new(1, GridUnitType.Star) : new(17) });
        if (!top) diagram.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var bars = new StackPanel { Spacing = 2, Padding = top ? new(0) : new(2, 3, 1, 0), Orientation = top ? Orientation.Horizontal : Orientation.Vertical };
        Border WireBar(double width, string style) => new() { Width = width, Height = 3, CornerRadius = new(2), HorizontalAlignment = HorizontalAlignment.Left, Style = LocalStyle(style) };
        bars.Children.Add(WireBar(top ? 12 : 11, "SettingWireAccent"));
        bars.Children.Add(WireBar(8, "SettingRule"));
        bars.Children.Add(WireBar(10, "SettingRule"));
        Place(diagram, bars);
        Place(diagram, new Border { Style = LocalStyle("SettingWireContent"), CornerRadius = new(3), BorderThickness = new(1) },
            top ? 0 : 1, top ? 1 : 0);
        var wire = new Border
        {
            Width = 64, Height = 42, Padding = new(4), BorderThickness = new(1),
            Style = LocalStyle("SettingInset"), CornerRadius = new(5), Child = diagram
        };
        var content = Leading(wire, Texts(title, desc));
        content.ColumnSpacing = 13;
        var button = new ToggleButton
        {
            Style = LocalStyle("SettingNavigationCard"), Content = content
        };
        NameControl(button, title, top ? "Settings.Navigation.Top" : "Settings.Navigation.Left");
        Bind(s => button.IsChecked = s.TopNavigation == top);
        button.Click += async (_, _) => { button.IsChecked = true; await Save(s => s with { TopNavigation = top }); };
        return button;
    }
}

/// <summary>Equal-width theme previews or layout cards, with fewer columns when the available DIP width shrinks.</summary>
internal sealed partial class SettingsTilePanel : Panel
{
    internal int MaximumColumns { get; init; }
    internal double MinimumTileWidth { get; init; }
    internal double Gap { get; init; }
    internal double RowGap { get; init; }
    private int ColumnCount(double width)
    {
        if (width >= MaximumColumns * MinimumTileWidth + (MaximumColumns - 1) * Gap) return MaximumColumns;
        return MaximumColumns > 2 && width >= 2 * MinimumTileWidth + Gap ? 2 : 1;
    }
    protected override Size MeasureOverride(Size available)
    {
        int n = ColumnCount(available.Width);
        double w = Math.Max(0, (available.Width - (n - 1) * Gap) / n);
        foreach (var el in Children) el.Measure(new(w, double.PositiveInfinity));
        double used = 0, naturalWidth = 0;
        for (int i = 0; i < Children.Count; i += n)
        {
            double height = 0;
            for (int c = i; c < Math.Min(i + n, Children.Count); c++)
            {
                height = Math.Max(height, Children[c].DesiredSize.Height);
                naturalWidth = Math.Max(naturalWidth, Children[c].DesiredSize.Width);
            }
            used += height + (i > 0 ? RowGap : 0);
        }
        double width = double.IsFinite(available.Width) ? available.Width : n * naturalWidth + (n - 1) * Gap;
        return new(width, used);
    }
    protected override Size ArrangeOverride(Size final)
    {
        int n = ColumnCount(final.Width);
        double w = Math.Max(0, (final.Width - (n - 1) * Gap) / n), y = 0;
        for (int i = 0; i < Children.Count; i += n)
        {
            int end = Math.Min(i + n, Children.Count);
            double h = 0;
            for (int c = i; c < end; c++) h = Math.Max(h, Children[c].DesiredSize.Height);
            for (int c = i; c < end; c++) Children[c].Arrange(new((c - i) * (w + Gap), y, w, h));
            y += h + RowGap;
        }
        return final;
    }
}
