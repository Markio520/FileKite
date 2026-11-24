using System.Collections;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    /// <summary>The screen and any pending recycle operations see one coherent generation, including during a cross-directory move.</summary>
    public SpaceSnapshot OpenSpace(string requestedRoot, CancellationToken token, long indexCacheBytes = 0)
    {
        var db = Open(path, false);
        SqliteTransaction? tx = null;
        try
        {
            tx = db.BeginTransaction(deferred: true);
            using var stop = InterruptOnCancel(db, token);
            var info = ReadInfo(db, tx) ?? throw new InvalidDataException("索引尚未建立。");
            if (info.Method == NameIndexMethod) throw new InvalidOperationException("正在核对空间占用。");
            var view = new SpaceDatabaseView(db, tx, info, requestedRoot, token, indexCacheBytes);
            return view.Metadata.WithEntries(view);
        }
        catch
        {
            tx?.Dispose();
            db.Dispose();
            throw;
        }
    }

    private sealed class SpaceDatabaseView : IIndexedSpaceView
    {
        private readonly SqliteConnection db;
        private readonly SqliteTransaction tx;
        private readonly string root;
        private readonly bool whole;
        private readonly int dbRoot;
        private readonly int firstId;
        private readonly int rootRank;
        // A snapshot uses dense public ordinals. Nearly contiguous scopes need
        // only their missing IDs; a sparse subtree stores its actual members.
        private readonly int[]? mappedIds;
        private readonly bool mapsGaps;
        private readonly object sync = new();
        private readonly Dictionary<int, (SpaceEntry Entry, string Path)> cache = [];
        private readonly Queue<int> recent = [];
        private bool disposed;
        public SpaceScanMetadata Metadata { get; }
        public string Generation { get; }
        public int Count { get; }

        public SpaceDatabaseView(SqliteConnection db, SqliteTransaction tx, IndexInfo info, string requested, CancellationToken token, long indexCacheBytes)
        {
            this.db = db;
            this.tx = tx;
            Generation = info.Generation;
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
            using (var cmd = Cmd("SELECT id FROM live_dirs WHERE path=$p0 COLLATE WINORD ORDER BY path=$p0 COLLATE BINARY DESC LIMIT 1", root))
                dbRoot = cmd.ExecuteScalar() is long id ? checked((int)id) : throw new DirectoryNotFoundException(root);
            whole = dbRoot == 0;
            int folders = info.Folders, files = info.Files;
            if (!whole)
            {
                Run(db, tx, """
                    CREATE TEMP TABLE members(id INTEGER PRIMARY KEY);
                    WITH RECURSIVE subtree(id) AS (SELECT $p0 UNION ALL SELECT e.id FROM entries e JOIN subtree s ON e.parent=s.id)
                    INSERT INTO members SELECT id FROM subtree;
                    """, dbRoot);
                using var cmd = Cmd("SELECT count(*),sum(kind=1) FROM entries e WHERE e.id IN members");
                using var r = cmd.ExecuteReader();
                r.Read();
                folders = r.GetInt32(1);
                files = r.GetInt32(0) - folders;
            }

            Count = checked(files + folders);
            string table = whole ? "entries" : "members";
            firstId = dbRoot;
            if (!whole)
            {
                using var minimum = Cmd("SELECT min(id) FROM members");
                firstId = checked((int)(long)minimum.ExecuteScalar()!);
            }
            using var maximum = Cmd("SELECT max(id) FROM " + table);
            int last = checked((int)(long)maximum.ExecuteScalar()!);
            long holes = (long)last - firstId + 1 - Count;
            if (holes < 0) throw new InvalidDataException("目录记录数和索引代次不符。");
            if (holes > 0 && holes < Count / 2)
            {
                mapsGaps = true;
                mappedIds = ReadGaps(table, firstId, last, (int)holes, token);
            }
            else if (holes != 0)
            {
                mappedIds = new int[Count];
                using var cmd = Cmd("SELECT id FROM " + table + " ORDER BY id");
                using var r = cmd.ExecuteReader();
                int i = 0;
                while (r.Read())
                {
                    token.ThrowIfCancellationRequested();
                    mappedIds[i++] = r.GetInt32(0);
                }

                if (i != Count)
                    throw new InvalidDataException("目录记录数和索引代次不符。");
            }

            // A directory moved beneath a newer parent can have a lower database ID.
            // Keep the requested root at public ordinal zero in that case as well.
            rootRank = dbRoot == firstId ? 0 : SortedOrdinal(dbRoot);
            var issues = whole ? info.Issues : info.Issues.Where(x => Within(x.Path, root)).ToArray();
            var counts = whole ? info.IssueCounts : issues.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Count());
            var volume = info.Volume;
            if (volume is not null && SpaceVolume.ReadLocalRoot(volume.Root) is { } liveVolume)
                volume = liveVolume with { IndexCacheBytes = indexCacheBytes, NtfsAttributeBytes = volume.NtfsAttributeBytes <= liveVolume.UsedBytes ? volume.NtfsAttributeBytes : 0 };
            var notice = info.Notice;
            Metadata = new(root, files, folders, volume, counts, issues, TimeSpan.Zero, false, "自建共享索引 · " + info.Method, notice);
        }

        private SqliteCommand Cmd(string sql, params object? [] args) => Command(db, tx, sql, args);
        private int[] ReadGaps(string table, int first, int last, int holes, CancellationToken token)
        {
            var gaps = new int[holes];
            const int block = 8192;
            string range = " FROM " + table + " WHERE id BETWEEN $p0 AND $p1";
            using var count = Cmd("SELECT count(*)" + range, first, last);
            using var ids = Cmd("SELECT id" + range + " ORDER BY id", first, last);
            int written = 0;
            for (long lo = first; lo <= last && written < holes; lo += block)
            {
                token.ThrowIfCancellationRequested();
                long hi = Math.Min(last, lo + block - 1);
                count.Parameters[0].Value = lo;
                count.Parameters[1].Value = hi;
                if ((long)count.ExecuteScalar()! == hi - lo + 1) continue;
                ids.Parameters[0].Value = lo;
                ids.Parameters[1].Value = hi;
                using var rows = ids.ExecuteReader();
                long next = lo;
                while (rows.Read())
                {
                    int present = rows.GetInt32(0);
                    while (next < present) gaps[written++] = (int)next++;
                    next++;
                }
                while (next <= hi) gaps[written++] = (int)next++;
            }
            if (written != holes) throw new InvalidDataException("目录编号与索引代次不符。");
            return gaps;
        }

        private int SortedOrdinal(int id)
        {
            if (id < firstId) return -1;
            int ordinal = id - firstId;
            if (mappedIds is not null)
            {
                int position = Array.BinarySearch(mappedIds, id);
                if (!mapsGaps) return position >= 0 ? position : -1;
                if (position >= 0) return -1;
                ordinal -= ~position;
            }
            return (uint)ordinal < (uint)Count ? ordinal : -1;
        }
        private int External(int id)
        {
            if (id == dbRoot) return 0;
            int ordinal = SortedOrdinal(id);
            return ordinal >= 0 && ordinal < rootRank ? ordinal + 1 : ordinal;
        }
        private int Internal(int id)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(id);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, Count);
            if (id == 0) return dbRoot;
            if (id <= rootRank) id--;
            if (mappedIds is null) return firstId + id;
            if (!mapsGaps) return mappedIds[id];
            // Gap i starts at logical position gap[i] - first - i; adjacent
            // gaps share that position and must all be skipped.
            int lo = 0, hi = mappedIds.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (mappedIds[mid] - firstId - mid <= id) lo = mid + 1;
                else hi = mid;
            }
            return firstId + id + lo;
        }

        private T Read<T>(Func<T> action, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                using var stop = InterruptOnCancel(db, token);
                try
                {
                    return action();
                }
                catch (SqliteException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }
        }

        private (SpaceEntry Entry, string Path) Row(int id) => Read(() =>
        {
            if (cache.TryGetValue(id, out var found))
                return found;
            using var cmd = Cmd("SELECT " + Columns + ",(SELECT prefix FROM live_dirs WHERE live_dirs.id=entries.parent) FROM entries WHERE id=$p0", Internal(id));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                throw new InvalidDataException("文件记录不存在。");
            var e = GetEntry(r);
            var value = (e with { Parent = id == 0 ? -1 : External(e.Parent) }, id == 0 ? root : r.GetString(14) + e.Name);
            if (cache.Count >= 512)
                cache.Remove(recent.Dequeue());
            recent.Enqueue(id);
            cache.Add(id, value);
            return value;
        });
        public SpaceEntry this[int id] => Row(id).Entry;
        public string PathOf(int id) => Row(id).Path;
        public bool HasFolders(int id) => Read(() =>
        {
            using var c = Cmd("SELECT 1 FROM entries WHERE parent=$p0 AND kind=1 LIMIT 1", Internal(id));
            return c.ExecuteScalar() != null;
        });
        public int[] Children(int id) => Read(() =>
        {
            using var c = Cmd("SELECT id FROM entries WHERE parent=$p0 ORDER BY id", Internal(id));
            using var r = c.ExecuteReader();
            var found = new List<int>();
            while (r.Read())
                found.Add(External(r.GetInt32(0)));
            return found.ToArray();
        });
        public int? Find(string path) => Read<int?>(() =>
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (!Within(path, root))
                return null;
            using var c = Cmd("""
                SELECT e.id FROM live_dirs d JOIN entries e ON e.parent=d.id WHERE d.path=$p0 COLLATE WINORD
                    AND e.name=$p1 COLLATE WINORD ORDER BY d.path=$p0 COLLATE BINARY DESC,e.name=$p1 COLLATE BINARY DESC LIMIT 1
                """, Path.GetDirectoryName(path), Path.GetFileName(path));
            if (c.ExecuteScalar() is not long v)
                return null;
            int id = External((int)v);
            return id >= 0 ? id : null;
        });
        public SpaceMapData Map(int id, CancellationToken token, bool logical = false) => Read(() =>
        {
            var summary = ReadMap(db, tx, Internal(id), logical);
            return new SpaceMapData(id, summary.Largest.Select(x => (External(x.Id), x.Bytes)).ToArray(), summary.Count, summary.Bytes);
        }, token);
        public IReadOnlyList<SpaceTypeTotal> Types(CancellationToken token, bool logical = false) => Read(() =>
        {
            var bytes = logical ? "max(logical,0)" : "counted";
            var size = logical ? "logical" : "allocated";
            using var c = Cmd(whole && !logical ? "SELECT kind,count,bytes,unknown FROM types ORDER BY bytes DESC,kind"
                : $"SELECT kind,count(*),sum({bytes}) AS total,sum({size}<0) FROM entries WHERE {(whole ? "" : "id IN members AND ")}kind<>1 GROUP BY kind ORDER BY total DESC,kind");
            using var r = c.ExecuteReader();
            var totals = new List<SpaceTypeTotal>();
            while (r.Read())
                totals.Add(new((SpaceKind)r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3)));
            return totals;
        }, token);
        public SpaceQueryResult List(int folder, SpaceView view, SpaceFilter filter, SpaceSort sort, bool descending, CancellationToken token, bool logical = false) => Read(() =>
        {
            bool directory = view == SpaceView.Directory;
            var size = logical ? "logical" : "allocated";
            using var c = db.CreateCommand();
            c.Transaction = tx;
            var where = new List<string>();
            void Add(string condition, object value)
            {
                var name = "$v" + c.Parameters.Count;
                c.Parameters.AddWithValue(name, value);
                where.Add(condition + name);
            }

            if (directory)
                Add("parent=", Internal(folder));
            else
            {
                where.Add("kind<>1");
                if (!whole)
                    where.Add("id IN members");
                if (filter.Kind != SpaceKind.All)
                    Add("kind=", (int)filter.Kind);
                if (filter.Minimum is { } min)
                    Add(size + ">=", min);
                if (filter.Maximum is { } max)
                {
                    where.Add(size + ">=0");
                    Add(size + "<=", max);
                }

                if (filter.ModifiedFrom.HasValue || filter.ModifiedBefore.HasValue)
                    where.Add("modified BETWEEN 1 AND 2650467743999999999");
                if (filter.ModifiedFrom is { } from)
                    Add("modified>=", from.UtcTicks - DateTime.FromFileTimeUtc(0).Ticks);
                if (filter.ModifiedBefore is { } to)
                    Add("modified<", to.UtcTicks - DateTime.FromFileTimeUtc(0).Ticks);
            }

            var key = sort switch
            {
                SpaceSort.Modified => "(CASE WHEN modified=0 THEN -1 ELSE modified END)",
                SpaceSort.Logical => "logical",
                SpaceSort.Allocated => "allocated",
                _ => "name COLLATE WINORD"
            };
            var order = (sort == SpaceSort.Name ? "" : "(" + key + "<0),") + key + (descending ? " DESC" : "") + ",name COLLATE WINORD,id";
            c.CommandText = $"SELECT id,{size},counted,flags,volume,file_low,file_high,links,kind FROM entries WHERE " + string.Join(" AND ", where) + " ORDER BY " + order;
            using var r = c.ExecuteReader();
            bool allFiles = !directory && filter is { Kind: SpaceKind.All, Minimum: null, Maximum: null, ModifiedFrom: null, ModifiedBefore: null };
            int[]? allIds = allFiles ? new int[Metadata.Files] : null;
            List<int>? selected = allFiles ? null : [];
            int index = 0;
            var shared = new HashSet<SpaceIdentity>();
            int unknown = 0;
            long bytes = 0;
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                int id = External(r.GetInt32(0));
                if (allIds is null) selected!.Add(id);
                else allIds[index++] = id;
                var allocation = r.GetInt64(1);
                var flags = (SpaceEntryFlags)r.GetInt32(3);
                if (allocation < 0 || (flags & SpaceEntryFlags.Partial) != 0 && (!logical || r.GetInt32(8) == (int)SpaceKind.Folder))
                    unknown++;
                if (logical) bytes += Math.Max(0, allocation);
                else if (directory)
                    bytes += r.GetInt64(2);
                else if (allocation >= 0 && ((flags & SpaceEntryFlags.Shared) == 0 && (r.GetInt64(7) != 0 || r.GetInt64(5) == 0 && r.GetInt64(6) == 0) || shared.Add(new((ulong)r.GetInt64(4), (ulong)r.GetInt64(5), (ulong)r.GetInt64(6)))))
                    bytes += allocation;
            }

            if (allIds is not null && index != allIds.Length)
                throw new InvalidDataException("文件总数与索引代次不符。");
            return new SpaceQueryResult(allIds ?? selected!.ToArray(), bytes, unknown);
        }, token);
        public IEnumerator<SpaceEntry> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;
                disposed = true;
                cache.Clear();
                recent.Clear();
                tx.Dispose();
                db.Dispose();
            }
        }
    }
}
