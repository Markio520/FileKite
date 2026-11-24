using System.Buffers.Binary;
using System.Text;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.FileSystem.Ntfs;

namespace FileAssistant.Infrastructure.Tests;

public sealed class NtfsScanTests
{
    [Fact]
    public void SparseRecordTableRejectsStaleSequencesAndRemovesOnlyExactReferences()
    {
        var table = new NtfsRecordTable();
        var first = new NtfsRecord { Reference = (7UL << 48) | 5 };
        var distant = new NtfsRecord { Reference = (2UL << 48) | (1UL << 35) };
        table.Add(first); table.Add(distant);
        Assert.False(table.TryGetValue(5, out _));
        Assert.False(table.Remove((6UL << 48) | 5));
        Assert.True(table.TryGetValue(first.Reference, out var same)); Assert.Same(first, same);
        Assert.True(table.Remove(first.Reference)); Assert.False(table.Remove(first.Reference));
        Assert.Same(distant, Assert.Single(table.Values));
    }

    [Fact]
    public void ResidentDataUsesDataAttributeRatherThanStaleFileNameSizes()
    {
        var record = Record((0x30, Name("图片.png", 9, 1)), (0x80, new byte[37]));
        var parsed = NtfsRecord.Parse(record, 42)!;
        Assert.Equal(37, parsed.Length);
        Assert.Equal(40, parsed.Allocation);
        Assert.Equal((3UL << 48) | 42, parsed.Reference);
        Assert.Equal(1, parsed.NameCount);
        Assert.Equal("图片.png", parsed.NameAt(0).Name);
    }

    [Fact]
    public void DosAliasIsNotCountedAsAnotherHardLink()
    {
        var parsed = NtfsRecord.Parse(Record((0x30, Name("long filename.bin", 9, 1)),
            (0x30, Name("LONGFI~1.BIN", 9, 2)), (0x30, Name("second.bin", 10, 1)), (0x80, [])), 42)!;
        Assert.Equal(2, parsed.NameCount);
    }

    [Fact]
    public void NameDeduplicationKeepsDistinctParentsAndOriginalOrder()
    {
        var parsed = NtfsRecord.Parse(Record((0x30, Name("same.txt", 9, 1)), (0x30, Name("same.txt", 9, 1)),
            (0x30, Name("same.txt", 10, 1)), (0x30, Name("short~1.txt", 10, 2)), (0x80, [])), 42)!;
        parsed.FinalizeLinks();
        Assert.Equal(2, parsed.Links);
        Assert.Equal((9UL, "same.txt"), parsed.NameAt(0));
        Assert.Equal((10UL, "same.txt"), parsed.NameAt(1));
    }

    [Fact]
    public void SectorFixupsRejectTornWrites()
    {
        var data = Record((0x80, []));
        Put16(data, 4, 48); Put16(data, 6, 3); Put16(data, 48, 0xbeef);
        Put16(data, 50, 123); Put16(data, 52, 456); Put16(data, 510, 0xbeef); Put16(data, 1022, 0xbeef);
        var torn = data.ToArray(); Put16(torn, 1022, 9);
        Assert.Throws<IOException>(() => NtfsRecord.RestoreSectors(torn));
        NtfsRecord.RestoreSectors(data);
        Assert.Equal(123, NtfsRecord.U16(data, 510)); Assert.Equal(456, NtfsRecord.U16(data, 1022));
    }

    [Fact]
    public void RunlistHandlesSignedDeltasAndSparseRuns()
    {
        var data = new byte[96];
        Put64(data, 24, 5); Put16(data, 32, 64);
        new byte[] { 0x11, 2, 100, 0x11, 2, 0xfb, 0x01, 2, 0 }.CopyTo(data, 64);
        Assert.Equal(new[] { new NtfsRun(0, 2, 100), new NtfsRun(2, 2, 95), new NtfsRun(4, 2, -1) }, NtfsRecord.Runs(data));
        data[64] = 0x19;
        Assert.Throws<IOException>(() => NtfsRecord.Runs(data));
    }

