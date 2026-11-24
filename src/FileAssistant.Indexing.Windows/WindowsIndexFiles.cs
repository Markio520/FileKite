using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Indexing.Windows;

internal sealed class WindowsIndexFiles : IIndexFileSystem
{
    public SpaceEntry? Read(string path) => IndexEntryReader.Read(path);
    public IEnumerable<ListedSpaceFile> Names(string directory) => IndexEntryReader.Names(directory);

    public bool TryReadNamedFile(string path, out SpaceEntry entry)
    {
        entry = default;
        // Read performs the same no-reparse ancestor and file checks as a full reconciliation.
        // The directory lookup also requires list access and returns the on-disk long spelling.
        if (Read(path) is not { IsDirectory: false } item) return false;
        var extended = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using var found = FindFirstFileExW(extended, 1, out var data, 0, 0, 0);
        if (found.IsInvalid || !string.Equals(data.Name, item.Name, StringComparison.Ordinal)) return false;
        entry = item;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFindHandle FindFirstFileExW(string path, int infoLevel, out FindData data, int searchOp, nint filter, uint flags);

    public IEnumerable<string> HardLinks(string path)
    {
        var size = 512u;
        SafeFindHandle handle;
        StringBuilder name;
        while (true)
        {
            name = new StringBuilder(checked((int)size));
            handle = FindFirstFileNameW(path, 0, ref size, name);
            if (!handle.IsInvalid)
                break;
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is 2 or 3 or 5 or 50)
                yield break;
            if (error != 234)
                throw new Win32Exception(error);
        }

        using (handle)
        {
            while (true)
            {
                yield return Path.Join(Path.GetPathRoot(path), name.ToString().TrimStart('\\'));
                while (true)
                {
                    size = (uint)name.Capacity;
                    if (FindNextFileNameW(handle, ref size, name))
                        break;
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 38)
                        yield break;
                    if (error != 234)
                        throw new Win32Exception(error);
                    name = new StringBuilder(checked((int)size));
                }
            }
        }
    }

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle() : base(true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileNameW(string path, uint flags, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileNameW(SafeFindHandle h, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(nint h);
}
