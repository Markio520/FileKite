using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.FileSystem;

public static class IndexStorageAllocation
{
    /// <summary>New SQLite files and their sidecars inherit compression from the dedicated Indexes directory.</summary>
    public static bool TryEnableCompressionForNewFiles(string indexDirectory) => TryCompress(indexDirectory, directory: true);

    /// <summary>Compress an offline FileKite database under exclusive access. Large files take seconds.</summary>
    public static bool TryCompressDatabase(string databasePath) => TryCompress(databasePath, directory: false);

    private static bool TryCompress(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(full)!;
            var name = Path.GetFileName(full);
            if (root.Length != 3 || (directory ? !name.Equals("Indexes", StringComparison.OrdinalIgnoreCase)
                    : name.Length != 72 || !name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                      name.AsSpan(0, 64).IndexOfAnyExcept("0123456789ABCDEFabcdef") >= 0)) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
                !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return false;

            SpaceScanner.CheckAncestors(full);
            var extended = @"\\?\" + full;
            const uint readWrite = 0xc0000000, backupSemantics = 0x02000000, openReparsePoint = 0x00200000, openNoRecall = 0x00100000;
            using var handle = CreateFileW(extended, readWrite, directory ? FileShare.ReadWrite : FileShare.None, 0, 3,
                backupSemantics | openReparsePoint | openNoRecall, 0);
            if (handle.IsInvalid) return false;
            var metadata = SpaceFileMetadata.Read(handle);
            if (metadata.Stamp.IsDirectory != directory || metadata.Cloud ||
                (metadata.Stamp.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.ReadOnly)) != 0 ||
                !directory && metadata.Stamp.Links != 1) return false;

            // Validate the opened object as well as the preceding name checks in case an ancestor was replaced.
            var resolved = new char[extended.Length + 1];
            if (GetFinalPathNameByHandleW(handle, resolved, (uint)resolved.Length, 0) != extended.Length ||
                !extended.AsSpan().Equals(resolved.AsSpan(0, extended.Length), StringComparison.OrdinalIgnoreCase)) return false;
            if (!directory)
            {
                Span<byte> header = stackalloc byte[72];
                if (RandomAccess.Read(handle, header, 0) != header.Length ||
                    !header[..16].SequenceEqual("SQLite format 3\0"u8) || !header[68..].SequenceEqual("FAIX"u8)) return false;
            }
            if ((metadata.Stamp.Attributes & FileAttributes.Compressed) != 0) return true;
            const uint fsctlSetCompression = 0x0009c040;
            ushort defaultCompression = 1;
            return DeviceIoControl(handle, fsctlSetCompression, in defaultCompression, sizeof(ushort), 0, 0, out _, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, in ushort input, uint inputSize,
        nint output, uint outputSize, out uint returned, nint overlapped);
}
