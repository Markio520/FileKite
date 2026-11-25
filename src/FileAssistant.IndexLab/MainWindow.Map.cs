using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.IndexLab;

public sealed partial class MainWindow
{
    private void MapResized(object sender, SizeChangedEventArgs args) => DrawMap();

    private void DrawMap()
    {
        MapCanvas.Children.Clear();
        if (map is not { Bytes: > 0 } data) { MapCanvas.Visibility = Visibility.Collapsed; return; }
        MapCanvas.Visibility = Visibility.Visible;
        var ordered = data.Largest.Select(x => (x.Id, x.Bytes)).ToArray();
        foreach (var tile in SpaceTreemap.Layout(ordered, data.Count, data.Bytes, MapCanvas.ActualWidth, MapCanvas.ActualHeight))
        {
            var item = data.Largest.FirstOrDefault(i => i.Id == tile.Id);
            var label = item?.Name ?? $"其他 {tile.Grouped:N0} 项";
            var summary = label + " · " + DisplayRow.Bytes(tile.Bytes);
            var width = Math.Max(0, tile.Width - 3);
            var height = Math.Max(0, tile.Height - 3);
            var caption = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
            var content = new StackPanel { Spacing = 1 };
            if (width >= 34 && height >= 17) content.Children.Add(caption);
            if (width >= 40 && height >= 40) content.Children.Add(new TextBlock { Text = DisplayRow.Bytes(tile.Bytes), FontSize = 11 });
            var brush = item?.IsFolder == true ? "WorkspaceAccentSubtleBrush" : "WorkspaceSubtleBrush";
            var button = new Button
            {
                Width = width,
                Height = height,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(5, 2, 5, 2),
                Content = content,
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = (Brush)Application.Current.Resources[brush],
                IsTabStop = item?.IsFolder == true
            };
            AutomationProperties.SetName(button, summary);
            ToolTipService.SetToolTip(button, summary);
            if (item?.IsFolder == true) button.Click += async (_, _) => await BrowseAsync(item.Id);
            else button.Click += (_, _) => ScanMessage(summary);
            Canvas.SetLeft(button, tile.X); Canvas.SetTop(button, tile.Y);
            MapCanvas.Children.Add(button);
        }
    }
}
