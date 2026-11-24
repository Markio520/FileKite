using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Indexing;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing.Tests;

public sealed class BulkStoreTests
{
    [Fact]
    public void LivePagesPreserveUnicodeKeysAndFullPathOrderAcrossPageBoundaries()
    {
        using var fixture = new Fixture();
        var entries = fixture.Entries;
        var store = fixture.Store;
        string[] labels = ["项目_µ", "项目_Σ", "项目_í", "项目_📁", "项目_ſ", "项目_i"];
        for (int i = 0; i < 260; i++)
            for (int parent = 0; parent < 5; parent++)
                fixture.File(parent, $"{labels[i % labels.Length]}-{i:D3}.{(parent % 2 == 0 ? "txt" : "TXT")}");
        fixture.File(1, ".TxT");
        fixture.File(1, "multi.part..TXT");
        fixture.File(1, "no_extension");
        fixture.File(1, "trailing.");
        var info = fixture.Publish();
        var matches = entries.Skip(5).Where(e => e.Name.Contains("项目", StringComparison.Ordinal))
            .Select(e => (e.Name, Path: Path.Join(fixture.Directories[e.Parent], e.Name)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Path).ToArray();
        foreach (int offset in new[] { 0, 500, 1018, 1280 })
        {
            var page = IndexedFileSearch.Query([store], new() { Text = "项目", Offset = offset, PageSize = 32 }, default);
            Assert.Equal(matches.Length, page.Total);
            Assert.Equal(matches.Skip(offset).Take(32), page.Items.Select(x => x.FullPath));
        }
        foreach (var term in new[] { "µ", "σ", "Í", "📁", "ſ" })
        {
            var key = term.ToUpperInvariant();
            var expected = entries.Skip(5).Count(e => e.Name.ToUpperInvariant().Contains(key, StringComparison.Ordinal));
            Assert.Equal(expected, IndexedFileSearch.Query([store], new() { Text = term }, default).Total);
        }
        Assert.Equal(matches.Length + 2, IndexedFileSearch.Query([store], new() { Text = "ext:txt" }, default).Total);
        Assert.Equal(entries.Count - 5, info.Files);

        // Compare the entire on-disk expression index, including empty extensions,
        // to the original .NET extension and collation definitions.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
        db.Open();
        db.CreateCollation("WINORD", StringComparer.OrdinalIgnoreCase.Compare);
        db.CreateFunction<string, string>("WINEXT", name => Path.GetExtension(name).TrimStart('.'), isDeterministic: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", cmd.ExecuteScalar());
    }

    [Fact]
    public void CancellationAfterStreamingTheLastRowPreservesThePublishedLiveIndex()
    {
        using var fixture = new Fixture();
        fixture.File(3, "original.txt");
        var original = fixture.Publish();
        fixture.File(4, "cancelled.txt");
        using var cancel = new CancellationTokenSource();
        var lastRow = new CancelAfterWrite(fixture.Entries.Count, cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Publish(cancel.Token, lastRow));
        Assert.Equal(original.Generation, fixture.Store.GetLiveState()!.Info.Generation);
        Assert.Equal(1, IndexedFileSearch.Query([fixture.Store], new() { Text = "original" }, default).Total);
        Assert.Equal(0, IndexedFileSearch.Query([fixture.Store], new() { Text = "cancelled" }, default).Total);
    }

    private sealed class CancelAfterWrite(int expected, CancellationTokenSource cancel) : IProgress<int>
    {
        public void Report(int count) { if (count == expected) cancel.Cancel(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string temp = Directory.CreateTempSubdirectory("FileKite-BulkStore-").FullName;
        public List<SpaceEntry> Entries { get; } = [];
        public List<string> Directories { get; } = [];
        public IndexStore Store { get; }
        public Fixture()
        {
            string root = Path.Join(temp, "root");
            Folder(-1, root);
            Folder(0, "a");
            Folder(0, "A");
            Folder(1, "é");
            Folder(1, "B");
            Store = new IndexCatalog(Path.Join(temp, "cache")).ForRoot(root);
        }
        private void Folder(int parent, string name)
        {
            Directories.Add(parent < 0 ? name : Path.Join(Directories[parent], name));
            Entries.Add(new(name, parent, Stamp(FileAttributes.Directory), 0, 0, 0));
        }
        public void File(int parent, string name) => Entries.Add(new(name, parent, Stamp(FileAttributes.Normal), 0, 0, 0));
        private SpaceStamp Stamp(FileAttributes attributes) => new(new(47, (ulong)Entries.Count + 20, 0), 123, 456, attributes, 1);
        public IndexInfo Publish(CancellationToken token = default, IProgress<int>? writing = null)
        {
            using var scan = new SpaceSnapshot(Directories[0], Entries, Entries.Count - Directories.Count, Directories.Count,
                null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
            return Store.ReplaceLive(scan.Metadata, Entries.Count, Entries, token, null, writing);
        }
        public void Dispose() => Directory.Delete(temp, true);
    }
}
