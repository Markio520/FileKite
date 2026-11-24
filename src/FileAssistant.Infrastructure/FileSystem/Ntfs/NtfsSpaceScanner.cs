using System.Diagnostics;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

/// <summary>Read-only bulk MFT scan. Keep the directory hierarchy and extension records in memory.</summary>
public sealed class NtfsSpaceScanner
{
    public void ScanTo(BinaryWriter writer, string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        => DirectoryScanEntries.Stream(rows => Collect(root, progress, token, rows), (metadata, count, entries) =>
        {
            SpaceScanWire.Write(writer, metadata, count, entries, includeAttributes: true);
            return true;
        }, token);

    public SpaceSnapshot Scan(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
    {
        var rows = new DirectoryScanEntries();
        return Collect(root, progress, token, rows).WithEntries(rows.MemoryRows);
    }

    private static SpaceScanMetadata Collect(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token, DirectoryScanEntries rows)
    {
        var clock = Stopwatch.StartNew();
        using var volume = new NtfsVolume(root);
        var journal = volume.Journal();
        var directories = new NtfsRecordTable();
        var extensions = new Dictionary<ulong, List<NtfsRecord>>();
        var parents = new Dictionary<ulong, (int Id, int Slot, bool System)>();
        var issues = new List<SpaceIssue>();
        var counts = new Dictionary<SpaceIssueKind, int>();
        int files = 0, folders = 0;
        long bytes = 0, attributeBytes = 0, lastReport = -300;
        var buffer = new byte[Math.Max(1024 * 1024, volume.RecordSize)];
        string? spoolName = Path.GetFileName(rows.SpoolPath);
        const ulong numberMask = 0x0000ffffffffffffUL;

        void Issue(string message)
        {
            counts[SpaceIssueKind.Metadata] = counts.GetValueOrDefault(SpaceIssueKind.Metadata) + 1;
            if (issues.Count < 100) issues.Add(new(root, SpaceIssueKind.Metadata, message));
        }
        void ReadPass(bool hierarchy, Action<NtfsRecord> visit)
        {
            for (long offset = 0; offset < volume.MftLength;)
            {
                token.ThrowIfCancellationRequested();
                int length = (int)Math.Min(buffer.Length, volume.MftLength - offset);
                length -= length % volume.RecordSize;
                if (length == 0) { if (hierarchy) Issue("MFT 尾部记录不完整。"); break; }
                volume.ReadMft(buffer.AsSpan(0, length), offset);
                for (int p = 0; p < length; p += volume.RecordSize)
                {
                    var data = buffer.AsSpan(p, volume.RecordSize);
                    if (NtfsRecord.U32(data, 0) == 0) continue;
                    bool directoryOrExtension = (NtfsRecord.U16(data, 22) & 2) != 0 || NtfsRecord.U64(data, 32) != 0;
                    if (hierarchy != directoryOrExtension) continue;
                    NtfsRecord? record;
                    try
                    {
                        NtfsRecord.RestoreSectors(data);
                        record = NtfsRecord.Parse(data, (ulong)((offset + p) / volume.RecordSize), volume.ClusterSize);
                    }
                    catch (Exception error) when (error is IOException or OverflowException or ArgumentOutOfRangeException)
                    { Issue($"MFT 记录 {(offset + p) / volume.RecordSize} 无法核实：{error.Message}"); continue; }
                    if (record is not null) visit(record);
                }
                offset += length;
                if (clock.ElapsedMilliseconds - lastReport >= 300)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    progress?.Report(new(files, folders, counts.Values.Sum(), bytes, clock.Elapsed, [])
                        { Stage = hierarchy ? "正在读取磁盘文件索引" : "正在核对文件占用" });
                }
            }
        }
        void Complete(NtfsRecord record)
        {
            if (extensions.Remove(record.Reference, out var extras))
                foreach (var extra in extras)
                {
                    for (int i = 0; i < extra.NameCount; i++) record.AddName(extra.NameAt(i));
                    record.DosNames += extra.DosNames;
                    record.MissingAttributeAllocation |= extra.MissingAttributeAllocation;
                    record.OtherAllocation = checked(record.OtherAllocation + extra.OtherAllocation);
                    if (record.Length < 0 && extra.Length >= 0)
                    { record.Length = extra.Length; record.Allocation = extra.Allocation; record.DataCompressed = extra.DataCompressed; }
                }
            if (record.MissingAttributeAllocation) Issue($"记录 {record.Reference & numberMask} 的附加流占用未能核实。");
            record.FinalizeLinks();
            if (!record.DataCompressed && record.Length >= 0 &&
                (record.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                record.Allocation = record.Length;
            if (record.Length >= 0 && (record.Attributes & FileAttributes.Compressed) != 0)
                record.Allocation = Math.Min(record.Allocation, record.Length);
        }
        SpaceStamp Stamp(NtfsRecord r) => new(new(volume.Serial, r.Reference, 0), r.Modified, r.Changed, r.Attributes, r.Links);
        static SpaceEntryFlags Flags(SpaceStamp stamp, bool system)
        {
            var flags = system ? SpaceEntryFlags.ContainsUnverifiedItems : SpaceEntryFlags.None;
            if (!stamp.IsDirectory && stamp.Links > 1) flags |= SpaceEntryFlags.Shared;
            if ((stamp.Attributes & (FileAttributes.Offline | (FileAttributes)0x00440000)) != 0) flags |= SpaceEntryFlags.Cloud;
            if (stamp.IsReparsePoint) flags |= SpaceEntryFlags.Skipped | SpaceEntryFlags.ContainsUnverifiedItems;
            return flags;
        }

        ReadPass(true, record =>
        {
            if (record.BaseReference == 0) directories.Add(record);
            else
            {
                if (!extensions.TryGetValue(record.BaseReference, out var extras))
                    extensions[record.BaseReference] = extras = [];
                extras.Add(record);
            }
        });
        var rootRecord = directories.Values.FirstOrDefault(r => (r.Reference & numberMask) == 5)
            ?? throw new IOException("未读到 NTFS 根目录。");
        foreach (var dir in directories.Values)
        {
            Complete(dir);
            if (dir == rootRecord) continue;
            for (int i = 0; i < dir.NameCount; i++)
            {
                var alias = dir.NameAt(i);
                if (directories.TryGetValue(alias.Parent, out var parent)) (parent.Children ??= []).Add((dir, alias.Name));
            }
        }
        var topFolders = new List<(string Name, int Slot)>();
        var queue = new Queue<NtfsRecord>();
        folders = 1;
        parents.Add(rootRecord.Reference, (0, rows.Add(new(root,-1,Stamp(rootRecord),0,0,0),-1), false));
        directories.Remove(rootRecord.Reference);
        queue.Enqueue(rootRecord);
        while (queue.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            attributeBytes = checked(attributeBytes + directory.OtherAllocation);
            if (directory.Children is not { } children) continue;
            var parent = parents[directory.Reference];
            foreach (var (child, name) in children)
            {
                if (!directories.Remove(child.Reference)) { Issue("发现重复或循环目录引用。"); continue; }
                var system = parent.System || (child.Reference & numberMask) < 16;
                var stamp = Stamp(child);
                int id = rows.Count;
                int slot = rows.Add(new(name, parent.Id, stamp, 0, 0, 0, Flags(stamp, system)), parent.Slot);
                if (parent.Id == 0) topFolders.Add((name, slot));
                parents.Add(child.Reference, (id, slot, system));
                folders++;
                queue.Enqueue(child);
            }
            directory.Children = null;
        }
        foreach (var dir in directories.Values) Issue($"目录记录 {dir.Reference & numberMask} 缺少有效父目录。");

        ReadPass(false, record =>
        {
            Complete(record);
            bool seen = false;
            var stamp = Stamp(record);
            for (int i = 0; i < record.NameCount; i++)
            {
                var alias = record.NameAt(i);
                if (alias.Name == spoolName) continue;
                if (!parents.TryGetValue(alias.Parent, out var parent)) { Issue($"{alias.Name} 缺少有效父目录。"); continue; }
                var system = parent.System || (record.Reference & numberMask) < 16;
                var flags = Flags(stamp, system);
                long length = record.Length, allocated = record.Allocation;
                if (system && length < 0 && allocated < 0) length = allocated = 0;
                if (length < 0 || allocated < 0) { flags |= SpaceEntryFlags.Partial; Issue($"{alias.Name} 的扩展属性未完整读取。"); }
                var charged = seen ? 0 : Math.Max(0, allocated);
                rows.Add(new(alias.Name, parent.Id, stamp, length, allocated, charged, flags), parent.Slot);
                bytes += charged;
                files++;
                seen = true;
            }
            if (seen) attributeBytes = checked(attributeBytes + record.OtherAllocation);
            if (record.NameCount == 0 && (record.Reference & numberMask) >= 16) Issue($"记录 {record.Reference & numberMask} 缺少文件名。");
        });
        if (counts.Count != 0) rows.MarkPartial(0);
        rows.Finish();
        var largest = topFolders.Select(folder => new SpaceProgressFolder(folder.Name, rows.CountedAt(folder.Slot)))
            .OrderByDescending(folder => folder.Bytes).Take(6).ToArray();
        progress?.Report(new(files, folders, counts.Values.Sum(), bytes, clock.Elapsed, largest)
            { Stage = "目录占用已统计，正在保存明细" });
        var drive = new DriveInfo(root);
        return new(root, files, folders, new SpaceVolume(root, drive.TotalSize, drive.TotalFreeSpace) { NtfsAttributeBytes = attributeBytes }, counts, issues, clock.Elapsed, false, "NTFS 快速扫描",
            journal != volume.Journal() ? "扫描期间磁盘有写入；大小反映读取时的状态，处理前将重新核实。" : "");
    }
}
