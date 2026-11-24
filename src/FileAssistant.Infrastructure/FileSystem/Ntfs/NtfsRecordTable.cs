using System.Diagnostics.CodeAnalysis;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

// MFT slots are read in physical order. Index blocks sparsely and verify the sequence on every reference lookup.
internal sealed class NtfsRecordTable
{
    private const int BlockSize = 1024;
    private const ulong NumberMask = 0x0000ffffffffffffUL;
    private readonly Dictionary<ulong, NtfsRecord?[]> blocks = [];

    public void Add(NtfsRecord record)
    {
        var number = record.Reference & NumberMask;
        var page = number / BlockSize;
        if (!blocks.TryGetValue(page, out var block)) blocks.Add(page, block = new NtfsRecord?[BlockSize]);
        var slot = (int)(number % BlockSize);
        if (block[slot] is not null) throw new IOException("MFT 记录编号重复。");
        block[slot] = record;
    }

    public bool TryGetValue(ulong reference, [NotNullWhen(true)] out NtfsRecord? record)
    {
        var number = reference & NumberMask;
        record = blocks.TryGetValue(number / BlockSize, out var block) ? block[number % BlockSize] : null;
        if (record?.Reference == reference) return true;
        record = null;
        return false;
    }

    public bool Remove(ulong reference)
    {
        var number = reference & NumberMask;
        if (!blocks.TryGetValue(number / BlockSize, out var block) || block[number % BlockSize]?.Reference != reference) return false;
        block[number % BlockSize] = null;
        return true;
    }

    public IEnumerable<NtfsRecord> Values
    {
        get
        {
            foreach (var block in blocks.Values)
                foreach (var record in block)
                    if (record is not null) yield return record;
        }
    }
}
