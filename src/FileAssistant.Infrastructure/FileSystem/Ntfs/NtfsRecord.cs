using System.Buffers.Binary;
using System.Text;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

internal sealed class NtfsRecord
{
    public ulong Reference;
    public ulong BaseReference;
    public ushort Links;
    public FileAttributes Attributes;
    public long Modified, Changed;
    public long Length = -1, Allocation = -1;
    public long OtherAllocation;
    public bool MissingAttributeAllocation;
    public bool HasList;
    public int DosNames;
    public bool DataCompressed;
    // Most live records have exactly one non-DOS name.
    private (ulong Parent, string Name) firstName;
    private List<(ulong Parent, string Name)>? otherNames;
    public int NameCount => firstName.Name is null ? 0 : 1 + (otherNames?.Count ?? 0);
    public (ulong Parent, string Name) NameAt(int index) => index == 0 ? firstName : otherNames![index - 1];
    public void AddName((ulong Parent, string Name) name)
    {
        if (firstName.Name is null) firstName = name;
        else (otherNames ??= []).Add(name);
    }
    public void FinalizeLinks()
    {
        if (otherNames is not null)
        {
            var seen = new HashSet<(ulong, string)> { firstName };
            otherNames.RemoveAll(name => !seen.Add(name));
        }
        Links = checked((ushort)Math.Max(1, NameCount));
    }
    public List<(NtfsRecord Record, string Name)>? Children;

