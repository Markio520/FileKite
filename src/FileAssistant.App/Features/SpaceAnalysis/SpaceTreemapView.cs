using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace FileAssistant.App.Features.SpaceAnalysis;

/// <summary>Bounded native tiles. The canvas never creates a visual for every file.</summary>
public sealed class SpaceTreemapView : UserControl
{
    private readonly Canvas canvas = new();
    private sealed record TileVisual(int Id, int Grouped, Button Button, Border Accent, TextBlock Label,
        string Name, string NameAndSize);
    private readonly List<TileVisual> visuals = [];
    private SpaceSnapshot? snapshot;
    private SpaceMapData? data;
    private int? selected;

    public event EventHandler<int>? TileInvoked;
    public event EventHandler<int>? FolderInvoked;

    public SpaceTreemapView()
    {
        Content = canvas;
        SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw(reset: true);
        AutomationProperties.SetName(this, "当前目录的空间矩形图");
    }

    public void SetData(SpaceSnapshot? value, SpaceMapData? map)
    {
        if (ReferenceEquals(snapshot, value) && ReferenceEquals(data, map)) return;
        var drillingSameSnapshot = snapshot is not null && ReferenceEquals(snapshot, value) && data is not null && map is not null;
        var zoomIn = drillingSameSnapshot && map!.Bytes <= data!.Bytes;
        snapshot = value;
        data = map;
        Draw(reset: true);
        if (drillingSameSnapshot) Controls.WorkspaceMotion.AnimateTreemapDrill(canvas, zoomIn);
    }

    public void RefreshDisplay() => Draw(reset: true);

    public void Select(int? id)
    {
        if (selected == id) return;
        selected = id;
        foreach (var tile in canvas.Children.OfType<Button>()) SetSelection(tile);
    }

    private void Draw(bool reset = false)
    {
        if (reset || snapshot is null || data is null || ActualWidth < 16 || ActualHeight < 16)
        {
            canvas.Children.Clear();
            visuals.Clear();
        }
        if (snapshot is null || data is null || ActualWidth < 16 || ActualHeight < 16) return;
        var tiles = SpaceTreemap.Layout(data.Largest, data.Count, data.Bytes, ActualWidth, ActualHeight);
        while (visuals.Count > tiles.Length)
        {
            var last = visuals.Count - 1;
            canvas.Children.RemoveAt(last);
            visuals.RemoveAt(last);
        }
        for (var index = 0; index < tiles.Length; index++)
        {
            var tile = tiles[index];
            if (index == visuals.Count)
            {
                var visual = CreateTile(snapshot, tile);
                visuals.Add(visual);
                canvas.Children.Add(visual.Button);
            }
            else if (visuals[index].Id != tile.Id || visuals[index].Grouped != tile.Grouped)
            {
                var visual = CreateTile(snapshot, tile);
                visuals[index] = visual;
                canvas.Children[index] = visual.Button;
            }
            PositionTile(visuals[index], tile);
        }
    }

