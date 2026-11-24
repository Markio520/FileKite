using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Indexing.Windows;

public readonly record struct JournalHead(ulong Id, long First, long Next)
{
    public bool Contains(JournalCursor value) => value.JournalId == Id && value.Next >= First && value.Next <= Next;
    public JournalCursor Tail => new(Id, Next);
}

public sealed record JournalBatch(JournalCursor End, IReadOnlyList<JournalChange> Changes);
public interface IVolumeJournal
{
    JournalHead? Head(string root);
    JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token);
}

public sealed class NtfsJournal : IVolumeJournal
{
    private const uint QueryJournal = 0x900f4, ReadUnprivileged = 0x903ab;
    private static SafeFileHandle Open(string root) => CreateFileW(Path.GetPathRoot(root)!, 0, 7, 0, 3, 0x02000000, 0);
    public JournalHead? Head(string root)
    {
        var volume = Path.GetPathRoot(root);
        if (volume is not { Length: 3 } || volume[1] != ':')
            return null;
        using var handle = Open(root);
        var data = new byte[128];
        if (handle.IsInvalid || !DeviceIoControl(handle, QueryJournal, [], 0, data, data.Length, out var size, 0) || size < 56)
            return null;
        return new(U64(data, 0), Math.Max(I64(data, 8), I64(data, 24)), I64(data, 16));
    }

    public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = Open(root);
        var input = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(input, cursor.Next);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), cursor.JournalId);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 3);
        var data = new byte[64 * 1024];
        if (handle.IsInvalid || !DeviceIoControl(handle, ReadUnprivileged, input, input.Length, data, data.Length, out var used, 0))
            throw new IOException("无法继续读取 NTFS 变更日志。", new Win32Exception(Marshal.GetLastPInvokeError()));
        return Decode(cursor, through, data.AsSpan(0, used));
    }

    public static JournalBatch Decode(JournalCursor start, long through, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            throw new InvalidDataException("变更日志缺少下一位置。");
        long next = Math.Min(through, I64(bytes, 0));
        if (next <= start.Next && through > start.Next)
            throw new InvalidDataException("变更日志没有推进。");
        var changes = new List<JournalChange>();
        for (int p = 8; p < bytes.Length;)
        {
            var r = bytes[p..];
            if (r.Length < 8)
                throw new InvalidDataException("变更记录不完整。");
            var length = BinaryPrimitives.ReadInt32LittleEndian(r);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(r[4..]);
            var v3 = version == 3;
            var header = v3 ? 76 : 60;
            if (version is not (2 or 3) || length < header || length > r.Length)
                throw new InvalidDataException("变更记录版本或大小无效。");
            r = r[..length];
            var n = BinaryPrimitives.ReadUInt16LittleEndian(r[(header - 4)..]);
            var at = BinaryPrimitives.ReadUInt16LittleEndian(r[(header - 2)..]);
            if ((n & 1) != 0 || n != 0 && at < header || at + n > length)
                throw new InvalidDataException("变更记录名称无效。");
            var usn = I64(r, v3 ? 40 : 24);
            if (usn >= through)
                break;
            if (usn < start.Next)
                throw new InvalidDataException("变更记录早于读取位置。");
            var reason = BinaryPrimitives.ReadUInt32LittleEndian(r[(v3 ? 56 : 40)..]);
            if ((reason & 0x7fffffff) != 0)
                changes.Add(new(U64(r, v3 ? 24 : 16), v3 ? U64(r, 32) : 0, U64(r, 8), v3 ? U64(r, 16) : 0, Encoding.Unicode.GetString(r.Slice(at, n)), reason));
            p += length;
        }

        return new(new(start.JournalId, next), changes);
    }

    private static long I64(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadInt64LittleEndian(b[i..]);
    private static ulong U64(ReadOnlySpan<byte> b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b[i..]);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inputSize, [Out] byte[] output, int capacity, out int used, nint overlapped);
}
