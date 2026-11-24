using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Controls;

/// <summary>A persistent, keyboard-resizable context or operation pane.</summary>
public sealed partial class ResizablePane : UserControl
{
    private const double StandardPaneWidth = 304;
    private const double MaximumPaneWidth = 560;
    private double? preferredPaneWidth;
    private bool? wasCompact;
    private bool? lastOpenState;

    public static readonly DependencyProperty IsPaneOnLeftProperty = DependencyProperty.Register(
        nameof(IsPaneOnLeft), typeof(bool), typeof(ResizablePane), new PropertyMetadata(false, LayoutChanged));
    public static readonly DependencyProperty MinimumPaneWidthProperty = DependencyProperty.Register(
        nameof(MinimumPaneWidth), typeof(double), typeof(ResizablePane), new PropertyMetadata(240d, LayoutChanged));
    public static readonly DependencyProperty CollapseBelowWidthProperty = DependencyProperty.Register(
        nameof(CollapseBelowWidth), typeof(double), typeof(ResizablePane), new PropertyMetadata(0d, LayoutChanged));

    public static readonly DependencyProperty MainContentProperty = DependencyProperty.Register(
        nameof(MainContent), typeof(UIElement), typeof(ResizablePane), new PropertyMetadata(null));
    public static readonly DependencyProperty PaneContentProperty = DependencyProperty.Register(
        nameof(PaneContent), typeof(UIElement), typeof(ResizablePane), new PropertyMetadata(null));
    public static readonly DependencyProperty IsPaneOpenProperty = DependencyProperty.Register(
        nameof(IsPaneOpen), typeof(bool), typeof(ResizablePane), new PropertyMetadata(true, LayoutChanged));
    public static readonly DependencyProperty DefaultPaneWidthProperty = DependencyProperty.Register(
        nameof(DefaultPaneWidth), typeof(double), typeof(ResizablePane), new PropertyMetadata(StandardPaneWidth, LayoutChanged));
    public static readonly DependencyProperty WidthPreferenceKeyProperty = DependencyProperty.Register(
        nameof(WidthPreferenceKey), typeof(string), typeof(ResizablePane), new PropertyMetadata(null, PreferenceKeyChanged));
    public static readonly DependencyProperty PaneSpacingProperty = DependencyProperty.Register(
        nameof(PaneSpacing), typeof(double), typeof(ResizablePane), new PropertyMetadata(ResizeHandle.PaneSpacing, LayoutChanged));
    public static readonly DependencyProperty CollapsedPaneContentProperty = DependencyProperty.Register(
        nameof(CollapsedPaneContent), typeof(UIElement), typeof(ResizablePane), new PropertyMetadata(null, LayoutChanged));
    public static readonly DependencyProperty MinimumContentWidthProperty = DependencyProperty.Register(
        nameof(MinimumContentWidth), typeof(double), typeof(ResizablePane), new PropertyMetadata(320d, LayoutChanged));

    public UIElement? MainContent { get => (UIElement?)GetValue(MainContentProperty); set => SetValue(MainContentProperty, value); }
    public UIElement? PaneContent { get => (UIElement?)GetValue(PaneContentProperty); set => SetValue(PaneContentProperty, value); }
    public bool IsPaneOpen { get => (bool)GetValue(IsPaneOpenProperty); set => SetValue(IsPaneOpenProperty, value); }
    public double DefaultPaneWidth { get => (double)GetValue(DefaultPaneWidthProperty); set => SetValue(DefaultPaneWidthProperty, value); }
    public string? WidthPreferenceKey { get => (string?)GetValue(WidthPreferenceKeyProperty); set => SetValue(WidthPreferenceKeyProperty, value); }
    public double PaneSpacing { get => (double)GetValue(PaneSpacingProperty); set => SetValue(PaneSpacingProperty, value); }
    public UIElement? CollapsedPaneContent { get => (UIElement?)GetValue(CollapsedPaneContentProperty); set => SetValue(CollapsedPaneContentProperty, value); }
    public double MinimumContentWidth { get => (double)GetValue(MinimumContentWidthProperty); set => SetValue(MinimumContentWidthProperty, value); }
    public bool IsPaneOnLeft { get => (bool)GetValue(IsPaneOnLeftProperty); set => SetValue(IsPaneOnLeftProperty, value); }
    public double MinimumPaneWidth { get => (double)GetValue(MinimumPaneWidthProperty); set => SetValue(MinimumPaneWidthProperty, value); }
    public double CollapseBelowWidth { get => (double)GetValue(CollapseBelowWidthProperty); set => SetValue(CollapseBelowWidthProperty, value); }