    [Fact]
    public void InvalidAttributeCannotReadOutsideRecord()
    {
        var data = Record((0x80, new byte[16]));
        Put32(data, 60, 4096);
        Assert.Throws<IOException>(() => NtfsRecord.Parse(data, 30));
    }

    [Fact]
    public void CompressedNonresidentDataUsesTotalAllocatedNotVirtualLength()
    {
        var data = Record();
        const int p = 56;
        Put32(data, p, 0x80); Put32(data, p + 4, 80); data[p + 8] = 1;
        Put16(data, p + 12, 1); Put16(data, p + 32, 72);
        Put64(data, p + 40, 131072); Put64(data, p + 48, 131000); Put64(data, p + 64, 8192);
        Put32(data, p + 80, -1); Put32(data, 24, p + 88);
        var parsed = NtfsRecord.Parse(data, 42)!;
        Assert.Equal(131000, parsed.Length); Assert.Equal(8192, parsed.Allocation);
        Assert.True(parsed.DataCompressed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void NamedAttributeUsesOnlyMappedClustersEvenWithoutSparseFlag(int startVcn)
    {
        var data = Record();
        const int p = 56;
        Put32(data, p, 0x80); Put32(data, p + 4, 88); data[p + 8] = 1; data[p + 9] = 1;
        Put16(data, p + 10, 64); Put16(data, p + 64, 'X'); Put16(data, p + 32, 72);
        Put64(data, p + 16, startVcn); Put64(data, p + 24, startVcn + 101);
        Put64(data, p + 40, 102L * 8192);
        new byte[] { 0x01, 100, 0x11, 2, 15, 0 }.CopyTo(data, p + 72);
        Put32(data, p + 88, -1); Put32(data, 24, p + 96);
        var parsed = NtfsRecord.Parse(data, 42, 8192)!;
        Assert.Equal(2 * 8192, parsed.OtherAllocation);
        Assert.False(parsed.MissingAttributeAllocation);
        Put16(data, p + 32, 128);
        var incomplete = NtfsRecord.Parse(data, 42, 8192)!;
        Assert.True(incomplete.MissingAttributeAllocation);
        Assert.Equal(0, incomplete.OtherAllocation);
    }

    [Fact]
    public void ProgressTransportsMeasuredTopFoldersBeforeSnapshot()
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
        SpaceScanWire.WriteProgress(writer, new(5, 2, 0, 77, TimeSpan.FromSeconds(1), [new("样本", 77)]));
        buffer.Position = 0;
        using var reader = new BinaryReader(buffer);
        Assert.Equal(5, reader.ReadByte());
        Assert.Equal(new SpaceProgressFolder("样本", 77), Assert.Single(SpaceScanWire.ReadProgress(reader, true).LargestFolders));
    }

    [Fact]
    public void DeletedRecordsAndExtensionIdentityArePreservedCorrectly()
    {
        var data = Record((0x80, [])); Put16(data, 22, 0);
        Assert.Null(NtfsRecord.Parse(data, 42));
        Put16(data, 22, 1); Put64(data, 32, 1234567);
        Assert.Equal(1234567UL, NtfsRecord.Parse(data, 42)!.BaseReference);
    }

    [Fact]
    public void SnapshotBlocksPreserveExceptionalIdsAndCountsAfterUpdatesAndDrain()
    {
        var stamp = new SpaceStamp(new(4, 20, 0), 123, 456, FileAttributes.Archive, 1);
        var file = new SpaceEntry("普通.jpg", 0, stamp, 21, 4096, 4096);
        var rare = file with { Stamp = stamp with { Identity = new(95, 3, ulong.MaxValue), Links = 8 },
            Flags = SpaceEntryFlags.Shared | SpaceEntryFlags.Partial, CountedBytes = 707 };
        var excluded = file with { CountedBytes = 0, AllocatedBytes = -1 };
        var entries = new SpaceEntryBlocks(4);
        foreach (var row in new[] { file, rare, excluded, file }) entries.Add(row);
        Assert.Equal(new[] { file, rare, excluded, file }, entries.ToArray());

        entries[0] = rare;
        entries[1] = file;
        entries[2] = file with { Flags = SpaceEntryFlags.Skipped, CountedBytes = 0 };
        Assert.Equal(new[] { rare, file, entries[2], file }, entries.Drain().ToArray());
    }

    [Fact]
    public void WirePreservesIdentityAndDirectoryTotals()
    {
        var stamp = new SpaceStamp(new(12, 34, 0), 55, 66, FileAttributes.Directory, 1);
        var snapshot = new SpaceSnapshot("D:\\", [new("D:\\", -1, stamp, 50, 4096, 4096),
            new("内容.txt", 0, stamp with { Attributes = FileAttributes.Archive }, 50, 4096, 4096)],
            1, 1, new("D:\\", 8192, 4096), new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.FromSeconds(2), false)
            { ScanMethod = "NTFS 快速扫描" };
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        SpaceScanWire.Write(writer, snapshot); stream.Position = 0;
        using var reader = new BinaryReader(stream); Assert.Equal(2, reader.ReadByte());
        var restored = SpaceScanWire.Read(reader);
        Assert.Equal(snapshot.Entries, restored.Entries); Assert.Equal(snapshot.ScanMethod, restored.ScanMethod);
        Assert.Equal("D:\\内容.txt", restored.PathOf(1)); Assert.True(restored.IsWholeVolume);
    }

    [Fact]
    public void WireKeepsDirectoryLinksAndFileSizesAcrossStorageBlocks()
    {
        var dir = new SpaceStamp(new(12, 34, 0), 55, 66, FileAttributes.Directory, 1);
        var file = dir with { Attributes = FileAttributes.Archive };
        const int folderIndex = 8192;
        var entries = new List<SpaceEntry> { new(@"D:\", -1, dir, folderIndex, folderIndex, folderIndex) };
        for (var i = 1; i < folderIndex; i++) entries.Add(new($"file{i}", 0, file, 1, 1, 1));
        entries.Add(new("deep", 0, dir, 1, 1, 1));
        entries.Add(new("child", folderIndex, file, 1, 1, 1));
        var snapshot = new SpaceSnapshot(@"D:\", entries, folderIndex, 2, null,
            new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        SpaceScanWire.Write(writer, snapshot);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        Assert.Equal(2, reader.ReadByte());
        var received = SpaceScanWire.Read(reader);
        Assert.Equal(entries, received.Entries.ToArray());
        Assert.Equal(folderIndex + 1, received.Find(@"D:\deep\child"));
        Assert.Equal(folderIndex, received.Children(0).Sum(i => received.Entries[i].CountedBytes));
    }

    [Fact]
    public async Task FolderScopeDoesNotLaunchOrRequireHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), "FileAssistant-mft-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "sample.txt"), "sample");
            var result = await new FastSpaceScanner("missing.exe").ScanAsync(root, null, CancellationToken.None);
            Assert.Equal("目录扫描", result.ScanMethod); Assert.Equal(1, result.Files);
            Assert.Equal("", result.ScanNotice);
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] Record(params (int Type, byte[] Value)[] attributes)
    {
        var result = new byte[1024];
        Put32(result, 0, 0x454c4946); Put16(result, 16, 3); Put16(result, 18, 1); Put16(result, 20, 56); Put16(result, 22, 1);
        var p = 56;
        foreach (var a in attributes)
        {
            var length = (24 + a.Value.Length + 7) & ~7;
            Put32(result, p, a.Type); Put32(result, p + 4, length); Put32(result, p + 16, a.Value.Length); Put16(result, p + 20, 24);
            a.Value.CopyTo(result, p + 24); p += length;
        }
        Put32(result, p, -1); Put32(result, 24, p + 8); return result;
    }

    private static byte[] Name(string name, ulong parent, byte kind)
    {
        var result = new byte[66 + name.Length * 2]; Put64(result, 0, (long)parent);
        Put64(result, 48, 999999); result[64] = (byte)name.Length; result[65] = kind;
        Encoding.Unicode.GetBytes(name).CopyTo(result, 66); return result;
    }
    private static void Put16(byte[] b, int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), v);
    private static void Put32(byte[] b, int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(p), v);
    private static void Put64(byte[] b, int p, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), v);
}
