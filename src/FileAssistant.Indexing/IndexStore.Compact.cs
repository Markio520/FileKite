using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    /// <summary>Reads the database header and a few metadata rows. Name-only snapshots are still being built.</summary>
    public bool NeedsStorageCompaction()
    {
        if (!File.Exists(path)) return false;
        using var db = Open(path, false);
        using var tx = db.BeginTransaction(deferred: true);
        int version = CompleteStorageVersion(db, tx);
        return version != 0 && (version < SchemaVersion || VacuumDue(db, tx));
    }

    /// <summary>Upgrades the existing snapshot without walking the filesystem or changing its generation.
    /// WAL readers retain a complete old snapshot while the new tables and indexes are built.</summary>
    public bool CompactStorage(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!NeedsStorageCompaction()) return false;
        using var db = Open(path, true);
        using var stop = InterruptOnCancel(db, token);
        try
        {
            token.ThrowIfCancellationRequested();
            using var maintenance = db.CreateCommand();
            maintenance.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-4096;";
            maintenance.ExecuteNonQuery();
            using (var tx = db.BeginTransaction())
            {
                int version = CompleteStorageVersion(db, tx);
                if (version == 0 || version == SchemaVersion && !VacuumDue(db, tx)) return false;
                token.ThrowIfCancellationRequested();
                if (version < SchemaVersion) MigrateStorage(db, tx, version, token);
                // A cancellation or disk-full error after the schema commit can retry the physical
                // cleanup, even for a small snapshot below the normal idle-vacuum threshold.
                Run(db, tx, "CREATE TABLE IF NOT EXISTS vacuum_pending(id INTEGER PRIMARY KEY)");
                token.ThrowIfCancellationRequested();
                tx.Commit();
            }

            token.ThrowIfCancellationRequested();
            // A space-analysis window can pin the v2 WAL. Wait for a later idle pass instead of
            // appending a second copy of the whole compacted database to that WAL during VACUUM.
            maintenance.CommandText = "PRAGMA busy_timeout=0";
            maintenance.ExecuteNonQuery();
            if (!TryCheckpoint(maintenance)) return true;
            token.ThrowIfCancellationRequested();
            maintenance.CommandText = "PRAGMA freelist_count";
            if (Convert.ToInt64(maintenance.ExecuteScalar()) > 0)
            {
                maintenance.CommandText = "VACUUM";
                maintenance.ExecuteNonQuery();
                token.ThrowIfCancellationRequested();
                if (!TryCheckpoint(maintenance)) return true;
            }
            token.ThrowIfCancellationRequested();
            maintenance.CommandText = "DROP TABLE IF EXISTS vacuum_pending";
            maintenance.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    private static bool TryCheckpoint(SqliteCommand command)
    {
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var state = command.ExecuteReader();
        return state.Read() && state.GetInt32(0) == 0;
    }

    private static int CompleteStorageVersion(SqliteConnection db, SqliteTransaction tx)
    {
        int version = CheckVersion(db, tx);
        if (version == 0) return 0;
        using var snapshot = Command(db, tx, "SELECT 1 FROM scan WHERE id=1 AND method<>$p0", NameIndexMethod);
        return snapshot.ExecuteScalar() is null ? 0 : version;
    }

    private static bool VacuumDue(SqliteConnection db, SqliteTransaction tx)
    {
        // Small holes are useful to the next journal batch. Repack a settled database when at
        // least 16 MiB and one fifth of its pages are free, or finish a previously requested vacuum.
        using var check = Command(db, tx, """
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name='vacuum_pending')
                OR (freelist_count*page_size>=16777216 AND freelist_count*5>=page_count)
            FROM pragma_freelist_count,pragma_page_size,pragma_page_count
            """);
        return Convert.ToBoolean(check.ExecuteScalar());
    }

    private static void MigrateStorage(SqliteConnection db, SqliteTransaction tx, int version, CancellationToken token)
    {
        bool live = IsLive(db, tx);
        using var marker = Command(db, tx, "SELECT 1 FROM sqlite_schema WHERE name='parent_names_unique'");
        bool verified = marker.ExecuteScalar() is not null;

        // Return the redundant trees to SQLite's free list first, to reuse that space while copying
        // the data. The old tables, search pages, dirty IDs, cursor and metadata roll back together.
        Run(db, tx, """
            DROP INDEX IF EXISTS names; DROP INDEX IF EXISTS parent_names_unique; DROP INDEX IF EXISTS live_name_order;
            ALTER TABLE entries RENAME TO previous_entries;
            """ + Schema + """
            INSERT INTO entries(id,parent,name,upper_override,volume,file_low,file_high,modified,changed,attrs,links,logical,allocated,counted,flags,kind)
                SELECT id,parent,name,nullif(name_key COLLATE BINARY,upper(name)),volume,file_low,file_high,modified,changed,attrs,links,logical,allocated,counted,flags,kind
                FROM previous_entries;
            DROP TABLE previous_entries;
            """ + CreateIndexes);
        token.ThrowIfCancellationRequested();
        if (live)
        {
            Run(db, tx, "ALTER TABLE live_dirs RENAME TO previous_dirs;" + DirectorySchema + """
                INSERT INTO live_dirs(id,path,prefix_override,own_flags)
                    SELECT id,path,nullif(prefix COLLATE BINARY,rtrim(path,char(92))||char(92)),own_flags FROM previous_dirs;
                DROP TABLE previous_dirs;
                """ + LiveEntryIndexes);
            CreateLiveMaintenance(db, tx, verified);
        }
        else Run(db, tx, OfflineNameIndex);
        token.ThrowIfCancellationRequested();
        if (version == 1)
        {
            Run(db, tx, "DELETE FROM name_pages");
            BuildNamePages(db, tx, token, live);
        }
        else
            CompactNamePages(db, tx, token);
    }
}