    public ResizablePane()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutWidths();
        Loaded += (_, _) => { App.PaneWidths.Reset += ResetWidths; UpdateLayoutWidths(); };
        Unloaded += (_, _) => App.PaneWidths.Reset -= ResetWidths;
    }

    private void ResetWidths(object? sender, EventArgs e)
    {
        preferredPaneWidth = null;
        UpdateLayoutWidths();
    }

    private static void LayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((ResizablePane)sender).UpdateLayoutWidths();

    private static void PreferenceKeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var pane = (ResizablePane)sender;
        pane.preferredPaneWidth = App.PaneWidths.GetWidth(pane.WidthPreferenceKey);
        pane.UpdateLayoutWidths();
    }

    private double VisualGap => double.IsFinite(PaneSpacing) ? Math.Max(0, PaneSpacing) : 0;
    private double AvailablePaneWidth => Math.Clamp(ActualWidth - VisualGap - (double.IsFinite(MinimumContentWidth) ? Math.Max(0, MinimumContentWidth) : 320), MinimumPaneWidth, MaximumPaneWidth);
    private double CurrentPaneWidth => Math.Clamp(preferredPaneWidth ?? (double.IsFinite(DefaultPaneWidth) ? DefaultPaneWidth : StandardPaneWidth),
        MinimumPaneWidth, AvailablePaneWidth);

    private void UpdateLayoutWidths()
    {
        if (PaneColumn is null) return;
        if (ActualWidth > 0 && CollapseBelowWidth > 0)
        {
            var compact = ActualWidth < CollapseBelowWidth;
            if (wasCompact != compact)
            {
                wasCompact = compact;
                IsPaneOpen = !compact;
            }
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Handle, IsPaneOnLeft ? "调整左侧栏宽度" : "调整右侧栏宽度");
        var hasCollapsedContent = !IsPaneOpen && CollapsedPaneContent is not null;
        CollapsedPresenter.Visibility = hasCollapsedContent ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(CollapsedPresenter, IsPaneOnLeft ? 0 : 2);
        Grid.SetColumnSpan(CollapsedPresenter, 1);
        CollapsedPresenter.Margin = new Thickness(0);

        var targetWidth = IsPaneOpen ? CurrentPaneWidth : hasCollapsedContent ? 48 : 0;
        var currentCol = IsPaneOnLeft ? MainColumn : PaneColumn;
        var starCol = IsPaneOnLeft ? PaneColumn : MainColumn;
        starCol.Width = new GridLength(1, GridUnitType.Star);

        if (IsLoaded && ActualWidth > 0 && lastOpenState is not null && lastOpenState != IsPaneOpen)
        {
            var opening = IsPaneOpen;
            lastOpenState = IsPaneOpen;
            var prevOverlap = HandleColumn.Width.Value > VisualGap ? (HandleColumn.Width.Value - VisualGap) * 0.5 : 0;
            var fromWidth = currentCol.Width.IsAbsolute ? currentCol.Width.Value + prevOverlap : (opening ? (hasCollapsedContent ? 48 : 0) : CurrentPaneWidth);

            Handle.Visibility = Visibility.Collapsed;
            HandleColumn.Width = new GridLength(VisualGap);
            Grid.SetColumn(MainPresenter, IsPaneOnLeft ? 2 : 0);
            Grid.SetColumnSpan(MainPresenter, 1);
            MainPresenter.Margin = new Thickness(0);
            Grid.SetColumn(PanePresenter, IsPaneOnLeft ? 0 : 2);
            Grid.SetColumnSpan(PanePresenter, 1);
            PanePresenter.Margin = new Thickness(0);
            PanePresenter.Visibility = Visibility.Visible;

            WorkspaceMotion.AnimateWidth(DispatcherQueue, this, fromWidth, targetWidth, 200, w =>
            {
                currentCol.Width = new GridLength(Math.Max(0, w));
                PanePresenter.Opacity = opening
                    ? Math.Clamp((w - 24) / Math.Max(1, targetWidth - 24), 0, 1)
                    : Math.Clamp(w / Math.Max(1, fromWidth), 0, 1);
            }, () =>
            {
                PanePresenter.Opacity = 1;
                ApplyRestingGeometry(opening, hasCollapsedContent, opening ? CurrentPaneWidth : (hasCollapsedContent ? 48 : 0));
            });
            return;
        }

        WorkspaceMotion.CancelWidthAnimation(this);
        lastOpenState = IsPaneOpen;
        PanePresenter.Opacity = 1;
        ApplyRestingGeometry(IsPaneOpen, hasCollapsedContent, targetWidth);
    }

    private void ApplyRestingGeometry(bool open, bool hasCollapsedContent, double targetWidth)
    {
        var currentCol = IsPaneOnLeft ? MainColumn : PaneColumn;
        PanePresenter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Handle.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (!open)
        {
            HandleColumn.Width = new GridLength(hasCollapsedContent ? VisualGap : 0);
            Grid.SetColumn(MainPresenter, IsPaneOnLeft ? 2 : 0);
            Grid.SetColumnSpan(MainPresenter, 1);
            MainPresenter.Margin = new Thickness(0);
            Grid.SetColumn(PanePresenter, IsPaneOnLeft ? 0 : 2);
            Grid.SetColumnSpan(PanePresenter, 1);
            PanePresenter.Margin = new Thickness(0);
            currentCol.Width = new GridLength(Math.Max(0, targetWidth));
            return;
        }

        var hitColWidth = Math.Max(ResizeHandle.HitTargetThickness, VisualGap);
        var overlap = Math.Max(0, (hitColWidth - VisualGap) * 0.5);
        HandleColumn.Width = new GridLength(hitColWidth);
        currentCol.Width = new GridLength(Math.Max(0, targetWidth - overlap));

        if (IsPaneOnLeft)
        {
            Grid.SetColumn(PanePresenter, 0);
            Grid.SetColumnSpan(PanePresenter, overlap > 0 ? 2 : 1);
            PanePresenter.Margin = new Thickness(0, 0, overlap, 0);

            Grid.SetColumn(MainPresenter, overlap > 0 ? 1 : 2);
            Grid.SetColumnSpan(MainPresenter, overlap > 0 ? 2 : 1);
            MainPresenter.Margin = new Thickness(overlap, 0, 0, 0);
        }
        else
        {
            Grid.SetColumn(MainPresenter, 0);
            Grid.SetColumnSpan(MainPresenter, overlap > 0 ? 2 : 1);
            MainPresenter.Margin = new Thickness(0, 0, overlap, 0);

            Grid.SetColumn(PanePresenter, overlap > 0 ? 1 : 2);
            Grid.SetColumnSpan(PanePresenter, overlap > 0 ? 2 : 1);
            PanePresenter.Margin = new Thickness(overlap, 0, 0, 0);
        }
    }

    private void Pane_ResizeRequested(object? sender, double delta)
    {
        if (!IsPaneOpen || !double.IsFinite(delta)) return;
        var current = CurrentPaneWidth;
        var width = Math.Clamp(current + (IsPaneOnLeft ? delta : -delta), MinimumPaneWidth, AvailablePaneWidth);
        if (width != current) SetPreferredWidth(width);
    }

    private void Pane_ResetRequested(object? sender, EventArgs e)
    {
        var from = CurrentPaneWidth;
        preferredPaneWidth = null;
        App.PaneWidths.SetWidth(WidthPreferenceKey, null);
        var to = CurrentPaneWidth;
        if (IsLoaded && IsPaneOpen && Math.Abs(to - from) > 1)
        {
            var col = IsPaneOnLeft ? MainColumn : PaneColumn;
            var hitColWidth = Math.Max(ResizeHandle.HitTargetThickness, VisualGap);
            var overlap = Math.Max(0, (hitColWidth - VisualGap) * 0.5);
            WorkspaceMotion.AnimateWidth(DispatcherQueue, from, to, 190,
                w => col.Width = new GridLength(Math.Max(0, w - overlap)),
                UpdateLayoutWidths);
        }
        else UpdateLayoutWidths();
    }

    private void SetPreferredWidth(double? width)
    {
        preferredPaneWidth = width;
        UpdateLayoutWidths();
        App.PaneWidths.SetWidth(WidthPreferenceKey, width);
    }
}
