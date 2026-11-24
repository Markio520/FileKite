using FileAssistant.App.Controls;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private Style LocalStyle(string key) => (Style)Resources[key];
    private static SolidColorBrush ColorBrush(int rgb, byte alpha = 255) =>
        new(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private TextBlock Label(string value, string style = "SettingLabel") => new() { Text = value, Style = LocalStyle(style) };
    private static FontIcon Icon(string glyph, double size = 17) => new() { Glyph = glyph, FontSize = size };
    private static StackPanel Horizontal(double gap, params UIElement[] elements)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var el in elements) panel.Children.Add(el);
        return panel;
    }
    private static SettingsFlowPanel Flow(double gap, params UIElement[] elements)
    {
        var panel = new SettingsFlowPanel { Gap = gap };
        foreach (var el in elements) panel.Children.Add(el);
        return panel;
    }

    private static void NameControl(DependencyObject control, string name, string? id = null)
    {
        AutomationProperties.SetName(control, name);
        if (id is not null) AutomationProperties.SetAutomationId(control, id);
        ToolTipService.SetToolTip(control, name);
    }

    private FrameworkElement Texts(string title, string? description = null, string? glyph = null)
    {
        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Label(title));
        if (!string.IsNullOrEmpty(description)) stack.Children.Add(Label(description, "SettingCaption"));
        if (glyph is null) return stack;
        return Leading(Icon(glyph), stack);
    }
    private static Grid Leading(FrameworkElement leading, FrameworkElement inner)
    {
        var grid = new Grid { ColumnSpacing = 13, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.Children.Add(leading);
        Grid.SetColumn(inner, 1);
        grid.Children.Add(inner);
        return grid;
    }

    private static FrameworkElement Pair(FrameworkElement left, FrameworkElement? right, double minInline)
    {
        if (right is null) return left;
        var panel = new SettingsPairPanel { MinimumInlineWidth = minInline };
        panel.Children.Add(left);
        panel.Children.Add(right);
        return panel;
    }

    private Border Card(FrameworkElement content, double minHeight = 48) =>
        new() { Style = LocalStyle("SettingCard"), Child = content, MinHeight = minHeight };
    private void Row(StackPanel group, string title, FrameworkElement? control = null, string? desc = null, string? glyph = null, double minInline = 383)
    {
        group.Children.Add(Card(Pair(Texts(title, desc, glyph), control, minInline), desc is null ? 48 : 56));
    }
    private Button ActionButton(string label, Func<Task> action, bool danger = false, Func<bool>? canExecute = null)
    {
        var button = new Button { Content = label, Style = LocalStyle(danger ? "SettingDangerButton" : "SettingButton") };
        if (canExecute is not null) button.IsEnabled = canExecute();
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Notify(T("操作未完成", "Could not complete the action"), error.Message, InfoBarSeverity.Error); }
            finally { button.IsEnabled = canExecute?.Invoke() ?? true; }
        };
        return button;
    }
    private ToggleSwitch Switch(string title, Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set)
    {
        var item = new ToggleSwitch
        {
            OnContent = null, OffContent = null, MinWidth = 0, MinHeight = 24, Padding = new(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        NameControl(item, title);
        Bind(s => item.IsOn = get(s));
        item.Toggled += async (_, _) =>
        {
            if (applying) return;
            var value = item.IsOn;
            await Save(s => set(s, value));
        };
        return item;
    }
    private void SwitchRow(StackPanel cards, string title, Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set,
        string? desc = null, string? glyph = null) =>
        Row(cards, title, Switch(title, get, set), desc, glyph, 210);

    private SelectionDropDown Select<TValue>(string title, Func<AppSettings, TValue> get, Func<TValue, Task> choose,
        (TValue Value, string Label)[] options, double width = 212)
    {
        var control = new SelectionDropDown
        {
            Style = LocalStyle("SettingDropDownStyle"),
            MinWidth = 0, Width = width, FontSize = 12.5,
            ItemsSource = options.Select((o, i) => new SelectionOption(i, o.Label)).ToArray()
        };
        NameControl(control, title);
        Bind(s => control.SelectedValue = Array.FindIndex(options, o => EqualityComparer<TValue>.Default.Equals(get(s), o.Value)));
        control.SelectionCommitted += async (_, _) =>
        {
            if (applying || control.SelectedValue < 0) return;
            try { await choose(options[control.SelectedValue].Value); }
            catch (Exception error) { Notify(T("未能更改此选项", "Could not change this setting"), error.Message, InfoBarSeverity.Error); }
            finally { if (opened) Rebind(); }
        };
        return control;
    }
    private void ChoiceRow<TValue>(StackPanel cards, string title, Func<AppSettings, TValue> get, Func<AppSettings, TValue, AppSettings> set,
        (TValue Value, string Label)[] options, string? desc = null, string? glyph = null) =>
        Row(cards, title, Select(title, get, v => Save(s => set(s, v)), options), desc, glyph);

    private Border Segments<TValue>(string title, Func<AppSettings, TValue> get, Func<TValue, Task> select, (TValue Value, string Name)[] options)
    {
        var group = Flow(2);
        foreach (var option in options)
        {
            var button = new ToggleButton
            {
                Style = LocalStyle("SettingSegment"), Content = option.Name
            };
            NameControl(button, title + " · " + option.Name);
            Bind(s =>
            {
                var selected = EqualityComparer<TValue>.Default.Equals(get(s), option.Value);
                button.IsChecked = selected;
                button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            });
            button.Click += async (_, _) =>
            {
                button.IsChecked = true;
                await select(option.Value);
                if (opened) Rebind();
            };
            group.Children.Add(button);
        }
        return new Border
        {
            Style = LocalStyle("SettingInset"), CornerRadius = new(7), Padding = new(3), BorderThickness = new(1), Child = group
        };
    }

    private Expander Disclosure(string title, FrameworkElement header, FrameworkElement contents, bool expanded)
    {
        var headerHost = new Border { Padding = new(0, 4, 4, 4), HorizontalAlignment = HorizontalAlignment.Stretch, Child = header };
        var contentHost = new Border { Padding = new(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch, Child = contents };
        var panel = new Expander { Style = LocalStyle("SettingExpander"), Header = headerHost, Content = contentHost, IsExpanded = expanded };
        AutomationProperties.SetName(panel, title);
        return panel;
    }

    private static void Place(Grid grid, FrameworkElement element, int col = 0, int row = 0)
    {
        Grid.SetColumn(element, col);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

}

/// <summary>Keep each setting's value beside its label when both fit, then reflow it below at narrow widths.</summary>
internal sealed partial class SettingsPairPanel : Panel
{
    internal double MinimumInlineWidth { get; init; }
    private const double Gap = 12;

    protected override Size MeasureOverride(Size available)
    {
        var left = Children[0]; var right = Children[1];
        var width = available.Width;
        right.Measure(new(width, double.PositiveInfinity));
        var stacked = width < MinimumInlineWidth || width - right.DesiredSize.Width - Gap < 88;
        left.Measure(new(stacked ? width : Math.Max(0, width - right.DesiredSize.Width - Gap), double.PositiveInfinity));
        return stacked
            ? new(Math.Max(left.DesiredSize.Width, right.DesiredSize.Width), left.DesiredSize.Height + 6 + right.DesiredSize.Height)
            : new(left.DesiredSize.Width + Gap + right.DesiredSize.Width, Math.Max(left.DesiredSize.Height, right.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var left = Children[0]; var right = Children[1];
        var rw = Math.Min(final.Width, right.DesiredSize.Width);
        var stacked = final.Width < MinimumInlineWidth || final.Width - rw - Gap < 88;
        if (stacked)
        {
            left.Arrange(new(0, 0, final.Width, left.DesiredSize.Height));
            right.Arrange(new(0, left.DesiredSize.Height + 6, rw, right.DesiredSize.Height));
        }
        else
        {
            left.Arrange(new(0, (final.Height - left.DesiredSize.Height) / 2, Math.Max(0, final.Width - rw - Gap), left.DesiredSize.Height));
            right.Arrange(new(final.Width - rw, (final.Height - right.DesiredSize.Height) / 2, rw, right.DesiredSize.Height));
        }
        return final;
    }
}

/// <summary>Wrap buttons, volume chips, and removable exclusion tags without a horizontal scrollbar.</summary>
internal sealed partial class SettingsFlowPanel : Panel
{
    internal double Gap { get; init; } = 6;
    protected override Size MeasureOverride(Size available)
    {
        foreach (var child in Children) child.Measure(new(available.Width, double.PositiveInfinity));
        return Layout(available.Width, arrange: false);
    }
    protected override Size ArrangeOverride(Size final)
    {
        Layout(final.Width, arrange: true);
        return final;
    }
    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, used = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var w = Math.Min(width, child.DesiredSize.Width);
            if (x > 0 && x + w > width) { x = 0; y += rowHeight + Gap; rowHeight = 0; }
            if (arrange) child.Arrange(new(x, y, w, child.DesiredSize.Height));
            used = Math.Max(used, x + w);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            x += w + Gap;
        }
        return new(used, y + rowHeight);
    }
}