    private TileVisual CreateTile(SpaceSnapshot source, SpaceTile tile)
    {
        var id = tile.Id;
        var entry = id < 0 ? default : source.Entries[id];
        var kind = id < 0 ? SpaceKind.Other : SpaceKinds.Of(entry);
        var name = id < 0 ? $"其他 {tile.Grouped:N0} 项" : FileAssistant.Core.Settings.FileDisplay.Name(entry.Name, entry.IsDirectory);
        var size = SpaceLabels.Size(tile.Bytes);
        var description = id < 0 ? $"{name} · {size}，点击查看本层列表" : $"{name} · {SpaceKinds.Label(kind)} · {size}";
        var accent = new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -3, -4, 0),
            Background = new SolidColorBrush(ActualTheme == ElementTheme.Light
                ? ColorHelper.FromArgb(140, 255, 255, 255) : ColorHelper.FromArgb(46, 255, 255, 255)),
            IsHitTestVisible = false
        };
        var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        var button = new Button
        {
            Tag = id, MinWidth = 0, MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = SpaceChartColors.Brush(kind, ActualTheme),
            Foreground = SpaceChartColors.Text(ActualTheme),
            Content = new Grid { Children = { accent, label } }
        };
        Controls.WorkspaceMotion.AttachTactilePress(button, 0.975f);
        if (tile.Width >= 28 && tile.Height >= 20) Controls.WorkspaceMotion.AttachHoverLift(button, -1.5f);
        AutomationProperties.SetName(button, description);
        ToolTipService.SetToolTip(button, description);
        button.Click += (_, _) => TileInvoked?.Invoke(this, id);
        if (entry.IsDirectory) button.DoubleTapped += (_, e) =>
        {
            FolderInvoked?.Invoke(this, id);
            e.Handled = true;
        };
        SetSelection(button);
        return new(id, tile.Grouped, button, accent, label, name, $"{name}\n{size}");
    }

    private static void PositionTile(TileVisual visual, SpaceTile tile)
    {
        var compact = tile.Width < 68 || tile.Height < 38;
        var button = visual.Button;
        button.Width = Math.Max(0.5, tile.Width - 3);
        button.Height = Math.Max(0.5, tile.Height - 3);
        button.Padding = compact ? new Thickness(2, 0, 2, 0) : new Thickness(6, 4, 6, 4);
        button.CornerRadius = new CornerRadius(tile.Width >= 14 && tile.Height >= 14 ? 6 : 3);
        button.IsTabStop = tile.Width >= 8 && tile.Height >= 8;
        visual.Accent.Visibility = tile.Width >= 20 && tile.Height >= 14 ? Visibility.Visible : Visibility.Collapsed;
        visual.Label.Visibility = tile.Width >= 25 && tile.Height >= 17 ? Visibility.Visible : Visibility.Collapsed;
        visual.Label.MaxLines = compact ? 1 : 2;
        visual.Label.Text = !compact && tile.Height >= 44 ? visual.NameAndSize : visual.Name;
        Canvas.SetLeft(button, tile.X);
        Canvas.SetTop(button, tile.Y);
    }

    private void SetSelection(Button tile)
    {
        var active = selected.HasValue && Equals(tile.Tag, selected.Value);
        tile.BorderBrush = active ? SpaceChartColors.Text(ActualTheme) : new SolidColorBrush(Colors.Transparent);
        tile.BorderThickness = new Thickness(active ? 2 : 0);
    }
}

internal static class SpaceChartColors
{
    private static readonly AccessibilitySettings Accessibility = new();

    // The chart palette is independent of control selection colors.
    public static Brush Brush(SpaceKind kind, ElementTheme theme)
    {
        if (Accessibility.HighContrast) return SystemBrush("SystemColorHighlightColor", Colors.DarkBlue);
        var light = theme == ElementTheme.Light;
        uint color = kind switch
        {
            SpaceKind.Folder => light ? 0xDCE1D7u : 0x39423Au,
            SpaceKind.Video => light ? 0xE2D9EEu : 0x4F3D66u,
            SpaceKind.Image => light ? 0xD0E6DFu : 0x285A51u,
            SpaceKind.Audio => light ? 0xEFD9E4u : 0x683C55u,
            SpaceKind.Document => light ? 0xD6E1F1u : 0x3B5279u,
            SpaceKind.Archive => light ? 0xF2E3C4u : 0x65502Bu,
            SpaceKind.Program => light ? 0xF1D7D2u : 0x6D4139u,
            _ => light ? 0xE5E2DAu : 0x44434Au
        };
        return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }

    public static Brush SegmentBrush(SpaceKind kind, ElementTheme theme)
    {
        if (Accessibility.HighContrast) return SystemBrush("SystemColorHighlightColor", Colors.DarkBlue);
        var light = theme == ElementTheme.Light;
        uint color = kind switch
        {
            SpaceKind.Video => light ? 0x795BB8u : 0x9C7FD9u,
            SpaceKind.Image => light ? 0x368F68u : 0x58B389u,
            SpaceKind.Audio => light ? 0xA8577Eu : 0xCF78A2u,
            SpaceKind.Document => light ? 0x3F74BAu : 0x5B8FD9u,
            SpaceKind.Archive => light ? 0xC67F2Fu : 0xD99B52u,
            SpaceKind.Program => light ? 0xB8534Au : 0xD06A60u,
            SpaceKind.Folder => light ? 0x6C7A68u : 0x7A8D78u,
            _ => light ? 0x7E847Au : 0x7B8190u
        };
        return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }

    public static Brush Text(ElementTheme theme) => Accessibility.HighContrast
        ? SystemBrush("SystemColorHighlightTextColor", Colors.White)
        : new SolidColorBrush(theme == ElementTheme.Light ? ColorHelper.FromArgb(255, 33, 36, 32) : Colors.White);

    private static Brush SystemBrush(string key, Windows.UI.Color fallback) =>
        new SolidColorBrush(Application.Current.Resources.TryGetValue(key, out var value) && value is Windows.UI.Color color ? color : fallback);
}
