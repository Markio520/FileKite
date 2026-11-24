using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

// NTFS records normally share one volume, a 64-bit file ID and one allocated/counted size.
// Keep the public snapshot lossless, and allocate the exceptional columns only in blocks that need them.
internal sealed class SpaceEntryBlocks(int expected) : IReadOnlyList<SpaceEntry>
{
    private const int BlockSize = 8192;
    private readonly Block[] blocks = new Block[(expected + BlockSize - 1) / BlockSize];

    private enum CountSource : byte { Allocated, Zero, Explicit }

    private readonly record struct Row(string Name, ulong FileId, long Modified, long Changed, long LogicalBytes,
        long AllocatedBytes, int Parent, FileAttributes Attributes, uint Links, SpaceEntryFlags Flags, CountSource CountSource);

    private sealed class Block(int capacity, ulong volume)
    {
        private readonly Row[] rows = new Row[capacity];
        private ulong[]? volumes;
        private ulong[]? highIds;
        private long[]? explicitCounts;

        public SpaceEntry this[int offset]
        {
            get
            {
                ref readonly var row = ref rows[offset];
                var identity = new SpaceIdentity(volumes?[offset] ?? volume, row.FileId, highIds?[offset] ?? 0);
                var stamp = new SpaceStamp(identity, row.Modified, row.Changed, row.Attributes, row.Links);
                var counted = row.CountSource switch
                {
                    CountSource.Zero => 0,
                    CountSource.Explicit => explicitCounts![offset],
                    _ => row.AllocatedBytes
                };
                return new(row.Name, row.Parent, stamp, row.LogicalBytes, row.AllocatedBytes, counted, row.Flags);
            }
            set
            {
                var stamp = value.Stamp;
                var identity = stamp.Identity;
                if (volumes is null && identity.Volume != volume)
                {
                    volumes = new ulong[capacity];
                    Array.Fill(volumes, volume);
                }
                if (volumes is not null) volumes[offset] = identity.Volume;
                if (identity.High != 0) highIds ??= new ulong[capacity];
                if (highIds is not null) highIds[offset] = identity.High;

                var source = value.CountedBytes == value.AllocatedBytes ? CountSource.Allocated :
                    value.CountedBytes == 0 ? CountSource.Zero : CountSource.Explicit;
                if (source == CountSource.Explicit) (explicitCounts ??= new long[capacity])[offset] = value.CountedBytes;
                rows[offset] = new(value.Name, identity.Low, stamp.Modified, stamp.Changed, value.LogicalBytes,
                    value.AllocatedBytes, value.Parent, stamp.Attributes, stamp.Links, value.Flags, source);
            }
        }
    }

    public int Count { get; private set; }

    public SpaceEntry this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return blocks[index / BlockSize][index % BlockSize];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            blocks[index / BlockSize][index % BlockSize] = value;
        }
    }

    public void Add(SpaceEntry entry)
    {
        var block = Count / BlockSize;
        var offset = Count % BlockSize;
        if (offset == 0) blocks[block] = new Block(Math.Min(BlockSize, expected - Count), entry.Stamp.Identity.Volume);
        blocks[block][offset] = entry;
        Count++;
    }

    // Only the short-lived helper drains blocks. The receiver supports repeated reads.
    public IEnumerable<SpaceEntry> Drain()
    {
        for (var start = 0; start < Count; start += BlockSize)
        {
            var index = start / BlockSize;
            var block = blocks[index];
            blocks[index] = null!;
            var length = Math.Min(BlockSize, Count - start);
            for (var offset = 0; offset < length; offset++) yield return block[offset];
        }
    }

    public IEnumerator<SpaceEntry> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
