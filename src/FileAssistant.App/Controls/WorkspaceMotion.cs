using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace FileAssistant.App.Controls;

/// <summary>
/// GPU-accelerated WinUI 3 Composition motion and micro-interaction helpers.
/// All methods degrade safely when elements are unloaded or running in headless unit tests.
/// </summary>
internal static class WorkspaceMotion
{
    private static readonly Windows.UI.ViewManagement.UISettings SystemSettings = new();
    public static bool Enabled => (App.Preferences?.Current.Animations ?? true) && SystemSettings.AnimationsEnabled;

    // A cached operation panel can be loaded multiple times with the same buttons.
    private static readonly ConditionalWeakTable<FrameworkElement, object> tactileElements = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> hoverElements = new();
    private static readonly ConditionalWeakTable<object, DispatcherQueueTimer> activeWidthTimers = new();
    private static readonly object attached = new();
    public static void PlayStageEntrance(UIElement? element, int direction = 1)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(0f, new Vector3(0f, direction >= 0 ? 9f : -9f, 0f));
            offsetAnim.InsertKeyFrame(1f, Vector3.Zero, ease);
            offsetAnim.Duration = TimeSpan.FromMilliseconds(185);

            var fadeAnim = compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.InsertKeyFrame(0f, 0.08f);
            fadeAnim.InsertKeyFrame(1f, 1f, ease);
            fadeAnim.Duration = TimeSpan.FromMilliseconds(185);

