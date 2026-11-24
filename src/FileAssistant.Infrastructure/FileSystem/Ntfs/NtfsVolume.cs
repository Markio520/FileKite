using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static FileAssistant.Infrastructure.FileSystem.Ntfs.NtfsRecord;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

internal sealed class NtfsVolume : IDisposable
{
    private readonly SafeFileHandle handle;
    public readonly ulong Serial;
    public readonly int RecordSize, ClusterSize;
    public readonly long MftLength;
    private readonly List<NtfsRun> runs = [];

    public NtfsVolume(string root)
    {
        if (root.Length != 3 || root[1] != ':' || root[2] != '\\' || !char.IsAsciiLetter(root[0]))
            throw new ArgumentException("快速扫描只接受本地磁盘根目录。");
        handle = CreateFileW(@"\\.\" + root[..2], 0x80000000, 7, 0, 3, 0, 0);
        if (handle.IsInvalid) { var error = new Win32Exception(Marshal.GetLastPInvokeError()); handle.Dispose(); throw error; }
        try
        {
            var info = Control(0x90064, [], 128);
            if (info.Length < 104 || U16(info, 100) != 3 || U16(info, 102) > 1) throw new IOException("不支持此 NTFS 版本。");
            Serial = U64(info, 0); ClusterSize = checked((int)U32(info, 44)); RecordSize = checked((int)U32(info, 48));
            MftLength = I64(info, 56);
            if (ClusterSize is < 512 or > 2097152 || RecordSize is < 512 or > 65536 || RecordSize % 512 != 0 || MftLength < RecordSize)
                throw new IOException("NTFS 卷参数无效。");
            var first = new byte[RecordSize];
            ReadDisk(first, checked(I64(info, 64) * ClusterSize)); RestoreSectors(first);
            AddRuns(first);
            // Attribute lists describe additional fragments of $MFT. Resolve only those records through NTFS.
            foreach (var range in AttributeRanges(first))
            {
                var a = first.AsSpan(range.Offset, range.Length);
                if (U32(a, 0) != 0x20) continue;
                byte[] list;
                if (a[8] == 0) list = ResidentValue(a).ToArray();
                else
                {
                    var length = I64(a, 48);
                    if (length is < 0 or > 16777216) throw new IOException("MFT 属性清单过大。");
                    var aligned = new byte[checked((int)((length + 511) & ~511L))];
                    ReadMapped(aligned, 0, Runs(a));
                    list = aligned[..(int)length];
                }
                var seen = new HashSet<ulong>();
                for (var p = 0; p + 26 <= list.Length;)
                {
                    var length = U16(list, p + 4);
                    if (length < 26 || length > list.Length - p) throw new IOException("MFT 属性清单损坏。");
                    if (U32(list, p) == 0x80 && list[p + 6] == 0)
                    {
                        var reference = U64(list, p + 16);
                        if ((reference & 0x0000ffffffffffffUL) != 0 && seen.Add(reference))
                        {
                            var extra = GetRecord(reference);
                            if (U64(extra, 32) != ((ulong)U16(first, 16) << 48)) throw new IOException("MFT 扩展记录已变化。");
                            AddRuns(extra);
                        }
                    }
                    p += length;
                }
            }
            runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            long end = 0;
            foreach (var run in runs)
            {
                if (run.Vcn != end || run.Lcn < 0) throw new IOException("MFT 数据映射不连续。");
                end = checked(end + run.Count);
            }
            if (checked(end * ClusterSize) < MftLength) throw new IOException("MFT 数据映射不足。");
        }
        catch { handle.Dispose(); throw; }
    }

    private void AddRuns(byte[] record)
    {
        foreach (var range in AttributeRanges(record))
        {
            var a = record.AsSpan(range.Offset, range.Length);
            if (U32(a, 0) == 0x80 && a[9] == 0 && a[8] == 1) runs.AddRange(Runs(a));
        }
    }

    public byte[] GetRecord(ulong reference)
    {
        var data = Control(0x90068, BitConverter.GetBytes(reference & 0x0000ffffffffffffUL), RecordSize + 16);
        if (data.Length < 12 + RecordSize || (U64(data, 0) & 0x0000ffffffffffffUL) != (reference & 0x0000ffffffffffffUL) ||
            U32(data, 8) != RecordSize) throw new IOException("NTFS 记录已删除或替换。");
        var record = data.AsSpan(12, RecordSize).ToArray();
        if (reference >> 48 != 0 && U16(record, 16) != reference >> 48) throw new IOException("NTFS 记录身份已变化。");
        return record;
    }

    public void ReadMft(Span<byte> destination, long offset) => ReadMapped(destination, offset, runs);

    private void ReadMapped(Span<byte> destination, long offset, List<NtfsRun> mapping)
    {
        while (!destination.IsEmpty)
        {
            var vcn = offset / ClusterSize;
            var found = false;
            foreach (var run in mapping)
            {
                if (vcn < run.Vcn || vcn >= run.Vcn + run.Count) continue;
                var within = offset - checked(run.Vcn * ClusterSize);
                var count = (int)Math.Min(destination.Length, checked(run.Count * ClusterSize) - within);
                if (run.Lcn < 0) destination[..count].Clear();
                else ReadDisk(destination[..count], checked(run.Lcn * ClusterSize + within));
                destination = destination[count..]; offset += count; found = true; break;
            }
            if (!found) throw new IOException("无法映射 MFT 数据段。");
        }
    }

    private void ReadDisk(Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(handle, destination, offset);
            if (read == 0) throw new EndOfStreamException("磁盘元数据读取不完整。");
            destination = destination[read..]; offset += read;
        }
    }

    public (ulong Id, long Next)? Journal()
    {
        try { var data = Control(0x900f4, [], 80); return data.Length >= 24 ? (U64(data, 0), I64(data, 16)) : null; }
        catch (Win32Exception) { return null; }
    }

    private byte[] Control(uint code, byte[] input, int size)
    {
        var output = new byte[size];
        if (!DeviceIoControl(handle, code, input, input.Length, output, output.Length, out var count, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return output[..count];
    }

    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
}
