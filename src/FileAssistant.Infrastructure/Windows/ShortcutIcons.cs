using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FileAssistant.Infrastructure.Windows;

public static class ShortcutIcons
{
    public static void UpdateForCurrentUser(string executable, string icon)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        int changed = UpdateFolder(desktop, false, executable, icon) + UpdateFolder(programs, true, executable, icon);
        if (changed > 0) SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED
    }

    internal static int UpdateFolder(string folder, bool recursive, string executable, string icon)
    {
        if (!Directory.Exists(folder) || !File.Exists(icon)) return 0;
        int changed = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.lnk", options))
            {
                try { if (UpdateLink(path, executable, icon)) changed++; }
                catch (Exception error) when (Unavailable(error)) { }
            }
        }
        catch (Exception error) when (Unavailable(error)) { }
        return changed;
    }

    private static bool Unavailable(Exception error) => error is COMException or IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static bool IsSamePath(string candidate, string expected)
    {
        var path = Environment.ExpandEnvironmentVariables(candidate);
        return Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static bool UpdateLink(string path, string executable, string icon)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            file.Load(path, 0); // Read without resolving the target or asking Windows to show UI.
            var buffer = new StringBuilder(32768);
            link.GetPath(buffer, buffer.Capacity, 0, 4); // SLGP_RAWPATH
            if (!IsSamePath(buffer.ToString(), executable)) return false;
            buffer.Clear();
            link.GetIconLocation(buffer, buffer.Capacity, out int index);
            if (index == 0 && IsSamePath(buffer.ToString(), icon)) return false;
            link.SetIconLocation(icon, 0);
            file.Save(path, true);
            return true;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short value);
        void SetHotkey(short value);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
