using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Infrastructure.FileSystem;

internal static class NamingFileSystem
{
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, nint buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, nint security);
    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    public static SafeFileHandle Open(string path, bool writing)
    {
        CopyFileSystem.CheckPath(path);
        var handle = CreateFileW(Extended(path), writing ? 0x10080u : 0x80u, writing ? 1u : 7u, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) { var code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException(new Win32Exception(code).Message, unchecked((int)(0x80070000u | (uint)code))); }
        return handle;
    }
    public static FileStamp Stamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(((long)info.SizeHigh << 32) | info.SizeLow, DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow).Ticks,
            $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{info.CreationHigh:X8}{info.CreationLow:X8}");
    }
    public static NamingSource Read(string path, bool hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); path = CopyPlanner.Normalize(path); CopyFileSystem.CheckPath(path);
        var attr = File.GetAttributes(path); var directory = (attr & FileAttributes.Directory) != 0;
        using var handle = Open(path, false); var stamp = Stamp(handle);
        if (hash && !directory)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            stamp = Stamp(stream.SafeFileHandle) with { Hash = CopyFileSystem.Hash(stream, token) };
        }
        return new(path, directory, stamp);
    }
    public static bool Matches(string path, FileStamp stamp, bool contents = false)
    {
        if (!CopyFileSystem.Exists(path)) return false;
        var actual = Read(path, contents && stamp.Hash is not null, CancellationToken.None).Stamp!;
        return contents ? SameContents(stamp, actual) : SameIdentity(stamp, actual);
    }
    // NTFS name tunneling can change creation time during a rename. The volume and
    // file index identify the object; retain the timestamp suffix for old journals.
    public static bool SameIdentity(FileStamp expected, FileStamp actual)
        => expected.Identity.Length == 42 && actual.Identity.Length == 42 &&
            expected.Identity.AsSpan(0, 25).SequenceEqual(actual.Identity.AsSpan(0, 25));
    public static bool SameContents(FileStamp expected, FileStamp actual)
        => SameIdentity(expected, actual) && expected.Length == actual.Length &&
            expected.ModifiedUtcTicks == actual.ModifiedUtcTicks && (expected.Hash is null || expected.Hash == actual.Hash);
    public static void Move(string source, string target, FileStamp expected)
    {
        CopyFileSystem.CheckPath(source); CopyFileSystem.CheckPath(target);
        using var handle = Open(source, true);
        if (!SameIdentity(expected, Stamp(handle))) throw new IOException("文件已被替换，请重新生成计划。");
        var name = System.Text.Encoding.Unicode.GetBytes(Extended(target)); var offset = IntPtr.Size == 8 ? 20 : 12;
        var buffer = Marshal.AllocHGlobal(offset + name.Length + 2);
        try
        {
            for (var i = 0; i < offset; i++) Marshal.WriteByte(buffer, i, 0); // ReplaceIfExists = false.
            Marshal.WriteInt32(buffer, offset - 4, name.Length); Marshal.Copy(name, 0, buffer + offset, name.Length);
            Marshal.WriteInt16(buffer, offset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)(offset + name.Length + 2)))
            { var code = Marshal.GetLastWin32Error(); throw new IOException(new Win32Exception(code).Message, unchecked((int)(0x80070000u | (uint)code))); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static void CreateDirectory(string path)
    {
        CopyFileSystem.CheckPath(path);
        if (!CreateDirectoryW(Extended(path), 0)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
    public static void DeleteOwned(string path, FileStamp stamp, bool directory)
    {
        // The handle forbids replacement or writes between the check and disposition.
        using var handle = Open(path, true);
        if (!SameIdentity(stamp, Stamp(handle))) throw new IOException("对象身份已改变，未执行撤销。");
        if (!directory)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var actual = Stamp(stream.SafeFileHandle) with { Hash = CopyFileSystem.Hash(stream, CancellationToken.None) };
            if (!SameContents(stamp, actual)) throw new IOException("创建的文件后来被修改，未执行撤销。");
        }
        if (directory && Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("目录包含其他内容，未执行撤销。");
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(handle, 4, buffer, 1)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
