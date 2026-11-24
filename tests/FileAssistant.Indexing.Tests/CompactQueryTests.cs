using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing.Tests;

public sealed class CompactQueryTests : IDisposable
{
    private readonly string temporary = Directory.CreateTempSubdirectory("FileKite-CompactQueries-").FullName;

    [Fact]
    public void CountsAndRanksMatchAcrossPackedPrefixesPostingsVisibilityAndReopen()
    {
        var root = Path.Join(temporary, "数据");
        var catalog = new IndexCatalog(Path.Join(temporary, "索引"));
        var entries = new List<SpaceEntry>();
        var paths = new List<SearchFile>();
        void Add(int parent, string name, FileAttributes attributes)
        {
            int id = entries.Count;
            var stamp = new SpaceStamp(new(17, (ulong)(1024 + id), 0), 0, 0, attributes, 1);
            var full = parent < 0 ? root : Path.Join(paths[parent].FullPath, name);
            bool folder = (attributes & FileAttributes.Directory) != 0;
            paths.Add(new(full, folder, folder ? null : 0, null));
            entries.Add(new(name, parent, stamp, 0, 0, 0));
        }
        Add(-1, root, FileAttributes.Directory);
        foreach (var name in new[] { "A", "a", "隐藏", "node_modules", "alias.txt", "别处", "跨页" })
            Add(0, name, FileAttributes.Directory | (name == "隐藏" ? FileAttributes.Hidden : 0));
        for (int i = 0; i < 96; i++)
            Add(7, $"group-{i:D2}", FileAttributes.Directory);
        for (int i = 0; i < 2600; i++)
        {
            string suffix = (i % 13) switch { 0 => "重庆", 1 => "ASCII绿", 2 => "报价", 3 => "A📁ü", _ => "ordinal" };
            Add(8 + i % 96, $"Common-Prefix-{i % 127:D3}-{suffix}-{i / 96:D2}.TxT",
                i % 29 == 0 ? FileAttributes.Hidden : FileAttributes.Normal);
        }
        for (int i = 1; i <= 103; i++) Add(i, "NODE.txt", FileAttributes.Normal);
        Add(1, ".TXT", FileAttributes.Normal);
        Add(3, "hidden-doc.txt", FileAttributes.Normal);
        Add(4, "excluded-doc.txt", FileAttributes.Normal);
        Add(5, "file.longextension", FileAttributes.Normal);

        using var snapshot = new SpaceSnapshot(root, entries, entries.Count - 104, 104,
            null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
        var store = catalog.ForRoot(root);
        store.ReplaceLive(snapshot.Metadata, entries.Count, entries, default, null);
        var mode = new SearchRequest { PinyinSearch = true, ShowHidden = false, ExcludedDirectories = ["NoDe_MoDuLeS"] };
        var excludes = new SearchDirectoryFilter(["node_modules", paths[3].FullPath]);
        var visible = paths.Where((file, id) => !excludes.Excludes(file.IsFolder ? file.FullPath : file.Directory)
                && (entries[id].Stamp.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).ToList();
        // Build and another IndexStore instance exercise separate read transactions and visibility caches.
        foreach (var reader in new[] { store, catalog.ForRoot(root) })
        {
            foreach (string term in new[] { "", "n", "no", "node", "mon-pre", "x-00", "ordinal", ".txt", "ext:txt",
                         "ext:longextension", "跨页", "baojia", "chongqing", "lv", "lü", "无此文件abc" })
            {
                var request = mode with { Text = term };
                var match = SearchRules.Compile(request);
                var expected = visible.Where(match.Matches).Order(IndexedFileSearch.Order(request)).Select(f => f.FullPath).ToArray();
                foreach (int offset in new[] { 0, 61, 509, 1017 })
                {
                    var actual = IndexedFileSearch.Query([reader], request with { Offset = offset, PageSize = 67 }, default);
                    Assert.Equal(expected.Length, actual.Total);
                    Assert.Equal(expected.Skip(offset).Take(67), actual.Items.Select(f => f.FullPath));
                }
            }
        }
        Assert.Equal(paths.Count(f => !f.IsFolder && f.Extension == "txt"),
            IndexedFileSearch.Query([store], new() { Text = "ext:txt" }, default).Total);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => IndexedFileSearch.Query([store], mode with { Text = "no" }, cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhoneticMasksMatchTheLiveIcuPredicateIncludingContextsAndVersionFallback(bool differentVersion)
    {
        string root = Path.Join(temporary, "kites");
        string[] names = ["A重庆B", "重庆银行", "重-庆", "A女V", "女绿率", "x绿1", "123女abc", "行长-v", "（二）重庆.txt",
            "报告_履历", "A📁重庆Z", "xx-银行-xx", "ü女", "É京", "Σ庆", "文件📁.txt", "index.VB", "𠀀-县", "没有扩展名"];
        var dir = new SpaceStamp(new(71, 48, 0), 0, 0, FileAttributes.Directory, 1);
        var entries = new List<SpaceEntry> { new(root, -1, dir, 0, 0, 0) };
        for (int i = 0; i < names.Length; i++)
            entries.Add(new(names[i], 0, dir with { Attributes = FileAttributes.Normal, Identity = new(71, (ulong)i + 49, 0) }, 0, 0, 0));
        using var snapshot = new SpaceSnapshot(root, entries, names.Length, 1, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
        var store = new IndexCatalog(temporary).ForRoot(root);
        store.ReplaceLive(snapshot.Metadata, entries.Count, entries, default, null);
        if (differentVersion)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString());
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE name_page_grams SET mask=0 WHERE section=-1";
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        string[] terms = ["n", "v", "ü", "u", "nv", "nü", "lv", "lü", "qn", "an", "bg", "xl", "a1", "1n", "zg", "vz", "uq",
            "cq", "zh", "cqyh", "chongqing", "chongqingb", "yinhan", "zhong", "hangzhang", "lvli", "报告", "文件", "123", "winxyz"];
        foreach (var term in terms)
        {
            var request = new SearchRequest { Scope = SearchScope.Folder, Folder = root, Text = term, PinyinSearch = true };
            var expected = names.Where(n => n.ToUpperInvariant().Contains(term.ToUpperInvariant(), StringComparison.Ordinal)
                || SearchPinyin.Matches(n, term)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var actual = IndexedFileSearch.Query([store], request, default);
            Assert.Equal(expected, actual.Items.Select(f => f.Name));
            Assert.Equal(expected.Length, actual.Total);
        }
    }

    [Fact]
    public void CachedShortTermsMergeReplacedAndNewRowsAfterJournalChangesAndReopen()
    {
        string root = Directory.CreateDirectory(Path.Join(temporary, "source")).FullName;
        string a = Directory.CreateDirectory(Path.Join(root, "A")).FullName;
        string b = Directory.CreateDirectory(Path.Join(root, "B")).FullName;
        string old = Path.Join(a, "node.txt"), other = Path.Join(b, "node.txt");
        File.WriteAllText(old, "");
        File.WriteAllText(other, "");
        var catalog = new IndexCatalog(Path.Join(temporary, "cache"));
        var store = catalog.ForRoot(root);
        using var initial = new SpaceScanner().Scan(root, null, default);
        var info = store.ReplaceLive(initial.Metadata, initial.Entries.Count, initial.Entries, default, null);
        var options = new SearchRequest { ShowHidden = false, PinyinSearch = true };
        Assert.Equal(2, IndexedFileSearch.Query([store], options with { Text = "no" }, default).Total);
        string moved = Path.Join(a, "成本.txt"), added = Path.Join(a, "Node-A.txt");
        File.Move(old, moved);
        File.WriteAllText(added, "");
        File.SetAttributes(other, File.GetAttributes(other) | FileAttributes.Hidden);
        var updated = store.ApplyChanges(info, [], [old, moved, added, other], null, Path.Join(temporary, "cache"), new LocalFiles(), default);
        Assert.NotEqual(info.Generation, updated.Generation);
        foreach (var reader in new[] { store, catalog.ForRoot(root) })
        {
            foreach (string term in new[] { "n", "no", "cb", "成本", "ext:txt" })
            {
                var request = options with { Text = term };
                var matches = SearchRules.Compile(request);
                var expected = new[] { added, moved }.Select(p => new SearchFile(p, false, 0, null))
                    .Where(matches.Matches).Order(IndexedFileSearch.Order(request)).Select(f => f.FullPath).ToArray();
                var result = IndexedFileSearch.Query([reader], request, default);
                Assert.Equal(expected.Length, result.Total);
                Assert.Equal(expected, result.Items.Select(f => f.FullPath));
            }
            Assert.Equal(2, IndexedFileSearch.Query([reader], options with { Text = "no", ShowHidden = true }, default).Total);
        }
    }

    private sealed class LocalFiles : IIndexFileSystem
    {
        public SpaceEntry? Read(string path) => IndexEntryReader.Read(path);
        public IEnumerable<ListedSpaceFile> Names(string directory) => IndexEntryReader.Names(directory);
        public IEnumerable<string> HardLinks(string path) => [];
    }

    public void Dispose() => Directory.Delete(temporary, recursive: true);
}