    internal static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b[p..]);
    internal static uint U32(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b[p..]);
    internal static ulong U64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt64LittleEndian(b[p..]);
    internal static long I64(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadInt64LittleEndian(b[p..]);

    public static void RestoreSectors(Span<byte> data)
    {
        if (data.Length < 48 || U32(data, 0) != 0x454c4946) throw new IOException("MFT 记录签名无效。");
        int offset = U16(data, 4), count = U16(data, 6);
        if (count != data.Length / 512 + 1 || offset < 8 || offset + count * 2 > Math.Min(510, data.Length))
            throw new IOException("MFT 扇区校验数组无效。");
        for (var i = 1; i < count; i++)
        {
            if (U16(data, i * 512 - 2) != U16(data, offset)) throw new IOException("MFT 记录正在变化或扇区不完整。");
            data.Slice(offset + i * 2, 2).CopyTo(data.Slice(i * 512 - 2, 2));
        }
    }

    // FSCTL_GET_NTFS_FILE_RECORD returns records with fixups already applied by NTFS.
    public static NtfsRecord? Parse(ReadOnlySpan<byte> data, ulong number, int clusterSize = 4096)
    {
        if (data.Length < 48 || U32(data, 0) != 0x454c4946) throw new IOException("MFT 记录头无效。");
        var flags = U16(data, 22);
        if ((flags & 1) == 0) return null;
        var result = new NtfsRecord
        {
            Reference = (number & 0x0000ffffffffffffUL) | ((ulong)U16(data, 16) << 48),
            BaseReference = U64(data, 32), Links = U16(data, 18),
            Attributes = (flags & 2) != 0 ? FileAttributes.Directory : 0
        };
        foreach (var range in AttributeRanges(data))
        {
            var a = data.Slice(range.Offset, range.Length);
            var type = U32(a, 0);
            if (type == 0x20) result.HasList = true;
            if (a[8] == 0)
            {
                var value = ResidentValue(a);
                if (type == 0x10)
                {
                    if (value.Length < 36) throw new IOException("MFT 标准信息不完整。");
                    result.Modified = I64(value, 8); result.Changed = I64(value, 16);
                    result.Attributes |= (FileAttributes)(U32(value, 32) & 0x0fffffff);
                }
                else if (type == 0x30)
                {
                    if (value.Length < 66 || value.Length < 66 + value[64] * 2) throw new IOException("MFT 文件名不完整。");
                    if (value[65] == 2) { result.DosNames++; continue; } // DOS alias, not a separate hard link.
                    var name = Encoding.Unicode.GetString(value.Slice(66, value[64] * 2));
                    if (name.Length == 0 || name.IndexOfAny(['\\', '/', '\0']) >= 0 || name is "." or "..") continue;
                    result.AddName((U64(value, 0), name));
                }
                else if (type == 0x80 && a[9] == 0)
                {
                    result.Length = value.Length;
                    // FILE_STANDARD_INFO reports the resident value aligned to eight bytes.
                    // No extra data clusters are allocated; retain the same Windows allocation basis as directory scans.
                    result.Allocation = (value.Length + 7L) & ~7L;
                }
            }
            else if (type == 0x80 && a[9] == 0 && I64(a, 16) == 0)
            {
                result.DataCompressed = (U16(a, 12) & 0x8001) != 0;
                result.Length = I64(a, 48);
                result.Allocation = result.DataCompressed
                    ? a.Length >= 72 && U16(a, 32) >= 72 ? I64(a, 64) : -1
                    : I64(a, 40);
                if (result.Length < 0 || result.Allocation < -1) throw new IOException("MFT 文件长度无效。");
            }
            else if (type != 0x80 || a[9] != 0)
            {
                // The runlist is authoritative even when $BadClus advertises a volume-sized stream
                // without a sparse attribute flag. Count only mapped clusters in this extent.
                try
                {
                    long mapped = 0;
                    foreach (var run in Runs(a))
                        if (run.Lcn >= 0) mapped = checked(mapped + run.Count * clusterSize);
                    result.OtherAllocation = checked(result.OtherAllocation + mapped);
                }
                catch (Exception error) when (error is IOException or OverflowException)
                {
                    result.MissingAttributeAllocation = true;
                }
            }
        }
        // On-disk 0x40000 denotes extended attributes for ordinary files; it is not exposed by FILE_BASIC_INFO.
        if ((result.Attributes & FileAttributes.ReparsePoint) == 0) result.Attributes &= ~(FileAttributes)0x40000;
        if (result.BaseReference == 0 && result.Attributes == 0) result.Attributes = FileAttributes.Normal;
        return result;
    }

    public static AttributeReader AttributeRanges(ReadOnlySpan<byte> record) => new(record);

    public ref struct AttributeReader
    {
        private readonly ReadOnlySpan<byte> record;
        private readonly int used;
        private int offset;
        public (int Offset, int Length) Current { get; private set; }

        internal AttributeReader(ReadOnlySpan<byte> record)
        {
            this.record = record;
            used = checked((int)U32(record, 24));
            offset = U16(record, 20);
            if (used > record.Length || used < 48 || offset < 48 || offset > used - 4)
                throw new IOException("MFT 属性边界无效。");
        }
        public readonly AttributeReader GetEnumerator() => this;
        public bool MoveNext()
        {
            if (offset <= used - 4 && U32(record, offset) == uint.MaxValue) return false;
            if (offset > used - 16) throw new IOException("MFT 属性结束标记缺失。");
            var length = checked((int)U32(record, offset + 4));
            if (length < 24 || (length & 7) != 0 || length > used - offset ||
                record[offset + 8] > 1 || record[offset + 8] == 1 && length < 64)
                throw new IOException("MFT 属性长度无效。");
            Current = (offset, length);
            offset += length;
            return true;
        }
    }

    public static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> a)
    {
        var offset = U16(a, 20); var length = checked((int)U32(a, 16));
        if (offset < 24 || offset > a.Length || length > a.Length - offset) throw new IOException("MFT 常驻属性越界。");
        return a.Slice(offset, length);
    }

    public static List<NtfsRun> Runs(ReadOnlySpan<byte> a)
    {
        var result = new List<NtfsRun>();
        long vcn = I64(a, 16), lcn = 0;
        int p = U16(a, 32);
        if (p < 64 || p >= a.Length || vcn < 0) throw new IOException("MFT 数据段偏移无效。");
        while (p < a.Length)
        {
            var header = a[p++];
            if (header == 0)
            {
                if (vcn != checked(I64(a, 24) + 1)) throw new IOException("MFT 数据段覆盖不完整。");
                return result;
            }
            int size = header & 15, delta = header >> 4;
            if (size is < 1 or > 8 || delta > 8 || p + size + delta > a.Length) throw new IOException("MFT 数据段编码无效。");
            long count = 0;
            for (var i = 0; i < size; i++) count |= (long)a[p++] << (i * 8);
            long change = delta > 0 && (a[p + delta - 1] & 128) != 0 ? -1L : 0;
            for (var i = delta - 1; i >= 0; i--) change = (change << 8) | a[p + i];
            p += delta; lcn = checked(lcn + change);
            if (count <= 0 || lcn < 0) throw new IOException("MFT 数据段范围无效。");
            result.Add(new(vcn, count, delta == 0 ? -1 : lcn));
            vcn = checked(vcn + count);
        }
        throw new IOException("MFT 数据段结束标记缺失。");
    }
}

internal readonly record struct NtfsRun(long Vcn, long Count, long Lcn);
