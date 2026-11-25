using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace FileAssistant.App;

public sealed partial class MainWindow : Window
{
    private bool released;
    private int rippleGeneration;
    private bool rippleActive;
    private CompositionScopedBatch? rippleBatch;
    private readonly WindowIcons icons;
    internal WorkspacePage? WorkspacePage => RootFrame.Content as WorkspacePage;

    public MainWindow()
    {
        InitializeComponent();
        Title = InterfaceText.WindowTitle;
        ApplyAppearance();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = App.Preferences.Current.AlwaysOnTop;
        App.Appearance.PropertyChanged += AppearanceChanged;
        Closed += (_, _) => ReleaseThemeHandlers();

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        icons = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        ApplyAppIcon(App.Preferences.Current.AppIcon);

        SetWorkspaceSize(compact: false);
        RootFrame.Navigate(typeof(WorkspacePage));
        UpdateCaptionColors();
    }

    public void SetWorkspaceSize(bool compact)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)((compact ? 960 : 1320) * scale), area.Width - 48);
        var height = Math.Min((int)((compact ? 640 : 820) * scale), area.Height - 48);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min((int)(520 * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(390 * scale), area.Height);
        }

        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    internal void ReleaseThemeHandlers()
    {
        if (released) return;
        released = true;
        CleanupThemeRipple();
        icons.Dispose();
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        App.Appearance.PropertyChanged -= AppearanceChanged;
    }

    internal async Task SwitchAppearanceWithRippleAsync(WorkspaceAppearance next, Point? originInWindowRoot)
    {
        if (released) return;
        var currentLight = WindowRoot.ActualTheme == ElementTheme.Light;
        var targetLight = Resources.WorkspaceMaterials.IsActiveLightTheme(
            App.Preferences.Current with { Mode = (Core.Settings.AppearanceMode)(int)next });

        if (currentLight == targetLight ||
            !Controls.WorkspaceMotion.Enabled ||
            !WindowRoot.IsLoaded ||
            WindowRoot.ActualWidth <= 0 ||
            WindowRoot.ActualHeight <= 0)
        {
            App.Appearance.Style = next;
            return;
        }

        CleanupThemeRipple();
        var gen = ++rippleGeneration;
        var width = (float)WindowRoot.ActualWidth;
        var height = (float)WindowRoot.ActualHeight;
        var origin = originInWindowRoot ?? new Point(Math.Max(0, width - 170f), 24f);
        var cx = Math.Clamp((float)origin.X, 0f, width);
        var cy = Math.Clamp((float)origin.Y, 0f, height);

        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(RippleRevealHost);
            if (released || gen != rippleGeneration) return;

            var oldBg = ResolveThemeColor(currentLight, "AppBackgroundColor");
            var newBg = ResolveThemeColor(targetLight, "AppBackgroundColor");
            var accent = ResolveThemeColor(targetLight, "AppAccentColor");

            ThemeRippleBackdrop.Background = new SolidColorBrush(oldBg);
            ThemeRippleSnapshot.Source = rtb;
            ThemeRippleBackdrop.Visibility = Visibility.Visible;

            var hostVisual = ElementCompositionPreview.GetElementVisual(RippleRevealHost);
            var compositor = hostVisual.Compositor;
            var center = new Vector2(cx, cy);
            var dx = MathF.Max(cx, width - cx);
            var dy = MathF.Max(cy, height - cy);
            var maxRadius = MathF.Sqrt(dx * dx + dy * dy) + 16f;

            var clipEllipse = compositor.CreateEllipseGeometry();
            clipEllipse.Center = center;
            clipEllipse.Radius = Vector2.Zero;
            hostVisual.Clip = compositor.CreateGeometricClip(clipEllipse);
            RippleRevealHost.Background = new SolidColorBrush(newBg);

            var ringEllipse = compositor.CreateEllipseGeometry();
            ringEllipse.Center = center;
            ringEllipse.Radius = Vector2.Zero;
            var ringShape = compositor.CreateSpriteShape(ringEllipse);
            ringShape.StrokeBrush = compositor.CreateColorBrush(accent);
            ringShape.StrokeThickness = 2f;
            var ringVisual = compositor.CreateShapeVisual();
            ringVisual.Size = new Vector2(width, height);
            ringVisual.Opacity = 0.44f;
            ringVisual.Shapes.Add(ringShape);
            ElementCompositionPreview.SetElementChildVisual(ThemeRippleRingHost, ringVisual);

            rippleActive = true;
            App.Appearance.Style = next;

            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));
            var duration = TimeSpan.FromMilliseconds(440);

            var radiusAnim = compositor.CreateVector2KeyFrameAnimation();
            radiusAnim.InsertKeyFrame(0f, Vector2.Zero);
            radiusAnim.InsertKeyFrame(1f, new Vector2(maxRadius, maxRadius), ease);
            radiusAnim.Duration = duration;

            var ringFade = compositor.CreateScalarKeyFrameAnimation();
            ringFade.InsertKeyFrame(0f, 0.44f);
            ringFade.InsertKeyFrame(0.65f, 0.22f, ease);
            ringFade.InsertKeyFrame(1f, 0f, ease);
            ringFade.Duration = duration;

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            rippleBatch = batch;
            clipEllipse.StartAnimation("Radius", radiusAnim);
            ringEllipse.StartAnimation("Radius", radiusAnim);
            ringVisual.StartAnimation("Opacity", ringFade);
            batch.End();
            batch.Completed += (_, _) =>
            {
                if (gen == rippleGeneration) CleanupThemeRipple();
            };
        }
        catch (Exception)
        {
            CleanupThemeRipple();
            App.Appearance.Style = next;
        }
    }

    internal bool IsThemeRippleActive => rippleActive;

    internal void CleanupThemeRipple()
    {
        rippleGeneration++;
        rippleActive = false;
        if (rippleBatch is not null)
        {
            try { rippleBatch.Dispose(); } catch (Exception) { }
            rippleBatch = null;
        }
        try
        {
            if (RippleRevealHost is not null)
            {
                ElementCompositionPreview.GetElementVisual(RippleRevealHost).Clip = null;
                RippleRevealHost.Background = null;
            }
            if (ThemeRippleRingHost is not null)
                ElementCompositionPreview.SetElementChildVisual(ThemeRippleRingHost, null);
            if (ThemeRippleBackdrop is not null)
            {
                ThemeRippleBackdrop.Visibility = Visibility.Collapsed;
                ThemeRippleBackdrop.Background = null;
            }
            if (ThemeRippleSnapshot is not null)
                ThemeRippleSnapshot.Source = null;
        }
        catch (Exception) { }
    }

    private static Color ResolveThemeColor(bool light, string key)
    {
        var dictKey = light ? "Light" : "Dark";
        foreach (var dict in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (dict.ThemeDictionaries.TryGetValue(dictKey, out var themed) &&
                themed is ResourceDictionary td &&
                td.TryGetValue(key, out var val) &&
                val is Color color)
                return color;
        }
        return light ? ColorHelper.FromArgb(255, 0xEE, 0xEE, 0xE8) : ColorHelper.FromArgb(255, 0x16, 0x18, 0x1D);
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args)
    {
        Resources.WorkspaceMaterials.Apply();
        UpdateCaptionColors();
    }

    private void AppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppearanceViewModel.Style)) ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        var style = App.Appearance.Style;
        Resources.WorkspaceMaterials.Apply();
        WindowRoot.RequestedTheme = style switch
        {
            WorkspaceAppearance.System => ElementTheme.Default,
            WorkspaceAppearance.Light => ElementTheme.Light,
            _ => ElementTheme.Dark
        };
        Resources.WorkspaceMaterials.Apply();
        SystemBackdrop = App.Preferences.Current.Mica ? new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt } : null;
        UpdateCaptionColors();
        if (!rippleActive) Controls.WorkspaceMotion.PlayThemeCrossfade(WindowRoot);
    }

    internal void ApplyPreferences(Core.Settings.AppSettings previous, Core.Settings.AppSettings next)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = next.AlwaysOnTop;
        if (previous.AlwaysOnTop != next.AlwaysOnTop) WorkspacePage?.ApplyAlwaysOnTop(next.AlwaysOnTop, animate: true);
        if (previous.Mode != next.Mode || previous.Palette != next.Palette || previous.Mica != next.Mica) ApplyAppearance();
        if (previous.Shortcuts != next.Shortcuts) WorkspacePage?.RebindShortcuts();
        if (previous.Language != next.Language) Title = InterfaceText.WindowTitle;
        if (previous.AppIcon != next.AppIcon) ApplyAppIcon(next.AppIcon);
        if (previous.AppIcon != next.AppIcon || previous.Language != next.Language) WorkspacePage?.ApplyBrand(next);
    }

    private void ApplyAppIcon(string id)
    {
        try
        {
            icons.SetIcon(AppIconAssets.Ico(id), path => AppWindow.SetIcon(path),
                handle => AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(handle)));
        }
        catch (Exception error) { App.ReportFailure(error); }
    }

    private void UpdateCaptionColors()
    {
        // WinUI can raise ActualThemeChanged after the native window has already been detached.
        if (released || WindowRoot is null || AppWindow?.TitleBar is not { } caption) return;
        caption.ButtonBackgroundColor = Colors.Transparent;
        caption.ButtonInactiveBackgroundColor = Colors.Transparent;
        var light = WindowRoot.ActualTheme == ElementTheme.Light;
        var fg = light
            ? ColorHelper.FromArgb(255, 0x25, 0x26, 0x20)
            : ColorHelper.FromArgb(255, 0xF2, 0xF3, 0xF6);
        caption.ButtonForegroundColor = fg;
        caption.ButtonInactiveForegroundColor = ColorHelper.FromArgb(160, fg.R, fg.G, fg.B);
        caption.ButtonHoverForegroundColor = fg;
        caption.ButtonHoverBackgroundColor = light
            ? ColorHelper.FromArgb(24, 0x25, 0x26, 0x20)
            : ColorHelper.FromArgb(28, 0xFF, 0xFF, 0xFF);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
