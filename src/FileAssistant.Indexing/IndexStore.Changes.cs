using System.ComponentModel;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public interface IIndexFileSystem
{
    SpaceEntry? Read(string path);
    IEnumerable<ListedSpaceFile> Names(string directory);
    IEnumerable<string> HardLinks(string path);
    // Return true only when the stored spelling and the current directory entry agree exactly.
    bool TryReadNamedFile(string path, out SpaceEntry entry) { entry = default; return false; }
}

public sealed partial class IndexStore
{
    public IndexInfo ApplyChanges(IndexInfo expected, IReadOnlyList<JournalChange> records, IReadOnlyList<string> notifications, JournalCursor? end, string excludedDirectory, IIndexFileSystem files, CancellationToken token, bool checkpointIdle = false)
    {
        token.ThrowIfCancellationRequested();
        using var db = Open(path, true);
        using var stop = InterruptOnCancel(db, token);
        try
        {
            using var tx = db.BeginTransaction();
            bool compact = CheckVersion(db, tx) == SchemaVersion;
            var info = ReadInfo(db, tx) ?? throw new InvalidDataException("索引尚未建立。");
            if (info.Generation != expected.Generation)
                throw new IndexGenerationChangedException();
            var patch = new LivePatch(db, tx, root, excludedDirectory, files, token, compact);
            patch.Collect(records, notifications);
            var changed = patch.Reconcile();
            if (!changed && !checkpointIdle)
                return info;
            if (end is { } cursor)
                Run(db, tx, "UPDATE live_state SET journal=$p0,next=$p1 WHERE id=1", unchecked((long)cursor.JournalId), cursor.Next);
            if (changed)
            {
                patch.Publish();
                using var dirty = Command(db, tx, "SELECT 1 FROM live_dirty LIMIT 1 OFFSET 8191");
                if (dirty.ExecuteScalar() is not null)
                {
                    Run(db, tx, "DELETE FROM name_pages");
                    BuildNamePages(db, tx, token, true);
                    Run(db, tx, "DELETE FROM live_dirty");
                }
            }

            var result = ReadInfo(db, tx)!;
            token.ThrowIfCancellationRequested();
            tx.Commit();
            return result;
        }
        catch (SqliteException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    private sealed class LivePatch(SqliteConnection db, SqliteTransaction tx, string root, string excluded, IIndexFileSystem source, CancellationToken token, bool compact)
    {
        private readonly Dictionary<string, Targets> targets = new(StringComparer.Ordinal);
        private readonly Queue<(int Id, string Path)> newDirectories = new();
        private readonly HashSet<(ulong Low, ulong High)> refreshDirectories = [];
        private readonly HashSet<(ulong Low, ulong High)> locateLinks = [];
        private readonly HashSet<string> discoveredLinks = new(StringComparer.Ordinal);
        private readonly HashSet<string> observedBoundaries = new(StringComparer.Ordinal);
        private readonly Queue<string> pendingBoundaries = new();
        private readonly HashSet<int> addedFolders = [];
        private int next;
        private bool changed;
        private sealed class Targets
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<(ulong, ulong)> Ids { get; } = [];
            public Dictionary<string, (int Id, SpaceEntry Entry)> Indexed { get; } = new(StringComparer.Ordinal);
            public bool DataOnly = true;
        }

        private Targets For(string parent)
        {
            if (!targets.TryGetValue(parent, out var group))
                targets[parent] = group = new();
            return group;
        }

        private bool InScope(string p) => Within(p, root) && !Within(p, excluded);
        private bool IsNestedBoundary(SpaceTraversalBoundaryException error) =>
            Within(error.BoundaryPath, root) && !string.Equals(error.BoundaryPath, root, StringComparison.OrdinalIgnoreCase);
        private void ObserveBoundary(SpaceTraversalBoundaryException error)
        {
            var boundary = error.BoundaryPath;
            if (InScope(boundary) && observedBoundaries.Add(boundary)) pendingBoundaries.Enqueue(boundary);
        }

        private bool BehindObservedBoundary(string path)
        {
            if (observedBoundaries.Count == 0) return false;
            for (string? part = path; part != null; part = Path.GetDirectoryName(part))
                if (observedBoundaries.Contains(part)) return true;
            return false;
        }
        private void AddPath(string p)
        {
            if (!InScope(p) || string.Equals(p, root, StringComparison.OrdinalIgnoreCase))
                return;
            if (Path.GetDirectoryName(p) is { } dir)
            {
                var group = For(dir);
                group.Names.Add(Path.GetFileName(p));
                group.DataOnly = false;
            }
        }

        private static bool ValidName(string name) => name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['\\', '/', '\0', ':']) < 0;
        private void Exec(string sql, params object? [] args) => Run(db, tx, sql, args);
        private SqliteCommand Cmd(string sql, params object? [] args) => Command(db, tx, sql, args);
        [Flags]
        private enum Lookup { Parent = 1, File = 2, Links = 4, Enumerate = 8 }

        // These USN reasons only change file data. Topology, security and unknown reasons
        // always need directory reconciliation, even when the journal supplies a name.
        private static bool DataOnly(uint reason)
        {
            const uint data = 0x77;
            return (reason & data) != 0 && (reason & ~(data | 0x80000000u)) == 0;
        }

        public void Collect(IReadOnlyList<JournalChange> records, IReadOnlyList<string> paths)
        {
            var identities = new Dictionary<(ulong Low, ulong High), Lookup>();
            void Include((ulong, ulong) id, Lookup role)
            {
                if (id == default && role != Lookup.Parent) return;
                identities[id] = identities.GetValueOrDefault(id) | role;
                if ((role & Lookup.Links) != 0) locateLinks.Add(id);
            }

            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                AddPath(path);
                if (!InScope(path) || Path.GetDirectoryName(path) is not { } directory)
                    continue;
                using var cmd = Cmd("""
                    SELECT e.file_low,e.file_high FROM entries e JOIN live_dirs d ON d.id=e.parent
                    WHERE d.path=$p0 COLLATE WINORD AND e.name=$p1 COLLATE WINORD AND e.links<>1 AND e.kind<>1
                    """, directory, Path.GetFileName(path));
                using (var known = cmd.ExecuteReader())
                    while (known.Read())
                        Include((unchecked((ulong)known.GetInt64(0)), unchecked((ulong)known.GetInt64(1))), Lookup.File | Lookup.Links | Lookup.Enumerate);
                try
                {
                    if (source.Read(path) is { IsDirectory: false, Stamp.Links: > 1 } item)
                    {
                        Include((item.Stamp.Identity.Low, item.Stamp.Identity.High), Lookup.File | Lookup.Links | Lookup.Enumerate);
                        FindLinks(path, item, AddPath);
                    }
                }
                catch (Exception ex) when (Denied(ex) || Missing(ex))
                {
                }
                catch (SpaceTraversalBoundaryException ex) when (IsNestedBoundary(ex))
                {
                    ObserveBoundary(ex);
                }
            }

            foreach (var change in records)
            {
                token.ThrowIfCancellationRequested();
                Include((change.ParentLow, change.ParentHigh), Lookup.Parent);
                var id = (change.FileLow, change.FileHigh);
                if (id == default) continue;
                if ((change.Reason & 0x800) != 0) refreshDirectories.Add(id);
                var role = DataOnly(change.Reason) ? Lookup.File : Lookup.File | Lookup.Enumerate;
                Include(id, (change.Reason & 0x10000) != 0 ? role | Lookup.Links : role);
            }
            if (identities.Count == 0) return;

            Exec("CREATE TEMP TABLE journal_ids(low INTEGER,high INTEGER,role INTEGER,PRIMARY KEY(low,high)) WITHOUT ROWID");
            using (var insert = Cmd("INSERT INTO journal_ids VALUES($p0,$p1,$p2)", 0L, 0L, 0))
            {
                foreach (var (id, role) in identities)
                {
                    token.ThrowIfCancellationRequested();
                    insert.Parameters[0].Value = unchecked((long)id.Low);
                    insert.Parameters[1].Value = unchecked((long)id.High);
                    insert.Parameters[2].Value = (int)role;
                    insert.ExecuteNonQuery();
                }
            }

            var parents = new Dictionary<(ulong, ulong), string>();
            using (var query = Cmd("""
                SELECT i.low,i.high,d.path FROM journal_ids i
                CROSS JOIN entries e ON e.file_low=i.low AND e.file_high=i.high
                CROSS JOIN live_dirs d ON d.id=e.id WHERE (i.role&1)<>0 AND (e.flags&2)=0
                """))
            using (var rows = query.ExecuteReader())
                while (rows.Read())
                    parents.TryAdd((unchecked((ulong)rows.GetInt64(0)), unchecked((ulong)rows.GetInt64(1))), rows.GetString(2));

            var linkPaths = new Dictionary<(ulong, ulong), List<string>>();
            void LinkCandidate((ulong, ulong) id, string path)
            {
                if (!linkPaths.TryGetValue(id, out var aliases)) linkPaths.Add(id, aliases = []);
                aliases.Add(path);
            }

            foreach (var change in records)
            {
                if (!parents.TryGetValue((change.ParentLow, change.ParentHigh), out var directory) || !InScope(directory))
                    continue;
                var group = For(directory);
                var id = (change.FileLow, change.FileHigh);
                group.Ids.Add(id);
                group.DataOnly &= id != default && DataOnly(change.Reason);
                if (!ValidName(change.Name)) continue;
                group.Names.Add(change.Name);
                if ((change.Reason & 0x10000) != 0) LinkCandidate(id, Path.Join(directory, change.Name));
            }

            var columns = "e." + Columns.Replace(",", ",e.");
            using (var query = Cmd("SELECT " + columns + """
                ,d.path,i.role FROM journal_ids i
                CROSS JOIN entries e ON e.file_low=i.low AND e.file_high=i.high
                CROSS JOIN live_dirs d ON d.id=e.parent WHERE (i.role&2)<>0
                """))
            using (var rows = query.ExecuteReader())
                while (rows.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var entry = GetEntry(rows);
                    var directory = rows.GetString(14);
                    var path = Path.Join(directory, entry.Name);
                    if (!InScope(path) || IsReservedRootEntry(entry.Parent, entry)) continue;
                    var role = (Lookup)rows.GetInt32(15);
                    var group = For(directory);
                    group.Names.Add(entry.Name);
                    group.Indexed[entry.Name] = (rows.GetInt32(0), entry);
                    group.DataOnly &= (role & Lookup.Enumerate) == 0;
                    if ((role & Lookup.Links) != 0) LinkCandidate((entry.Stamp.Identity.Low, entry.Stamp.Identity.High), path);
                }

            // NTFS may report a new link against any still-live alias of the same identity.
            foreach (var (id, aliases) in linkPaths)
                foreach (var alias in aliases)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!locateLinks.Contains(id)) break;
                        if (source.Read(alias) is not { } item || (item.Stamp.Identity.Low, item.Stamp.Identity.High) != id)
                            continue;
                        FindLinks(alias, item, AddPath);
                    }
                    catch (Exception ex) when (Denied(ex) || Missing(ex))
                    {
                    }
                    catch (SpaceTraversalBoundaryException ex) when (IsNestedBoundary(ex))
                    {
                        ObserveBoundary(ex);
                    }
                }
        }

        public bool Reconcile()
        {
            if (targets.Count == 0 && pendingBoundaries.Count == 0)
                return false;
            Exec(Tracking);
            using (var max = Cmd("SELECT max(id) FROM entries"))
                next = checked((int)(long)max.ExecuteScalar()! + 1);
            do
            {
                ReconcileTargets();
                AddNewDirectories();
                targets.Clear();
                RepairBoundaries();
                foreach (var path in discoveredLinks) AddPath(path);
                discoveredLinks.Clear();
            } while (targets.Count != 0);
            return changed;
        }

        private void ReconcileTargets()
        {
            foreach (var pair in targets.OrderBy(p => p.Key.Length))
            {
                token.ThrowIfCancellationRequested();
                if (BehindObservedBoundary(pair.Key)) continue;
                var parent = DirectoryId(pair.Key);
                if (parent is null || addedFolders.Contains(parent.Value))
                    continue;
                var known = pair.Value.Indexed;
                Dictionary<string, SpaceEntry> current = new(StringComparer.Ordinal);
                try
                {
                    if (!TryReadFileData(pair.Key, parent.Value, pair.Value, current))
                    {
                        current.Clear();
                        known = ReadKnownChildren(parent.Value, pair.Value);
                        foreach (var name in source.Names(pair.Key))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!ValidName(name.Name) || !(pair.Value.Names.Contains(name.Name) || pair.Value.Ids.Contains((name.Low, name.High)) || name.Low == 0 && name.High == 0 && pair.Value.Ids.Count != 0))
                                continue;
                            var path = Path.Join(pair.Key, name.Name);
                            if (!InScope(path))
                                continue;
                            if (ReadChild(path, parent.Value, name) is { } e && !IsReservedRootEntry(parent.Value, e))
                                current[name.Name] = e;
                        }
                    }
                }
                catch (Exception ex) when (Missing(ex))
                {
                    continue;
                } // Its parent handles the directory removal.
                catch (Exception ex) when (Denied(ex))
                {
                    MarkUnreadable(parent.Value);
                    continue;
                }
                catch (SpaceTraversalBoundaryException ex) when (IsNestedBoundary(ex))
                {
                    ObserveBoundary(ex);
                    continue;
                }

                try
                {
                    RefreshParentMetadata(pair.Key, parent.Value);
                }
                catch (Exception ex) when (Denied(ex))
                {
                    MarkUnreadable(parent.Value);
                    continue;
                }
                catch (Exception ex) when (Missing(ex))
                {
                    continue;
                }
                catch (SpaceTraversalBoundaryException ex) when (IsNestedBoundary(ex))
                {
                    ObserveBoundary(ex);
                    continue;
                }

                foreach (var(name, old)in known)
                    if (!current.TryGetValue(name, out var value) || !SameEntry(old.Entry, value))
                        Remove(old.Id, old.Entry.IsDirectory);
                foreach (var(name, value)in current)
                {
                    var old = known.GetValueOrDefault(name);
                    if (old.Id != 0 && SameEntry(old.Entry, value))
                        Update(old.Id, old.Entry, value);
                    else
                        Add(value, Path.Join(pair.Key, name));
                }
            }

        }

        private void AddNewDirectories()
        {
            while (newDirectories.TryDequeue(out var pending))
            {
                token.ThrowIfCancellationRequested();
                if (BehindObservedBoundary(pending.Path)) continue;
                using var exists = Cmd("SELECT 1 FROM live_dirs WHERE id=$p0", pending.Id);
                if (exists.ExecuteScalar() is null)
                    continue;
                try
                {
                    foreach (var child in source.Names(pending.Path))
                    {
                        token.ThrowIfCancellationRequested();
                        var path = Path.Join(pending.Path, child.Name);
                        if (InScope(path) && ReadChild(path, pending.Id, child) is { } entry)
                            Add(entry, path);
                    }
                }
                catch (Exception ex) when (Denied(ex))
                {
                    MarkUnreadable(pending.Id);
                }
                catch (Exception ex) when (Missing(ex))
                {
                    Remove(pending.Id, true);
                }
                catch (SpaceTraversalBoundaryException ex) when (IsNestedBoundary(ex))
                {
                    ObserveBoundary(ex);
                }
            }

        }

        private void RepairBoundaries()
        {
            while (pendingBoundaries.TryDequeue(out var path))
            {
                token.ThrowIfCancellationRequested();
                // Reconcile the leaf through its safe parent to recover its current spelling
                // or a replacement. Older MFT snapshots can already contain children beneath
                // a junction, even when the junction itself is stored as skipped.
                AddPath(path);
                var cols = "e." + Columns.Replace(",", ",e.");
                using var lookup = Cmd("SELECT " + cols + """
                     FROM live_dirs d JOIN entries e ON e.id=d.id
                    WHERE d.path=$p0 COLLATE WINORD AND d.path=$p0 COLLATE BINARY LIMIT 1
                    """, path);
                int id;
                SpaceEntry prior;
                using (var row = lookup.ExecuteReader())
                {
                    if (!row.Read()) continue;
                    id = row.GetInt32(0);
                    prior = GetEntry(row);
                }
                SpaceEntry? leaf;
                try
                {
                    leaf = source.Read(path); // Reads the boundary itself without following it.
                }
                catch (SpaceTraversalBoundaryException ex) when (IsNestedBoundary(ex))
                {
                    ObserveBoundary(ex);
                    continue;
                }
                catch (Exception ex) when (Denied(ex))
                {
                    leaf = prior with { LogicalBytes = 0, AllocatedBytes = -1, CountedBytes = 0,
                        Flags = SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped | SpaceEntryFlags.AccessDenied };
                }
                catch (Exception ex) when (Missing(ex))
                {
                    leaf = null;
                }
                if (leaf is { IsSkipped: false, IsCloud: false } current && !current.Stamp.IsReparsePoint)
                    observedBoundaries.Remove(path); // Replaced again; enumerate the now-local directory normally.
                Remove(id, true);
                if (leaf is { } item) Add(item with { Parent = prior.Parent, Name = prior.Name }, path);
            }
        }

        private Dictionary<string, (int Id, SpaceEntry Entry)> ReadKnownChildren(int parent, Targets group)
        {
            var known = new Dictionary<string, (int Id, SpaceEntry Entry)>(StringComparer.Ordinal);
            // The identity join already added every indexed alias to Names. A small set can be
            // resolved through the name index, including case-distinct siblings; absent IDs
            // with redacted names require no sibling rows from SQLite.
            if (group.Names.Count <= 16 && !group.Ids.Contains(default))
            {
                using var byName = Cmd("SELECT " + Columns + """
                     FROM entries INDEXED BY live_name_order WHERE name=$p0 COLLATE WINORD AND parent=$p1
                    """, "", parent);
                foreach (var name in group.Names)
                {
                    byName.Parameters[0].Value = name;
                    using var rows = byName.ExecuteReader();
                    while (rows.Read())
                    {
                        var item = GetEntry(rows);
                        if (!IsReservedRootEntry(parent, item)) known[item.Name] = (rows.GetInt32(0), item);
                    }
                }
            }
            else
            {
                using var byParent = Cmd("SELECT " + Columns + " FROM entries WHERE parent=$p0", parent);
                using var rows = byParent.ExecuteReader();
                while (rows.Read())
                {
                    var item = GetEntry(rows);
                    if (!IsReservedRootEntry(parent, item) &&
                        (group.Names.Contains(item.Name) || group.Ids.Contains((item.Stamp.Identity.Low, item.Stamp.Identity.High))))
                        known.Add(item.Name, (rows.GetInt32(0), item));
                }
            }
            return known;
        }

        private void FindLinks(string path, SpaceEntry item, Action<string> add)
        {
            var key = (item.Stamp.Identity.Low, item.Stamp.Identity.High);
            if (item.IsDirectory || !locateLinks.Contains(key)) return;
            if (item.Stamp.Links == 1)
            {
                locateLinks.Remove(key);
                return;
            }
            bool found = false;
            try
            {
                foreach (var link in source.HardLinks(path))
                {
                    found = true;
                    if (!string.Equals(link, path, StringComparison.Ordinal)) add(link);
                }
                if (found) locateLinks.Remove(key);
            }
            catch (Exception ex) when (Denied(ex) || Missing(ex))
            {
            }
        }

        private bool TryReadFileData(string directory, int parent, Targets group, Dictionary<string, SpaceEntry> current)
        {
            if (!group.DataOnly || group.Indexed.Count is 0 or > 128 ||
                group.Names.Any(name => !group.Indexed.ContainsKey(name)) ||
                !group.Ids.IsSubsetOf(group.Indexed.Values.Select(row => (row.Entry.Stamp.Identity.Low, row.Entry.Stamp.Identity.High))))
                return false;
            foreach (var (name, row) in group.Indexed)
            {
                token.ThrowIfCancellationRequested();
                if (row.Entry.Parent != parent || row.Entry.IsDirectory || row.Entry.IsSkipped) return false;
                try
                {
                    if (!source.TryReadNamedFile(Path.Join(directory, name), out var item) ||
                        item.IsDirectory || item.IsSkipped || item.Stamp.Identity != row.Entry.Stamp.Identity ||
                        item.Stamp.Links != row.Entry.Stamp.Links)
                        return false;
                    current.Add(name, item with { Parent = parent, Name = name });
                }
                catch (Exception ex) when (Denied(ex) || Missing(ex))
                {
                    return false;
                }
            }
            return true;
        }

        private bool SameEntry(SpaceEntry prior, SpaceEntry item) => prior.IsDirectory == item.IsDirectory && prior.Stamp.Identity == item.Stamp.Identity && prior.IsSkipped == item.IsSkipped && !(item.IsDirectory && refreshDirectories.Contains((item.Stamp.Identity.Low, item.Stamp.Identity.High)));
        private SpaceEntry? ReadChild(string path, int parent, ListedSpaceFile child)
        {
            try
            {
                if (source.Read(path) is not { } e) return null;
                // Both names may be new and the only recorded notification may be redacted.
                if (locateLinks.Count != 0) FindLinks(path, e, link => discoveredLinks.Add(link));
                return e with { Name = child.Name, Parent = parent };
            }
            catch (Exception ex) when (Missing(ex))
            {
                return null;
            }
            catch (Exception ex) when (Denied(ex))
            {
                return child.Restricted(parent);
            }
        }

        private void RefreshParentMetadata(string path, int id)
        {
            if (source.Read(path) is not { IsDirectory: true } item)
                return;
            using var q = Cmd("SELECT " + Columns + " FROM entries WHERE id=$p0", id);
            SpaceEntry old;
            using (var row = q.ExecuteReader())
            {
                if (!row.Read())
                    return;
                old = GetEntry(row);
            }

            if (old.Stamp.Identity == item.Stamp.Identity)
                Update(id, old, item);
        }

        private bool IsReservedRootEntry(int parent, SpaceEntry e) => parent == 0 && Path.GetPathRoot(root) == root && e.Name.StartsWith('$') && e.Stamp.Identity.IsKnown && e.Stamp.Identity.High == 0 && (e.Stamp.Identity.Low & 0x0000ffffffffffffUL) < 16;
        private static bool Missing(Exception e) => e is FileNotFoundException or DirectoryNotFoundException or Win32Exception { NativeErrorCode: 2 or 3 or 303 };
        private static bool Denied(Exception e) => e is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 or 32 };
        private int? DirectoryId(string path)
        {
            using var q = Cmd("SELECT id FROM live_dirs WHERE (own_flags&18)=0 AND path=$p0 COLLATE WINORD ORDER BY path=$p0 COLLATE BINARY DESC LIMIT 1", path);
            return q.ExecuteScalar() is long value ? checked((int)value) : null;
        }

        private void MarkUnreadable(int id)
        {
            using var q = Cmd("SELECT own_flags FROM live_dirs WHERE id=$p0", id);
            if (q.ExecuteScalar() is not long flags || (flags & (int)SpaceEntryFlags.AccessDenied) != 0)
                return;
            Exec("UPDATE live_dirs SET own_flags=own_flags|$p1 WHERE id=$p0; INSERT OR IGNORE INTO affected VALUES($p0)", id, (int)(SpaceEntryFlags.Partial | SpaceEntryFlags.AccessDenied));
            changed = true;
        }

        private void Remove(int id, bool directory)
        {
            if (directory)
            {
                RemoveBranch(db, tx, id);
            }
            else
                Exec("DELETE FROM entries WHERE id=$p0", id);
            changed = true;
        }

        private static void RemoveBranch(SqliteConnection db, SqliteTransaction tx, int id) => Run(db, tx, """
            DELETE FROM removed;
            WITH RECURSIVE branch(id) AS (SELECT $p0 UNION ALL SELECT e.id FROM entries e JOIN branch b ON e.parent=b.id)
            INSERT INTO removed SELECT id FROM branch;
            DELETE FROM live_dirs WHERE id IN removed;
            DELETE FROM entries WHERE id IN removed;
            """, id);
        private void Update(int id, SpaceEntry old, SpaceEntry item)
        {
            if (item.IsDirectory)
            {
                if (old.Stamp == item.Stamp)
                    return;
                Exec("UPDATE entries SET modified=$p0,changed=$p1,attrs=$p2,links=$p3 WHERE id=$p4", item.Stamp.Modified, item.Stamp.Changed, (int)item.Stamp.Attributes, (long)item.Stamp.Links, id);
            }
            else
            {
                if (item with
                {
                    CountedBytes = old.CountedBytes
                }

                == old)
                    return;
                // The same identity at the same name keeps its row and cached name-page position.
                // A shared file's charge will be chosen across all aliases after reconciliation.
                var counted = item.Stamp.Links != 1 && item.Stamp.Identity.IsKnown ? old.CountedBytes : item.CountedBytes;
                Exec("""
                    UPDATE entries SET modified=$p0,changed=$p1,attrs=$p2,links=$p3,
                        logical=$p4,allocated=$p5,counted=$p6,flags=$p7 WHERE id=$p8
                    """, item.Stamp.Modified, item.Stamp.Changed, (int)item.Stamp.Attributes, (long)item.Stamp.Links,
                    item.LogicalBytes, item.AllocatedBytes, counted, (int)item.Flags, id);
            }

            changed = true;
        }

        private void Add(SpaceEntry entry, string path)
        {
            // A redacted journal notification can identify the new occupant of a name without mentioning the displaced file.
            using var lookup = Cmd("SELECT " + Columns + " FROM entries WHERE parent=$p0 AND name=$p1 COLLATE WINORD AND name=$p1 COLLATE BINARY", entry.Parent, entry.Name);
            int? previous = null;
            SpaceEntry prior = default;
            using (var row = lookup.ExecuteReader())
                if (row.Read())
                {
                    previous = row.GetInt32(0);
                    prior = GetEntry(row);
                }

            if (previous is { } old)
            {
                if (SameEntry(prior, entry))
                {
                    Update(old, prior, entry);
                    return;
                }

                Remove(old, prior.IsDirectory);
            }

            var id = checked(next++);
            Insert(id, entry);
            if (entry.IsDirectory)
            {
                if (compact)
                    Exec("INSERT INTO live_dirs(id,path,own_flags) VALUES($p0,$p1,$p2)", id, path, (int)entry.Flags);
                else
                    Exec("INSERT INTO live_dirs(id,path,prefix,own_flags) VALUES($p0,$p1,$p2,$p3)", id, path, path + Path.DirectorySeparatorChar, (int)entry.Flags);
                if (!entry.IsSkipped)
                {
                    addedFolders.Add(id);
                    newDirectories.Enqueue((id, path));
                }
            }

            changed = true;
        }

        private void Insert(int id, SpaceEntry e) => Exec($"""
            INSERT INTO entries(id,parent,name,{(compact ? "upper_override" : "name_key")},volume,file_low,file_high,modified,changed,attrs,links,logical,allocated,counted,flags,kind)
            VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14,$p15)
            """, id, e.Parent, e.Name, compact ? UpperOverride(e.Name) : e.Name.ToUpperInvariant(), unchecked((long)e.Stamp.Identity.Volume), unchecked((long)e.Stamp.Identity.Low), unchecked((long)e.Stamp.Identity.High), e.Stamp.Modified, e.Stamp.Changed, (int)e.Stamp.Attributes, (long)e.Stamp.Links, e.LogicalBytes, e.AllocatedBytes, e.CountedBytes, (int)e.Flags, (int)SpaceKinds.Of(e));
        internal static void NormalizeBaseline(SqliteConnection db, SqliteTransaction tx, string? excluded, CancellationToken token)
        {
            Run(db, tx, Tracking);
            if (excluded is not null)
            {
                using var self = Command(db, tx, "SELECT id FROM live_dirs WHERE id<>0 AND path=$p0 COLLATE WINORD", excluded);
                if (self.ExecuteScalar() is long id)
                    RemoveBranch(db, tx, checked((int)id));
            }

            Run(db, tx, "INSERT OR IGNORE INTO physical SELECT file_low,file_high FROM entries WHERE links<>1 AND kind<>1 AND (file_low<>0 OR file_high<>0)");
            Rebalance(db, tx, token);
        }

        private const string Tracking = """
            CREATE TEMP TABLE affected(id INTEGER PRIMARY KEY);
            CREATE TEMP TABLE removed(id INTEGER PRIMARY KEY);
            CREATE TEMP TABLE physical(low INTEGER,high INTEGER,PRIMARY KEY(low,high));
            CREATE TEMP TABLE charges(id INTEGER PRIMARY KEY,bytes INTEGER NOT NULL);
            CREATE TEMP TABLE changes(kind INTEGER PRIMARY KEY,n INTEGER,bytes INTEGER,u INTEGER);
            CREATE TEMP TRIGGER inserted AFTER INSERT ON main.entries BEGIN
                INSERT OR IGNORE INTO affected VALUES(new.parent);
                INSERT OR IGNORE INTO physical SELECT new.file_low,new.file_high WHERE new.kind<>1 AND new.links<>1 AND (new.file_low<>0 OR new.file_high<>0);
                INSERT INTO changes VALUES(new.kind,1,new.counted,new.allocated<0)
                    ON CONFLICT(kind) DO UPDATE SET n=n+1,bytes=bytes+new.counted,u=u+(new.allocated<0);
            END;
            CREATE TEMP TRIGGER deleted AFTER DELETE ON main.entries BEGIN
                INSERT OR IGNORE INTO affected VALUES(old.parent);
                INSERT OR IGNORE INTO physical SELECT old.file_low,old.file_high WHERE old.kind<>1 AND old.links<>1 AND (old.file_low<>0 OR old.file_high<>0);
                INSERT INTO changes VALUES(old.kind,-1,-old.counted,-(old.allocated<0))
                    ON CONFLICT(kind) DO UPDATE SET n=n-1,bytes=bytes-old.counted,u=u-(old.allocated<0);
            END;
            CREATE TEMP TRIGGER recharged AFTER UPDATE OF counted ON main.entries WHEN new.kind<>1 AND new.counted<>old.counted BEGIN
                INSERT OR IGNORE INTO affected VALUES(new.parent);
                INSERT INTO changes VALUES(new.kind,0,new.counted-old.counted,0)
                    ON CONFLICT(kind) DO UPDATE SET bytes=bytes+new.counted-old.counted;
            END;
            CREATE TEMP TRIGGER file_changed AFTER UPDATE OF logical,allocated,flags,links ON main.entries WHEN new.kind<>1 AND
                (old.logical<>new.logical OR old.allocated<>new.allocated OR old.flags<>new.flags OR old.links<>new.links) BEGIN
                INSERT OR IGNORE INTO affected VALUES(new.parent);
                INSERT OR IGNORE INTO physical SELECT new.file_low,new.file_high
                    WHERE (old.links<>1 OR new.links<>1) AND (new.file_low<>0 OR new.file_high<>0);
                INSERT INTO changes SELECT new.kind,0,0,(new.allocated<0)-(old.allocated<0)
                    WHERE (new.allocated<0)<>(old.allocated<0)
                    ON CONFLICT(kind) DO UPDATE SET u=u+excluded.u;
            END;
            """;
        public void Publish()
        {
            Rebalance(db, tx, token);
        }

        private static void Rebalance(SqliteConnection db, SqliteTransaction tx, CancellationToken token)
        {
            void Exec(string sql, params object? [] args) => Run(db, tx, sql, args);
            SqliteCommand Cmd(string sql, params object? [] args) => Command(db, tx, sql, args);
            Exec("""
                WITH links AS (
                  SELECT e.id,e.allocated,e.counted, row_number() OVER(PARTITION BY p.low,p.high
                      ORDER BY (e.flags&2)<>0,(d.prefix||e.name) COLLATE WINORD,(d.prefix||e.name) COLLATE BINARY) AS rn
                  FROM physical p CROSS JOIN entries e ON e.file_low=p.low AND e.file_high=p.high
                  CROSS JOIN live_dirs d ON d.id=e.parent WHERE e.kind<>1),
                amounts AS (SELECT id,counted,CASE WHEN rn=1 THEN max(0,allocated) ELSE 0 END AS charge FROM links)
                INSERT INTO charges SELECT id,charge FROM amounts WHERE counted<>charge;
                UPDATE entries SET counted=(SELECT bytes FROM charges WHERE charges.id=entries.id) WHERE id IN (SELECT id FROM charges);
                WITH RECURSIVE parents(id) AS (SELECT id FROM affected WHERE id>=0 UNION
                    SELECT e.parent FROM entries e JOIN parents p ON p.id=e.id WHERE e.parent>=0)
                INSERT OR IGNORE INTO affected SELECT id FROM parents;
                """);
            var parents = new List<DirectoryTotal>();
            using (var q = Cmd("""
                SELECT a.id,e.parent,d.own_flags,e.file_low=0 AND e.file_high=0
                FROM affected a CROSS JOIN live_dirs d ON d.id=a.id CROSS JOIN entries e ON e.id=a.id
                ORDER BY a.id DESC
                """))
            using (var rows = q.ExecuteReader())
                while (rows.Read())
                    parents.Add(new(rows.GetInt32(0), rows.GetInt32(1), rows.GetInt32(2), rows.GetBoolean(3)));
            using var breadth = Cmd("""
                SELECT (SELECT count(*) FROM affected a CROSS JOIN entries e INDEXED BY directory_items
                    ON e.parent=a.id WHERE a.id>=0) * 4 >= files+folders FROM scan WHERE id=1
                """);
            // When a large fraction of entries needs summing, many indexed child ranges would
            // fetch the same table pages in different orders. Read the table once in row order.
            if (parents.Count != 0 && (long)breadth.ExecuteScalar()! != 0)
                RecomputeTogether(db, tx, parents, token);
            else if (parents.Count != 0)
            {
                using var update = Cmd("""
                    UPDATE entries SET (logical,allocated,counted,flags)=(
                        SELECT coalesce(sum(max(logical,0)),0),coalesce(sum(counted),0),coalesce(sum(counted),0),
                          (SELECT own_flags FROM live_dirs WHERE id=$p0) |
                          coalesce(max(flags&1),0) | coalesce(max(flags&8),0) |
                          coalesce(max(CASE WHEN flags&22 OR (file_low=0 AND file_high=0) THEN 16 ELSE 0 END),0)
                        FROM entries WHERE parent=$p0) WHERE id=$p0;
                    """, 0);
                foreach (var parent in parents)
                {
                    token.ThrowIfCancellationRequested();
                    update.Parameters[0].Value = parent.Id;
                    update.ExecuteNonQuery();
                }
            }

            Exec("""
                INSERT INTO types SELECT kind,n,bytes,u FROM changes WHERE kind<>1
                    ON CONFLICT(kind) DO UPDATE SET count=count+excluded.count,bytes=types.bytes+excluded.bytes,unknown=unknown+excluded.unknown;
                DELETE FROM types WHERE count=0;
                UPDATE scan SET generation=$p0,updated=$p1,files=files+coalesce((SELECT sum(n) FROM changes WHERE kind<>1),0),
                    folders=folders+coalesce((SELECT n FROM changes WHERE kind=1),0),bytes=(SELECT counted FROM entries WHERE id=0)
                    WHERE id=1;
                """, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        private sealed class DirectoryTotal(int id, int parent, int flags, bool unknownIdentity)
        {
            public int Id { get; } = id;
            public int Parent { get; } = parent;
            public bool UnknownIdentity { get; } = unknownIdentity;
            public int Flags = flags;
            public long Logical, Counted;

            public void Add(long logical, long counted, int childFlags, bool childUnknown)
            {
                Logical = checked(Logical + Math.Max(logical, 0));
                Counted = checked(Counted + counted);
                Flags |= childFlags & (int)(SpaceEntryFlags.Partial | SpaceEntryFlags.Shared);
                const SpaceEntryFlags unverified = SpaceEntryFlags.Skipped | SpaceEntryFlags.Cloud | SpaceEntryFlags.ContainsUnverifiedItems;
                if (childUnknown || (childFlags & (int)unverified) != 0)
                    Flags |= (int)SpaceEntryFlags.ContainsUnverifiedItems;
            }
        }

        private static void RecomputeTogether(SqliteConnection db, SqliteTransaction tx, List<DirectoryTotal> parents, CancellationToken token)
        {
            var byId = parents.ToDictionary(p => p.Id);
            using (var read = Command(db, tx, """
                SELECT id,parent,logical,counted,flags,file_low=0 AND file_high=0 FROM entries NOT INDEXED
                """))
            using (var rows = read.ExecuteReader())
                while (rows.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (byId.TryGetValue(rows.GetInt32(1), out var parent) && !byId.ContainsKey(rows.GetInt32(0)))
                        parent.Add(rows.GetInt64(2), rows.GetInt64(3), rows.GetInt32(4), rows.GetBoolean(5));
                }

            using var update = Command(db, tx, """
                UPDATE entries SET logical=$p0,allocated=$p1,counted=$p1,flags=$p2
                WHERE id=$p3 AND (logical<>$p0 OR allocated<>$p1 OR counted<>$p1 OR flags<>$p2)
                """, 0L, 0L, 0, 0);
            // Every indexed parent has a smaller row ID than its children, including live inserts.
            foreach (var folder in parents)
            {
                token.ThrowIfCancellationRequested();
                update.Parameters[0].Value = folder.Logical;
                update.Parameters[1].Value = folder.Counted;
                update.Parameters[2].Value = folder.Flags;
                update.Parameters[3].Value = folder.Id;
                update.ExecuteNonQuery();
                if (byId.TryGetValue(folder.Parent, out var parent))
                    parent.Add(folder.Logical, folder.Counted, folder.Flags, folder.UnknownIdentity);
            }
        }
    }
}
