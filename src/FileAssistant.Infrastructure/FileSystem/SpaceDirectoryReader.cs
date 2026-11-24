using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.FileSystem;

internal readonly record struct SpaceName(string Name, FileAttributes Attributes, long Length, long Modified, ulong IdLow = 0, ulong IdHigh = 0, long Allocation = -1, long Changed = 0);

/// <summary>Enumerates batches from an already opened directory, without following its final component.</summary>
internal sealed class SpaceDirectoryReader
{
    private const int FileIdExtdDirectoryInfo = 19;
    private const int FirstNameByte = 88;
    private readonly byte[] buffer = new byte[64 * 1024];
    private static readonly EnumerationOptions FallbackOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024
    };

    public IEnumerable<SpaceName> Read(SafeFileHandle directory, string path)
    {
        var first = true;
        while (true)
        {
            if (!ReadDirectory(directory, FileIdExtdDirectoryInfo, buffer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 18 or 2) yield break; // ERROR_NO_MORE_FILES or an empty directory
                if (first && error is 1 or 50 or 87 or 120 or 124)
                {
                    foreach (var entry in ReadFallback(path)) yield return entry;
                    yield break;
                }
                throw new Win32Exception(error);
            }

            first = false;
            var offset = 0;
            while (true)
            {
                var entry = Parse(buffer, offset, out var next);
                if (entry.Name is not ("." or "..")) yield return entry;
                if (next == 0) break;
                offset += next;
            }
        }
    }

    private static SpaceName Parse(byte[] bytes, int offset, out int next)
    {
        var row = bytes.AsSpan(offset);
        next = BinaryPrimitives.ReadInt32LittleEndian(row);
        var length = BinaryPrimitives.ReadInt32LittleEndian(row[60..]);
        if (length <= 0 || (length & 1) != 0 || length > row.Length - FirstNameByte ||
            (next != 0 && (next < FirstNameByte + length || next > row.Length - FirstNameByte)))
            throw new IOException("Windows 返回的目录记录不完整。");

        return new(new string(MemoryMarshal.Cast<byte, char>(row.Slice(FirstNameByte, length))),
            (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(row[56..]),
            BinaryPrimitives.ReadInt64LittleEndian(row[40..]), BinaryPrimitives.ReadInt64LittleEndian(row[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(row[72..]), BinaryPrimitives.ReadUInt64LittleEndian(row[80..]),
            BinaryPrimitives.ReadInt64LittleEndian(row[48..]), BinaryPrimitives.ReadInt64LittleEndian(row[32..]));
    }

    private static IEnumerable<SpaceName> ReadFallback(string path) => new FileSystemEnumerable<SpaceName>(path,
        (ref FileSystemEntry entry) => new(entry.FileName.ToString(), entry.Attributes,
            entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.ToFileTime()), FallbackOptions);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    private static extern bool ReadDirectory(SafeFileHandle directory, int kind, [Out] byte[] buffer, uint size);
}
