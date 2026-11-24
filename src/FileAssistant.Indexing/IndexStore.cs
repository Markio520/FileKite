using System.Text.Json;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Text;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    private const long ApplicationId = 0x46414958; // FAIX
    private const int SchemaVersion = 3;
    private readonly string path;
    private readonly string root;
    internal IndexStore(string path, string root) => (this.path, this.root) = (path, root);
    public string DatabasePath => path;

    internal static SqliteConnection Open(string path, bool write)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            db.Open();
            // A v2 process already tracking this volume must stop at its next commit after migration.
            db.CreateFunction("FILEKITE_V3_WRITER", static () => 1, isDeterministic: true);
            SqliteException.ThrowExceptionForRC(raw.sqlite3__create_collation_utf8(db.Handle, "WINORD", null,
                static (_, left, right) => CompareNames(left, right)), db.Handle);
            SqliteException.ThrowExceptionForRC(raw.sqlite3_create_function(db.Handle, "WINEXT", 1,
                raw.SQLITE_DETERMINISTIC, null, static (context, _, args) =>
                {
                    var name = raw.sqlite3_value_blob(args[0]);
                    int last = name.LastIndexOfAny((byte)'.', (byte)'/', (byte)'\\');
                    if (last >= 0 && name[last] == '.' && last + 1 < name.Length)
                        raw.sqlite3_result_text(context, name[(last + 1)..]);
                    else
                        raw.sqlite3_result_text(context, string.Empty);
                }), db.Handle);
            using var setup = db.CreateCommand();
            setup.CommandText = write ? "PRAGMA cache_size=-2048; PRAGMA temp_store=FILE;"
                : "PRAGMA cache_size=-1024; PRAGMA temp_store=FILE;";
            setup.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    // SQLite already supplies UTF-8. Most comparisons differ in an ASCII prefix,
    // even for paths with non-ASCII filenames. Preserve the existing WINORD order.
    internal static int CompareNames(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int length = Math.Min(left.Length, right.Length);
        int prefix = left.CommonPrefixLength(right);
        if (prefix == length) return left.Length.CompareTo(right.Length);
        // A shared byte prefix can end halfway through a Unicode code point.
        while (prefix > 0 && (left[prefix] & 0xC0) == 0x80) prefix--;
        for (int i = prefix; i < length; i++)
        {
            int a = left[i], b = right[i];
            if ((a | b) >= 128)
                return CompareUnicode(left[i..], right[i..]);
            if (a == b) continue;
            if (a is >= 'a' and <= 'z') a -= 32;
            if (b is >= 'a' and <= 'z') b -= 32;
            if (a != b) return a - b;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareUnicode(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        // Decoding a valid UTF-8 name needs at most one UTF-16 code unit per
        // input byte. Bound the callback stack even for very long NT paths.
        if (left.Length > 256 || right.Length > 256)
            return StringComparer.OrdinalIgnoreCase.Compare(Encoding.UTF8.GetString(left), Encoding.UTF8.GetString(right));
        Span<char> a = stackalloc char[left.Length];
        Span<char> b = stackalloc char[right.Length];
        int al = Encoding.UTF8.GetChars(left, a), bl = Encoding.UTF8.GetChars(right, b);
        return ((ReadOnlySpan<char>)a[..al]).CompareTo(b[..bl], StringComparison.OrdinalIgnoreCase);
    }

    internal static int CheckVersion(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT application_id,user_version FROM pragma_application_id,pragma_user_version;";
        using var row = cmd.ExecuteReader();
        row.Read();
        var version = row.GetInt32(1);
        if (row.GetInt64(0) == 0 && version == 0)
        {
            row.Close();
            cmd.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'";
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return 0;
            throw new InvalidDataException("此数据库不属于 FileKite。");
        }
        if (row.GetInt64(0) != ApplicationId || version is < 1 or > SchemaVersion)
            throw new InvalidDataException("索引版本不兼容，请使用创建它的测试版。");
        return version;
    }

    internal static IndexInfo? ReadInfo(SqliteConnection db, SqliteTransaction? tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT root,generation,updated,files,folders,bytes,method,notice,volume,issue_counts,issues FROM scan WHERE id=1;";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new(r.GetString(0), r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
            r.GetInt32(3), r.GetInt32(4), r.GetInt64(5), r.GetString(6), r.GetString(7),
            JsonSerializer.Deserialize<SpaceVolume>(r.GetString(8)),
            JsonSerializer.Deserialize<Dictionary<SpaceIssueKind, int>>(r.GetString(9))!,
            JsonSerializer.Deserialize<List<SpaceIssue>>(r.GetString(10))!);
    }

    internal static CancellationTokenRegistration InterruptOnCancel(SqliteConnection db, CancellationToken token)
        => token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));

    /// <summary>SQLite publishes schema, rows, navigation indexes and scan metadata in one commit.
    /// Readers stay on the previous complete generation while the writer works.</summary>
    public IndexInfo Replace(SpaceScanMetadata scan, int expected, IEnumerable<SpaceEntry> entries,
        CancellationToken token, IProgress<int>? writing = null)
    {
        return ReplaceCore(scan, expected, entries, token, writing, false, null);
    }

    public IndexInfo ReplaceLive(SpaceScanMetadata scan, int expected, IEnumerable<SpaceEntry> entries,
        CancellationToken token, JournalCursor? cursor, IProgress<int>? writing = null, string? excludedDirectory = null)
        => ReplaceCore(scan, expected, entries, token, writing, true, cursor, excludedDirectory);

    private IndexInfo ReplaceCore(SpaceScanMetadata scan, int expected, IEnumerable<SpaceEntry> entries,
        CancellationToken token, IProgress<int>? writing, bool live, JournalCursor? cursor, string? excludedDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        if (scan.Cancelled) throw new OperationCanceledException("扫描已取消。", token);
        if (!string.Equals(root, Path.TrimEndingDirectorySeparator(scan.Root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("扫描位置和索引位置不一致。");
        if (expected <= 0 || (long)scan.Folders + scan.Files != expected) throw new InvalidDataException("扫描记录数不一致。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(path, true);
        CheckVersion(db);
        using var configure = db.CreateCommand();
        configure.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-4096;";
        configure.ExecuteNonQuery();
        using var stop = InterruptOnCancel(db, token);
        try
        {
            using var tx = db.BeginTransaction();
            using var command = db.CreateCommand();
            command.Transaction = tx;
            CheckVersion(db, tx);
            command.CommandText = ResetSnapshot;
            command.ExecuteNonQuery();
            var (counted, types) = InsertEntries(db, entries, expected, scan.Folders, token, writing);
            token.ThrowIfCancellationRequested();
            foreach (var item in types.Where(t => t.Count > 0))
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO types(kind,count,bytes,unknown) VALUES($kind,$count,$bytes,$unknown);";
                command.Parameters.AddWithValue("$kind", (int)item.Kind);
                command.Parameters.AddWithValue("$count", item.Count);
                command.Parameters.AddWithValue("$bytes", item.Bytes);
                command.Parameters.AddWithValue("$unknown", item.Unknown);
                command.ExecuteNonQuery();
            }
            command.Parameters.Clear();
            command.CommandText = CreateIndexes + (live ? "" : OfflineNameIndex);
            command.ExecuteNonQuery();
            if (live) InitializeLive(db, tx, root, cursor);
            token.ThrowIfCancellationRequested();
            var info = new IndexInfo(root, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, scan.Files, scan.Folders,
                counted, scan.ScanMethod, scan.ScanNotice, scan.Volume, scan.IssueCounts, scan.Issues);
            command.CommandText = """
                INSERT OR REPLACE INTO scan(id,root,generation,updated,files,folders,bytes,method,notice,volume,issue_counts,issues)
                VALUES(1,$root,$gen,$updated,$files,$folders,$bytes,$method,$notice,$volume,$counts,$issues);
                """;
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$gen", info.Generation);
            command.Parameters.AddWithValue("$updated", info.Updated.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$files", info.Files);
            command.Parameters.AddWithValue("$folders", info.Folders);
            command.Parameters.AddWithValue("$bytes", counted);
            command.Parameters.AddWithValue("$method", info.Method);
            command.Parameters.AddWithValue("$notice", info.Notice);
            command.Parameters.AddWithValue("$volume", JsonSerializer.Serialize(info.Volume));
            command.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(info.IssueCounts));
            command.Parameters.AddWithValue("$issues", JsonSerializer.Serialize(info.Issues));
            command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            if (live)
            {
                LivePatch.NormalizeBaseline(db,tx,excludedDirectory,token);
                info=ReadInfo(db,tx)!;
                Run(db,tx,"DELETE FROM live_dirty");
            }
            BuildNamePages(db, tx, token, live, freshLive: live);
            token.ThrowIfCancellationRequested();
            tx.Commit();
            ReleaseBootstrapLookup();
            return info;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    // Replacing a whole scan already has a complete source. Recreate the two payload tables in its
    // transaction so replacing a v1/v2 snapshot goes directly to the current physical layout.
    private const string ResetSnapshot = """
        DROP TABLE IF EXISTS entries; DROP TABLE IF EXISTS live_dirs;
        DROP TABLE IF EXISTS live_dirty; DROP TABLE IF EXISTS live_state;
        """ + Schema + "DELETE FROM types; DELETE FROM name_pages;";

    private const string Schema = """
        PRAGMA application_id=0x46414958;
        PRAGMA user_version=3;
        CREATE TABLE IF NOT EXISTS scan(id INTEGER PRIMARY KEY CHECK(id=1),root TEXT NOT NULL,generation TEXT NOT NULL,
            updated INTEGER NOT NULL,files INTEGER NOT NULL,folders INTEGER NOT NULL,bytes INTEGER NOT NULL,method TEXT NOT NULL,
            notice TEXT NOT NULL,volume TEXT NOT NULL,issue_counts TEXT NOT NULL,issues TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS types(kind INTEGER PRIMARY KEY,count INTEGER NOT NULL,bytes INTEGER NOT NULL,unknown INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS name_pages(page INTEGER PRIMARY KEY,keys BLOB NOT NULL,ids BLOB NOT NULL);
        CREATE TRIGGER IF NOT EXISTS scan_writer_version BEFORE UPDATE ON scan BEGIN SELECT FILEKITE_V3_WRITER(); END;
        """ + EntrySchema;
    private const string EntrySchema = """
        CREATE TABLE entries(id INTEGER PRIMARY KEY,parent INTEGER NOT NULL,name TEXT NOT NULL,upper_override TEXT,
            name_key TEXT AS (coalesce(upper_override,upper(name))) VIRTUAL,
            volume INTEGER NOT NULL,file_low INTEGER NOT NULL,file_high INTEGER NOT NULL,modified INTEGER NOT NULL,
            changed INTEGER NOT NULL,attrs INTEGER NOT NULL,links INTEGER NOT NULL,logical INTEGER NOT NULL,
            allocated INTEGER NOT NULL,counted INTEGER NOT NULL,flags INTEGER NOT NULL,kind INTEGER NOT NULL);
        """;
    private const string OfflineNameIndex = "CREATE INDEX names ON entries(name_key);";
    // id is the rowid, already the final ascending key in each SQLite index.
    private const string CreateIndexes = """
        CREATE INDEX directory_items ON entries(parent,counted DESC);
        CREATE INDEX largest_files ON entries(allocated DESC) WHERE kind<>1;
        """;

    // SQLite upper() folds ASCII only. Store a complete key only when the runtime's invariant
    // Unicode mapping differs. The common ASCII and uncased CJK names cost one NULL serial type.
    private static string? UpperOverride(string name)
    {
        if (Ascii.IsValid(name)) return null;
        var upper = name.ToUpperInvariant();
        if (upper.Length != name.Length) return upper;
        for (int i = 0; i < name.Length; i++)
            if (name[i] > 127 && name[i] != upper[i]) return upper;
        return null;
    }

    private static (long Counted, SpaceTypeTotal[] Types) InsertEntries(SqliteConnection db,
        IEnumerable<SpaceEntry> entries, int expected, int expectedFolders, CancellationToken token, IProgress<int>? writing)
    {
        // One statement on the connection's existing transaction. Bind by position
        // to avoid re-resolving and boxing sixteen named parameters for each file.
        var handle = db.Handle!;
        const string sql = """
            INSERT INTO entries(id,parent,name,upper_override,volume,file_low,file_high,modified,changed,attrs,links,
                logical,allocated,counted,flags,kind)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
            """;
        void Check(int rc)
        {
            if (rc != raw.SQLITE_OK) SqliteException.ThrowExceptionForRC(rc, handle);
        }
        Check(raw.sqlite3_prepare_v2(handle, sql, out var prepared));
        using var insert = prepared;
        void Integer(int column, long value) => Check(raw.sqlite3_bind_int64(insert, column, value));
        var groups = new (int Count, long Bytes, int Unknown)[Enum.GetValues<SpaceKind>().Length];
        var directory = new System.Collections.BitArray(expected);
        int id = 0, folders = 0;
        long rootBytes = 0;
        foreach (var e in entries)
        {
            token.ThrowIfCancellationRequested();
            if (id >= expected || (id == 0 ? e.Parent != -1 || !e.IsDirectory :
                e.Parent < 0 || e.Parent >= id || !directory[e.Parent]))
                throw new InvalidDataException("索引记录的父目录无效。");
            if (id == 0) rootBytes = e.CountedBytes;
            directory[id] = e.IsDirectory;
            var stamp = e.Stamp;
            var kind = SpaceKinds.Of(e);
            Integer(1, id);
            Integer(2, e.Parent);
            Check(raw.sqlite3_bind_text(insert, 3, e.Name));
            if (UpperOverride(e.Name) is { } upper)
                Check(raw.sqlite3_bind_text(insert, 4, upper));
            else
                Check(raw.sqlite3_bind_null(insert, 4));
            Integer(5, unchecked((long)stamp.Identity.Volume));
            Integer(6, unchecked((long)stamp.Identity.Low));
            Integer(7, unchecked((long)stamp.Identity.High));
            Integer(8, stamp.Modified);
            Integer(9, stamp.Changed);
            Integer(10, (int)stamp.Attributes);
            Integer(11, stamp.Links);
            Integer(12, e.LogicalBytes);
            Integer(13, e.AllocatedBytes);
            Integer(14, e.CountedBytes);
            Integer(15, (int)e.Flags);
            Integer(16, (int)kind);
            int step = raw.sqlite3_step(insert);
            if (step != raw.SQLITE_DONE) SqliteException.ThrowExceptionForRC(step, handle);
            Check(raw.sqlite3_reset(insert));
            if (e.IsDirectory) folders++;
            else
            {
                ref var group = ref groups[(int)kind];
                group.Count++;
                group.Bytes += e.CountedBytes;
                if (e.AllocatedBytes < 0) group.Unknown++;
            }
            if ((++id & 8191) == 0) writing?.Report(id);
        }
        if (id != expected || folders != expectedFolders) throw new InvalidDataException("索引记录传输不完整。");
        writing?.Report(id);
        return (rootBytes, groups.Select((g, k) => new SpaceTypeTotal((SpaceKind)k, g.Count, g.Bytes, g.Unknown)).ToArray());
    }
}
