using System.ComponentModel;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Indexing;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.FileSystem.Ntfs;

namespace FileAssistant.Indexing.Tests;

public sealed class IndexTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("FileAssistant-IndexTests-").FullName;
    private IndexCatalog Catalog => new(Path.Combine(temp, "catalog"));
    private string Scope => Path.Combine(temp, "资料");
    private static readonly SpaceIdentity Identity = new(ulong.MaxValue, 0xf123456789abcdef, 0xabcd1234567890ef);
    private static readonly SpaceStamp Dir = new(new(42, 3, 0), 1234, 2345, FileAttributes.Directory, 1);
    private static readonly SpaceStamp FileStamp = new(Identity, 321, 456, FileAttributes.Archive, 2);
    private SpaceSnapshot Sample(string last = "另一个名称.txt")
    {
        SpaceEntry[] entries = [
            new("资料",-1,Dir,205,4096,4096), new("图纸",0,Dir,100,4096,4096),
            new("项目[一].PDF",1,FileStamp,100,4096,4096,SpaceEntryFlags.Shared),
            new(last,0,FileStamp,100,4096,0,SpaceEntryFlags.Shared),
            new("δelta.txt",0,FileStamp with { Identity = default, Links = 1 },5,-1,0,SpaceEntryFlags.Partial)
        ];
        return new(Scope, entries, 3, 2, new("C:\\", 10000, 2000), new Dictionary<SpaceIssueKind, int> { { SpaceIssueKind.Metadata, 1 } },
            [new(Scope, SpaceIssueKind.Metadata, "示例")], TimeSpan.FromSeconds(1), false)
        { ScanMethod = "样本" };
    }
    private IndexInfo Save(SpaceSnapshot s)
        => Catalog.ForRoot(s.Root).Replace(s.Metadata, s.Entries.Count, s.Entries, default);
    private IndexPage Page(IndexInfo info, IndexView mode, int dir = 0, string text = "", int start = 0)
        => Catalog.ForRoot(info.Root).Page(info, mode, dir, text, SpaceKind.All, start, default);

    [Fact]
    public void SharedNamesKeepDistinctPathsAndCompleteIdentity()
    {
        var scan = Sample();
        var info = Save(scan);
        var page = Page(info, IndexView.Search, text: "项目[?].p*");
        var item = Assert.Single(page.Rows);
        Assert.Equal(Path.Combine(Scope, "图纸", "项目[一].PDF"), item.Path);
        Assert.Equal(scan.Entries[2], item.Entry);
        Assert.Equal(2U, Page(info, IndexView.Search, text: "*名称*").Rows.Single().Entry.Stamp.Links);
        Assert.Equal(1, Page(info, IndexView.Search, text: "ΔELTA").Total);
        Assert.Equal(0, Page(info, IndexView.Search, text: "%' OR 1=1 --").Total);
        var types = Catalog.ForRoot(Scope).Types(info, default);
        Assert.Equal(SpaceQueries.Types(scan, default).ToArray(), types.ToArray());
        var dirs = Page(info, IndexView.Directory);
        var map = Assert.IsType<IndexMap>(dirs.Map);
        Assert.Equal(SpaceQueries.Map(scan, 0, default).Bytes, map.Bytes);
        var saved = Assert.Single(Catalog.List());
        Assert.Equal(info.Generation, saved.Generation);
        Assert.Equal(scan.Volume, saved.Volume);
        Assert.Equal(scan.Issues.Single(), saved.Issues.Single());
        Assert.Equal(scan.IssueCounts[SpaceIssueKind.Metadata], saved.IssueCounts[SpaceIssueKind.Metadata]);
    }

    [Fact]
    public void CancelledOrTruncatedBuildPreservesLastCompleteGeneration()
    {
        var old = Save(Sample());
        var store = Catalog.ForRoot(Scope);
        var next = Sample("改名.txt");
        using var cts = new CancellationTokenSource();
        IEnumerable<SpaceEntry> Interrupted()
        {
            yield return next.Entries[0];
            cts.Cancel();
            yield return next.Entries[1];
        }
        Assert.ThrowsAny<OperationCanceledException>(() => store.Replace(next.Metadata, 5, Interrupted(), cts.Token));
        Assert.Throws<InvalidDataException>(() => store.Replace(next.Metadata, 5, next.Entries.Take(2), default));
        Assert.Equal(old.Generation, Assert.Single(Catalog.List()).Generation);
        Assert.Equal(1, Page(old, IndexView.Search, text: "另一个").Total);
        var updated = Save(next);
        Assert.Throws<IndexGenerationChangedException>(() => Page(old, IndexView.Directory));
        Assert.Equal(1, Page(updated, IndexView.Search, text: "改名").Total);
    }

    [Fact]
    public async Task ReadersSeeCompleteGenerationDuringRebuild()
    {
        var old = Save(Sample());
        using var started = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var next = Sample("新名字");
        IEnumerable<SpaceEntry> Pause()
        {
            yield return next.Entries[0];
            started.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            foreach (var row in next.Entries.Skip(1)) yield return row;
        }
        var building = Task.Run(() => Catalog.ForRoot(Scope).Replace(next.Metadata, 5, Pause(), default));
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, Page(old, IndexView.Search, text: "另一个").Total);
        }
        finally { resume.Set(); }
        var updated = await building;
        Assert.Equal(1, Page(updated, IndexView.Search, text: "新名字").Total);
    }

    [Fact]
    public void ExactTotalsAndPageBoundariesSurviveTies()
    {
        var list = new List<SpaceEntry> { new("资料", -1, Dir, 803, 803, 803) };
        for (int i = 0; i < 401; i++) list.Add(new($"项目{i:D4}", 0, FileStamp, 2, 2, 2));
        list.Add(new("收据", 0, FileStamp, 1, 1, 1));
        var info = Save(new(Scope, list, 402, 1, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false));
        var first = Page(info, IndexView.Search, text: "项目");
        var second = Page(info, IndexView.Search, text: "项目", start: 200);
        var third = Page(info, IndexView.Search, text: "项目", start: 400);
        Assert.Equal(401, first.Total);
        Assert.Equal(200, first.Rows.Count);
        Assert.Single(third.Rows);
        Assert.Equal(401, first.Rows.Concat(second.Rows).Concat(third.Rows).Select(r => r.Id).Distinct().Count());
        Assert.Equal(402, Page(info, IndexView.LargeFiles).Total);
    }

    [Fact]
    public void WireReceiverRejectsFileParentAndDoesNotPublishIt()
    {
        var sample = Sample();
        var store = Catalog.ForRoot(Scope);
        var old = Save(sample);
        var bad = sample.Entries.ToArray();
        bad[3] = bad[3] with { Parent = 2 };
        var input = new SpaceSnapshot(Scope, bad, 3, 2, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true);
        SpaceScanWire.Write(writer, input);
        data.Position = 0;
        using var reader = new BinaryReader(data);
        Assert.Equal(2, reader.ReadByte());
        Assert.Throws<IOException>(() => SpaceScanWire.Read(reader, (s, n, rows) => store.Replace(s, n, rows, default), default));
        Assert.Equal(old.Generation, Catalog.List().Single().Generation);
    }

    [Theory]
    [InlineData("file-root")]
    [InlineData("wrong-folders")]
    [InlineData("wrong-rows")]
    [InlineData("bad-size")]
    [InlineData("bad-issue")]
    [InlineData("bad-issue-sample")]
    [InlineData("nested-name")]
    [InlineData("negative-elapsed")]
    public void InvalidHelperMetadataPreservesTheLastPublishedGeneration(string fault)
    {
        using var sample = Sample();
        var store = Catalog.ForRoot(Scope);
        var old = Save(sample);
        var entries = sample.Entries.ToArray();
        if (fault == "file-root") entries[0] = entries[0] with { Stamp = FileStamp };
        if (fault == "bad-size") entries[2] = entries[2] with { LogicalBytes = -2 };
        if (fault == "nested-name") entries[2] = entries[2] with { Name = @"parent\child" };
        using var input = new SpaceSnapshot(Scope, entries, fault == "wrong-folders" ? 2 : 3,
            fault is "wrong-folders" or "wrong-rows" ? 3 : 2, null,
            fault == "bad-issue" ? new Dictionary<SpaceIssueKind, int> { [(SpaceIssueKind)999] = 1 } : sample.IssueCounts,
            fault == "bad-issue-sample" ? [new(Scope, (SpaceIssueKind)999, "未知")] : [],
            fault == "negative-elapsed" ? TimeSpan.FromTicks(-1) : TimeSpan.Zero, false);
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true);
        SpaceScanWire.Write(writer, input);
        data.Position = 0;
        using var reader = new BinaryReader(data);
        Assert.Equal(2, reader.ReadByte());
        Assert.Throws<IOException>(() => SpaceScanWire.Read(reader, (s, n, rows) => store.Replace(s, n, rows, default), default));
        Assert.Equal(old.Generation, Assert.Single(Catalog.List()).Generation);
    }

    [Fact]
    public void HardLinkTotalsAndUnknownAllocationChangeWithTheSelectedSizeMetric()
    {
        using var scan = Sample();
        var allocated = SpaceQueries.List(scan, 0, SpaceView.LargeFiles, new(), SpaceSort.Allocated, true, default);
        var logical = SpaceQueries.List(scan, 0, SpaceView.LargeFiles, new(), SpaceSort.Logical, true, default, true);
        Assert.Equal(4096, allocated.Bytes);
        Assert.Equal(205, logical.Bytes);
        Assert.Equal(1, allocated.Unknown);
        Assert.Equal(0, logical.Unknown);
        Assert.Equal(205, SpaceQueries.Map(scan, 0, default, true).Bytes);
        Assert.Equal(205, SpaceQueries.Types(scan, default, true).Sum(type => type.Bytes));
    }

    [Fact]
    public async Task RealFolderUsesSamePhysicalAccountingAndSupportsRebuildAfterMove()
    {
        Directory.CreateDirectory(Path.Combine(Scope, "原稿"));
        var file = Path.Combine(Scope, "原稿", "素材.bin");
        File.WriteAllBytes(file, new byte[8193]);
        if (!CreateHardLinkW(Path.Combine(Scope, "共享素材.bin"), file, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var expected = new SpaceScanner().Scan(Scope, null, default);
        var scanner = new IndexedScanner(Catalog, new FastSpaceScanner("unused-for-directory.exe"));
        var initial = await scanner.BuildAsync(Scope, null, default);
        Assert.Equal(expected.Entries[0].CountedBytes, initial.CountedBytes);
        var links = Page(initial, IndexView.Search, text: "素材").Rows;
        Assert.Equal(2, links.Count);
        Assert.Equal(links[0].Entry.Stamp.Identity, links[1].Entry.Stamp.Identity);
        Assert.Equal(initial.CountedBytes, links.Sum(i => i.Entry.CountedBytes));
        Directory.Move(Path.Combine(Scope, "原稿"), Path.Combine(Scope, "成片"));
        var updated = await scanner.BuildAsync(Scope, null, default);
        Assert.Contains(Page(updated, IndexView.Search, text: "素材").Rows, r => r.Path.Contains("成片"));
        var other = await scanner.BuildAsync(Path.Combine(Scope, "成片"), null, default);
        Assert.Equal(2, Catalog.List().Count);
        Assert.Single(Page(other, IndexView.Search, text: "素材").Rows);
    }

    [Fact]
    public void OlderIndexCanBeSearchedAndUpgradedWithoutLosingMetadata()
    {
        var original = Sample();
        var info = Save(original);
        var store = Catalog.ForRoot(Scope);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + store.DatabasePath))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE name_pages; PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(1, Page(info, IndexView.Search, text: "项目").Total);
        var updated = Save(original);
        Assert.Equal(info.CountedBytes, updated.CountedBytes);
        Assert.Equal(original.Entries[2], Assert.Single(Page(updated, IndexView.Search, text: "项目").Rows).Entry);
    }

    [Fact]
    public void FolderStreamIsLosslessForEmptyAndNestedDirectoriesAndAliases()
    {
        for (int i = 0; i < 530; i++)
        {
            var child = Directory.CreateDirectory(Path.Join(Scope, $"目录{i:D3}"));
            if (i % 64 == 0) File.WriteAllBytes(Path.Join(child.FullName, "内容.bin"), new byte[4221 + i]);
        }
        Directory.CreateDirectory(Path.Combine(Scope, "空目录"));
        Directory.CreateDirectory(Path.Combine(Scope, "a", "b"));
        File.WriteAllText(Path.Combine(Scope, "根文件"), "sample");
        var first = Path.Combine(Scope, "a", "b", "foo");
        File.WriteAllBytes(first, new byte[32678]);
        if (!CreateHardLinkW(Path.Combine(Scope, "别名"), first, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var scanner = new SpaceScanner();
        var reference = scanner.Scan(Scope, null, default);
        scanner.ScanTo(Scope, null, default, (header, count, rows) =>
        {
            Assert.Equal(reference.Root, header.Root);
            Assert.Equal(reference.Files, header.Files);
            Assert.Equal(reference.Folders, header.Folders);
            Assert.Equal(reference.Entries.Count, count);
            Assert.Equal(reference.Entries.ToArray(), rows.ToArray());
            return true;
        });
    }

    [Fact]
    public void SpaceOrdinalsRoundTripAcrossGapsBlockBoundariesAndOlderChildren()
    {
        const int count = 8220;
        var entries = new List<SpaceEntry>
        {
            new("根", -1, Dir, 0, 0, 0), new("主目录", 0, Dir, 0, 0, 0),
            new("早期文件", 0, FileStamp, 0, 0, 0), new("稀疏目录", 0, Dir, 0, 0, 0)
        };
        int[] rare = [2, 5, 6, 8192, 8193, 8218];
        for (int id = entries.Count; id < count; id++)
            entries.Add(new($"文件{id}", rare.Contains(id) ? 3 : 1, FileStamp, 0, 0, 0));
        var sample = new SpaceSnapshot(Scope, entries, count - 3, 3, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
        var store = Catalog.ForRoot(Scope);
        store.ReplaceLive(sample.Metadata, count, entries, default, null);
        // Model a previously indexed file moved under a more recently created folder.
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Pooling=False;Data Source=" + store.DatabasePath))
        {
            db.Open();
            db.CreateCollation("WINORD", StringComparer.OrdinalIgnoreCase.Compare);
            using var move = db.CreateCommand();
            move.CommandText = "UPDATE entries SET parent=3 WHERE id=2";
            move.ExecuteNonQuery();
        }
        foreach (int parent in new[] { 1, 3 })
        {
            string root = Path.Join(Scope, entries[parent].Name);
            using var view = store.OpenSpace(root, default);
            var expected = parent == 3 ? rare : Enumerable.Range(4, count - 4).Except(rare).ToArray();
            Assert.Equal(root, view.PathOf(0));
            Assert.Equal(-1, view.Entries[0].Parent);
            Assert.Equal(expected.Length + 1, view.Entries.Count);
            Assert.Equal(Enumerable.Range(1, expected.Length), view.Children(0));
            foreach (int i in Enumerable.Range(0, expected.Length).Where(i => i < 6 || i >= expected.Length - 34))
            {
                string path = Path.Join(root, entries[expected[i]].Name);
                Assert.Equal(path, view.PathOf(i + 1));
                Assert.Equal(i + 1, view.Find(path));
                Assert.Equal(0, view.Entries[i + 1].Parent);
            }
        }
    }

    [Fact]
    public void ShortUnicodeNamesDoNotMatchAcrossPageOrNameBoundaries()
    {
        var entries = new List<SpaceEntry> { new("根", -1, Dir, 0, 0, 0) };
        for (int i = 0; i < 1100; i++)
            entries.Add(new($"文{i:D4}图纸📁图纸", 0, FileStamp, 0, 0, 0));
        var info = Save(new(Scope, entries, 1100, 1, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false));
        Assert.Equal(1100, Page(info, IndexView.Search, text: "图纸").Total);
        Assert.Equal(1100, Page(info, IndexView.Search, text: "📁").Total);
        Assert.Equal(100, Page(info, IndexView.Search, text: "纸", start: 1000).Rows.Count);
        var crossing = Page(info, IndexView.Search, text: "图纸", start: 508);
        Assert.Equal(Enumerable.Range(508, crossing.Rows.Count).Select(i => entries[i + 1].Name), crossing.Rows.Select(x => x.Entry.Name));
        Assert.Equal(0, Page(info, IndexView.Search, text: "纸文").Total);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newPath, string existingPath, nint security);
    public void Dispose() => Directory.Delete(temp, recursive: true);
}
