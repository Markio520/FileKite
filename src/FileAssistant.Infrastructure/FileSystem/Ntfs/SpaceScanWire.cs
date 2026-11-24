using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

public static class SpaceScanWire
{
    public static void Write(BinaryWriter w, SpaceSnapshot s) => Write(w, s, s.Entries);

    internal static void Write(BinaryWriter w, SpaceSnapshot s, IEnumerable<SpaceEntry> rows)
        => Write(w, new SpaceScanMetadata(s.Root, s.Files, s.Folders, s.Volume, s.IssueCounts, s.Issues,
            s.Elapsed, s.Cancelled, s.ScanMethod, s.ScanNotice), s.Entries.Count, rows);

    internal static void Write(BinaryWriter w, SpaceScanMetadata s, int count, IEnumerable<SpaceEntry> rows, bool includeAttributes = false)
    {
        if (includeAttributes) { w.Write((byte)4); w.Write(s.Volume?.NtfsAttributeBytes ?? 0); }
        else w.Write((byte)2);
        w.Write(s.Root); w.Write(s.Files); w.Write(s.Folders);
        w.Write(s.Volume is not null);
        if (s.Volume is { } v) { w.Write(v.Root); w.Write(v.TotalBytes); w.Write(v.FreeBytes); }
        w.Write(s.Elapsed.Ticks); w.Write(s.Cancelled); w.Write(s.ScanMethod); w.Write(s.ScanNotice);
        w.Write(s.IssueCounts.Count);
        foreach (var c in s.IssueCounts) { w.Write((int)c.Key); w.Write(c.Value); }
        w.Write(s.Issues.Count);
        foreach (var issue in s.Issues) { w.Write(issue.Path); w.Write((int)issue.Kind); w.Write(issue.Message); }
        w.Write(count);
        foreach (var e in rows) WriteEntry(w, e);
        w.Flush();
    }

    public static SpaceSnapshot Read(BinaryReader r, long attributeBytes = 0)
        => Read(r, static (metadata, count, rows) =>
        {
            var entries = new SpaceEntryBlocks(count);
            foreach (var row in rows) entries.Add(row);
            return metadata.WithEntries(entries);
        }, CancellationToken.None, attributeBytes);

    // The receiver consumes rows synchronously while the verified helper pipe is open.
    public static T Read<T>(BinaryReader r, Func<SpaceScanMetadata, int, IEnumerable<SpaceEntry>, T> receive,
        CancellationToken token, long attributeBytes = 0)
    {
        token.ThrowIfCancellationRequested();
        var root = r.ReadString(); var files = r.ReadInt32(); var folders = r.ReadInt32();
        if (!Path.IsPathFullyQualified(root) || files < 0 || folders < 1 || attributeBytes < 0) throw new IOException("快速扫描元数据无效。");
        SpaceVolume? volume = r.ReadBoolean() ? new SpaceVolume(r.ReadString(), r.ReadInt64(), r.ReadInt64()) { NtfsAttributeBytes = attributeBytes } : null;
        if (volume is not null && (volume.TotalBytes < 0 || volume.FreeBytes < 0 || volume.FreeBytes > volume.TotalBytes))
            throw new IOException("快速扫描卷容量无效。");
        var elapsed = TimeSpan.FromTicks(r.ReadInt64()); var cancelled = r.ReadBoolean(); var method = r.ReadString(); var notice = r.ReadString();
        if (elapsed < TimeSpan.Zero) throw new IOException("快速扫描耗时无效。");
        var counts = new Dictionary<SpaceIssueKind, int>();
        var count = Count(r, 32);
        for (var i = 0; i < count; i++)
        {
            var kind = (SpaceIssueKind)r.ReadInt32(); var number = r.ReadInt32();
            if (!Enum.IsDefined(kind) || number < 0 || !counts.TryAdd(kind, number)) throw new IOException("快速扫描问题计数无效。");
        }
        var issues = new List<SpaceIssue>(); count = Count(r, 100);
        for (var i = 0; i < count; i++)
        {
            var path = r.ReadString(); var kind = (SpaceIssueKind)r.ReadInt32(); var message = r.ReadString();
            if (!Enum.IsDefined(kind)) throw new IOException("快速扫描问题类型无效。");
            issues.Add(new(path, kind, message));
        }
        count = Count(r, 100_000_000);
        if (count != (long)files + folders) throw new IOException("快速扫描文件和目录数量不符。");
        var metadata = new SpaceScanMetadata(root, files, folders, volume, counts, issues.AsReadOnly(), elapsed, cancelled, method, notice);
        return receive(metadata, count, ReadRows(r, count, folders, token));
    }

