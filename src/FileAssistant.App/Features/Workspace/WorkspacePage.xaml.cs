using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.SpaceAnalysis;
using FileAssistant.App.Resources;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace FileAssistant.App.Features.Workspace;

public sealed partial class WorkspacePage : Page
{
    private const string RailWidthPreferenceKey = "Workspace.NavigationRail";
    private const double ExpandedRailWidth = 192;
    private const double MinimumExpandedRailWidth = 156;
    private const double MaximumExpandedRailWidth = 320;
    private const double CompactRailWidth = 56;
    private static readonly string[] PageOrder = ["workspace", "search", "convert", "naming", "organize", "space", "cleanup", "outputs"];

    public UIElement TitleBarElement => WorkspaceTitleBar;
    public WorkspaceViewModel ViewModel => App.Workspace;
    public WorkspaceViewModel CopyViewModel => App.CopyInput;
    public Outputs.OutputsViewModel Outputs => App.Outputs;
    public Tasks.ProcessingJobsViewModel Jobs => App.Jobs;
    public AppearanceViewModel Appearance => App.Appearance;

    private SettingsDialog? settingsDialog;
    private SpaceAnalysisView? spacePanel;
    private Cleanup.CleanupView? cleanupPanel;
    private readonly DropTargetHighlight navDropHighlight = new();
    private readonly DispatcherQueueTimer navTransitionTimer;
    private readonly DispatcherQueueTimer indicatorTimer;
    private readonly DispatcherQueueTimer taskDoneResetTimer;
    private readonly DispatcherQueueTimer taskHoverOpenTimer;
    private readonly DispatcherQueueTimer taskHoverCloseTimer;
    private bool isPointerOverTaskTrigger;
    private bool isPointerOverTaskFlyout;
    private Action<double>? activeTransitionStep;
    private Action? activeTransitionComplete;
    private long transitionStartTicks;
    private double transitionDurationMs;
    private CompositionScopedBatch? modeBatch;
    private readonly List<(UIElement Element, Vector3KeyFrameAnimation Animation, Vector3 RestingValue)> modeTransforms = [];
    private long modeStart;
    private double modeDurationMs;
    private float modeFrom, modeTo;
    private long indicatorStartTicks;
    private double fromLeftIndicatorY, toLeftIndicatorY;
    private double fromTopIndicatorX, toTopIndicatorX, fromTopIndicatorW, toTopIndicatorW;
    private bool? userLeftRailCollapsed;
    private double? preferredRailWidth;
    private string currentPage = "workspace";
    private int lastStagingCount;
    private int lastOutputsCount;
    private bool wasRunningJobs;
    private bool isShowingTaskDone;
    private UIElement? hoveredDropNav;
    private Point? lastThemeClickOrigin;

