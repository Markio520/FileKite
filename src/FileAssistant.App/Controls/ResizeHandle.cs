using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileAssistant.App.Controls;

/// <summary>Shared drag and keyboard input for table columns, side panes and list height.</summary>
public sealed class ResizeHandle : UserControl
{
    public const double PaneSpacing = 0;
    public const double HitTargetThickness = 10;
    public const double HitTargetOverlap = 5;
    public event EventHandler<double>? ResizeRequested;
    public event EventHandler? ResetRequested;
    private readonly Thumb thumb;
    public static readonly DependencyProperty ResizeDirectionProperty = DependencyProperty.Register(
        nameof(ResizeDirection), typeof(Orientation), typeof(ResizeHandle),
        new PropertyMetadata(Orientation.Horizontal, (owner, _) => ((ResizeHandle)owner).UpdateDirection()));
    public Orientation ResizeDirection
    {
        get => (Orientation)GetValue(ResizeDirectionProperty);
        set => SetValue(ResizeDirectionProperty, value);
    }

    public ResizeHandle()
    {
        thumb = new Thumb();
        Content = thumb;
        Style = (Style)Application.Current.Resources["ResizeHandleStyle"];
        UpdateDirection();
        Loaded += (_, _) =>
        {
            AutomationProperties.SetName(thumb, AutomationProperties.GetName(this));
            if (ResetRequested is null) return;
            const string hint = "拖动或使用左右方向键调整宽度，双击恢复默认宽度";
            AutomationProperties.SetHelpText(thumb, hint);
            ToolTipService.SetToolTip(this, hint);
        };
        AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(Handle_DoubleTapped), handledEventsToo: true);
        thumb.DragStarted += (_, _) => thumb.Focus(FocusState.Pointer);
        thumb.DragDelta += (_, e) => ResizeRequested?.Invoke(this, ResizeDirection == Orientation.Vertical ? e.VerticalChange : e.HorizontalChange);
        thumb.KeyDown += Handle_KeyDown;
    }

    private void UpdateDirection()
    {
        var vertical = ResizeDirection == Orientation.Vertical;
        thumb.Style = (Style)Application.Current.Resources[vertical ? "VerticalResizeThumbStyle" : "ResizeThumbStyle"];
        ProtectedCursor = InputSystemCursor.Create(vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
    }

    private void Handle_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var decrease = ResizeDirection == Orientation.Vertical ? VirtualKey.Up : VirtualKey.Left;
        var increase = ResizeDirection == Orientation.Vertical ? VirtualKey.Down : VirtualKey.Right;
        if (e.Key != decrease && e.Key != increase) return;
        ResizeRequested?.Invoke(this, e.Key == decrease ? -8 : 8);
        e.Handled = true;
    }

    private void Handle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResetRequested is not { } reset) return;
        reset(this, EventArgs.Empty);
        e.Handled = true;
    }
}