    private static IEnumerable<SpaceEntry> ReadRows(BinaryReader r, int count, int expectedFolders, CancellationToken token)
    {
        var directories = new System.Collections.BitArray(count);
        var folders = 0;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = ReadEntry(r);
            var parent = entry.Parent;
            if (i == 0 ? parent != -1 || !entry.IsDirectory : parent < 0 || parent >= i || !directories[parent])
                throw new IOException("快速扫描目录关系无效。");
            if (i != 0 && (entry.Name.Length == 0 || entry.Name is "." or ".." || entry.Name.AsSpan().ContainsAny('\\', '/', '\0')))
                throw new IOException("快速扫描条目名称无效。");
            if (entry.CountedBytes < 0 || entry.AllocatedBytes < -1 || entry.LogicalBytes < -1) throw new IOException("快速扫描条目大小无效。");
            directories[i] = entry.IsDirectory;
            if (entry.IsDirectory) folders++;
            yield return entry;
        }
        if (folders != expectedFolders) throw new IOException("快速扫描目录数量不符。");
    }

    internal static void WriteEntry(BinaryWriter writer, SpaceEntry entry)
    {
        var stamp = entry.Stamp;
        writer.Write(entry.Name); writer.Write(entry.Parent);
        writer.Write(stamp.Identity.Volume); writer.Write(stamp.Identity.Low); writer.Write(stamp.Identity.High);
        writer.Write(stamp.Modified); writer.Write(stamp.Changed); writer.Write((int)stamp.Attributes); writer.Write(stamp.Links);
        writer.Write(entry.LogicalBytes); writer.Write(entry.AllocatedBytes); writer.Write(entry.CountedBytes); writer.Write((byte)entry.Flags);
    }

    internal static SpaceEntry ReadEntry(BinaryReader reader)
    {
        var name = reader.ReadString(); var parent = reader.ReadInt32();
        var identity = new SpaceIdentity(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        var stamp = new SpaceStamp(identity, reader.ReadInt64(), reader.ReadInt64(), (FileAttributes)reader.ReadInt32(), reader.ReadUInt32());
        return new(name, parent, stamp, reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), (SpaceEntryFlags)reader.ReadByte());
    }

    private static int Count(BinaryReader r, int maximum)
    {
        var count = r.ReadInt32();
        return count >= 0 && count <= maximum ? count : throw new IOException("快速扫描数据长度无效。");
    }

    public static void WriteProgress(BinaryWriter writer, SpaceScanProgress p)
    {
        writer.Write(p.LargestFolders.Count == 0 ? (byte)1 : (byte)5); writer.Write(p.Files); writer.Write(p.Folders); writer.Write(p.Skipped);
        writer.Write(p.Bytes); writer.Write(p.Elapsed.Ticks); writer.Write(p.Stage);
        if (p.LargestFolders.Count != 0)
        {
            writer.Write(p.LargestFolders.Count);
            foreach (var folder in p.LargestFolders) { writer.Write(folder.Name); writer.Write(folder.Bytes); }
        }
        writer.Flush();
    }

    public static SpaceScanProgress ReadProgress(BinaryReader r, bool hasFolders = false)
    {
        var progress = new SpaceScanProgress(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt64(),
            TimeSpan.FromTicks(r.ReadInt64()), []) { Stage = r.ReadString() };
        if (!hasFolders) return progress;
        var folders = new SpaceProgressFolder[Count(r, 8)];
        for (int i = 0; i < folders.Length; i++) folders[i] = new(r.ReadString(), r.ReadInt64());
        return progress with { LargestFolders = folders };
    }
}
