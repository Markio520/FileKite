using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FileAssistant.App.Controls;

/// <summary>
/// Shared bottom-right resize grip for MainWindow, paired with <see cref="ResizableDialogShell"/>
/// so both the main window and all centered modal dialogs expose a unified resize affordance.
/// </summary>
public sealed class WindowResizeGrip : UserControl
{
    private readonly Border surface;
    private readonly Microsoft.UI.Xaml.Shapes.Path glyph;
    private bool dragging;
    private uint activePointerId;
    private PointInt32 startCursor;
    private Windows.Graphics.SizeInt32 startWindowSize;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt32 { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 lpPoint);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public WindowResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
        IsTabStop = false;

        glyph = ResizableDialogShell.CreateGripGlyph();
        surface = new Border
        {
            Style = Application.Current.Resources.TryGetValue("WorkspaceWindowResizeGripStyle", out var s) && s is Style st ? st : null,
            Child = glyph,
            Opacity = 0.42
        };
        Content = surface;

        WorkspaceLocalization.SetName(this, "调整窗口大小");
        WorkspaceLocalization.SetTip(this, "拖动边缘或右下角调整窗口大小，双击恢复默认尺寸");

        PointerEntered += (_, _) => { surface.Opacity = 0.92; glyph.Stroke = ResizableDialogShell.ResolveBrush(ActualTheme, "WorkspaceAccentBrush"); };
        PointerExited += (_, _) => { if (!dragging) ResetVisual(); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnDoubleTapped), handledEventsToo: true);

        Loaded += (_, _) =>
        {
            ResetVisual();
            if (App.Window?.AppWindow is { } appWin)
            {
                UpdateMaximizedVisibility(appWin);
                appWin.Changed += AppWindow_Changed;
            }
        };
        Unloaded += (_, _) =>
        {
            if (App.Window?.AppWindow is { } appWin)
                appWin.Changed -= AppWindow_Changed;
        };
        ActualThemeChanged += (_, _) => { if (!dragging) ResetVisual(); };
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange)
            DispatcherQueue?.TryEnqueue(() => UpdateMaximizedVisibility(sender));
    }

    private void UpdateMaximizedVisibility(AppWindow appWin)
    {
        var maximized = appWin.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetVisual()
    {
        surface.Opacity = 0.42;
        glyph.Stroke = ResizableDialogShell.ResolveBrush(ActualTheme, "WorkspaceSecondaryBrush");
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (App.Window?.AppWindow is not { } appWin) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!GetCursorPos(out startCursor)) return;
        dragging = true;
        activePointerId = e.Pointer.PointerId;
        startWindowSize = appWin.Size;
        CapturePointer(e.Pointer);
        surface.Opacity = 1.0;
        glyph.Stroke = ResizableDialogShell.ResolveBrush(ActualTheme, "WorkspaceAccentBrush");
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging || e.Pointer.PointerId != activePointerId || App.Window is null) return;
        if (!GetCursorPos(out var cur)) return;
        var hwnd = App.WindowHandle;
        var scale = Math.Max(1.0, GetDpiForWindow(hwnd) / 96.0);
        var minW = (int)Math.Round(520 * scale);
        var minH = (int)Math.Round(390 * scale);
        var nextW = Math.Max(minW, startWindowSize.Width + (cur.X - startCursor.X));
        var nextH = Math.Max(minH, startWindowSize.Height + (cur.Y - startCursor.Y));
        SetWindowPos(hwnd, 0, 0, 0, nextW, nextH, SwpNoMove | SwpNoZOrder | SwpNoActivate);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging || e.Pointer.PointerId != activePointerId) return;
        dragging = false;
        ReleasePointerCapture(e.Pointer);
        ResetVisual();
        e.Handled = true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleasePointerCaptures();
        ResetVisual();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (App.Window is MainWindow main)
        {
            main.SetWorkspaceSize(compact: false);
            e.Handled = true;
        }
    }
}

/// <summary>
/// Shared modal window chrome for all <see cref="ContentDialog"/> surfaces:
/// outer 4-edge + 4-corner anchored resizing (opposite edge stays stationary),
/// top-header drag-to-move with double-click center reset, and bottom-right corner grip.
/// </summary>
public static class ResizableDialogShell
{
    private static readonly ConditionalWeakTable<ContentDialog, ChromeState> attached = new();

    private sealed class ChromeState
    {
        public FrameworkElement? CardElement;
        public TranslateTransform? Translate;
    }

    private sealed class ResizeZone : Grid
    {
        public ResizeZone(InputSystemCursorShape cursorShape)
        {
            Background = new SolidColorBrush(Colors.Transparent);
            ProtectedCursor = InputSystemCursor.Create(cursorShape);
        }
    }

    internal static Brush ResolveBrush(ElementTheme theme, string key) =>
        Resources.WorkspaceMaterials.ThemeBrush(theme, key);

