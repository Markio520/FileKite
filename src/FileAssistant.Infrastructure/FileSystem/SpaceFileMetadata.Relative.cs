using System.Runtime.InteropServices;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.FileSystem;

internal static partial class SpaceFileMetadata
{
    private static bool relativeStatAvailable = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);

    internal static unsafe SpaceStamp ReadDirectoryStamp(SafeFileHandle directory)
    {
        if (relativeStatAvailable && NtQueryInformationFile(directory, out _, out var stat, (uint)sizeof(StatBasic),
            FileStatBasicInformation) == 0 && TryMetadata(stat, out var result)) return result.Stamp;
        return Read(directory).Stamp;
    }

    private const int FileStatBasicInformation = 77;

    // The caller holds the enumerated directory open until all of its children have been checked.
    // A counted, relative name lets NTFS look up the child without resolving every ancestor again.
    internal static unsafe bool TryReadRelative(nint directory, string name, FileAttributes listed, out Metadata result)
    {
        result = default;
        if (!relativeStatAvailable || (listed & SpecialAttributes) != 0 || name.Length > ushort.MaxValue / 2) return false;
        fixed (char* buffer = name)
        {
            var text = new UnicodeName { Length = (ushort)(name.Length * sizeof(char)), Buffer = buffer };
            text.MaximumLength = text.Length;
            var attributes = new ObjectAttributes { Length = (uint)sizeof(ObjectAttributes), Root = directory, Name = &text };
            try
            {
                var status = NtQueryInformationByName(&attributes, out _, out var stat, (uint)sizeof(StatBasic), FileStatBasicInformation);
                return status == 0 && TryMetadata(stat, out result);
            }
            catch (EntryPointNotFoundException) { relativeStatAvailable = false; return false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeName { public ushort Length, MaximumLength; public char* Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint Root;
        public UnicodeName* Name;
        public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public nint Status; public nuint Information; }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern unsafe int NtQueryInformationByName(ObjectAttributes* name, out IoStatus status, out StatBasic info, uint length, int kind);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatus status, out StatBasic info, uint length, int kind);
}
