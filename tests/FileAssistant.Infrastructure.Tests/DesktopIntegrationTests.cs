using System.IO.Pipes;
using System.Runtime.InteropServices;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Windows;
using Microsoft.Win32;

namespace FileAssistant.Infrastructure.Tests;

public sealed class DesktopIntegrationTests
{
    [Fact]
    public void Login_startup_quotes_the_current_build_and_removes_only_its_own_value()
    {
        var keyName = @"Software\FileAssistant-Tests-" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyName, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
            key.SetValue("other", "preserve");
            const string executable = @"C:\有 空格\FileAssistant.App.exe";
            DesktopShell.WriteLoginStartup(key, "app", executable, true);
            Assert.Equal("\"" + executable + "\" --background", key.GetValue("app"));
            Assert.Equal(RegistryValueKind.String, key.GetValueKind("app"));
            DesktopShell.WriteLoginStartup(key, "app", executable, false);
            DesktopShell.WriteLoginStartup(key, "app", executable, false);
            Assert.Null(key.GetValue("app"));
            Assert.Equal("preserve", key.GetValue("other"));
        }
        finally { Registry.CurrentUser.DeleteSubKey(keyName, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Branded_login_value_migrates_only_the_matching_legacy_executable(bool enabled)
    {
        var testKey = @"Software\FileAssistant-Tests-" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(testKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.Volatile);
            const string executable = @"C:\新 版本\FileAssistant.App.exe";
            key.SetValue("FileAssistant", "\"C:\\previous\\FileAssistant.App.exe\" --background");
            key.SetValue("FileAssistant-IndexTrial", "untouched trial");
            DesktopShell.WriteLoginStartup(key, "FileKite", executable, enabled, "FileAssistant");
            Assert.Null(key.GetValue("FileAssistant"));
            Assert.Equal(enabled ? "\"" + executable + "\" --background" : null, key.GetValue("FileKite"));
            Assert.Equal("untouched trial", key.GetValue("FileAssistant-IndexTrial"));
            key.SetValue("FileAssistant", "\"C:\\another.exe\" --background");
            DesktopShell.WriteLoginStartup(key, "FileKite", executable, false, "FileAssistant");
            Assert.Equal("\"C:\\another.exe\" --background", key.GetValue("FileAssistant"));
            Assert.Null(key.GetValue("FileKite"));
            key.SetValue("FileKite", "another product");
            Assert.Throws<IOException>(() => DesktopShell.WriteLoginStartup(key, "FileKite", executable, true, "FileAssistant"));
            Assert.Equal("another product", key.GetValue("FileKite"));
        }
        finally { Registry.CurrentUser.DeleteSubKey(testKey, false); }
    }

    [Fact]
    public void Windows_hotkey_conflict_preserves_the_old_binding_and_dispose_releases_it()
    {
        // A message-only window receives native hotkey messages without a taskbar entry or visible window.
        var hwnd = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new(-3), 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        const uint modifiers = 1 | 2 | 4;
        const uint f22 = 0x85, f23 = 0x86, f24 = 0x87;
        try
        {
            Assert.True(RegisterHotKey(hwnd, 300, modifiers, f22));
            var initial = new ShortcutBindings(ToggleWindow: "Ctrl+Alt+Shift+F23", GlobalSearch: "Ctrl+Alt+Shift+F24");
            using (var shell = new DesktopShell(hwnd, initial, "FileAssistant test"))
            {
                Assert.Empty(shell.Warnings);
                TrayCommand? command = null;
                ShortcutAction? shortcut = null;
                shell.Command += value => command = value;
                shell.Hotkey += value => shortcut = value;
                SendMessage(hwnd, 0x8000 + 61, 0, 0x0400); // NIN_SELECT
                SendMessage(hwnd, 0x0312, (int)ShortcutAction.GlobalSearch + 101, 0); // WM_HOTKEY
                Assert.Equal(TrayCommand.Open, command);
                Assert.Equal(ShortcutAction.GlobalSearch, shortcut);
                Assert.False(RegisterHotKey(hwnd, 301, modifiers, f23));
                Assert.Throws<InvalidOperationException>(() => shell.ChangeHotkeys(initial with { ToggleWindow = "Ctrl+Alt+Shift+F22" }));
                Assert.False(RegisterHotKey(hwnd, 301, modifiers, f23));
                shell.SuspendHotkeys();
                Assert.True(RegisterHotKey(hwnd, 301, modifiers, f23));
                Assert.True(UnregisterHotKey(hwnd, 301));
                shell.ResumeHotkeys();
                Assert.Empty(shell.Warnings);
                Assert.False(RegisterHotKey(hwnd, 301, modifiers, f24));
            }
            Assert.True(RegisterHotKey(hwnd, 301, modifiers, f23));
        }
        finally
        {
            UnregisterHotKey(hwnd, 300);
            UnregisterHotKey(hwnd, 301);
            DestroyWindow(hwnd);
        }
    }

    [Fact]
    public void Isolated_release_and_trial_profiles_use_valid_distinct_mutex_and_pipe_names()
    {
        const string sid = "S-1-5-21-1000";
        var profile = Path.Combine(Path.GetTempPath(), "纸鸢 FileKite", Guid.NewGuid().ToString("N"));
        Assert.Equal("FileAssistant.IndexTrial." + sid, InstanceActivation.GetInstanceName(sid, null, true));
        var legacyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid)))[..12];
        Assert.Equal("FileAssistant-" + legacyKey, InstanceActivation.GetInstanceName(sid, null, false));

        var release = InstanceActivation.GetInstanceName(sid, profile, false);
        var trial = InstanceActivation.GetInstanceName(sid, profile, true);
        Assert.NotEqual(release, trial);
        foreach (var indexTrial in new[] { false, true })
        {
            var name = indexTrial ? trial : release;
            Assert.Equal(name, InstanceActivation.GetInstanceName(sid, profile.ToUpperInvariant(), indexTrial));
            Assert.NotEqual(name, InstanceActivation.GetInstanceName(sid, profile + "2", indexTrial));
            using var mutex = new Mutex(false, @"Local\" + name, out var created);
            Assert.True(created);
            using var pipe = new NamedPipeServerStream(name, PipeDirection.In);
        }
    }

    [Fact]
    public async Task Secondary_launch_recovers_from_a_truncated_activation_and_sends_exact_paths()
    {
        var name = "FileAssistant-Activation-Test-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var active = new InstanceActivation(name);
        var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listen = active.ListenAsync(paths => received.TrySetResult(paths), _ => broken.TrySetResult(), stop.Token);
        try
        {
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(stop.Token);
                await client.WriteAsync(new byte[] { 12, 0 }, stop.Token);
            }
            await broken.Task.WaitAsync(stop.Token);
            string[] paths = [@"D:\试验目录\照片 1.png", @"C:\样本\2.pdf"];
            await active.SendAsync(paths, stop.Token);
            Assert.Equal(paths, await received.Task.WaitAsync(stop.Token));
        }
        finally
        {
            await stop.CancelAsync();
            await listen.WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width,
        int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
