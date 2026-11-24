using System.Numerics;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Features.SpaceAnalysis;

// The colored strip and its legend entry are two accessible targets for the same filter.
internal sealed class SpaceUsageSegment
{
    private sealed class FilterButton : Button
    {
        public FilterButton() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private readonly Border stripe;
    private readonly Border dot;
    private Button? hovered;
    private bool selected;

    public SpaceKind Kind { get; }
    public Button Bar { get; }
    public Button Legend { get; }

    public SpaceUsageSegment(SpaceKind kind, Brush color, string size, string summary, Style style, Action<SpaceKind> select)
    {
        Kind = kind;
        stripe = new Border { Height = 9, Background = color, CornerRadius = new CornerRadius(3), CenterPoint = new Vector3(0, 4.5f, 0) };
        dot = new Border { Width = 8, Height = 8, Background = color, CornerRadius = new CornerRadius(4), CenterPoint = new Vector3(4, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        if (Controls.WorkspaceMotion.Enabled)
        {
            stripe.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(115) };
            dot.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(115) };
        }

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        label.Children.Add(dot);
        label.Children.Add(new TextBlock { Text = $"{SpaceKinds.Label(kind)} {size}", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        Bar = CreateButton(stripe);
        Legend = CreateButton(label);

        Button CreateButton(UIElement content)
        {
            var button = new FilterButton { Style = style, Content = content };
            AutomationProperties.SetName(button, summary + "，筛选该类型大文件");
            ToolTipService.SetToolTip(button, summary + " · 点击筛选");
            button.Click += (_, _) => select(kind);
            button.PointerEntered += Enter;
            button.PointerExited += Leave;
            button.GotFocus += FocusChanged;
            button.LostFocus += FocusChanged;
            return button;
        }
    }

    private void Enter(object sender, PointerRoutedEventArgs e)
    {
        hovered = (Button)sender;
        UpdateHighlight();
    }

    private void Leave(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, hovered)) hovered = null;
        UpdateHighlight();
    }

    private void FocusChanged(object sender, RoutedEventArgs e) => UpdateHighlight();

    public void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        UpdateHighlight();
    }

    private void UpdateHighlight()
    {
        var active = selected || hovered is not null || Bar.FocusState == FocusState.Keyboard || Legend.FocusState == FocusState.Keyboard;
        stripe.Scale = active ? new Vector3(1, 1.38f, 1) : Vector3.One;
        dot.Scale = active ? new Vector3(1.25f, 1.25f, 1) : Vector3.One;
    }
}
