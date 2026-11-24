using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Windows;

namespace FileAssistant.Infrastructure.Tests;

public sealed class FileKiteShellTests
{
    private static string IconPath(string id)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FileAssistant.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "src", "FileAssistant.App", "Assets", "AppIcons", id + ".ico");
    }

    [Fact]
    public void Window_replacement_and_disposal_release_only_owned_icons_even_after_a_failed_switch()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new(-3), 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        var shared = LoadIcon(0, 32512);
        SendMessage(hwnd, 0x80, 0, shared);
        SendMessage(hwnd, 0x80, 1, shared);
        try
        {
            using var icons = new WindowIcons(hwnd);
            nint previousSmall = 0, previousLarge = 0;
            for (int i = 0; i < 80; i++)
            {
                var id = AppIconCatalog.Choices[i % AppIconCatalog.Choices.Count].Id;
                var path = IconPath(id);
                nint temporary = 0;
                icons.SetIcon(path, selected =>
                {
                    Assert.Equal(path, selected);
                    temporary = WindowIcons.LoadFile(selected, 96, false);
                    SendMessage(hwnd, 0x80, 0, temporary);
                    SendMessage(hwnd, 0x80, 1, temporary);
                }, SelectHandle);
                Assert.False(IsLive(temporary));
                if (previousSmall != 0)
                {
                    Assert.False(IsLive(previousSmall));
                    Assert.False(IsLive(previousLarge));
                }
                previousSmall = SendMessage(hwnd, 0x7F, 0, 0);
                previousLarge = SendMessage(hwnd, 0x7F, 1, 0);
                Assert.True(IsLive(previousSmall));
                Assert.True(IsLive(previousLarge));
                Assert.NotEqual(previousSmall, previousLarge);
                icons.SetIcon(path, _ => throw new InvalidOperationException("Same icon was reloaded"), SelectHandle);
            }
            Assert.Throws<IOException>(() => icons.SetIcon(IconPath("cobalt"), _ => throw new IOException("Simulated failure"), SelectHandle));
            var transient = WindowIcons.LoadFile(IconPath("pine"), 96, false);
            Assert.Throws<IOException>(() => icons.SetIcon(IconPath("pine"), _ => SelectHandle(transient),
                _ => throw new IOException("Simulated handle selection failure")));
            Assert.False(IsLive(transient));
            Assert.Equal(previousSmall, SendMessage(hwnd, 0x7F, 0, 0));
            Assert.Equal(previousLarge, SendMessage(hwnd, 0x7F, 1, 0));
            icons.Dispose();
            icons.Dispose();
            Assert.Equal(shared, SendMessage(hwnd, 0x7F, 0, 0));
            Assert.Equal(shared, SendMessage(hwnd, 0x7F, 1, 0));
            Assert.True(IsLive(shared));
            Assert.False(IsLive(previousSmall));
            Assert.False(IsLive(previousLarge));

            void SelectHandle(nint icon)
            {
                SendMessage(hwnd, 0x80, 0, icon);
                SendMessage(hwnd, 0x80, 1, icon);
            }
        }
        finally { DestroyWindow(hwnd); }
    }

    [Fact]
    public void Physical_icon_pack_contains_the_five_transparent_previews_and_nine_windows_sizes()
    {
        Assert.Equal(new[] { "cobalt", "charcoal", "pine", "vermilion", "amber" }, AppIconCatalog.Choices.Select(c => c.Id));
        var hashes = new HashSet<string>();
        foreach (var choice in AppIconCatalog.Choices)
        {
            var file = IconPath(choice.Id);
            var ico = File.ReadAllBytes(file);
            Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
            Assert.Equal(9, BitConverter.ToUInt16(ico, 4));
            int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
            for (var i = 0; i < sizes.Length; i++)
            {
                var entry = 6 + i * 16;
                Assert.Equal(sizes[i], ico[entry] == 0 ? 256 : ico[entry]);
                Assert.Equal(ico[entry], ico[entry + 1]);
                var length = BitConverter.ToInt32(ico, entry + 8);
                var offset = BitConverter.ToInt32(ico, entry + 12);
                Assert.InRange(offset, 6 + sizes.Length * 16, ico.Length - 1);
                Assert.InRange(length, 1, ico.Length - offset);
            }
            using var png = new ImageMagick.MagickImage(Path.ChangeExtension(file, ".png"));
            Assert.Equal((512u, 512u), (png.Width, png.Height));
            Assert.True(png.HasAlpha);
            Assert.True(hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ico))));
        }
        var defaultFile = Path.Combine(Path.GetDirectoryName(IconPath("cobalt"))!, "..", "AppIcon.ico");
        Assert.Equal(File.ReadAllBytes(IconPath("cobalt")), File.ReadAllBytes(defaultFile));
    }

    [Fact]
    public void Tray_updates_the_registered_entry_and_recovers_from_a_taskbar_created_message()
    {
        var hwnd = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new(-3), 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        try
        {
            var explorer = FindWindow("Shell_TrayWnd", null) != 0;
            using var tray = new DesktopShell(hwnd, new(ToggleWindow: "", GlobalSearch: ""), "FileKite", IconPath("cobalt"));
            foreach (var choice in AppIconCatalog.Choices)
            {
                tray.SetIcon(IconPath(choice.Id));
                Assert.Equal(explorer, tray.TrayAvailable);
            }
            tray.SetTitle("紙鳶 · FileKite");
            SendMessage(hwnd, RegisterWindowMessage("TaskbarCreated"), 0, 0);
            Assert.Equal("紙鳶 · FileKite", tray.Title);
            Assert.Equal(explorer, tray.TrayAvailable);
            Assert.Throws<System.ComponentModel.Win32Exception>(() => tray.SetIcon(IconPath("missing")));
            Assert.Equal(explorer, tray.TrayAvailable);
        }
        finally { DestroyWindow(hwnd); }
    }

    [Fact]
    public void Existing_shortcuts_targeting_this_executable_keep_arguments_and_working_directory()
    {
        var root = Directory.CreateTempSubdirectory("FileKite-快捷方式-").FullName;
        try
        {
            var executable = Environment.ProcessPath!;
            var nested = Directory.CreateDirectory(Path.Combine(root, "Programs")).FullName;
            var matching = Path.Combine(nested, "纸鸢.lnk");
            var other = Path.Combine(root, "other.lnk");
            var alternateExe = Path.Combine(root, Path.GetFileName(executable));
            File.WriteAllBytes(alternateExe, []);
            WriteShortcut(matching, executable);
            WriteShortcut(other, alternateExe);
            File.WriteAllText(Path.Combine(root, "broken.lnk"), "not a link");
            var otherBytes = File.ReadAllBytes(other);
            var icon = IconPath("pine");
            Assert.Equal(0, ShortcutIcons.UpdateFolder(root, false, executable, icon));
            Assert.Equal(1, ShortcutIcons.UpdateFolder(root, true, executable, icon));
            Assert.Equal(0, ShortcutIcons.UpdateFolder(root, true, executable, icon));
            var link = (ShortcutIcons.IShellLinkW)new ShortcutIcons.ShellLink();
            try
            {
                ((IPersistFile)link).Load(matching, 0);
                var buffer = new StringBuilder(32768);
                link.GetIconLocation(buffer, buffer.Capacity, out var index);
                Assert.Equal(icon, buffer.ToString(), ignoreCase: true);
                Assert.Equal(0, index);
                buffer.Clear();
                link.GetArguments(buffer, buffer.Capacity);
                Assert.Equal("--background", buffer.ToString());
                buffer.Clear();
                link.GetWorkingDirectory(buffer, buffer.Capacity);
                Assert.Equal(root, buffer.ToString(), ignoreCase: true);
                Assert.Equal(otherBytes, File.ReadAllBytes(other));
                Assert.Equal(3, Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories).Length);
            }
            finally { Marshal.FinalReleaseComObject(link); }

            void WriteShortcut(string file, string target)
            {
                var item = (ShortcutIcons.IShellLinkW)new ShortcutIcons.ShellLink();
                try
                {
                    item.SetPath(target);
                    item.SetArguments("--background");
                    item.SetWorkingDirectory(root);
                    item.SetIconLocation(IconPath("cobalt"), 0);
                    ((IPersistFile)item).Save(file, true);
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static bool IsLive(nint handle)
    {
        if (!GetIconInfo(handle, out var info)) return false;
        if (info.Color != 0) DeleteObject(info.Color);
        if (info.Mask != 0) DeleteObject(info.Mask);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public int IsIcon; public uint X, Y; public nint Mask, Color; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint message, nint index, nint hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint id);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
}
