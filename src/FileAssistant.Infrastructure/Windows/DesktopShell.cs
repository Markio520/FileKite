using System.ComponentModel;
using System.Runtime.InteropServices;
using FileAssistant.Core.Settings;
using Microsoft.Win32;

namespace FileAssistant.Infrastructure.Windows;

public enum TrayCommand { Open = 1, Index, Settings, Exit }

/// <summary>Owns this window's notification icon, hotkeys and native callback for the UI thread.</summary>
public sealed class DesktopShell : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 61, ModNoRepeat = 0x4000;
    private const uint IconId = 1;
    private readonly nint window;
    private readonly SubclassProc procedure;
    private readonly uint taskbarCreated;
    private readonly Dictionary<ShortcutAction, ShortcutChord> registered = [];
    private readonly Dictionary<ShortcutAction, string> warnings = [];
    private nint icon;
    private string? iconPath;
    private bool ownsIcon, disposed;
    private ShortcutBindings bindings;
    private static readonly ShortcutAction[] GlobalActions = [ShortcutAction.ToggleWindow, ShortcutAction.GlobalSearch];
    public bool TrayAvailable { get; private set; }
    public IReadOnlyDictionary<ShortcutAction, string> Warnings => warnings;
    public event Action<TrayCommand>? Command;
    public event Action<ShortcutAction>? Hotkey;
    public Func<TrayCommand, string> MenuLabel { get; set; } = command => command.ToString();

    public DesktopShell(nint window, ShortcutBindings initial, string title, string? initialIconPath = null)
    {
        this.window = window;
        bindings = initial;
        procedure = HandleMessage;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (!SetWindowSubclass(window, procedure, 61, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Title = title;
        var path = initialIconPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        try
        {
            icon = WindowIcons.LoadFile(path, WindowIcons.GetDpiForWindow(window), smallIcon: true);
            iconPath = path;
            ownsIcon = true;
        }
        catch (Win32Exception) { icon = LoadIcon(0, 32512); }
        AddIcon();
        ResumeHotkeys();
    }

    public string Title { get; private set; }

    public void SetIcon(string icoPath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (StringComparer.OrdinalIgnoreCase.Equals(iconPath, icoPath)) return;
        var next = WindowIcons.LoadFile(icoPath, WindowIcons.GetDpiForWindow(window), smallIcon: true);
        var old = ownsIcon ? icon : 0;
        icon = next;
        ownsIcon = true;
        iconPath = icoPath;
        try
        {
            var data = IconData(2); // NIF_ICON
            if (!TrayAvailable || !Shell_NotifyIcon(1, ref data)) AddIcon();
        }
        finally { WindowIcons.Release(old); }
    }

    public void SetTitle(string text)
    {
        Title = text;
        var data = IconData(4 | 0x80);
        Shell_NotifyIcon(1, ref data);
    }

    private NotifyData IconData(uint flags) => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window,
        Id = IconId, Flags = flags, Message = CallbackMessage, Icon = icon, Tip = Title, Info = "", InfoTitle = "" };

    private void AddIcon()
    {
        if (FindWindow("Shell_TrayWnd", null) == 0) { TrayAvailable = false; return; }
        var data = IconData(1 | 2 | 4 | 0x80);
        TrayAvailable = Shell_NotifyIcon(0, ref data) || Shell_NotifyIcon(1, ref data);
        if (!TrayAvailable) return;
        data.Version = 4;
        Shell_NotifyIcon(4, ref data);
    }

    public void Notify(string title, string body)
    {
        if (!TrayAvailable) return;
        var data = IconData(0x10);
        data.InfoTitle = title[..Math.Min(title.Length, 63)];
        data.Info = body[..Math.Min(body.Length, 255)];
        data.InfoFlags = 1 | 0x80; // NIIF_INFO | NIIF_RESPECT_QUIET_TIME
        Shell_NotifyIcon(1, ref data);
    }

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint context)
    {
        if (msg == taskbarCreated && taskbarCreated != 0) AddIcon();
        else if (msg == 0x0312 && GlobalActions.Contains((ShortcutAction)((int)wParam - 101)))
        {
            Hotkey?.Invoke((ShortcutAction)((int)wParam - 101));
            return 0;
        }
        else if (msg == CallbackMessage)
        {
            switch ((uint)(long)lParam & 0xffff)
            {
                case 0x400: case 0x401: case 0x0203: Command?.Invoke(TrayCommand.Open); break;
                case 0x007B: ShowMenu(); break;
            }
            return 0;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            foreach (var entry in Enum.GetValues<TrayCommand>())
            {
                if (entry == TrayCommand.Exit) AppendMenu(menu, 0x800, 0, null);
                AppendMenu(menu, 0, (nuint)entry, MenuLabel(entry));
            }
            GetCursorPos(out var at);
            SetForegroundWindow(window);
            var result = TrackPopupMenuEx(menu, 0x0100 | 0x0002, at.X, at.Y, window, 0);
            PostMessage(window, 0, 0, 0);
            if (Enum.IsDefined((TrayCommand)result)) Command?.Invoke((TrayCommand)result);
        }
        finally { DestroyMenu(menu); }
    }

    public void SuspendHotkeys()
    {
        foreach (var action in registered.Keys.ToArray()) Release(action);
    }

    public void ResumeHotkeys()
    {
        warnings.Clear();
        foreach (var action in GlobalActions)
            if (ShortcutChord.TryParse(bindings.Get(action), out var chord) && !TryRegister(action, chord))
                warnings[action] = bindings.Get(action) + " · Windows 已占用此组合键";
    }

    public void ChangeHotkeys(ShortcutBindings next, bool retryUnavailable = false)
    {
        var changed = GlobalActions.Where(a => next.Get(a) != bindings.Get(a) || retryUnavailable && !registered.ContainsKey(a)).ToArray();
        var old = registered.ToDictionary();
        foreach (var action in changed) Release(action);
        try
        {
            foreach (var action in changed)
            {
                if (!ShortcutChord.TryParse(next.Get(action), out var chord) || !TryRegister(action, chord))
                    throw new InvalidOperationException(next.Get(action) + " · 快捷键已被 Windows 或其他程序占用。");
            }
        }
        catch
        {
            foreach (var action in changed) Release(action);
            foreach (var action in changed)
                if (old.TryGetValue(action, out var chord)) TryRegister(action, chord);
            throw;
        }
        bindings = next;
        foreach (var action in changed) warnings.Remove(action);
    }

    private bool TryRegister(ShortcutAction action, ShortcutChord chord)
    {
        if (registered.TryGetValue(action, out var existing)) return existing == chord;
        if (!RegisterHotKey(window, (int)action + 101, chord.Modifiers | ModNoRepeat, chord.Key)) return false;
        registered[action] = chord;
        return true;
    }

    private void Release(ShortcutAction action)
    {
        if (registered.Remove(action)) UnregisterHotKey(window, (int)action + 101);
    }

    public bool TryGetIconRect(out NativeRect rect)
    {
        var item = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = window, Id = IconId };
        return Shell_NotifyIconGetRect(ref item, out rect) == 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SuspendHotkeys();
        var data = IconData(0);
        Shell_NotifyIcon(2, ref data);
        RemoveWindowSubclass(window, procedure, 61);
        if (ownsIcon) WindowIcons.Release(icon);
        icon = 0;
        TrayAvailable = false;
    }

    public static void SetLoginStartup(bool enable)
    {
        if (FileAssistant.Core.AppProfile.OverrideDirectory is not null) return;
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var run = enable ? Registry.CurrentUser.CreateSubKey(runKey, true) : Registry.CurrentUser.OpenSubKey(runKey, true);
        if (run is null) return;
        var legacy = FileAssistant.Core.AppProfile.Name;
        var name = "FileKite" + legacy["FileAssistant".Length..];
        WriteLoginStartup(run, name, Environment.ProcessPath ?? throw new InvalidOperationException("无法读取当前程序路径。"), enable, legacy);
    }

    internal static void WriteLoginStartup(RegistryKey key, string valueName, string executable, bool enable, string? legacyName = null)
    {
        var existing = key.GetValue(valueName);
        if (enable)
        {
            if (existing is not null && !OwnCommand(existing))
                throw new IOException("Windows 登录项 " + valueName + " 已由其他程序使用。");
            var command = $"\"{executable}\" --background";
            if (!string.Equals(existing as string, command, StringComparison.Ordinal))
                key.SetValue(valueName, command, RegistryValueKind.String);
        }
        else if (OwnCommand(existing)) key.DeleteValue(valueName, false);
        if (legacyName is not null && OwnCommand(key.GetValue(legacyName))) key.DeleteValue(legacyName, false);

        bool OwnCommand(object? value)
        {
            const string tail = "\" --background";
            return value is string text && text.StartsWith('"') && text.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                && text.Length > tail.Length + 1 && Path.GetFileName(text[1..^tail.Length])
                    .Equals(Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase);
        }
    }

    [StructLayout(LayoutKind.Sequential)] public struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint context);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint context);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
}
