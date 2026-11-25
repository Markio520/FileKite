using System.Diagnostics;
using System.Runtime.InteropServices;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Tasks;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI.Windowing;

namespace FileAssistant.App;

public partial class App
{
    private static bool BackgroundLaunch => Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase);
    private static string[] LaunchPaths() => Environment.GetCommandLineArgs().Skip(1).Where(path =>
        Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path))).ToArray();

    internal static void ReportFailure(Exception error)
    {
        try
        {
            var folder = Path.Combine(ProfileDirectory, "Logs");
            Directory.CreateDirectory(folder);
            lock (logLock) File.AppendAllText(Path.Combine(folder, "app-errors.log"), $"{DateTimeOffset.Now:O}\n{error}\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { Debug.WriteLine(logError); }
    }
    private static readonly object logLock = new();

    internal static async Task SavePreferenceAsync(Func<AppSettings, AppSettings> change)
    {
        try { await Preferences.UpdateAsync(change); }
        catch (Exception e) { ReportFailure(e); }
    }

    private void InstallDesktopShell()
    {
        shell = new(WindowHandle, Preferences.Current.Shortcuts, InterfaceText.Brand, AppIconAssets.Ico(Preferences.Current.AppIcon));
        shell.MenuLabel = cmd => cmd switch
        {
            TrayCommand.Open => InterfaceText.Get("打开主窗口", "Open main window"),
            TrayCommand.Index => InterfaceText.Get("搜索与索引", "Search and indexing"),
            TrayCommand.Settings => InterfaceText.Get("设置", "Settings"),
            _ => InterfaceText.Get("彻底退出纸鸢", "Quit FileKite")
        };
        shell.Command += command => DispatcherQueue.TryEnqueue(() => _ = HandleTrayAsync(command));
        shell.Hotkey += action => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || decidingClose || SettingsDialog.IsRecordingShortcut) return;
            if (action == ShortcutAction.ToggleWindow && Window.AppWindow.IsVisible && shell.TrayAvailable)
                Window.AppWindow.Hide();
            else
            {
                RestoreMain();
                if (action == ShortcutAction.GlobalSearch) ((MainWindow)Window).WorkspacePage?.OpenGlobalSearch();
            }
        });
        trayDrop = new(shell, ActivatePathsAsync);
        Preferences.PrepareChange = PrepareSettings;
        // Migrate the legacy Run value and refresh the path when an unpackaged build moves.
        try { DesktopShell.SetLoginStartup(Preferences.Current.StartAtLogin); } catch (Exception error) { ReportFailure(error); }
        SyncShortcutIcons(Preferences.Current.AppIcon);
    }

    private Task shortcutUpdates = Task.CompletedTask;
    private string? latestShortcutIcon;

    private void SyncShortcutIcons(string id)
    {
        if (Core.AppProfile.OverrideDirectory is not null || Environment.ProcessPath is not { } executable) return;
        var path = AppIconAssets.Ico(id);
        Volatile.Write(ref latestShortcutIcon, path);
        var pending = shortcutUpdates;
        shortcutUpdates = Task.Run(async () =>
        {
            await pending.ConfigureAwait(false);
            if (Volatile.Read(ref latestShortcutIcon) != path) return;
            try { ShortcutIcons.UpdateForCurrentUser(executable, path); }
            catch (Exception error) { ReportFailure(error); }
        });
    }

    private void PrepareSettings(AppSettings previous, AppSettings next)
    {
        if (previous.AppIcon != next.AppIcon)
        {
            var path = AppIconAssets.Ico(next.AppIcon);
            if (!File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".png")))
                throw new FileNotFoundException(InterfaceText.Get("缺少所选图标的资源文件。", "The selected icon files are missing."), path);
        }
        ValidateEngine(previous.FfmpegPath, next.FfmpegPath, "ffmpeg.exe");
        ValidateEngine(previous.OfficePath, next.OfficePath, "soffice.exe");
        if (previous.Shortcuts != next.Shortcuts) shell?.ChangeHotkeys(next.Shortcuts);
        try
        {
            if (previous.StartAtLogin != next.StartAtLogin) DesktopShell.SetLoginStartup(next.StartAtLogin);
        }
        catch
        {
            if (previous.Shortcuts != next.Shortcuts) shell?.ChangeHotkeys(previous.Shortcuts);
            throw;
        }
        static void ValidateEngine(string oldPath, string path, string name)
        {
            if (oldPath == path || path.Length == 0) return;
            if (!Path.IsPathFullyQualified(path) || !(string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)
                || name == "soffice.exe" && string.Equals(Path.GetFileName(path), "soffice.com", StringComparison.OrdinalIgnoreCase)) || !File.Exists(path))
                throw new ArgumentException(InterfaceText.Get("请选择存在的本机程序文件", "Select an existing local executable") + " · " + name);
        }
    }

    public static IReadOnlyDictionary<ShortcutAction, string> ShortcutWarnings =>
        ((App)Current).shell?.Warnings ?? new Dictionary<ShortcutAction, string>();
    public static void SetShortcutRecording(bool recording)
    {
        var shell = ((App)Current).shell;
        if (recording) shell?.SuspendHotkeys(); else shell?.ResumeHotkeys();
    }
    public static async Task<string?> TrySetShortcutAsync(ShortcutAction action, string chord)
    {
        try
        {
            var bindings = Preferences.Current.Shortcuts;
            if (bindings.Get(action) == chord && ShortcutWarnings.ContainsKey(action))
                ((App)Current).shell?.ChangeHotkeys(bindings, retryUnavailable: true);
            else await Preferences.UpdateAsync(s => s with { Shortcuts = s.Shortcuts.With(action, chord) });
            return null;
        }
        catch (Exception error) { return error.Message; }
    }
    public static async Task<string?> ResetShortcutsAsync()
    {
        try
        {
            var defaults = new ShortcutBindings();
            if (Preferences.Current.Shortcuts == defaults) ((App)Current).shell?.ChangeHotkeys(defaults, retryUnavailable: true);
            else await Preferences.UpdateAsync(s => s with { Shortcuts = defaults });
            return null;
        }
        catch (Exception error) { return error.Message; }
    }

    private void ApplyPreferences(bool initial)
    {
        var s = Preferences.Current;
        var old = initial ? null : applied;
        applied = s;
        if (initial || old!.Units != s.Units || old.ShowExtensions != s.ShowExtensions)
            FileDisplay.Configure(s);
        if (initial || old!.Mode != s.Mode || old.TopNavigation != s.TopNavigation)
            Appearance.Apply(s);
        Jobs.ApplyDefaults(old, s);
        Conversion.ApplyDefaults(old, s); Copy.ApplyDefaults(old, s);
        Naming.ApplyDefaults(old, s); Cleanup.ApplyDefaults(old, s);
        Search.LoadIndexOnDemand = !s.PrewarmIndex;
        if (initial || old!.PinyinSearch != s.PinyinSearch || old.ShowHidden != s.ShowHidden
            || old.ContentLimitMB != s.ContentLimitMB || old.PrewarmIndex != s.PrewarmIndex
            || !old.ExcludedDirectories.SequenceEqual(s.ExcludedDirectories, StringComparer.OrdinalIgnoreCase))
            Search.ApplyPreferences();
        if (initial || old!.LogicalSpaceSize != s.LogicalSpaceSize)
            Space.ApplyPreferences();
        if (initial) return;
        if (old!.PrewarmIndex != s.PrewarmIndex && s.PrewarmIndex) indexWarmup = WarmIndexAsync();
        if (old.IndexedVolumes != s.IndexedVolumes && indexRuntime is not null) _ = UpdateVolumesAsync();
        if (old.Units != s.Units || old.ShowExtensions != s.ShowExtensions)
        {
            foreach (var vm in new[] { Workspace, CopyInput, Conversion.Input, Naming.Input }) vm.RefreshDisplay();
            Outputs.RefreshDisplay();
        }
        if (old.FfmpegPath != s.FfmpegPath || old.OfficePath != s.OfficePath) _ = EngineSettings.RefreshCommand.ExecuteAsync(null);
        if (old.Language != s.Language) shell?.SetTitle(InterfaceText.Brand);
        if (old.AppIcon != s.AppIcon)
        {
            try { shell?.SetIcon(AppIconAssets.Ico(s.AppIcon)); } catch (Exception error) { ReportFailure(error); }
            SyncShortcutIcons(s.AppIcon);
        }
        if (Window is MainWindow main) main.ApplyPreferences(old, s);
    }

    private async Task UpdateVolumesAsync()
    {
        try { if (indexRuntime is not null) await indexRuntime.ApplyVolumeSettingsAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ReportFailure(error); }
    }

    private static void RestoreMain()
    {
        Window.AppWindow.Show();
        if (Window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Window.Activate();
        SetForegroundWindow(WindowHandle);
    }

    private async Task HandleTrayAsync(TrayCommand command)
    {
        if (closing || decidingClose) return;
        try
        {
            if (command == TrayCommand.Exit) { await RequestExitAsync(forceQuit: true); return; }
            RestoreMain();
            if (command is TrayCommand.Settings or TrayCommand.Index && ((MainWindow)Window).WorkspacePage is { } workspace)
                await workspace.OpenSettingsAsync(initialPage: command == TrayCommand.Index ? "search" : null);
        }
        catch (Exception e) { ReportFailure(e); }
    }

    private async Task ActivatePathsAsync(IReadOnlyList<string> paths)
    {
        if (closing) return;
        try
        {
            if (paths.Count == 0) { RestoreMain(); return; }
            await collectionsLoading;
            if (closing) return;
            var dest = Preferences.Current.DropTarget;
            if (dest != DropDestination.SilentStaging) RestoreMain();
            if (((MainWindow)Window).WorkspacePage is { } page) await page.ImportExternalAsync(paths, dest);
        }
        catch (Exception error) { ReportFailure(error); }
    }

    private async Task<bool> ShouldExitAsync(bool forceQuit)
    {
        var s = Preferences.Current;
        if (!forceQuit)
        {
            var action = s.CloseAction;
            if (action == CloseBehavior.Ask)
            {
                var answer = MessageBox(WindowHandle,
                    InterfaceText.Get("是：最小化到托盘\n否：退出纸鸢\n取消：继续使用", "Yes: keep running in the tray\nNo: quit FileKite\nCancel: return to the window"),
                    InterfaceText.Get("关闭主窗口", "Close main window"), 0x23);
                if (answer == 2 || answer == 0) return false;
                action = answer == 6 ? CloseBehavior.Tray : CloseBehavior.Exit;
            }
            if (action == CloseBehavior.Tray && shell?.TrayAvailable == true)
            {
                if (placement is not null) await placement.SaveAsync(Window.AppWindow);
                Window.AppWindow.Hide();
                return false;
            }
        }
        var busy = Jobs.HasRunningJobs || Copy.IsBusy || Conversion.IsBusy || Naming.IsBusy || Cleanup.IsBusy || Space.IsBusy || EngineSettings.IsBusy ||
            Search.IsBusy || Workspace.IsLoading || CopyInput.IsLoading || Conversion.Input.IsLoading || Naming.Input.IsLoading || !indexWarmup.IsCompleted;
        return !s.ConfirmBusyExit || !busy || MessageBox(WindowHandle,
            InterfaceText.Get("仍有后台任务。退出会取消这些任务，是否继续？", "Background tasks are running. Cancel them and quit?"),
            InterfaceText.Get("退出纸鸢", "Quit FileKite"), 0x124) == 6;
    }

    private void OnBatchCompleted(object? sender, BatchCompletion result)
    {
        if (closing) return;
        if (result.Behavior == CompletionBehavior.Tray && (!Window.AppWindow.IsVisible || GetForegroundWindow() != WindowHandle))
            shell?.Notify(InterfaceText.Get("批处理完成", "Batch complete"), result.Summary);
        if (result.Behavior == CompletionBehavior.OpenFolder)
            foreach (var directory in result.OutputDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
                try { if (Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose(); }
                catch (Exception error) { ReportFailure(error); }
    }

    private async Task RunStartupMaintenanceAsync()
    {
        try { await SettingsOperations.PruneExpiredAsync(); } catch (Exception e) { ReportFailure(e); }
        if (!Preferences.Current.CheckUpdatesAtStartup || lifetime.IsCancellationRequested) return;
        try
        {
            var info = await SettingsOperations.QueryUpdateAsync();
            if (info.UpdateAvailable && !closing) shell?.Notify(InterfaceText.Get("有可用更新", "Update available"), info.Message);
        }
        catch (Exception e) { ReportFailure(e); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint window, string text, string caption, uint buttons);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