    public WorkspacePage()
    {
        InitializeComponent();
        LeftRailResizeHandle.Translation = new System.Numerics.Vector3(0, 0, 12);
        ApplyBrand(App.Preferences.Current);

        navTransitionTimer = DispatcherQueue.CreateTimer();
        navTransitionTimer.Interval = TimeSpan.FromMilliseconds(8);
        navTransitionTimer.IsRepeating = true;
        navTransitionTimer.Tick += NavTransitionTimer_Tick;

        indicatorTimer = DispatcherQueue.CreateTimer();
        indicatorTimer.Interval = TimeSpan.FromMilliseconds(8);
        indicatorTimer.IsRepeating = true;
        indicatorTimer.Tick += IndicatorTimer_Tick;

        taskDoneResetTimer = DispatcherQueue.CreateTimer();
        taskDoneResetTimer.Interval = TimeSpan.FromSeconds(2.8);
        taskDoneResetTimer.IsRepeating = false;
        taskDoneResetTimer.Tick += (_, _) => ResetTaskIdleIcons();

        taskHoverOpenTimer = DispatcherQueue.CreateTimer();
        taskHoverOpenTimer.Interval = TimeSpan.FromMilliseconds(115);
        taskHoverOpenTimer.IsRepeating = false;
        taskHoverOpenTimer.Tick += (_, _) =>
        {
            if (isPointerOverTaskTrigger && !TaskCenterDialog.IsOpen && !SettingsDialog.IsOpen)
                ViewModel.IsQueueExpanded = true;
        };

        taskHoverCloseTimer = DispatcherQueue.CreateTimer();
        taskHoverCloseTimer.Interval = TimeSpan.FromMilliseconds(240);
        taskHoverCloseTimer.IsRepeating = false;
        taskHoverCloseTimer.Tick += (_, _) =>
        {
            if (isPointerOverTaskTrigger || isPointerOverTaskFlyout) return;
            if (HasNestedFlyoutOpenAboveTaskQueue())
            {
                taskHoverCloseTimer.Start();
                return;
            }
            ViewModel.IsQueueExpanded = false;
        };

        foreach (var trigger in new UIElement[] { LeftTasksButton, TopTasksButton })
        {
            trigger.PointerEntered += (_, _) =>
            {
                isPointerOverTaskTrigger = true;
                taskHoverCloseTimer.Stop();
                if (!ViewModel.IsQueueExpanded && !TaskCenterDialog.IsOpen && !SettingsDialog.IsOpen)
                    taskHoverOpenTimer.Start();
            };
            trigger.PointerExited += (_, _) =>
            {
                isPointerOverTaskTrigger = false;
                taskHoverOpenTimer.Stop();
                if (ViewModel.IsQueueExpanded) taskHoverCloseTimer.Start();
            };
        }
        TaskQueueScroll.PointerEntered += (_, _) => { isPointerOverTaskFlyout = true; taskHoverCloseTimer.Stop(); };
        TaskQueueScroll.PointerExited += (_, _) => { isPointerOverTaskFlyout = false; if (ViewModel.IsQueueExpanded) taskHoverCloseTimer.Start(); };

        foreach (var btn in AllNavigationButtons) WorkspaceMotion.AttachTactilePress(btn, 0.968f);
        foreach (var chrome in new UIElement[] { TitlePinButton, NavModeToggleButton, TitleAppearanceButton, TitleSettingsButton, LeftTasksButton, TopTasksButton })
            WorkspaceMotion.AttachTactilePress(chrome, 0.94f);
        foreach (var themeOption in new UIElement[] { ThemeLightItem, ThemeCharcoalItem, ThemeSystemItem })
        {
            themeOption.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                if (App.Window?.Content is UIElement root)
                {
                    try { lastThemeClickOrigin = args.GetCurrentPoint(root).Position; } catch (Exception) { }
                }
            }), handledEventsToo: true);
        }

        Loaded += (_, _) =>
        {
            App.Window?.SetTitleBar(WorkspaceTitleBar);
            DialogDismiss.RegisterWindowRoot(this);
            preferredRailWidth = App.PaneWidths.GetWidth(RailWidthPreferenceKey);
            lastStagingCount = ViewModel.TotalReferenceCount;
            lastOutputsCount = Outputs.TotalCount;
            wasRunningJobs = Jobs.HasRunningJobs;

            ViewModel.PropertyChanged += QueueStateChanged;
            Outputs.PropertyChanged += OutputsChanged;
            Jobs.PropertyChanged += JobsChanged;
            Appearance.PropertyChanged += AppearanceChanged;

            App.PaneWidths.Reset += ResetRailWidth;
            if (!initialModuleApplied)
            {
                initialModuleApplied = true;
                var s = App.Preferences.Current;
                NavigateTo(s.StartupModule == "last" ? s.LastModule : s.StartupModule);
            }
            UpdateAppearanceGlyph();
            ApplyAlwaysOnTop(App.Preferences.Current.AlwaysOnTop);
            UpdateTaskTriggerVisuals();
            UpdateNavigationLayout(animate: false);
            WorkspaceMotion.AlignSingleLineInputs(StagingPanel);
            DispatcherQueue.TryEnqueue(() => UpdateSlidingIndicators(animate: false));
            UpdateTaskQueue();
        };
        Unloaded += (_, _) =>
        {
            StopTransition();
            StopModeMotion();
            indicatorTimer.Stop();
            taskDoneResetTimer.Stop();
            taskHoverOpenTimer.Stop();
            taskHoverCloseTimer.Stop();
            ViewModel.PropertyChanged -= QueueStateChanged;
            Outputs.PropertyChanged -= OutputsChanged;
            Jobs.PropertyChanged -= JobsChanged;
            Appearance.PropertyChanged -= AppearanceChanged;
            App.PaneWidths.Reset -= ResetRailWidth;
            navDropHighlight.Clear();
            TaskQueueFlyout.Hide();
        };
        ActualThemeChanged += (_, _) =>
        {
            UpdateAppearanceGlyph(animate: IsLoaded);
            ApplyAlwaysOnTop(App.Preferences.Current.AlwaysOnTop, animate: false);
            UpdateTaskTriggerVisuals();
            UpdateQueueLayout();
        };
        LeftNavigationScroll.ViewChanged += (_, _) => UpdateSlidingIndicators(animate: false);
        SizeChanged += (_, _) =>
        {
            UpdateQueueLayout();
            if (!navTransitionTimer.IsRunning) UpdateNavigationLayout(animate: false);
            UpdateSlidingIndicators(animate: false);
        };
        App.Conversion.OutputsRequested += (_, task) => { App.Outputs.TaskId = task; NavigateTo("outputs"); };
        App.Copy.OutputsRequested += (_, task) => { App.Outputs.TaskId = task; NavigateTo("outputs"); };
        App.Naming.OutputsRequested += (_, task) => { App.Outputs.TaskId = task; NavigateTo("outputs"); };
        App.Cleanup.FilesRequested += (_, _) => NavigateTo("cleanup");
        App.Conversion.EngineManagementRequested += async (_, _) => await OpenSettingsAsync(selectEngines: true);

        RebindShortcuts();
        AllowDrop = true;
        DragOver += External_DragOver;
        Drop += External_Drop;
    }

    private ToggleButton[] AllNavigationButtons =>
    [
        LeftWorkspaceNav, LeftSearchNav, LeftConversionNav, LeftNamingNav, LeftCopyNav, LeftSpaceNav, LeftCleanupNav, LeftOutputsNav,
        TopWorkspaceNav, TopSearchNav, TopConversionNav, TopNamingNav, TopCopyNav, TopSpaceNav, TopCleanupNav, TopOutputsNav
    ];

    private void Navigation_Click(object sender, RoutedEventArgs e) => NavigateTo((string)((ToggleButton)sender).Tag);

    private void NavigateTo(string page)
    {
        if (StagingPanel is null) return;
        var prevIndex = Array.IndexOf(PageOrder, currentPage);
        var nextIndex = Array.IndexOf(PageOrder, page);
        var changed = !string.Equals(currentPage, page, StringComparison.Ordinal);
        currentPage = page;
        if (changed && initialModuleApplied) _ = App.SavePreferenceAsync(s => s with { LastModule = page });

        foreach (var button in AllNavigationButtons)
            button.IsChecked = Equals(button.Tag, page);

        var searching = page is "search";
        var copying = page is "organize";
        var converting = page is "convert";
        var outputs = page is "outputs";
        var naming = page is "naming";
        var space = page is "space";
        var cleanup = page is "cleanup";

        // FindName realizes the deferred XAML once, including its declarative event handlers.
        var deferredPanel = page switch
        {
            "search" => nameof(SearchPanel), "organize" => nameof(CopyPanel),
            "convert" => nameof(ConversionPanel), "naming" => nameof(NamingPanel),
            "outputs" => nameof(OutputsPanel), _ => null
        };
        if (deferredPanel is not null) FindName(deferredPanel);
        if (!searching) SearchPanel?.Deactivate();
        StagingPanel.Visibility = page is "workspace" ? Visibility.Visible : Visibility.Collapsed;
        if (NamingPanel is not null) NamingPanel.Visibility = naming ? Visibility.Visible : Visibility.Collapsed;
        if (CopyPanel is not null) CopyPanel.Visibility = copying ? Visibility.Visible : Visibility.Collapsed;
        if (SearchPanel is not null) SearchPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        if (ConversionPanel is not null) ConversionPanel.Visibility = converting ? Visibility.Visible : Visibility.Collapsed;
        if (OutputsPanel is not null) OutputsPanel.Visibility = outputs ? Visibility.Visible : Visibility.Collapsed;

        if (space && spacePanel is null)
        {
            spacePanel = new SpaceAnalysisView();
            spacePanel.NavigateRequested += (_, destination) => NavigateTo(destination);
            SpaceHost.Content = spacePanel;
        }
        SpaceHost.Visibility = space ? Visibility.Visible : Visibility.Collapsed;
        spacePanel?.SetActive(space);

        if (cleanup && cleanupPanel is null)
        {
            cleanupPanel = new Cleanup.CleanupView();
            CleanupHost.Content = cleanupPanel;
        }
        CleanupHost.Visibility = cleanup ? Visibility.Visible : Visibility.Collapsed;
        cleanupPanel?.SetActive(cleanup);

        if (searching) SearchPanel?.Activate();
        if (converting) App.Conversion.RefreshCapabilities();
        ConversionPanel?.SetActive(converting);
        CopyPanel?.SetActive(copying);
        NamingPanel?.SetActive(naming);

        UpdateSlidingIndicators(animate: IsLoaded);
        if (changed && IsLoaded)
        {
            UIElement? activeStage = page switch
            {
                "search" => SearchPanel,
                "convert" => ConversionPanel,
                "naming" => NamingPanel,
                "organize" => CopyPanel,
                "outputs" => OutputsPanel,
                "space" => SpaceHost,
                "cleanup" => CleanupHost,
                _ => StagingPanel
            };
            WorkspaceMotion.AlignSingleLineInputs(activeStage);
            WorkspaceMotion.PlayStageEntrance(activeStage, nextIndex >= prevIndex ? 1 : -1);
        }
    }

    private void UpdateSlidingIndicators(bool animate)
    {
        if (!IsLoaded) return;
        var leftTarget = new[] { LeftWorkspaceNav, LeftSearchNav, LeftConversionNav, LeftNamingNav, LeftCopyNav, LeftSpaceNav, LeftCleanupNav, LeftOutputsNav }
            .FirstOrDefault(b => Equals(b.Tag, currentPage));
        var topTarget = new[] { TopWorkspaceNav, TopSearchNav, TopConversionNav, TopNamingNav, TopCopyNav, TopSpaceNav, TopCleanupNav }
            .FirstOrDefault(b => Equals(b.Tag, currentPage));

        LeftSlidingPill.Opacity = leftTarget is not null && leftTarget.ActualHeight > 0 ? 1 : 0;
        TopSlidingBar.Opacity = topTarget is not null && topTarget.ActualWidth > 0 ? 1 : 0;

        var nextLeftY = toLeftIndicatorY;
        if (leftTarget is not null && leftTarget.ActualHeight > 0)
        {
            var pt = leftTarget.TransformToVisual(LeftIndicatorLayer).TransformPoint(new Point(0, 0));
            nextLeftY = pt.Y + (leftTarget.ActualHeight - LeftSlidingPill.Height) * 0.5;
            if (leftTarget != LeftOutputsNav && (nextLeftY < 0 || nextLeftY + LeftSlidingPill.Height > LeftNavigationScroll.ActualHeight))
                LeftSlidingPill.Opacity = 0;
        }

        var nextTopX = toTopIndicatorX;
        var nextTopW = Math.Max(14, toTopIndicatorW);
        if (topTarget is not null && topTarget.ActualWidth > 0)
        {
            var pt = topTarget.TransformToVisual(TopMainNavigation).TransformPoint(new Point(0, 0));
            nextTopW = Math.Clamp(topTarget.ActualWidth * 0.36, 14, 26);
            nextTopX = pt.X + (topTarget.ActualWidth - nextTopW) * 0.5;
        }

        if (!animate || !WorkspaceMotion.Enabled)
        {
            indicatorTimer.Stop();
            LeftSlidingTransform.Y = fromLeftIndicatorY = toLeftIndicatorY = nextLeftY;
            TopSlidingTransform.X = fromTopIndicatorX = toTopIndicatorX = nextTopX;
            TopSlidingBar.Width = fromTopIndicatorW = toTopIndicatorW = nextTopW;
            return;
        }

        fromLeftIndicatorY = LeftSlidingTransform.Y;
        toLeftIndicatorY = nextLeftY;
        fromTopIndicatorX = TopSlidingTransform.X;
        toTopIndicatorX = nextTopX;
        fromTopIndicatorW = TopSlidingBar.Width > 0 ? TopSlidingBar.Width : nextTopW;
        toTopIndicatorW = nextTopW;
        indicatorStartTicks = Stopwatch.GetTimestamp();
        indicatorTimer.Start();
    }

    private void IndicatorTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var raw = Math.Clamp(Stopwatch.GetElapsedTime(indicatorStartTicks).TotalMilliseconds / 220.0, 0, 1);
        // Subtle spring overshoot curve
        var t = raw - 1.0;
        var eased = 1.0 + 2.1 * t * t * t + 1.1 * t * t;
        LeftSlidingTransform.Y = Lerp(fromLeftIndicatorY, toLeftIndicatorY, eased);
        TopSlidingTransform.X = Lerp(fromTopIndicatorX, toTopIndicatorX, eased);
        TopSlidingBar.Width = Math.Max(8, Lerp(fromTopIndicatorW, toTopIndicatorW, Math.Clamp(eased, 0, 1.1)));
        if (raw >= 1.0)
        {
            indicatorTimer.Stop();
            LeftSlidingTransform.Y = toLeftIndicatorY;
            TopSlidingTransform.X = toTopIndicatorX;
            TopSlidingBar.Width = toTopIndicatorW;
        }
    }

    private void UpdateAppearanceGlyph(bool animate = false)
    {
        if (TitleAppearanceIcon is null) return;
        var nextGlyph = ActualTheme == ElementTheme.Light ? "\uE706" : "\uE708";
        var changed = !string.Equals(TitleAppearanceIcon.Glyph, nextGlyph, StringComparison.Ordinal);
        TitleAppearanceIcon.Glyph = nextGlyph;
        if (animate && changed) WorkspaceMotion.PlayIconSwapSpin(TitleAppearanceIcon);
    }

    internal void ApplyAlwaysOnTop(bool pinned, bool animate = false)
    {
        if (TitlePinButton is null || TitlePinIcon is null) return;
        TitlePinButton.IsChecked = pinned;
        TitlePinIcon.Glyph = pinned ? "\uE840" : "\uE718";
        WorkspaceMotion.SetOrAnimateRotation(TitlePinIcon, pinned ? -45f : 0f, animate);
        var label = pinned ? "取消固定窗口（当前已置顶）" : "固定窗口（置顶显示）";
        Controls.WorkspaceLocalization.SetName(TitlePinButton, label);
        Controls.WorkspaceLocalization.SetTip(TitlePinButton, label);
    }

    private async void TitlePin_Click(object sender, RoutedEventArgs e)
    {
        var pinned = TitlePinButton.IsChecked == true;
        ApplyAlwaysOnTop(pinned, animate: true);
        await App.SavePreferenceAsync(s => s with { AlwaysOnTop = pinned });
    }

    private void NavModeToggle_Click(object sender, RoutedEventArgs e)
    {
        var toTop = Appearance.Navigation != NavigationPlacement.Top;
        Appearance.Navigation = toTop ? NavigationPlacement.Top : NavigationPlacement.Left;
        WorkspaceMotion.PlayIconFlip90(NavModeGlyph, clockwise: toTop);
    }

    private void RailCollapse_Click(object sender, RoutedEventArgs e) => ToggleLeftRail();

    private void AppearanceFlyout_Opening(object sender, object e)
    {
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 188d));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspacePopupBrush")));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceBorderBrush")));
        AppearanceMenuFlyout.FlyoutPresenterStyle = presenterStyle;

        var settings = App.Preferences.Current;
        var lightPreset = SettingsDialog.EffectivePaletteForMode(settings, light: true);
        var darkPreset = SettingsDialog.EffectivePaletteForMode(settings, light: false);
        ThemeLightLabel.Text = SettingsDialog.FormatModeMenuLabel(settings, light: true);
        ThemeCharcoalLabel.Text = SettingsDialog.FormatModeMenuLabel(settings, light: false);
        ThemeSystemLabel.Text = InterfaceText.Get("跟随系统", "System", settings.Language);

        if (Application.Current.Resources.TryGetValue("Palette_" + lightPreset.Id, out var lpObj) && lpObj is WorkspacePalette lp)
        {
            ThemeLightSwatch.Background = new SolidColorBrush(lp.Window);
            ThemeLightSwatch.BorderBrush = new SolidColorBrush(lp.Accent);
        }
        if (Application.Current.Resources.TryGetValue("Palette_" + darkPreset.Id, out var dpObj) && dpObj is WorkspacePalette dp)
        {
            ThemeCharcoalSwatch.Background = new SolidColorBrush(dp.Window);
            ThemeCharcoalSwatch.BorderBrush = new SolidColorBrush(dp.Accent);
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeLightItem, ThemeLightLabel.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeCharcoalItem, ThemeCharcoalLabel.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ThemeSystemItem, ThemeSystemLabel.Text);

        var current = Appearance.Style;
        ThemeLightItem.IsChecked = current == WorkspaceAppearance.Light;
        ThemeCharcoalItem.IsChecked = current == WorkspaceAppearance.CharcoalGlass;
        ThemeSystemItem.IsChecked = current == WorkspaceAppearance.System;
    }

    private async void AppearanceMenu_Click(object sender, RoutedEventArgs e)
    {
        var origin = lastThemeClickOrigin;
        lastThemeClickOrigin = null;
        if (origin is null && App.Window?.Content is UIElement root && TitleAppearanceButton.ActualWidth > 0)
        {
            try
            {
                origin = TitleAppearanceButton.TransformToVisual(root)
                    .TransformPoint(new Point(TitleAppearanceButton.ActualWidth * 0.5, TitleAppearanceButton.ActualHeight * 0.5));
            }
            catch (Exception) { }
        }

        AppearanceMenuFlyout.Hide();
        if (sender is ToggleButton { Tag: string raw } && Enum.TryParse<WorkspaceAppearance>(raw, out var next))
        {
            if (App.Window is MainWindow main)
                await main.SwitchAppearanceWithRippleAsync(next, origin);
            else
                Appearance.Style = next;
        }

        var current = Appearance.Style;
        ThemeLightItem.IsChecked = current == WorkspaceAppearance.Light;
        ThemeCharcoalItem.IsChecked = current == WorkspaceAppearance.CharcoalGlass;
        ThemeSystemItem.IsChecked = current == WorkspaceAppearance.System;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceMotion.PlaySpinOnce(TitleSettingsIcon, 360f, 440);
        await OpenSettingsAsync();
    }

    public void ToggleLeftRail()
    {
        if (Appearance.Navigation == NavigationPlacement.Top) return;
        if (modeBatch is not null) UpdateNavigationLayout(animate: false);
        var wasCompact = IsLeftRailCompact();
        var nextCompact = !wasCompact;
        userLeftRailCollapsed = nextCompact;
        AnimateLeftRailWidth(nextCompact);
    }

    private bool IsLeftRailCompact() => userLeftRailCollapsed ?? (ActualWidth > 0 && ActualWidth < 980);

    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppearanceViewModel.Navigation))
        {
            TaskQueueFlyout.Hide();
            UpdateNavigationLayout(animate: IsLoaded);
        }
    }

    private void OutputsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Outputs.TotalCount))
        {
            var next = Outputs.TotalCount;
            if (next > lastOutputsCount)
            {
                WorkspaceMotion.PlayPopBounce(LeftOutputsBadge, 1.24f);
                WorkspaceMotion.PlayPopBounce(TopOutputsBadge, 1.24f);
            }
            lastOutputsCount = next;
        }
    }

    private void JobsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Jobs.HasRunningJobs))
        {
            var running = Jobs.HasRunningJobs;
            if (wasRunningJobs && !running)
            {
                ShowTaskDoneCheckmark();
            }
            else
            {
                if (running) { isShowingTaskDone = false; taskDoneResetTimer.Stop(); }
                UpdateTaskTriggerVisuals();
            }
            wasRunningJobs = running;
        }
        else if (e.PropertyName == nameof(Jobs.CompactLabel))
        {
            UpdateTaskTriggerVisuals();
        }
    }

    private void ShowTaskDoneCheckmark()
    {
        isShowingTaskDone = true;
        UpdateTaskTriggerVisuals();
        WorkspaceMotion.PlayPopBounce(LeftTasksIdleIcon, 1.28f);
        WorkspaceMotion.PlayPopBounce(TopTasksIdleIcon, 1.28f);
        taskDoneResetTimer.Stop();
        taskDoneResetTimer.Start();
    }

    private void ResetTaskIdleIcons()
    {
        isShowingTaskDone = false;
        UpdateTaskTriggerVisuals();
    }

    private void UpdateTaskTriggerVisuals()
    {
        if (LeftTasksButton is null || TopTasksButton is null) return;

        if (Jobs.HasRunningJobs)
        {
            var softAccent = WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceAccentSoftBrush");
            LeftTasksButton.Background = TopTasksButton.Background = softAccent;
            LeftTasksButton.Foreground = WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceTextBrush");
            LeftTasksLabel.ClearValue(TextBlock.ForegroundProperty);
            LeftTasksLabel.FontWeight = FontWeights.SemiBold;
            WorkspaceLocalization.SetText(LeftTasksLabel, Jobs.CompactLabel);
            LeftTasksBadge.Opacity = TopTasksBadge.Opacity = 1;
            return;
        }

        if (isShowingTaskDone)
        {
            var successBrush = WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceSuccessBrush");
            var softSuccess = WorkspaceMaterials.ThemeBrush(ActualTheme, "OpSuccessSoftBrush");
            LeftTasksButton.Background = TopTasksButton.Background = softSuccess;
            LeftTasksButton.ClearValue(Control.ForegroundProperty);
            LeftTasksIdleIcon.Glyph = TopTasksIdleIcon.Glyph = "\uE73E";
            LeftTasksIdleIcon.Foreground = TopTasksIdleIcon.Foreground = successBrush;
            LeftTasksLabel.Foreground = successBrush;
            LeftTasksLabel.FontWeight = FontWeights.SemiBold;
            WorkspaceLocalization.SetText(LeftTasksLabel, "全部已完成");
            LeftTasksBadge.Opacity = TopTasksBadge.Opacity = 0;
            return;
        }

        LeftTasksIdleIcon.Glyph = TopTasksIdleIcon.Glyph = "\uE9D5";
        LeftTasksIdleIcon.ClearValue(IconElement.ForegroundProperty);
        TopTasksIdleIcon.ClearValue(IconElement.ForegroundProperty);
        LeftTasksLabel.ClearValue(TextBlock.ForegroundProperty);
        WorkspaceLocalization.SetText(LeftTasksLabel, Jobs.CompactLabel);
        LeftTasksBadge.Opacity = TopTasksBadge.Opacity = 1;

        if (ViewModel.IsQueueExpanded)
        {
            LeftTasksButton.Background = WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceNavigationSelectedBrush");
            LeftTasksButton.Foreground = WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceNavigationTextBrush");
            LeftTasksLabel.FontWeight = FontWeights.SemiBold;
            TopTasksButton.Background = WorkspaceMaterials.ThemeBrush(ActualTheme, "WorkspaceSubtleBrush");
        }
        else
        {
            LeftTasksButton.ClearValue(Control.BackgroundProperty);
            LeftTasksButton.ClearValue(Control.ForegroundProperty);
            LeftTasksLabel.FontWeight = FontWeights.Normal;
            TopTasksButton.ClearValue(Control.BackgroundProperty);
        }
    }

    internal void ApplyBrand(Core.Settings.AppSettings settings)
    {
        AppIconAssets.SetImage(TitleBarAppIcon, settings.AppIcon);
        ToolTipService.SetToolTip(TitleBarAppIcon, InterfaceText.Brand);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TitleBarAppIcon, InterfaceText.Brand);
        TitleBarBrandSubtitle.Visibility = InterfaceText.Get("FileKite", "", settings.Language).Length == 0
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyTopResponsiveChrome()
    {
        var compactBrand = Appearance.Navigation == NavigationPlacement.Top && ActualWidth is > 0 and < 1180;
        TitleBarBrandLockup.Visibility = compactBrand ? Visibility.Collapsed : Visibility.Visible;
        TitleBarCompactBrand.Visibility = compactBrand ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in TopMainNavigation.Children.OfType<ToggleButton>())
            if (button.Content is StackPanel panel && panel.Children.FirstOrDefault() is FontIcon icon)
                icon.Visibility = ActualWidth > 0 && ActualWidth < 1180 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyTitleBarModeChrome(bool topMode)
    {
        NavModeGlyph.Glyph = topMode ? "\uE745" : "\uE8A9";
        Controls.WorkspaceLocalization.SetTip(NavModeToggleButton, topMode ? "切换回左侧工作区导航" : "切换为单行融合顶栏");
    }

    private void ApplyLeftRailLabels(bool compact, double labelOpacity = 1.0)
    {
        var labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LeftWorkspaceHeaderTitle.Visibility = labelVisibility;
        LeftGroupCollect.Visibility = labelVisibility;
        LeftGroupProcess.Visibility = labelVisibility;
        LeftGroupSystem.Visibility = labelVisibility;
        LeftWorkspaceHeaderTitle.Opacity = LeftGroupCollect.Opacity = LeftGroupProcess.Opacity = LeftGroupSystem.Opacity = labelOpacity;

        LeftWorkspaceHeader.Padding = compact ? new Thickness(0, 2, 0, 2) : new Thickness(10, 2, 2, 2);
        LeftWorkspaceHeader.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(RailCollapseButton, compact ? 0 : 1);
        Grid.SetColumnSpan(RailCollapseButton, compact ? 2 : 1);
        RailCollapseButton.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        Controls.WorkspaceLocalization.SetTip(RailCollapseButton, compact ? "展开工作区侧栏" : "收起工作区侧栏");

        foreach (var button in new ButtonBase[] { LeftWorkspaceNav, LeftSearchNav, LeftConversionNav, LeftNamingNav, LeftCopyNav, LeftSpaceNav, LeftCleanupNav, LeftOutputsNav, LeftTasksButton })
        {
            button.Padding = compact ? new Thickness(8, 6, 8, 6) : new Thickness(10, 6, 10, 6);
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            if (button.Content is Grid row)
            {
                foreach (var child in row.Children.OfType<FrameworkElement>().Where(el => Equals(el.Tag, "RailLabel")))
                {
                    child.Visibility = labelVisibility;
                    child.Opacity = labelOpacity;
                }
            }
        }
    }

    private double AvailableExpandedRailWidth
        => ActualWidth > 0 ? Math.Clamp(ActualWidth - 620, MinimumExpandedRailWidth, MaximumExpandedRailWidth) : MaximumExpandedRailWidth;

    private double CurrentExpandedRailWidth
        => Math.Clamp(preferredRailWidth ?? ExpandedRailWidth, MinimumExpandedRailWidth, AvailableExpandedRailWidth);

    private double PrepareRailForTransition()
    {
        var visualWidth = LeftNavigationColumn.Width.Value + (LeftRailHandleColumn.Width.Value > 0 ? ResizeHandle.HitTargetOverlap : 0);
        LeftRailResizeHandle.Visibility = Visibility.Collapsed;
        LeftRailHandleColumn.Width = new GridLength(0);
        LeftNavigationColumn.Width = new GridLength(Math.Max(0, visualWidth));
        Grid.SetColumn(LeftNavigationRail, 0);
        Grid.SetColumnSpan(LeftNavigationRail, 1);
        LeftNavigationRail.Margin = new Thickness(0);
        Grid.SetColumn(WorkbenchSheet, 2);
        Grid.SetColumnSpan(WorkbenchSheet, 1);
        var currentLeftMargin = WorkbenchSheet.Margin.Left == ResizeHandle.HitTargetOverlap ? 0 : WorkbenchSheet.Margin.Left;
        WorkbenchSheet.Margin = new Thickness(currentLeftMargin, 0, 8, 8);
        return visualWidth;
    }

    private void ApplyRestingRailGeometry(bool topMode, bool compact)
    {
        if (topMode || compact)
        {
            LeftRailResizeHandle.Visibility = Visibility.Collapsed;
            LeftRailHandleColumn.Width = new GridLength(0);
            LeftNavigationColumn.Width = new GridLength(topMode ? 0 : CompactRailWidth);
            Grid.SetColumn(LeftNavigationRail, 0);
            Grid.SetColumnSpan(LeftNavigationRail, 1);
            LeftNavigationRail.Margin = new Thickness(0);
            Grid.SetColumn(WorkbenchSheet, 2);
            Grid.SetColumnSpan(WorkbenchSheet, 1);
            WorkbenchSheet.Margin = topMode ? new Thickness(8, 0, 8, 8) : new Thickness(0, 0, 8, 8);
            return;
        }

        var width = CurrentExpandedRailWidth;
        LeftRailHandleColumn.Width = new GridLength(ResizeHandle.HitTargetThickness);
        LeftNavigationColumn.Width = new GridLength(Math.Max(0, width - ResizeHandle.HitTargetOverlap));
        Grid.SetColumn(LeftNavigationRail, 0);
        Grid.SetColumnSpan(LeftNavigationRail, 2);
        LeftNavigationRail.Margin = new Thickness(0, 0, ResizeHandle.HitTargetOverlap, 0);
        Grid.SetColumn(WorkbenchSheet, 1);
        Grid.SetColumnSpan(WorkbenchSheet, 2);
        WorkbenchSheet.Margin = new Thickness(ResizeHandle.HitTargetOverlap, 0, 8, 8);
        LeftRailResizeHandle.Visibility = Visibility.Visible;
    }

    private void LeftRail_ResizeRequested(object? sender, double delta)
    {
        if (Appearance.Navigation == NavigationPlacement.Top || IsLeftRailCompact() || !double.IsFinite(delta)) return;
        StopTransition();
        var current = CurrentExpandedRailWidth;
        var next = Math.Clamp(current + delta, MinimumExpandedRailWidth, AvailableExpandedRailWidth);
        if (Math.Abs(next - current) < 0.1) return;
        preferredRailWidth = next;
        ApplyRestingRailGeometry(topMode: false, compact: false);
        App.PaneWidths.SetWidth(RailWidthPreferenceKey, next);
    }

    private void LeftRail_ResetRequested(object? sender, EventArgs e)
    {
        if (Appearance.Navigation == NavigationPlacement.Top || IsLeftRailCompact()) return;
        StopTransition();
        var fromWidth = CurrentExpandedRailWidth;
        preferredRailWidth = null;
        App.PaneWidths.SetWidth(RailWidthPreferenceKey, null);
        var toWidth = CurrentExpandedRailWidth;
        if (IsLoaded && Math.Abs(toWidth - fromWidth) > 1)
        {
            PrepareRailForTransition();
            StartTransition(190,
                eased => LeftNavigationColumn.Width = new GridLength(Math.Max(0, Lerp(fromWidth, toWidth, eased))),
                () =>
                {
                    ApplyRestingRailGeometry(topMode: false, compact: false);
                    DispatcherQueue.TryEnqueue(() => UpdateSlidingIndicators(animate: false));
                });
        }
        else ApplyRestingRailGeometry(topMode: false, compact: false);
    }

    private void AnimateLeftRailWidth(bool compact)
    {
        if (!IsLoaded || !WorkspaceMotion.Enabled) { UpdateNavigationLayout(animate: false); return; }
        StopTransition();
        var fromWidth = PrepareRailForTransition();
        var toWidth = compact ? CompactRailWidth : CurrentExpandedRailWidth;
        if (!compact) ApplyLeftRailLabels(compact: false, labelOpacity: 0);

        StartTransition(210, eased =>
        {
            LeftNavigationColumn.Width = new GridLength(Lerp(fromWidth, toWidth, eased));
            if (compact)
            {
                var fade = Math.Clamp(1.0 - eased * 2.2, 0, 1);
                LeftWorkspaceHeaderTitle.Opacity = LeftGroupCollect.Opacity = LeftGroupProcess.Opacity = LeftGroupSystem.Opacity = LeftTasksLabel.Opacity = fade;
            }
            else
            {
                var fade = Math.Clamp((eased - 0.25) / 0.75, 0, 1);
                ApplyLeftRailLabels(compact: false, labelOpacity: fade);
            }
        }, () =>
        {
            ApplyRestingRailGeometry(topMode: false, compact);
            ApplyLeftRailLabels(compact, labelOpacity: 1.0);
            DispatcherQueue.TryEnqueue(() => UpdateSlidingIndicators(animate: false));
        });
    }

    private void UpdateNavigationLayout(bool animate)
    {
        if (LeftNavigationRail is null || TopIntegratedNavHost is null || WorkbenchSheet is null) return;

        var top = Appearance.Navigation == NavigationPlacement.Top;
        var compact = IsLeftRailCompact();
        var from = CurrentModePosition();
        StopTransition();
        StopModeMotion();
        ApplyTopResponsiveChrome();
        ApplyTitleBarModeChrome(top);

        var target = top ? 1f : 0f;
        if (!animate || ActualWidth <= 0 || Math.Abs(target - from) < 0.005f || !WorkspaceMotion.Enabled)
        {
            SetRestingNavigation(top, compact);
            return;
        }

        // Apply the target layout geometry ONCE upfront so ModeMotionCompleted triggers zero layout reflow,
        // and use pure GPU Translation + Opacity (no Scale text distortion or per-frame ThemeShadow recalculation).
        var railWidth = compact ? CompactRailWidth : CurrentExpandedRailWidth;
        ApplyRestingRailGeometry(top, compact);
        if (top)
        {
            LeftNavigationRail.Width = railWidth;
            LeftNavigationRail.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(LeftNavigationRail, 0);
            Grid.SetColumnSpan(LeftNavigationRail, 3);
        }
        else
        {
            LeftNavigationRail.Width = double.NaN;
            LeftNavigationRail.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyLeftRailLabels(compact);
        }

        TopIntegratedNavHost.Visibility = TopRightQuickPod.Visibility = LeftNavigationRail.Visibility = Visibility.Visible;
        SetNavigationInput(top);
        UpdateLayout();
        UpdateSlidingIndicators(animate: false);

        var rail = ElementCompositionPreview.GetElementVisual(LeftNavigationRail);
        var nav = ElementCompositionPreview.GetElementVisual(TopIntegratedNavHost);
        var pod = ElementCompositionPreview.GetElementVisual(TopRightQuickPod);
        var compositor = rail.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
        modeFrom = from;
        modeTo = target;
        modeDurationMs = 195 * Math.Abs(target - from);
        var duration = TimeSpan.FromMilliseconds(modeDurationMs);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        modeBatch = batch;

        void Transform(UIElement element, string property, Vector3 start, Vector3 end, Vector3 rest)
        {
            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Target = property;
            animation.InsertKeyFrame(0, start);
            animation.InsertKeyFrame(1, end, ease);
            animation.Duration = duration;
            modeTransforms.Add((element, animation, rest));
            element.StartAnimation(animation);
        }

        void Slide(UIElement element, Vector3 start, Vector3 end)
        {
            var rest = element.Translation;
            Transform(element, nameof(UIElement.Translation), rest + start, rest + end, rest);
        }

        void Scalar(CompositionObject surface, string property, float start, float end)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0, start);
            animation.InsertKeyFrame(1, end, ease);
            animation.Duration = duration;
            surface.StartAnimation(property, animation);
        }

        var delta = target - from;
        var sheetShift = (float)Math.Min(railWidth - 8, 128) * delta;
        var railTravel = (float)Math.Min(railWidth * 0.48, 72);
        Slide(WorkbenchSheet, new Vector3(sheetShift, 0, 0), Vector3.Zero);
        Slide(LeftNavigationRail, new Vector3(-railTravel * from, 0, 0), new Vector3(-railTravel * target, 0, 0));
        Slide(TopIntegratedNavHost, new Vector3(-14 * (1 - from), 0, 0), new Vector3(-14 * (1 - target), 0, 0));
        Scalar(rail, "Opacity", 1 - from, 1 - target);
        Scalar(nav, "Opacity", from, target);
        Scalar(pod, "Opacity", from, target);

        modeStart = Stopwatch.GetTimestamp();
        batch.Completed += ModeMotionCompleted;
        batch.End();
    }

    private float CurrentModePosition()
    {
        if (modeBatch is null) return TopIntegratedNavHost.Visibility == Visibility.Visible ? 1 : 0;
        var t = Math.Clamp(Stopwatch.GetElapsedTime(modeStart).TotalMilliseconds / modeDurationMs, 0, 1);
        return modeFrom + (modeTo - modeFrom) * (float)(1 - Math.Pow(1 - t, 4));
    }

    private void ModeMotionCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (!Equals(sender, modeBatch)) return;
        StopModeMotion();
        SetRestingNavigation(Appearance.Navigation == NavigationPlacement.Top, IsLeftRailCompact());
    }

    private void SetNavigationInput(bool top)
    {
        TopIntegratedNavHost.IsHitTestVisible = TopRightQuickPod.IsHitTestVisible = top;
        LeftNavigationRail.IsHitTestVisible = !top;
        var buttons = AllNavigationButtons;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as ButtonBase;
        foreach (var button in buttons) button.IsTabStop = button.Name.StartsWith("Top", StringComparison.Ordinal) == top;
        TopTasksButton.IsTabStop = top;
        LeftTasksButton.IsTabStop = RailCollapseButton.IsTabStop = !top;
        if (focused is null || focused.IsTabStop) return;
        if (focused is ToggleButton nav && buttons.Contains(nav))
            buttons.FirstOrDefault(button => button.IsTabStop && Equals(button.Tag, nav.Tag))?.Focus(FocusState.Programmatic);
        else if (focused == TopTasksButton || focused == LeftTasksButton)
            (top ? TopTasksButton : LeftTasksButton).Focus(FocusState.Programmatic);
        else if (focused == RailCollapseButton) NavModeToggleButton.Focus(FocusState.Programmatic);
    }

    private void SetRestingNavigation(bool top, bool compact)
    {
        LeftNavigationRail.Width = double.NaN;
        LeftNavigationRail.HorizontalAlignment = HorizontalAlignment.Stretch;
        TopIntegratedNavHost.Visibility = TopRightQuickPod.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        LeftNavigationRail.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        SetNavigationInput(top);
        ApplyRestingRailGeometry(top, compact);
        if (!top) ApplyLeftRailLabels(compact);
        DispatcherQueue.TryEnqueue(() => UpdateSlidingIndicators(animate: false));
    }

    private void StopModeMotion()
    {
        if (modeBatch is null) return;
        modeBatch.Completed -= ModeMotionCompleted;
        modeBatch = null;
        foreach (var (element, animation, rest) in modeTransforms)
        {
            element.StopAnimation(animation);
            if (animation.Target == nameof(UIElement.Translation)) element.Translation = rest;
            else element.Scale = rest;
        }
        modeTransforms.Clear();
        foreach (var surface in new FrameworkElement[] { LeftNavigationRail, TopIntegratedNavHost, TopRightQuickPod })
        {
            var visual = ElementCompositionPreview.GetElementVisual(surface);
            if (surface == LeftNavigationRail || surface == TopIntegratedNavHost) visual.Clip = null;
            visual.StopAnimation("Opacity");
            visual.Opacity = (float)surface.Opacity;
        }
    }

    private void StartTransition(double durationMs, Action<double> step, Action complete)
    {
        if (!WorkspaceMotion.Enabled) { StopTransition(); step(1); complete(); return; }
        transitionStartTicks = Stopwatch.GetTimestamp();
        transitionDurationMs = durationMs;
        activeTransitionStep = step;
        activeTransitionComplete = complete;
        navTransitionTimer.Start();
    }

    private void StopTransition()
    {
        navTransitionTimer.Stop();
        activeTransitionStep = null;
        activeTransitionComplete = null;
    }

    private void NavTransitionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (activeTransitionStep is null) { StopTransition(); return; }
        var elapsedMs = Stopwatch.GetElapsedTime(transitionStartTicks).TotalMilliseconds;
        var raw = Math.Clamp(elapsedMs / transitionDurationMs, 0, 1);
        var eased = 1.0 - Math.Pow(1.0 - raw, 4);
        activeTransitionStep(eased);
        if (raw >= 1.0)
        {
            var done = activeTransitionComplete;
            StopTransition();
            done?.Invoke();
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private void NavTarget_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string target || !FileInputSources.CanRead(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        var label = target switch
        {
            "convert" => "加入格式转换并打开",
            "naming" => "加入批量改名并打开",
            "organize" => "加入复制与整理并打开",
            "cleanup" => "加入清理清单并打开",
            _ => "加入文件暂存区并打开"
        };
        e.DragUIOverride.Caption = label;
        e.DragUIOverride.IsCaptionVisible = true;
        navDropHighlight.Show(button);
        if (!ReferenceEquals(hoveredDropNav, button))
        {
            if (hoveredDropNav is not null) WorkspaceMotion.AnimateScale(hoveredDropNav, 1f, 140);
            hoveredDropNav = button;
            WorkspaceMotion.AnimateScale(button, 1.055f, 140);
        }
        e.Handled = true;
    }

    private void NavTarget_DragLeave(object sender, DragEventArgs e)
    {
        navDropHighlight.Clear();
        if (sender is UIElement el)
        {
            WorkspaceMotion.AnimateScale(el, 1f, 150);
            if (ReferenceEquals(hoveredDropNav, el)) hoveredDropNav = null;
        }
    }

    private async void NavTarget_Drop(object sender, DragEventArgs e)
    {
        navDropHighlight.Clear();
        if (hoveredDropNav is not null) { WorkspaceMotion.AnimateScale(hoveredDropNav, 1f, 150); hoveredDropNav = null; }
        if (sender is UIElement sEl) WorkspaceMotion.AnimateScale(sEl, 1f, 150);
        if (sender is not ToggleButton button || button.Tag is not string target || !FileInputSources.CanRead(e.DataView)) return;
        var token = App.InputStopping;
        var deferral = e.GetDeferral();
        e.Handled = true;
        try
        {
            var paths = await FileInputSources.ReadAsync(e.DataView);
            if (paths.Count == 0) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            WorkspaceMotion.PlayPopBounce(button, 1.09f);
            switch (target)
            {
                case "convert":
                    NavigateTo("convert");
                    await App.Conversion.Input.QueueImportPathsAsync(paths, token);
                    break;
                case "naming":
                    if (!App.Naming.IsExecuting) App.Naming.ModeValue = 0;
                    NavigateTo("naming");
                    await App.Naming.Input.QueueImportPathsAsync(paths, token);
                    break;
                case "organize":
                    NavigateTo("organize");
                    await CopyViewModel.QueueImportPathsAsync(paths, token);
                    break;
                case "cleanup":
                    if (!App.Cleanup.IsBusy) App.Cleanup.AddPaths(paths, navigate: true);
                    break;
                default:
                    NavigateTo("workspace");
                    await ViewModel.QueueImportPathsAsync(paths, token);
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { App.ReportFailure(error); }
        finally { deferral.Complete(); }
    }

    private void Input_SearchRequested(object? sender, EventArgs e) => NavigateTo("search");

    public async Task OpenSettingsAsync(bool selectEngines = false, string? initialPage = null)
    {
        if (settingsDialog is not null)
        {
            if (initialPage is not null || selectEngines) settingsDialog.SelectCategory(initialPage ?? "engines");
            return;
        }
        ViewModel.IsQueueExpanded = false;
        TaskCenterDialog.HideActive();
        settingsDialog = new SettingsDialog(this, selectEngines, initialPage);
        try { await settingsDialog.ShowAsync(); }
        finally { settingsDialog = null; }
    }

    private async void Input_FilesForCopyRequested(object? sender, FileInputTransferEventArgs e)
    {
        var report = await FileCollectionDialogs.ImportAsync(CopyViewModel, e.Bundle, this);
        if (report is not null) NavigateTo("organize");
    }

    private async void Input_FilesForConversionRequested(object? sender, FileInputTransferEventArgs e)
    {
        var report = await FileCollectionDialogs.ImportAsync(App.Conversion.Input, e.Bundle, this);
        if (report is not null) NavigateTo("convert");
    }

    private async void Input_FilesForNamingRequested(object? sender, FileInputTransferEventArgs e)
    {
        var report = await FileCollectionDialogs.ImportAsync(App.Naming.Input, e.Bundle, this);
        if (report is not null)
        {
            if (!App.Naming.IsExecuting) App.Naming.ModeValue = 0;
            NavigateTo("naming");
        }
    }

    private async void TaskQueue_Click(object sender, RoutedEventArgs e)
    {
        taskHoverOpenTimer.Stop();
        taskHoverCloseTimer.Stop();
        isPointerOverTaskTrigger = false;
        isPointerOverTaskFlyout = false;
        ViewModel.IsQueueExpanded = false;
        TaskQueueFlyout.Hide();
        await TaskCenterDialog.ShowAsync(this, ViewModel);
    }

    private bool HasNestedFlyoutOpenAboveTaskQueue()
    {
        if (XamlRoot is null) return false;
        var openLightDismiss = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
            .Count(p => p.IsOpen && p.IsLightDismissEnabled);
        return openLightDismiss > 1;
    }

    private void QueueStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.IsQueueExpanded))
        {
            UpdateTaskTriggerVisuals();
            UpdateTaskQueue();
        }
        if (e.PropertyName == nameof(WorkspaceViewModel.TotalReferenceCount))
        {
            var next = ViewModel.TotalReferenceCount;
            if (next > lastStagingCount)
            {
                WorkspaceMotion.PlayPopBounce(LeftWorkspaceBadge, 1.24f);
                WorkspaceMotion.PlayPopBounce(TopWorkspaceBadge, 1.24f);
            }
            lastStagingCount = next;
        }
    }

    private void UpdateTaskQueue()
    {
        if (!IsLoaded) return;
        if (!ViewModel.IsQueueExpanded) { TaskQueueFlyout.Hide(); return; }
        UpdateQueueLayout();
        var topMode = Appearance.Navigation == NavigationPlacement.Top;
        var target = topMode ? TopTasksButton : LeftTasksButton;
        var options = new FlyoutShowOptions
        {
            Placement = topMode ? FlyoutPlacementMode.BottomEdgeAlignedRight : FlyoutPlacementMode.RightEdgeAlignedBottom,
            ShowMode = FlyoutShowMode.Transient
        };
        TaskQueueFlyout.ShowAt(target, options);
    }

    private void UpdateQueueLayout()
    {
        if (TaskQueuePanel is null) return;
        TaskQueuePanel.RequestedTheme = ActualTheme;
        TaskQueueScroll.Width = Math.Clamp(ActualWidth - 72, 300, 376);
        TaskQueueScroll.MaxHeight = Math.Max(140, ActualHeight - 96);
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 392d));
        TaskQueueFlyout.FlyoutPresenterStyle = style;
    }

    private void TaskQueueFlyout_Closed(object? sender, object e) => ViewModel.IsQueueExpanded = false;
}
