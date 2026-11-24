using System.Runtime.InteropServices;
using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Settings;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace FileAssistant.App.Bootstrap;

/// <summary>The shell owns the notification area. A nonactivating app window accepts storage items over our icon during a drag.</summary>
internal sealed class TrayFileDrop : IDisposable
{
    private readonly DesktopShell shell;
    private readonly Func<IReadOnlyList<string>, Task> receive;
    private readonly DispatcherQueueTimer timer;
    private Window? target;
    private bool previousDown, beganOutside, dropping;
    public TrayFileDrop(DesktopShell shell, Func<IReadOnlyList<string>, Task> receive)
    {
        this.shell = shell;
        this.receive = receive;
        timer = App.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(140);
        timer.Tick += Poll;
        timer.Start();
    }

    private void Poll(DispatcherQueueTimer sender, object args)
    {
        if (dropping) return;
        var down = (GetAsyncKeyState(1) & 0x8000) != 0;
        if (!down)
        {
            if (previousDown && target?.AppWindow.IsVisible == true) target.AppWindow.Hide();
            previousDown = beganOutside = false;
            return;
        }
        if (!GetCursorPos(out var point) || !shell.TryGetIconRect(out var rect)) return;
        var over = point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
        if (!previousDown) beganOutside = !over;
        previousDown = true;
        if (!beganOutside || !over)
        {
            if (target?.AppWindow.IsVisible == true) target.AppWindow.Hide();
            return;
        }
        if (target?.AppWindow.IsVisible == true) return;
        target ??= CreateTarget();
        target.AppWindow.MoveAndResize(new(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top)));
        target.AppWindow.Show(activateWindow: false);
    }

    private Window CreateTarget()
    {
        var panel = new Grid { AllowDrop = true, Background = (Brush)Application.Current.Resources["WorkspaceCardBrush"] };
        panel.Children.Add(new FontIcon { Glyph = "\uE896", FontSize = 15 });
        panel.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = InterfaceText.Get("加入纸鸢", "Add to FileKite");
            e.Handled = true;
        };
        panel.Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            dropping = true;
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var paths = await FileInputSources.ReadAsync(e.DataView);
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.Handled = true;
                target?.AppWindow.Hide();
                await receive(paths);
            }
            catch (Exception error) { App.ReportFailure(error); }
            finally { dropping = false; target?.AppWindow.Hide(); deferral.Complete(); }
        };
        var window = new Window { Content = panel };
        window.AppWindow.IsShownInSwitchers = false;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        return window;
    }

    public void Dispose() { timer.Stop(); timer.Tick -= Poll; target?.Close(); target = null; }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 point);
}