    internal static Microsoft.UI.Xaml.Shapes.Path CreateGripGlyph()
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry { StartPoint = new Point(10, 2.5), EndPoint = new Point(2.5, 10) });
        geometry.Children.Add(new LineGeometry { StartPoint = new Point(10, 6.5), EndPoint = new Point(6.5, 10) });
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
            UseLayoutRounding = true
        };
    }

    /// <summary>
    /// Automatically wires outer-rim resizing and top-header drag-to-move onto any generic ContentDialog
    /// (called by <see cref="DialogDismiss"/> on dialog open when not explicitly configured via <see cref="Attach"/>).
    /// </summary>
    internal static void AttachAuto(ContentDialog dialog)
    {
        if (attached.TryGetValue(dialog, out var existing))
        {
            EnsureCardTranslate(dialog, existing);
            return;
        }

        var bg = FindDescendant<FrameworkElement>(dialog, "BackgroundElement");
        var space = FindDescendant<Grid>(dialog, "DialogSpace");
        if (bg is null || space is null) return;

        Attach(
            dialog,
            space,
            minWidth: 320,
            minHeight: 160,
            onResized: (w, h) =>
            {
                bg.Width = w;
                bg.Height = h;
                bg.MaxWidth = 2400;
                bg.MaxHeight = 1600;
            },
            onReset: () =>
            {
                bg.Width = double.NaN;
                bg.Height = double.NaN;
                ResetPosition(dialog);
            },
            dragHeaderHeight: 54);
    }

    public static void ResetPosition(ContentDialog dialog)
    {
        if (attached.TryGetValue(dialog, out var state) && state.Translate is { } t)
        {
            t.X = 0;
            t.Y = 0;
        }
    }

    public static Border Attach(
        ContentDialog dialog,
        Grid layoutRoot,
        double minWidth = 380,
        double minHeight = 280,
        Action<double, double>? onResized = null,
        Action? onReset = null,
        double dragHeaderHeight = 56)
    {
        var state = attached.GetOrCreateValue(dialog);
        dialog.Resources["ContentDialogMaxWidth"] = 2400d;
        dialog.Resources["ContentDialogMaxHeight"] = 1600d;

        double defaultWidth = layoutRoot.Width;
        double defaultHeight = layoutRoot.Height;

        var gripGlyph = CreateGripGlyph();
        var gripVisual = new Border
        {
            Name = "DialogResizeGrip",
            Style = Application.Current.Resources.TryGetValue("WorkspaceWindowResizeGripStyle", out var s) && s is Style st ? st : null,
            Child = gripGlyph,
            Opacity = 0.45,
            IsHitTestVisible = false
        };

        void SyncGripBrush(bool highlighted)
        {
            var theme = layoutRoot.ActualTheme == ElementTheme.Default ? dialog.RequestedTheme : layoutRoot.ActualTheme;
            gripVisual.Opacity = highlighted ? 0.95 : 0.45;
            gripGlyph.Stroke = ResolveBrush(theme, highlighted ? "WorkspaceAccentBrush" : "WorkspaceSecondaryBrush");
        }

        dialog.Opened += (_, _) =>
        {
            EnsureCardTranslate(dialog, state);
            SyncGripBrush(false);
        };

        layoutRoot.Loaded += (_, _) =>
        {
            EnsureCardTranslate(dialog, state);
            if (double.IsNaN(defaultWidth) && layoutRoot.ActualWidth > 0) defaultWidth = layoutRoot.ActualWidth;
            if (double.IsNaN(defaultHeight) && layoutRoot.ActualHeight > 0) defaultHeight = layoutRoot.ActualHeight;
            SyncGripBrush(false);
        };
        layoutRoot.ActualThemeChanged += (_, _) => SyncGripBrush(false);

        // Compensate for any Padding on layoutRoot so all 4 outer edges & 4 outer corners always sit flush on the outer border.
        var pad = layoutRoot.Padding;

        void AddZone(int dxSign, int dySign, HorizontalAlignment hAlign, VerticalAlignment vAlign, double width, double height, InputSystemCursorShape cursor, bool isBottomRightGrip = false)
        {
            var zone = new ResizeZone(cursor)
            {
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign
            };
            if (!double.IsNaN(width)) zone.Width = width;
            if (!double.IsNaN(height)) zone.Height = height;

            double ml = hAlign == HorizontalAlignment.Stretch ? 14 - pad.Left : (hAlign == HorizontalAlignment.Left ? -pad.Left : 0);
            double mr = hAlign == HorizontalAlignment.Stretch ? 14 - pad.Right : (hAlign == HorizontalAlignment.Right ? -pad.Right : 0);
            double mt = vAlign == VerticalAlignment.Stretch ? 14 - pad.Top : (vAlign == VerticalAlignment.Top ? -pad.Top : 0);
            double mb = vAlign == VerticalAlignment.Stretch ? 14 - pad.Bottom : (vAlign == VerticalAlignment.Bottom ? -pad.Bottom : 0);
            zone.Margin = new Thickness(ml, mt, mr, mb);

            Grid.SetRowSpan(zone, 99);
            Grid.SetColumnSpan(zone, 99);
            Canvas.SetZIndex(zone, isBottomRightGrip ? 84 : (dxSign != 0 && dySign != 0 ? 82 : 80));

            if (isBottomRightGrip)
            {
                zone.Children.Add(gripVisual);
                WorkspaceLocalization.SetName(zone, "调整窗口大小");
                WorkspaceLocalization.SetTip(zone, "拖动四边或四角调整窗口大小，双击恢复默认尺寸");
                zone.PointerEntered += (_, _) => SyncGripBrush(true);
                zone.PointerExited += (_, _) => SyncGripBrush(false);
                zone.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, e) =>
                {
                    if (onReset is not null) onReset();
                    else
                    {
                        if (!double.IsNaN(defaultWidth) && defaultWidth > 0) layoutRoot.Width = defaultWidth;
                        if (!double.IsNaN(defaultHeight) && defaultHeight > 0) layoutRoot.Height = defaultHeight;
                    }
                    ResetPosition(dialog);
                    e.Handled = true;
                }), handledEventsToo: true);
            }

            bool dragging = false;
            uint pointerId = 0;
            Point startPt = default;
            double startW = 0, startH = 0, startTx = 0, startTy = 0;

            zone.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(zone).Properties.IsLeftButtonPressed) return;
                var card = EnsureCardTranslate(dialog, state);
                var w = card is not null && card.ActualWidth > 0 && onResized is not null ? layoutRoot.ActualWidth : layoutRoot.ActualWidth;
                var h = layoutRoot.ActualHeight;
                if (w <= 0 || h <= 0) return;
                dragging = true;
                pointerId = e.Pointer.PointerId;
                startPt = e.GetCurrentPoint(null).Position;
                startW = w;
                startH = h;
                startTx = state.Translate?.X ?? 0;
                startTy = state.Translate?.Y ?? 0;
                zone.CapturePointer(e.Pointer);
                if (isBottomRightGrip) SyncGripBrush(true);
                e.Handled = true;
            };

            zone.PointerMoved += (_, e) =>
            {
                if (!dragging || e.Pointer.PointerId != pointerId || dialog.XamlRoot is not { } xr) return;
                var cur = e.GetCurrentPoint(null).Position;
                var maxW = Math.Max(minWidth, xr.Size.Width - 20);
                var maxH = Math.Max(minHeight, xr.Size.Height - 20);

                // Anchored opposite-edge resize: adjust Width/Height by 1x delta and shift Translate by half delta,
                // so the dragged outer edge tracks the pointer 1:1 while the opposite edge stays completely stationary.
                var nextW = dxSign == 0 ? startW : Math.Clamp(startW + dxSign * (cur.X - startPt.X), minWidth, maxW);
                var nextH = dySign == 0 ? startH : Math.Clamp(startH + dySign * (cur.Y - startPt.Y), minHeight, maxH);

                if (onResized is not null) onResized(nextW, nextH);
                else
                {
                    if (dxSign != 0) layoutRoot.Width = nextW;
                    if (dySign != 0) layoutRoot.Height = nextH;
                }

                if (state.Translate is { } t)
                {
                    if (dxSign != 0) t.X = startTx + dxSign * (nextW - startW) * 0.5;
                    if (dySign != 0) t.Y = startTy + dySign * (nextH - startH) * 0.5;
                    ClampTranslate(xr.Size, layoutRoot, t);
                }
                e.Handled = true;
            };

            zone.PointerReleased += (_, e) =>
            {
                if (!dragging || e.Pointer.PointerId != pointerId) return;
                dragging = false;
                zone.ReleasePointerCapture(e.Pointer);
                if (isBottomRightGrip) SyncGripBrush(false);
                e.Handled = true;
            };

            zone.PointerCanceled += (_, _) =>
            {
                if (!dragging) return;
                dragging = false;
                zone.ReleasePointerCaptures();
                if (isBottomRightGrip) SyncGripBrush(false);
            };

            layoutRoot.Children.Add(zone);
        }

        // Outer 4 edges + outer 4 corners
        AddZone(-1, 0, HorizontalAlignment.Left, VerticalAlignment.Stretch, 8, double.NaN, InputSystemCursorShape.SizeWestEast);
        AddZone(1, 0, HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, InputSystemCursorShape.SizeWestEast);
        AddZone(0, -1, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 8, InputSystemCursorShape.SizeNorthSouth);
        AddZone(0, 1, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, InputSystemCursorShape.SizeNorthSouth);
        AddZone(-1, -1, HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14, InputSystemCursorShape.SizeNorthwestSoutheast);
        AddZone(1, -1, HorizontalAlignment.Right, VerticalAlignment.Top, 14, 14, InputSystemCursorShape.SizeNortheastSouthwest);
        AddZone(-1, 1, HorizontalAlignment.Left, VerticalAlignment.Bottom, 14, 14, InputSystemCursorShape.SizeNortheastSouthwest);
        AddZone(1, 1, HorizontalAlignment.Right, VerticalAlignment.Bottom, 18, 18, InputSystemCursorShape.SizeNorthwestSoutheast, isBottomRightGrip: true);

        // Shared top-header drag-to-move behavior
        WireHeaderMove(dialog, layoutRoot, state, dragHeaderHeight);

        return gripVisual;
    }

    private static void WireHeaderMove(ContentDialog dialog, Grid layoutRoot, ChromeState state, double headerHeight)
    {
        bool moving = false;
        uint movePointerId = 0;
        Point startPt = default;
        double startTx = 0, startTy = 0;

        layoutRoot.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(layoutRoot).Properties.IsLeftButtonPressed) return;
            var local = e.GetCurrentPoint(layoutRoot).Position;
            if (local.Y > headerHeight || local.Y < 8 || local.X < 8 || local.X > layoutRoot.ActualWidth - 8) return;
            if (IsInteractiveSource(e.OriginalSource as DependencyObject, layoutRoot)) return;
            EnsureCardTranslate(dialog, state);
            if (state.Translate is not { } t) return;

            moving = true;
            movePointerId = e.Pointer.PointerId;
            startPt = e.GetCurrentPoint(null).Position;
            startTx = t.X;
            startTy = t.Y;
            layoutRoot.CapturePointer(e.Pointer);
            e.Handled = true;
        };

        layoutRoot.PointerMoved += (_, e) =>
        {
            if (!moving || e.Pointer.PointerId != movePointerId || state.Translate is not { } t || dialog.XamlRoot is not { } xr) return;
            var cur = e.GetCurrentPoint(null).Position;
            t.X = startTx + (cur.X - startPt.X);
            t.Y = startTy + (cur.Y - startPt.Y);
            ClampTranslate(xr.Size, layoutRoot, t);
            e.Handled = true;
        };

        layoutRoot.PointerReleased += (_, e) =>
        {
            if (!moving || e.Pointer.PointerId != movePointerId) return;
            moving = false;
            layoutRoot.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };

        layoutRoot.PointerCanceled += (_, _) =>
        {
            if (!moving) return;
            moving = false;
            layoutRoot.ReleasePointerCaptures();
        };

        layoutRoot.DoubleTapped += (_, e) =>
        {
            if (e.Handled) return;
            var local = e.GetPosition(layoutRoot);
            if (local.Y > headerHeight || IsInteractiveSource(e.OriginalSource as DependencyObject, layoutRoot)) return;
            ResetPosition(dialog);
            e.Handled = true;
        };
    }

    private static bool IsInteractiveSource(DependencyObject? source, DependencyObject stopAt)
    {
        for (var cur = source; cur is not null && !ReferenceEquals(cur, stopAt); cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is ButtonBase or TextBox or AutoSuggestBox or ComboBox or Slider or ToggleSwitch or ScrollBar or ListViewItem or ResizeZone)
                return true;
        }
        return false;
    }

    private static FrameworkElement? EnsureCardTranslate(ContentDialog dialog, ChromeState state)
    {
        if (state.CardElement is null)
            state.CardElement = FindDescendant<FrameworkElement>(dialog, "BackgroundElement");
        if (state.CardElement is not { } card) return null;
        if (state.Translate is null)
        {
            if (card.RenderTransform is TranslateTransform existing)
                state.Translate = existing;
            else if (card.RenderTransform is TransformGroup group)
            {
                var found = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (found is null) { found = new TranslateTransform(); group.Children.Add(found); }
                state.Translate = found;
            }
            else
            {
                state.Translate = new TranslateTransform();
                card.RenderTransform = state.Translate;
            }
        }
        return card;
    }

    private static void ClampTranslate(Size viewport, FrameworkElement card, TranslateTransform translate)
    {
        var w = Math.Max(160, card.ActualWidth);
        var h = Math.Max(120, card.ActualHeight);
        var maxX = Math.Max(0, (viewport.Width + w) * 0.5 - 88);
        var maxY = Math.Max(0, (viewport.Height - h) * 0.5);
        var minY = -maxY;
        var bottomLimit = Math.Max(maxY, (viewport.Height + h) * 0.5 - 52);
        translate.X = Math.Clamp(translate.X, -maxX, maxX);
        translate.Y = Math.Clamp(translate.Y, minY, bottomLimit);
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }
}