            visual.StartAnimation("Offset", offsetAnim);
            visual.StartAnimation("Opacity", fadeAnim);
        }
        catch (Exception) { }
    }

    public static void PlayThemeCrossfade(UIElement? element)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0.84f);
            fade.InsertKeyFrame(1f, 1f, ease);
            fade.Duration = TimeSpan.FromMilliseconds(220);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception) { }
    }

    public static void SetOrAnimateRotation(UIElement? element, float targetDegrees, bool animate, int durationMs = 240)
    {
        if (element is not FrameworkElement fe) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            EnsureIconCenterPoint(fe, visual);
            if (!animate || !Enabled || !fe.IsLoaded)
            {
                visual.StopAnimation("RotationAngleInDegrees");
                visual.RotationAngleInDegrees = targetDegrees;
                return;
            }
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
            var rot = compositor.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(1f, targetDegrees, ease);
            rot.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("RotationAngleInDegrees", rot);
        }
        catch (Exception) { }
    }

    public static void PlayIconFlip90(UIElement? element, bool clockwise, int durationMs = 300)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            EnsureIconCenterPoint(fe, visual);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var rot = compositor.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(0f, clockwise ? -90f : 90f);
            rot.InsertKeyFrame(1f, 0f, ease);
            rot.Duration = TimeSpan.FromMilliseconds(durationMs);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.82f, 0.82f, 1f));
            scale.InsertKeyFrame(1f, Vector3.One, ease);
            scale.Duration = TimeSpan.FromMilliseconds(durationMs);

            visual.StartAnimation("RotationAngleInDegrees", rot);
            visual.StartAnimation("Scale", scale);
        }
        catch (Exception) { }
    }

    public static void PlayIconSwapSpin(UIElement? element, int durationMs = 380)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            EnsureIconCenterPoint(fe, visual);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var rot = compositor.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(0f, -180f);
            rot.InsertKeyFrame(1f, 0f, ease);
            rot.Duration = TimeSpan.FromMilliseconds(durationMs);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.68f, 0.68f, 1f));
            scale.InsertKeyFrame(1f, Vector3.One, ease);
            scale.Duration = TimeSpan.FromMilliseconds(durationMs);

            visual.StartAnimation("RotationAngleInDegrees", rot);
            visual.StartAnimation("Scale", scale);
        }
        catch (Exception) { }
    }

    public static void PlaySpinOnce(UIElement? element, float degrees = 360f, int durationMs = 440)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            EnsureIconCenterPoint(fe, visual);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var rot = compositor.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(0f, 0f);
            rot.InsertKeyFrame(1f, degrees, ease);
            rot.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("RotationAngleInDegrees", rot);
        }
        catch (Exception) { }
    }

    private static void EnsureIconCenterPoint(FrameworkElement fe, Microsoft.UI.Composition.Visual visual)
    {
        var w = fe.ActualWidth > 0 ? (float)fe.ActualWidth : 14f;
        var h = fe.ActualHeight > 0 ? (float)fe.ActualHeight : 14f;
        visual.CenterPoint = new Vector3(w * 0.5f, h * 0.5f, 0f);
    }

    public static void PlayCascadeItemEntrance(UIElement? container, int index)
    {
        if (!Enabled || container is not FrameworkElement fe || index < 0 || index >= 12) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(container);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
            var delay = TimeSpan.FromMilliseconds(Math.Min(index, 10) * 18);

            visual.Opacity = 0f;
            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.InsertKeyFrame(0f, new Vector3(0f, 10f, 0f));
            offset.InsertKeyFrame(1f, Vector3.Zero, ease);
            offset.DelayTime = delay;
            offset.Duration = TimeSpan.FromMilliseconds(210);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            fade.DelayTime = delay;
            fade.Duration = TimeSpan.FromMilliseconds(210);

            visual.StartAnimation("Offset", offset);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception) { }
    }

    public static void PlayPopBounce(UIElement? element, float peakScale = 1.18f)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            UpdateCenterPoint(fe, visual);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 1.25f), new Vector2(0.32f, 1f));

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(0.86f, 0.86f, 1f));
            scale.InsertKeyFrame(0.55f, new Vector3(peakScale, peakScale, 1f), ease);
            scale.InsertKeyFrame(1f, Vector3.One, ease);
            scale.Duration = TimeSpan.FromMilliseconds(230);
            visual.StartAnimation("Scale", scale);
        }
        catch (Exception) { }
    }

    public static void AnimateScale(UIElement? element, float targetScale, int durationMs = 150)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            UpdateCenterPoint(fe, visual);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(1f, new Vector3(targetScale, targetScale, 1f), ease);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Scale", anim);
        }
        catch (Exception) { }
    }

    public static void AttachTactilePress(UIElement? element, float pressedScale = 0.968f)
    {
        if (element is not FrameworkElement fe || !tactileElements.TryAdd(fe, attached)) return;
        fe.SizeChanged += (_, _) =>
        {
            try { UpdateCenterPoint(fe, ElementCompositionPreview.GetElementVisual(fe)); } catch (Exception) { }
        };
        fe.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => AnimateScale(fe, pressedScale, 95)), handledEventsToo: true);
        fe.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => AnimateScale(fe, 1f, 175)), handledEventsToo: true);
        fe.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => AnimateScale(fe, 1f, 160)), handledEventsToo: true);
        fe.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => AnimateScale(fe, 1f, 160)), handledEventsToo: true);
    }

    public static void AttachHoverLift(UIElement? element, float liftY = -2.5f)
    {
        // Virtualized file tiles reuse the same container on each scroll.
        if (element is not FrameworkElement fe || !hoverElements.TryAdd(fe, attached)) return;
        ElementCompositionPreview.SetIsTranslationEnabled(fe, true);
        fe.PointerEntered += (_, _) => AnimateTranslationY(fe, liftY, 170);
        fe.PointerExited += (_, _) => AnimateTranslationY(fe, 0f, 180);
    }

    public static void AnimateTranslationY(UIElement? element, float targetY, int durationMs = 170)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(fe, true);
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, targetY, ease);
            anim.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Translation.Y", anim);
        }
        catch (Exception) { }
    }

    public static void AnimateSpringIsland(FrameworkElement? island, bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (island is null || island.Visibility == visibility) return;
        try
        {
            Microsoft.UI.Composition.CompositionAnimationGroup? group = null;
            if (Enabled && island.IsLoaded)
            {
                ElementCompositionPreview.SetIsTranslationEnabled(island, true);
                var visual = ElementCompositionPreview.GetElementVisual(island);
                UpdateCenterPoint(island, visual);
                var compositor = visual.Compositor;
                group = compositor.CreateAnimationGroup();
                var ease = show
                    ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 1.24f), new Vector2(0.32f, 1f))
                    : compositor.CreateCubicBezierEasingFunction(new Vector2(0.32f, 0f), new Vector2(0.67f, 0f));
                var lift = compositor.CreateScalarKeyFrameAnimation();
                lift.Target = "Translation.Y";
                lift.InsertKeyFrame(0f, show ? 24f : 0f);
                lift.InsertKeyFrame(1f, show ? 0f : 18f, ease);
                lift.Duration = TimeSpan.FromMilliseconds(show ? 260 : 160);
                group.Add(lift);

                if (show)
                {
                    var scale = compositor.CreateVector3KeyFrameAnimation();
                    scale.Target = "Scale";
                    scale.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
                    scale.InsertKeyFrame(1f, Vector3.One, ease);
                    scale.Duration = lift.Duration;
                    group.Add(scale);
                }

                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Target = "Opacity";
                fade.InsertKeyFrame(0f, show ? 0f : 1f);
                fade.InsertKeyFrame(1f, show ? 1f : 0f);
                fade.Duration = TimeSpan.FromMilliseconds(show ? 180 : 150);
                group.Add(fade);
            }
            // XAML owns visibility and can interrupt a pending hide as soon as the user reselects.
            if (show) ElementCompositionPreview.SetImplicitShowAnimation(island, group);
            else ElementCompositionPreview.SetImplicitHideAnimation(island, group);
        }
        catch (System.Runtime.InteropServices.COMException) { }
        island.Visibility = visibility;
    }

    public static void AnimateTreemapDrill(UIElement? element, bool zoomIn)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            UpdateCenterPoint(fe, visual);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

            var scale = compositor.CreateVector3KeyFrameAnimation();
            var start = zoomIn ? 0.94f : 1.05f;
            scale.InsertKeyFrame(0f, new Vector3(start, start, 1f));
            scale.InsertKeyFrame(1f, Vector3.One, ease);
            scale.Duration = TimeSpan.FromMilliseconds(220);

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0.28f);
            fade.InsertKeyFrame(1f, 1f, ease);
            fade.Duration = TimeSpan.FromMilliseconds(200);

            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception) { }
    }

    public static void AnimateFadeIn(UIElement? element, int durationMs = 200)
    {
        if (!Enabled || element is not FrameworkElement fe || !fe.IsLoaded) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f, ease);
            fade.Duration = TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception) { }
    }

    public static void CancelWidthAnimation(object? tokenOwner)
    {
        if (tokenOwner is null) return;
        if (activeWidthTimers.TryGetValue(tokenOwner, out var existing))
        {
            existing.Stop();
            activeWidthTimers.Remove(tokenOwner);
        }
    }

    public static void AnimateWidth(DispatcherQueue? queue, double from, double to, double durationMs, Action<double> step, Action? completed = null) =>
        AnimateWidth(queue, tokenOwner: null, from, to, durationMs, step, completed);

    public static void AnimateWidth(DispatcherQueue? queue, object? tokenOwner, double from, double to, double durationMs, Action<double> step, Action? completed = null)
    {
        CancelWidthAnimation(tokenOwner);
        if (queue is null || Math.Abs(to - from) < 0.5 || !Enabled)
        {
            step(to);
            completed?.Invoke();
            return;
        }
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.IsRepeating = true;
        if (tokenOwner is not null) activeWidthTimers.AddOrUpdate(tokenOwner, timer);
        var startTicks = Stopwatch.GetTimestamp();
        timer.Tick += (_, _) =>
        {
            if (tokenOwner is not null && (!activeWidthTimers.TryGetValue(tokenOwner, out var active) || !ReferenceEquals(active, timer)))
            {
                timer.Stop();
                return;
            }
            var raw = Math.Clamp(Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds / durationMs, 0, 1);
            var eased = 1.0 - Math.Pow(1.0 - raw, 4);
            step(from + (to - from) * eased);
            if (raw >= 1.0)
            {
                timer.Stop();
                if (tokenOwner is not null) activeWidthTimers.Remove(tokenOwner);
                step(to);
                completed?.Invoke();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Shared smooth width + opacity transition for left sidebar drawers (分组目录 / 创建位置 / 软件分类).
    /// Safe against rapid re-toggling and respects Windows animation accessibility settings.
    /// </summary>
    public static void AnimateSidePaneToggle(
        FrameworkElement owner,
        ColumnDefinition paneColumn,
        ColumnDefinition gapColumn,
        FrameworkElement paneSurface,
        FrameworkElement collapsedRail,
        FrameworkElement resizeHandle,
        FrameworkElement mainContent,
        bool opening,
        double expandedWidth,
        double collapsedWidth,
        Action applyRestingGeometry,
        double durationMs = 200)
    {
        var targetWidth = opening ? expandedWidth : collapsedWidth;
        var visualGap = ResizeHandle.PaneSpacing;
        var prevOverlap = gapColumn.Width.IsAbsolute && gapColumn.Width.Value > visualGap
            ? (gapColumn.Width.Value - visualGap) * 0.5
            : 0;
        var currentWidth = paneColumn.Width.IsAbsolute
            ? paneColumn.Width.Value + prevOverlap
            : (opening ? collapsedWidth : expandedWidth);

        if (!owner.IsLoaded || owner.ActualWidth <= 0 || Math.Abs(targetWidth - currentWidth) < 0.5 || !Enabled)
        {
            CancelWidthAnimation(owner);
            paneSurface.Opacity = 1;
            applyRestingGeometry();
            return;
        }

        var startGap = gapColumn.Width.IsAbsolute ? Math.Min(gapColumn.Width.Value, visualGap) : (opening && collapsedWidth <= 0 ? 0 : visualGap);
        var endGap = opening || collapsedWidth > 0 ? visualGap : 0;

        resizeHandle.Visibility = Visibility.Collapsed;
        collapsedRail.Visibility = Visibility.Collapsed;
        gapColumn.Width = new GridLength(startGap);
        Grid.SetColumn(paneSurface, 0);
        Grid.SetColumnSpan(paneSurface, 1);
        paneSurface.Margin = new Thickness(0);
        Grid.SetColumn(mainContent, 2);
        Grid.SetColumnSpan(mainContent, 1);
        mainContent.Margin = new Thickness(0);
        paneSurface.Visibility = Visibility.Visible;

        var totalDelta = targetWidth - currentWidth;
        var opacitySpan = Math.Max(1, expandedWidth - collapsedWidth);
        AnimateWidth(owner.DispatcherQueue, owner, currentWidth, targetWidth, durationMs, w =>
        {
            paneColumn.Width = new GridLength(Math.Max(0, w));
            var progress = Math.Clamp((w - currentWidth) / totalDelta, 0, 1);
            gapColumn.Width = new GridLength(Math.Max(0, startGap + (endGap - startGap) * progress));
            paneSurface.Opacity = Math.Clamp((w - collapsedWidth) / opacitySpan, 0, 1);
        }, () =>
        {
            paneSurface.Opacity = 1;
            applyRestingGeometry();
        });
    }

    public static void AnimateRollingBytes(TextBlock? label, long targetBytes, Func<long, string> formatter, double durationMs = 420)
    {
        if (label is null) return;
        if (!Enabled || !label.IsLoaded || label.DispatcherQueue is not { } queue || targetBytes <= 0)
        {
            label.Text = formatter(targetBytes);
            return;
        }
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        var startTicks = Stopwatch.GetTimestamp();
        timer.Tick += (_, _) =>
        {
            var raw = Math.Clamp(Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds / durationMs, 0, 1);
            var eased = 1.0 - Math.Pow(1.0 - raw, 3);
            var val = (long)Math.Round(targetBytes * eased);
            label.Text = formatter(val);
            if (raw >= 1.0)
            {
                timer.Stop();
                label.Text = formatter(targetBytes);
            }
        };
        timer.Start();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object> AlignedInputRegistry = new();

    /// <summary>
    /// Ensures WinUI 3 single-line TextBox, NumberBox, ComboBox, and AutoSuggestBox controls vertically center
    /// their internal ContentElement and PlaceholderTextContentPresenter (which WinUI defaults to Top).
    /// </summary>
    public static void AlignSingleLineInputs(DependencyObject? root)
    {
        if (root is null) return;
        if (root is TextBox tb)
        {
            if (!tb.AcceptsReturn) AttachSingleLineCentering(tb);
            return;
        }
        if (root is NumberBox or ComboBox or AutoSuggestBox && root is FrameworkElement composite)
        {
            if (!AlignedInputRegistry.TryGetValue(composite, out _))
            {
                AlignedInputRegistry.Add(composite, string.Empty);
                composite.Loaded += (s, _) => AlignVisualDescendants((DependencyObject)s);
                composite.SizeChanged += (s, _) => AlignVisualDescendants((DependencyObject)s);
            }
            AlignVisualDescendants(composite);
            return;
        }

        switch (root)
        {
            case Panel panel:
                foreach (var child in panel.Children) AlignSingleLineInputs(child);
                break;
            case Border { Child: { } borderChild }:
                AlignSingleLineInputs(borderChild);
                break;
            case ScrollViewer { Content: DependencyObject scrollContent }:
                AlignSingleLineInputs(scrollContent);
                break;
            case ContentPresenter { Content: DependencyObject presenterContent }:
                AlignSingleLineInputs(presenterContent);
                break;
            case ContentControl { Content: DependencyObject controlContent }:
                AlignSingleLineInputs(controlContent);
                break;
            case UserControl uc:
                var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(uc);
                for (var i = 0; i < count; i++)
                    AlignSingleLineInputs(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(uc, i));
                break;
        }
    }

    private static void AlignVisualDescendants(DependencyObject parent)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox innerTb && !innerTb.AcceptsReturn)
                ApplyTextBoxVerticalCenter(innerTb);
            else
                AlignVisualDescendants(child);
        }
    }

    private static void AttachSingleLineCentering(TextBox tb)
    {
        if (!AlignedInputRegistry.TryGetValue(tb, out _))
        {
            AlignedInputRegistry.Add(tb, string.Empty);
            tb.Loaded += (s, _) => ApplyTextBoxVerticalCenter((TextBox)s);
            tb.SizeChanged += (s, _) => ApplyTextBoxVerticalCenter((TextBox)s);
        }
        ApplyTextBoxVerticalCenter(tb);
    }

    private static void ApplyTextBoxVerticalCenter(TextBox tb)
    {
        if (tb.AcceptsReturn || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(tb) == 0) return;
        CenterTextBoxTemplateParts(tb, tb.Padding.Left, tb.Padding.Right);
    }

    private static void CenterTextBoxTemplateParts(DependencyObject node, double leftPad, double rightPad)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer { Name: "ContentElement" } scroller)
            {
                scroller.VerticalAlignment = VerticalAlignment.Center;
                scroller.Margin = new Thickness(0);
                scroller.Padding = new Thickness(leftPad, 0, rightPad, 0);
            }
            else if (child is TextBlock { Name: "PlaceholderTextContentPresenter" } placeholderTb)
            {
                placeholderTb.VerticalAlignment = VerticalAlignment.Center;
                placeholderTb.Margin = new Thickness(0);
                placeholderTb.Padding = new Thickness(leftPad, 0, rightPad, 0);
            }
            else if (child is ContentControl { Name: "PlaceholderTextContentPresenter" } placeholderCc)
            {
                placeholderCc.VerticalAlignment = VerticalAlignment.Center;
                placeholderCc.VerticalContentAlignment = VerticalAlignment.Center;
                placeholderCc.Margin = new Thickness(0);
                placeholderCc.Padding = new Thickness(leftPad, 0, rightPad, 0);
            }
            else
            {
                CenterTextBoxTemplateParts(child, leftPad, rightPad);
            }
        }
    }

    private static void UpdateCenterPoint(FrameworkElement fe, Microsoft.UI.Composition.Visual visual)
    {
        if (fe.ActualWidth > 0 && fe.ActualHeight > 0)
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
    }
}
