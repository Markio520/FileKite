using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem.Ntfs;

namespace FileAssistant.Infrastructure.FileSystem;

internal sealed class DirectoryScanEntries(BinaryWriter? spool = null, string? spoolPath = null)
{
    internal static T Stream<T>(Func<DirectoryScanEntries, SpaceScanMetadata> collect,
        Func<SpaceScanMetadata, int, IEnumerable<SpaceEntry>, T> consume, CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), "FileAssistant-scan-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072,
            FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        var rows = new DirectoryScanEntries(writer, path);
        var metadata = collect(rows);
        token.ThrowIfCancellationRequested();
        writer.Flush();
        file.Position = 0;
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        return consume(metadata, rows.Count, rows.Replay(reader));
    }

    private readonly List<SpaceEntry>? all = spool is null ? [] : null;
    // The spool owns immutable metadata for every entry. Only mutable subtree
    // totals survive in memory once a directory has been traversed.
    private const int BlockSize = 512;
    private readonly List<DirectoryTotals[]> blocks = [];
    private int folders;
    public string? SpoolPath => spoolPath;
    public int Count { get; private set; }
    public IReadOnlyList<SpaceEntry> MemoryRows => all is null ? [] : all.AsReadOnly();

    private struct DirectoryTotals(SpaceEntry row, int parentSlot)
    {
        public readonly int ParentSlot = parentSlot;
        public readonly bool UnknownIdentity = !row.Stamp.Identity.IsKnown;
        public long Logical = row.LogicalBytes, Counted = row.CountedBytes;
        public SpaceEntryFlags Flags = row.Flags;

        public void Include(long logical, long counted, SpaceEntryFlags flags, bool unknownIdentity)
        {
            Logical += Math.Max(0, logical);
            Counted += counted;
            Flags |= flags & (SpaceEntryFlags.Partial | SpaceEntryFlags.Shared | SpaceEntryFlags.ContainsUnverifiedItems);
            if (unknownIdentity || (flags & (SpaceEntryFlags.Skipped | SpaceEntryFlags.Cloud)) != 0)
                Flags |= SpaceEntryFlags.ContainsUnverifiedItems;
        }

        public readonly SpaceEntry Apply(SpaceEntry original) => original with
        {
            LogicalBytes = Logical, CountedBytes = Counted,
            AllocatedBytes = original.AllocatedBytes + (Counted - original.CountedBytes), Flags = Flags
        };
    }

    private ref DirectoryTotals DirectoryAt(int slot) => ref blocks[slot / BlockSize][slot % BlockSize];

    public int Add(SpaceEntry row, int parentSlot)
    {
        if (all is not null)
        {
            all.Add(row);
            return Count++;
        }
        try { SpaceScanWire.WriteEntry(spool!, row); }
        catch (IOException ex) { throw new InvalidOperationException("临时扫描存储写入失败。", ex); }
        Count++;
        if (row.IsDirectory)
        {
            int slot = folders++;
            if (slot % BlockSize == 0) blocks.Add(new DirectoryTotals[BlockSize]);
            DirectoryAt(slot) = new(row, parentSlot);
            return slot;
        }
        DirectoryAt(parentSlot).Include(row.LogicalBytes, row.CountedBytes, row.Flags, !row.Stamp.Identity.IsKnown);
        return -1;
    }

    public void MarkPartial(int slot, bool accessDenied = false)
    {
        var flags = SpaceEntryFlags.Partial | (accessDenied ? SpaceEntryFlags.AccessDenied : 0);
        if (all is null) DirectoryAt(slot).Flags |= flags;
        else all[slot] = all[slot] with { Flags = all[slot].Flags | flags };
    }

    public long CountedAt(int slot) => all is null ? DirectoryAt(slot).Counted : all[slot].CountedBytes;

    public void Finish()
    {
        if (all is not null)
        {
            for (int i = all.Count - 1; i > 0; i--)
            {
                var child = all[i];
                var parent = all[child.Parent];
                var totals = new DirectoryTotals(parent, parent.Parent);
                totals.Include(child.LogicalBytes, child.CountedBytes, child.Flags, !child.Stamp.Identity.IsKnown);
                all[child.Parent] = totals.Apply(parent);
            }
            return;
        }
        for (int i = folders - 1; i > 0; i--)
        {
            var child = DirectoryAt(i);
            DirectoryAt(child.ParentSlot).Include(child.Logical, child.Counted, child.Flags, child.UnknownIdentity);
        }
    }

    public IEnumerable<SpaceEntry> Replay(BinaryReader reader)
    {
        int slot = 0;
        for (int i = 0; i < Count; i++)
        {
            var row = SpaceScanWire.ReadEntry(reader);
            if (row.IsDirectory)
            {
                row = DirectoryAt(slot).Apply(row);
                if (++slot % BlockSize == 0 || slot == folders)
                    blocks[(slot - 1) / BlockSize] = [];
            }
            yield return row;
        }
    }
}
