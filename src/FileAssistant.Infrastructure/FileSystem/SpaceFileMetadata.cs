using System.ComponentModel;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.FileSystem;

/// <summary>Read only metadata, including for offline placeholders. Never opens a data stream.</summary>
internal static partial class SpaceFileMetadata
{
    private const uint ReadAttributes = 0x80;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenNoRecall = 0x00100000;
    private const FileAttributes CloudFlags = FileAttributes.Offline | (FileAttributes)0x00440000;

    internal readonly record struct Metadata(SpaceStamp Stamp, long Length, long Allocation, bool Cloud);

    public static SafeFileHandle Open(string path, bool listDirectory = false)
    {
        var handle = CreateFileW(Extended(path), ReadAttributes | (listDirectory ? 1u : 0u), 7, 0, 3,
            BackupSemantics | OpenReparsePoint | OpenNoRecall, 0);
        if (!handle.IsInvalid) return handle;
        var error = LastError();
        handle.Dispose();
        throw error;
    }

    public static Metadata Read(string path)
    {
        using var handle = Open(path);
        return Read(handle);
    }

    // The name query returns the identity, timestamps, allocation and link count in one OS call.
    // Special streams still require the handle-based compression and reparse checks.
    private const FileAttributes SpecialAttributes = FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile | CloudFlags;
    private static bool byNameAvailable = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);
    internal static Metadata ReadForScan(string path, FileAttributes listed)
    {
        if (byNameAvailable && (listed & SpecialAttributes) == 0)
        {
            try
            {
                if (StatByName(Extended(path), 3, out var stat, (uint)Marshal.SizeOf<StatBasic>()) && TryMetadata(stat, out var item))
                    return item;
            }
            catch (EntryPointNotFoundException) { byNameAvailable = false; }
        }
        return Read(path);
    }

    private static bool TryMetadata(in StatBasic stat, out Metadata item)
    {
        if ((stat.Attributes & SpecialAttributes) == 0 && stat.Links > 0 && (stat.Low != 0 || stat.High != 0))
        {
            item = new(new(new(stat.Volume, stat.Low, stat.High), stat.Modified, stat.Change, stat.Attributes, stat.Links),
                stat.Length, stat.Allocated, false);
            return true;
        }
        item = default;
        return false;
    }

    internal static (long Modified, long Changed) ReadDirectoryTimes(SafeFileHandle directory)
    {
        if (!ReadBasic(directory, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())) throw LastError();
        return (basic.LastWrite, basic.Change);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBasic
    {
        public long Id, Created, Access, Modified, Change, Allocated, Length;
        public FileAttributes Attributes;
        public uint Tag, Links, Device, Characteristics, Reserved;
        public ulong Volume, Low, High;
    }
    [DllImport("kernelbase.dll", EntryPoint = "GetFileInformationByName", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool StatByName(string path, int infoClass, out StatBasic stat, uint size);

    public static Metadata Read(SafeFileHandle handle)
    {
        if (!ReadBasic(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()) ||
            !ReadStandard(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()))
            throw LastError();
        if (standard.DeletePending != 0) throw new Win32Exception(303);

        SpaceIdentity identity = default;
        if (ReadIdentity(handle, 18, out var id, (uint)Marshal.SizeOf<IdentityInfo>()))
            identity = new(id.Volume, id.Low, id.High);

        var attributes = basic.Attributes;
        var cloud = IsOffline(attributes);
        if ((attributes & FileAttributes.ReparsePoint) != 0 &&
            ReadTag(handle, 9, out var tag, (uint)Marshal.SizeOf<TagInfo>()))
            cloud = IsCloudTag(tag.Tag) || (cloud && tag.Tag is not (0xA0000003 or 0xA000000C));

        var stamp = new SpaceStamp(identity, basic.LastWrite, basic.Change, attributes, standard.Links);
        var allocation = standard.Allocation;
        if (!stamp.IsDirectory && !stamp.IsReparsePoint && !cloud &&
            (attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            allocation = ReadCompression(handle, 8, out var compression, (uint)Marshal.SizeOf<CompressionInfo>())
                ? compression.Bytes : -1;

        return new(stamp, standard.Length, allocation, cloud);
    }

    public static bool IsOffline(FileAttributes attributes) => (attributes & CloudFlags) != 0;
    private static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;
    private static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());
    private static string Extended(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long Created, LastAccess, LastWrite, Change; public FileAttributes Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo { public long Allocation, Length; public uint Links; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdentityInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TagInfo { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CompressionInfo { public long Bytes; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadBasic(SafeFileHandle file, int kind, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadStandard(SafeFileHandle file, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadIdentity(SafeFileHandle file, int kind, out IdentityInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadTag(SafeFileHandle file, int kind, out TagInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadCompression(SafeFileHandle file, int kind, out CompressionInfo info, uint size);
}
