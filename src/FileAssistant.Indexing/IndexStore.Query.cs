using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    public const int PageSize = 200;
    private const string Columns = "id,parent,name,volume,file_low,file_high,modified,changed,attrs,links,logical,allocated,counted,flags";

    public IndexPage Page(IndexInfo info, IndexView view, int folder, string text, SpaceKind kind, int offset, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (text.Length > 2048) throw new ArgumentException("搜索内容最多为 2048 个字符。", nameof(text));
        return Read(info, token, (db, tx) =>
        {
            using var paths = new PagePaths(db, tx, info.Root);
            var (folderPath, parent) = paths.Location(folder);
            if (view == IndexView.Search && text.Length > 0 && text.IndexOfAny(['*', '?']) < 0 && CheckVersion(db, tx) >= 2 && !IsLive(db, tx))
            {
                var (found, totalFound) = FindNames(db, tx, text, offset, token);
                var matches = ReadSelected(db, tx, paths, found, token);
                return new IndexPage(matches, totalFound, folderPath, parent, null);
            }
            string order;
            var where = "id>0";
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            switch (view)
            {
                case IndexView.Directory:
                    where = "parent=$parent"; order = "counted DESC,id";
                    cmd.Parameters.AddWithValue("$parent", folder);
                    break;
                case IndexView.LargeFiles:
                    where = "kind<>1"; order = "allocated DESC,id";
                    if (kind != SpaceKind.All)
                    {
                        where += " AND kind=$kind";
                        cmd.Parameters.AddWithValue("$kind", (int)kind);
                    }
                    break;
                case IndexView.Search:
                    order = "name_key,id";
                    var key = text.ToUpperInvariant();
                    if (key.IndexOfAny(['*', '?']) >= 0)
                    {
                        where += " AND name_key GLOB $term";
                        cmd.Parameters.AddWithValue("$term", key.Replace("[", "[[]", StringComparison.Ordinal));
                    }
                    else if (key.Length > 0) // A previous generation remains searchable during upgrade.
                    {
                        where += " AND instr(name_key,$term)>0";
                        cmd.Parameters.AddWithValue("$term", key);
                    }
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(view));
            }

            var source = " FROM entries WHERE " + where;
            int total;
            if (view == IndexView.LargeFiles && kind == SpaceKind.All) total = info.Files;
            else if (view == IndexView.Search && text.Length == 0) total = info.Files + info.Folders - 1;
            else
            {
                cmd.CommandText = "SELECT count(*)" + source;
                total = Convert.ToInt32(cmd.ExecuteScalar());
            }
            token.ThrowIfCancellationRequested();
            cmd.CommandText = "SELECT " + Columns + source + " ORDER BY " + order + " LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit", PageSize);
            cmd.Parameters.AddWithValue("$offset", offset);
            var entries = new List<(int Id, SpaceEntry Entry)>(PageSize);
            using (var r = cmd.ExecuteReader())
                while (r.Read()) entries.Add((r.GetInt32(0), GetEntry(r)));
            var rows = new List<IndexRow>(entries.Count);
            foreach (var (id, entry) in entries)
            {
                token.ThrowIfCancellationRequested();
                rows.Add(new(id, Path.Join(paths.Location(entry.Parent).Path, entry.Name), entry));
            }
            var map = view == IndexView.Directory ? ReadMap(db, tx, folder) : null;
            return new IndexPage(rows, total, folderPath, parent, map);
        });
    }

    private static IndexMap ReadMap(SqliteConnection db, SqliteTransaction tx, int folder, bool logical = false)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$folder", folder);
        var size = logical ? "logical" : "counted";
        cmd.CommandText = $"SELECT count(*),coalesce(sum({size}),0) FROM entries WHERE parent=$folder AND {size}>0";
        int count;
        long total;
        using (var r = cmd.ExecuteReader()) { r.Read(); count = r.GetInt32(0); total = r.GetInt64(1); }
        cmd.CommandText = $"SELECT id,name,kind,{size} FROM entries WHERE parent=$folder AND {size}>0 ORDER BY {size} DESC,id LIMIT 32";
        using var map = cmd.ExecuteReader();
        var largest = new List<MapItem>(32);
        while (map.Read()) largest.Add(new(map.GetInt32(0), map.GetString(1), map.GetInt32(2) == (int)SpaceKind.Folder, map.GetInt64(3)));
        return new(largest, count, total);
    }

    public IReadOnlyList<SpaceTypeTotal> Types(IndexInfo info, CancellationToken token)
        => Read(info, token, (db, tx) =>
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT kind,count,bytes,unknown FROM types ORDER BY bytes DESC,kind;";
            using var r = cmd.ExecuteReader();
            var rows = new List<SpaceTypeTotal>();
            while (r.Read()) rows.Add(new((SpaceKind)r.GetInt32(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3)));
            return rows;
        });

    private T Read<T>(IndexInfo info, CancellationToken token, Func<SqliteConnection, SqliteTransaction, T> read)
    {
        token.ThrowIfCancellationRequested();
        using var db = Open(path, false);
        using var stop = InterruptOnCancel(db, token);
        try
        {
            using var tx = db.BeginTransaction(deferred: true);
            using var gen = db.CreateCommand();
            gen.Transaction = tx;
            gen.CommandText = "SELECT generation FROM scan WHERE id=1";
            if (!Equals(info.Generation, gen.ExecuteScalar()))
                throw new IndexGenerationChangedException();
            var result = read(db, tx);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    private static SpaceEntry GetEntry(SqliteDataReader r)
    {
        var identity = new SpaceIdentity(unchecked((ulong)r.GetInt64(3)), unchecked((ulong)r.GetInt64(4)), unchecked((ulong)r.GetInt64(5)));
        var stamp = new SpaceStamp(identity, r.GetInt64(6), r.GetInt64(7), (FileAttributes)r.GetInt32(8), (uint)r.GetInt64(9));
        return new(r.GetString(2), r.GetInt32(1), stamp, r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), (SpaceEntryFlags)r.GetInt32(13));
    }

    // Only prefixes of paths returned on this page are materialized.
    private sealed class PagePaths : IDisposable
    {
        private readonly Dictionary<int, (string Path, int Parent)> known;
        private readonly SqliteCommand cmd;
        private readonly SqliteParameter parameter;
        public PagePaths(SqliteConnection db, SqliteTransaction tx, string rootPath)
        {
            known = new() { [0] = (rootPath, -1) };
            cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT parent,name FROM entries WHERE id=$id";
            parameter = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Prepare();
        }

        public (string Path, int Parent) Location(int id)
        {
            if (known.TryGetValue(id, out var value)) return value;
            var trail = new Stack<(int Id, int Parent, string Name)>();
            var current = id;
            while (!known.TryGetValue(current, out value))
            {
                parameter.Value = current;
                using var row = cmd.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("目录不在当前索引中。");
                var parent = row.GetInt32(0);
                if (parent < 0 || parent >= current) throw new InvalidDataException("索引中的目录关系无效。");
                trail.Push((current, parent, row.GetString(1)));
                current = parent;
            }
            foreach (var step in trail)
            {
                value = (Path.Join(value.Path, step.Name), step.Parent);
                known[step.Id] = value;
            }
            return value;
        }
        public void Dispose() => cmd.Dispose();
    }
}
