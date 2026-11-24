using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public readonly record struct JournalCursor(ulong JournalId, long Next);
public readonly record struct JournalChange(ulong ParentLow, ulong ParentHigh, ulong FileLow, ulong FileHigh, string Name, uint Reason);
public sealed record LiveIndexState(IndexInfo Info, SpaceIdentity Identity, JournalCursor? Cursor);
public sealed partial class IndexStore
{
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction tx, string sql, params object? [] args)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    private static void Run(SqliteConnection db, SqliteTransaction tx, string sql, params object? [] args)
    {
        using var cmd = Command(db, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static bool IsLive(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = Command(db, tx, "SELECT 1 FROM sqlite_schema WHERE name='live_state' AND type='table'");
        return cmd.ExecuteScalar() is not null;
    }

    private static void InitializeLive(SqliteConnection db, SqliteTransaction tx, string root, JournalCursor? cursor)
    {
        Run(db, tx, DirectorySchema + LiveEntryIndexes + """
            CREATE TABLE live_state(id INTEGER PRIMARY KEY CHECK(id=1),journal INTEGER,next INTEGER);
            WITH RECURSIVE dirs(id,path,prefix,flags,protected) AS (
              SELECT id,$p0,$p1,flags,0 FROM entries WHERE id=0
              UNION ALL SELECT e.id,d.prefix||e.name,d.prefix||e.name||char(92),e.flags,
                 CASE WHEN d.protected<>0 OR ((e.flags&16)<>0 AND e.file_high=0 AND (e.file_low&281474976710655)<16)
                     THEN 16 ELSE 0 END
                FROM dirs d JOIN entries e INDEXED BY live_directory_children ON e.parent=d.id AND e.kind=1)
            INSERT INTO live_dirs(id,path,prefix_override,own_flags)
                SELECT id,path,nullif(prefix,rtrim(path,char(92))||char(92)),(flags & 39) | protected FROM dirs;
            INSERT INTO live_state VALUES(1,$p2,$p3);
            CREATE TABLE live_dirty(id INTEGER PRIMARY KEY);
            """, root, Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, cursor.HasValue ? unchecked((long)cursor.Value.JournalId) : null, cursor?.Next);
        // The backing index also groups rare case-distinct siblings together. Only those groups
        // need a binary spelling check when establishing uniqueness on the initial bulk load.
        using var duplicates = Command(db, tx, """
            WITH siblings(name,parent) AS (
                SELECT name,parent FROM entries INDEXED BY live_name_order
                GROUP BY name COLLATE WINORD,parent HAVING count(*)>1)
            SELECT 1 FROM siblings s CROSS JOIN entries e INDEXED BY live_name_order
                ON e.name=s.name COLLATE WINORD AND e.parent=s.parent
            GROUP BY e.parent,e.name COLLATE BINARY HAVING count(*)>1 LIMIT 1
            """);
        if (duplicates.ExecuteScalar() is not null)
            throw new InvalidDataException("同一目录内有重复的索引名称。");
        CreateLiveMaintenance(db, tx, verified: true);
    }

    private const string DirectorySchema = """
        CREATE TABLE live_dirs(id INTEGER PRIMARY KEY,path TEXT NOT NULL,own_flags INTEGER NOT NULL DEFAULT 0,
            prefix_override TEXT,prefix TEXT AS (coalesce(prefix_override,rtrim(path,char(92))||char(92))) VIRTUAL);
        """;
    private const string LiveEntryIndexes = """
        CREATE INDEX physical_files ON entries(file_low,file_high);
        CREATE INDEX live_name_order ON entries(name COLLATE WINORD,parent);
        CREATE INDEX live_directory_children ON entries(parent) WHERE kind=1;
        """;
    // A case-sensitive directory can contain both spelling variants. Enforce exact uniqueness with
    // an indexed probe of their shared WINORD range instead of storing every filename a second time.
    private static void CreateLiveMaintenance(SqliteConnection db, SqliteTransaction tx, bool verified)
    {
        // Older experimental live stores without the verified-name marker still require their
        // existing reconciliation path. Compaction must not promote them to an authoritative scan.
        var insertTrigger = verified ? "parent_names_unique" : "check_parent_name";
        Run(db, tx, $"""
        CREATE INDEX dir_paths ON live_dirs(path COLLATE WINORD);
        CREATE TRIGGER {insertTrigger} BEFORE INSERT ON entries
        WHEN EXISTS(SELECT 1 FROM entries WHERE name=new.name COLLATE WINORD AND parent=new.parent AND name=new.name COLLATE BINARY)
        BEGIN SELECT raise(ABORT,'duplicate directory entry'); END;
        CREATE TRIGGER parent_names_update BEFORE UPDATE OF name,parent ON entries
        WHEN EXISTS(SELECT 1 FROM entries WHERE name=new.name COLLATE WINORD AND parent=new.parent AND id<>old.id AND name=new.name COLLATE BINARY)
        BEGIN SELECT raise(ABORT,'duplicate directory entry'); END;
        CREATE TRIGGER dirty_insert AFTER INSERT ON entries BEGIN INSERT OR IGNORE INTO live_dirty VALUES(new.id); END;
        CREATE TRIGGER dirty_delete AFTER DELETE ON entries BEGIN INSERT OR IGNORE INTO live_dirty VALUES(old.id); END;
        CREATE TRIGGER cursor_writer_version BEFORE UPDATE ON live_state BEGIN SELECT FILEKITE_V3_WRITER(); END;
        """ + ExtensionIndex);
    }

    private const string ExtensionIndex = "CREATE INDEX IF NOT EXISTS live_extension ON entries(WINEXT(name_key)) WHERE kind<>1";

    public void PrepareLiveSearch(CancellationToken token)
    {
        using var db=Open(path,true);
        using var stop=InterruptOnCancel(db,token);
        using var command=db.CreateCommand();
        command.CommandText=ExtensionIndex;
        try {command.ExecuteNonQuery();}
        catch(SqliteException) when(token.IsCancellationRequested){throw new OperationCanceledException(token);}
    }

    public LiveIndexState? GetLiveState()
    {
        if (!File.Exists(path))
            return null;
        using var db = Open(path, false);
        using var tx = db.BeginTransaction(deferred: true);
        if (CheckVersion(db, tx) == 0 || !IsLive(db, tx) || ReadInfo(db, tx) is not { } info)
            return null;
        using var version = Command(db, tx, "SELECT 1 FROM sqlite_schema WHERE name='parent_names_unique'");
        if (version.ExecuteScalar() is null)
            return null;
        using var cmd = Command(db, tx, "SELECT e.volume,e.file_low,e.file_high,s.journal,s.next FROM live_state s JOIN entries e ON e.id=0 WHERE s.id=1");
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        var identity = new SpaceIdentity((ulong)r.GetInt64(0), (ulong)r.GetInt64(1), (ulong)r.GetInt64(2));
        return new(info, identity, r.IsDBNull(3) ? null : new((ulong)r.GetInt64(3), r.GetInt64(4)));
    }

    public static bool Within(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
