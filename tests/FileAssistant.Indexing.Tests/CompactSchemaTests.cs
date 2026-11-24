using System.Text;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing.Tests;

public sealed class CompactSchemaTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("FileKite-Schema-").FullName;
    private string Root => Path.Join(temp, "offline-volume");
    private IndexStore Store => new IndexCatalog(temp).ForRoot(Root);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MigrationPreservesSparseIdsUnicodeAliasesAndAllMetadataWithAnOlderReaderOpen(int version)
    {
        var store = CreateLegacy(version);
        var info = Assert.Single(new IndexCatalog(temp).List());
        string[] before = Signature(version);
        string[] Search() => version == 2
            ? IndexedFileSearch.Query([store], new SearchRequest { Text = "ſub", ShowHidden = true }, default).Items.Select(i => i.FullPath).ToArray()
            : store.Page(info, IndexView.Search, 0, "ſub", SpaceKind.All, 0, default).Rows.Select(i => i.Path).ToArray();
        var originalHits = Search();
        Assert.Single(originalHits);
        using (var old = Open())
        using (var reader = old.BeginTransaction(deferred: true))
        using (var sql = old.CreateCommand())
        {
            sql.Transaction = reader;
            sql.CommandText = "PRAGMA user_version";
            Assert.Equal((long)version, sql.ExecuteScalar());
            Assert.True(store.NeedsStorageCompaction());
            Assert.True(store.CompactStorage(default));
            Assert.Equal((long)version, sql.ExecuteScalar());
            Assert.Equal(before, Signature(version));
            Assert.Equal(originalHits, Search());
            Assert.True(store.NeedsStorageCompaction()); // The old WAL snapshot still pins the checkpoint.
        }
        Assert.True(store.CompactStorage(default));
        Assert.False(store.NeedsStorageCompaction());
        Assert.False(store.CompactStorage(default));
        Assert.Equal(before, Signature(version));
        using var db = Open();
        Assert.Equal(3L, Scalar(db, "PRAGMA user_version"));
        Assert.Equal("ok", Scalar(db, "PRAGMA integrity_check"));
        Assert.Equal(3L, Scalar(db, "SELECT count(*) FROM entries WHERE upper_override IS NOT NULL"));
        Assert.Equal("HISTORICAL.ALIAS", Scalar(db, "SELECT name_key FROM entries WHERE id=7"));
        Assert.Throws<SqliteException>(() => Scalar(db, "UPDATE scan SET generation='old writer'"));
        if (version == 2)
        {
            Assert.Equal(new JournalCursor(unchecked((ulong)-17L), 91), store.GetLiveState()!.Cursor);
            Assert.Equal(1L, Scalar(db, "SELECT count(*) FROM live_dirs WHERE prefix_override IS NOT NULL"));
            Assert.Throws<SqliteException>(() => Scalar(db, "UPDATE live_state SET next=999"));
            Assert.Equal(0L, Scalar(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name IN ('names','parent_names_unique')"));
            Assert.Throws<SqliteException>(() => Scalar(db, "UPDATE entries SET name='MÉlange' WHERE id=2"));
        }
    }

    [Fact]
    public void FailedMigrationRollsBackTheSchemaAndCanRetryFromTheSameGeneration()
    {
        var store = CreateLegacy(2);
        var before = Signature(2);
        using var db = Open();
        var validPage = (byte[])Scalar(db, "SELECT keys FROM name_pages WHERE page=0")!;
        Scalar(db, "UPDATE name_pages SET keys=x'fe'");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.CompactStorage(cancel.Token));
        Assert.Throws<InvalidDataException>(() => store.CompactStorage(default));
        Assert.Equal(2L, Scalar(db, "PRAGMA user_version"));
        Assert.Equal(before, Signature(2));
        using (var repair = db.CreateCommand())
        {
            repair.CommandText = "UPDATE name_pages SET keys=$keys";
            repair.Parameters.AddWithValue("$keys", validPage);
            repair.ExecuteNonQuery();
        }
        Assert.True(store.CompactStorage(default));
        Assert.Equal(before, Signature(2));
    }

    [Fact]
    public async Task AnOlderWriterFinishingBeforeTheUpgradeKeepsItsNewValuesAndCursor()
    {
        var store = CreateLegacy(2);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var writer = db.CreateCommand();
        writer.Transaction = tx;
        writer.CommandText = """
            UPDATE entries SET changed=123456789,attrs=38 WHERE id=4;
            INSERT OR IGNORE INTO live_dirty VALUES(4);
            UPDATE scan SET generation='new-v2-generation';
            UPDATE live_state SET next=100;
            """;
        writer.ExecuteNonQuery();
        using var entered = new ManualResetEventSlim();
        var migration = Task.Run(() =>
        {
            entered.Set();
            return store.CompactStorage(default);
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEqual("new-v2-generation", store.GetLiveState()!.Info.Generation);
        tx.Commit();
        Assert.True(await migration);
        Assert.Equal("new-v2-generation", store.GetLiveState()!.Info.Generation);
        Assert.Equal(100L, store.GetLiveState()!.Cursor!.Value.Next);
        Assert.Equal(123456789L, Scalar(db, "SELECT changed FROM entries WHERE id=4 AND attrs=38"));
        Assert.Equal(4L, Scalar(db, "SELECT id FROM live_dirty WHERE id=4"));
    }

    [Fact]
    public void JournalUpdatesCanAddCaseFoldedFilesAndDirectoriesOnEitherSideOfTheUpgrade()
    {
        var store = CreateLegacy(2);
        var files = new FixtureFiles();
        string AddTree(string name, long next)
        {
            string directory = Directory.CreateDirectory(Path.Join(Root, name)).FullName;
            var child = Path.Join(directory, "项目-µ.txt");
            File.WriteAllText(child, "content");
            var saved = store.GetLiveState()!;
            store.ApplyChanges(saved.Info, [], [directory], new(81, next), Path.Join(temp, "cache"), files, default);
            var page = IndexedFileSearch.Query([store], new() { Text = name, ShowHidden = true }, default);
            Assert.Contains(page.Items, item => item.FullPath == directory);
            return child;
        }
        string first = AddTree("pré-ſ", 93);
        Assert.True(store.CompactStorage(default));
        string second = AddTree("a-μ", 94);
        var children = IndexedFileSearch.Query([store], new() { Text = "项目-μ", ShowHidden = true }, default);
        Assert.Equal(2, children.Total);
        Assert.Contains(children.Items, child => child.FullPath == first);
        Assert.Contains(children.Items, child => child.FullPath == second);
        Assert.Equal(new JournalCursor(81, 94), store.GetLiveState()!.Cursor);
    }

    private sealed class FixtureFiles : IIndexFileSystem
    {
        public SpaceEntry? Read(string path) => IndexEntryReader.Read(path);
        public IEnumerable<ListedSpaceFile> Names(string directory) => IndexEntryReader.Names(directory);
        public IEnumerable<string> HardLinks(string path) => [];
    }

    [Fact]
    public void ANewLiveSnapshotRejectsAnExactDuplicateWithoutReplacingItsCaseDistinctSiblings()
    {
        var store = Store;
        SpaceEntry Entry(string name, int parent, ulong id, FileAttributes attrs) =>
            new(name, parent, new(new(42, id, 99), 1, 2, attrs, 1), 0, 0, 0);
        SpaceEntry[] entries = [
            Entry(Root, -1, 100, FileAttributes.Directory), Entry("ſub.txt", 0, 101, FileAttributes.Normal),
            Entry("Sub.txt", 0, 102, FileAttributes.Normal)];
        var scan = new SpaceScanMetadata(Root, 2, 1, null, new Dictionary<SpaceIssueKind, int>(), [], default, false);
        var accepted = store.ReplaceLive(scan, entries.Length, entries, default, null);
        using (var db = Open())
            Assert.Equal("SUB.TXT", Scalar(db, "SELECT name_key FROM entries WHERE id=1"));
        Assert.Throws<InvalidDataException>(() => store.ReplaceLive(scan with { Files = 3 }, 4, [.. entries, entries[1]], default, null));
        Assert.Equal(accepted.Generation, store.GetLiveState()!.Info.Generation);
        Assert.False(store.NeedsStorageCompaction());
    }

    private IndexStore CreateLegacy(int version)
    {
        var store = Store;
        using var db = Open();
        Scalar(db, $"""
            PRAGMA journal_mode=WAL; PRAGMA application_id=0x46414958; PRAGMA user_version={version};
            CREATE TABLE entries(id INTEGER PRIMARY KEY,parent INTEGER,name TEXT,name_key TEXT,
                volume INTEGER,file_low INTEGER,file_high INTEGER,modified INTEGER,changed INTEGER,attrs INTEGER,
                links INTEGER,logical INTEGER,allocated INTEGER,counted INTEGER,flags INTEGER,kind INTEGER);
            CREATE TABLE scan(id INTEGER PRIMARY KEY,root TEXT,generation TEXT,updated INTEGER,files INTEGER,folders INTEGER,
                bytes INTEGER,method TEXT,notice TEXT,volume TEXT,issue_counts TEXT,issues TEXT);
            CREATE TABLE types(kind INTEGER PRIMARY KEY,count INTEGER,bytes INTEGER,unknown INTEGER);
            CREATE INDEX names ON entries(name_key,id);
            CREATE INDEX directory_items ON entries(parent,counted DESC,id);
            CREATE INDEX largest_files ON entries(allocated DESC,id) WHERE kind<>1;
            INSERT INTO entries VALUES(0,-1,'root','ROOT',-92,50,0,10,11,16,1,22,4096,4096,16,1),
                (1,0,'MÉlange','MÉLANGE',-92,51,0,15,12,16,1,11,4096,4096,8,1),
                (2,0,'mélange','MÉLANGE',-92,52,0,17,13,16,1,11,0,0,8,1),
                (4,1,'ſub.µ','SUB.Μ',-92,-50,94,20,21,32,2,11,4096,4096,8,0),
                (7,2,'Alias.BIN','HISTORICAL.ALIAS',-92,-50,94,20,21,32,2,11,4096,0,8,0),
                (9,0,'未核对.txt','未核对.TXT',0,0,0,-1,0,6,0,-1,-1,0,49,0);
            INSERT INTO types VALUES(0,3,4096,1);
            """);
        using var scan = db.CreateCommand();
        scan.CommandText = "INSERT INTO scan VALUES(1,$root,'retained-generation',120,3,3,4096,'saved','notice','null','{}','[]')";
        scan.Parameters.AddWithValue("$root", Root);
        scan.ExecuteNonQuery();
        if (version == 1) return store;
        Scalar(db, """
            CREATE TABLE live_dirs(id INTEGER PRIMARY KEY,path TEXT,prefix TEXT,own_flags INTEGER);
            CREATE INDEX dir_paths ON live_dirs(path COLLATE WINORD);
            CREATE UNIQUE INDEX parent_names_unique ON entries(parent,name);
            CREATE INDEX physical_files ON entries(file_low,file_high);
            CREATE INDEX live_name_order ON entries(name COLLATE WINORD);
            CREATE TABLE live_state(id INTEGER PRIMARY KEY,journal INTEGER,next INTEGER);
            INSERT INTO live_state VALUES(1,-17,91);
            CREATE TABLE live_dirty(id INTEGER PRIMARY KEY);
            INSERT INTO live_dirty VALUES(7),(99);
            CREATE TRIGGER dirty_insert AFTER INSERT ON entries BEGIN INSERT OR IGNORE INTO live_dirty VALUES(new.id); END;
            CREATE TRIGGER dirty_delete AFTER DELETE ON entries BEGIN INSERT OR IGNORE INTO live_dirty VALUES(old.id); END;
            CREATE TABLE name_pages(page INTEGER PRIMARY KEY,keys BLOB,ids BLOB);
            """);
        using var dirs = db.CreateCommand();
        dirs.CommandText = """
            INSERT INTO live_dirs VALUES(0,$root,$root||char(92),0),
                (1,$root||'\MÉlange',$root||'\MÉlange\',0),(2,$root||'\mélange',$root||'\mélange\retained\',32)
            """;
        dirs.Parameters.AddWithValue("$root", Root);
        dirs.ExecuteNonQuery();
        using var page = db.CreateCommand();
        page.CommandText = "INSERT INTO name_pages VALUES(0,$keys,$ids)";
        page.Parameters.AddWithValue("$keys", Encoding.UTF8.GetBytes("ALIAS.BIN\0MÉLANGE\0MÉLANGE\0SUB.Μ\0未核对.TXT\0"));
        page.Parameters.AddWithValue("$ids", new[] { 7, 1, 2, 4, 9 }.SelectMany(BitConverter.GetBytes).ToArray());
        page.ExecuteNonQuery();
        return store;
    }

    private string[] Signature(int version)
    {
        using var db = Open();
        var rows = new List<string>();
        Capture("SELECT id,parent,name,name_key,volume,file_low,file_high,modified,changed,attrs,links,logical,allocated,counted,flags,kind FROM entries ORDER BY id");
        Capture("SELECT * FROM types ORDER BY kind");
        Capture("SELECT * FROM scan");
        if (version == 2)
        {
            Capture("SELECT id,path,prefix,own_flags FROM live_dirs ORDER BY id");
            Capture("SELECT * FROM live_state");
            Capture("SELECT * FROM live_dirty ORDER BY id");
        }
        return rows.ToArray();

        void Capture(string query)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = query;
            using var read = cmd.ExecuteReader();
            while (read.Read()) rows.Add(string.Join('\t', Enumerable.Range(0, read.FieldCount).Select(read.GetValue)));
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Store.DatabasePath, Pooling = false }.ToString());
        db.Open();
        db.CreateCollation("WINORD", StringComparer.OrdinalIgnoreCase.Compare);
        db.CreateFunction<string, string>("WINEXT", name => Path.GetExtension(name).TrimStart('.'), isDeterministic: true);
        return db;
    }

    private static object? Scalar(SqliteConnection db, string query)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = query;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => Directory.Delete(temp, true);
}
