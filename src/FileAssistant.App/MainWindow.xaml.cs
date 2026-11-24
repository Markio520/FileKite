using System;
using System.Runtime.InteropServices;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FileAssistant.App;

public sealed partial class MainWindow : Window
{
    private bool released;
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
        icons.Dispose();
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        App.Appearance.PropertyChanged -= AppearanceChanged;
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
        Controls.WorkspaceMotion.PlayThemeCrossfade(WindowRoot);
    }

    internal void ApplyPreferences(Core.Settings.AppSettings previous, Core.Settings.AppSettings next)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = next.AlwaysOnTop;
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
