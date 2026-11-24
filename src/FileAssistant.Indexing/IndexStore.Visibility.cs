using FileAssistant.Core.Features.Search;
using Microsoft.Data.Sqlite;
using System.Numerics;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    // Readers share an immutable exclusion map for one committed generation and one policy.
    // Sparse blocks avoid allocating up to the highest row ID after years of deletes.
    internal sealed class SearchVisibility(string generation, bool showHidden, IReadOnlyList<string> exclusions)
    {
        private readonly Dictionary<int, ulong[]> blocks = [];
        private ulong[]?[]? firstBlocks;
        private readonly string[] directories = exclusions.ToArray();
        public int Count { get; private set; }
        public bool Matches(string current, SearchRequest request) => current == generation && request.ShowHidden == showHidden
            && directories.SequenceEqual(request.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);
        public bool Allows(int id)
        {
            int index = id >> 12;
            var head = firstBlocks;
            var block = head != null && (uint)index < (uint)head.Length ? head[index] : blocks.GetValueOrDefault(index);
            return block == null || (block[(id >> 6) & 63] & 1UL << (id & 63)) == 0;
        }
        public void Freeze()
        {
            if (blocks.Count == 0) return;
            var head = new ulong[]?[Math.Min(4096, blocks.Keys.Max() + 1)];
            foreach (var (id, block) in blocks)
                if (id < head.Length) head[id] = block;
            firstBlocks = head;
        }
        public void Exclude(int id)
        {
            if (!blocks.TryGetValue(id >> 12, out var block)) blocks.Add(id >> 12, block = new ulong[64]);
            ref ulong word = ref block[(id >> 6) & 63];
            ulong bit = 1UL << (id & 63);
            if ((word & bit) != 0) return;
            word |= bit;
            Count++;
        }
        public void ExcludeRange(int first, int end)
        {
            int firstWord = first >> 6, lastWord = (end - 1) >> 6;
            for (int index = firstWord; index <= lastWord; index++)
            {
                if (!blocks.TryGetValue(index >> 6, out var block)) blocks.Add(index >> 6, block = new ulong[64]);
                ulong bits = index == firstWord ? ulong.MaxValue << (first & 63) : ulong.MaxValue;
                if (index == lastWord) bits &= ulong.MaxValue >> (63 - ((end - 1) & 63));
                ref ulong word = ref block[index & 63];
                Count += BitOperations.PopCount(bits & ~word);
                word |= bits;
            }
        }
    }

    private SearchVisibility? lastVisibility;

    internal SearchVisibility? Visibility(SqliteConnection db, SqliteTransaction tx, SearchRequest request, string generation, CancellationToken token)
    {
        if (request.ShowHidden && request.ExcludedDirectories.Count == 0) return null;
        var cached = Volatile.Read(ref lastVisibility);
        if (cached?.Matches(generation, request) == true) return cached.Count == 0 ? null : cached;

        var result = new SearchVisibility(generation, request.ShowHidden, request.ExcludedDirectories);
        if (LookupFor(generation) is { } bootstrap)
        {
            bootstrap.FillVisibility(db, tx, request, result, token);
            result.Freeze();
            Volatile.Write(ref lastVisibility, result);
            return result.Count == 0 ? null : result;
        }
        // Complete compact snapshots maintain sparse indexes for hidden attributes and
        // their directory tree. Match the blocked roots once, then follow parent IDs.
        if (HasPackedNames(db, tx) && IsLive(db, tx))
        {
            FillLiveVisibility(db, tx, request, result, token);
            result.Freeze();
            Volatile.Write(ref lastVisibility, result);
            return result.Count == 0 ? null : result;
        }
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        var hiddenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!request.ShowHidden)
        {
            cmd.CommandText = "SELECT d.path FROM live_dirs d JOIN entries e ON e.id=d.id WHERE (e.attrs&6)<>0 OR (d.own_flags&32)<>0";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                var path = rows.GetString(0);
                if (!string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) hiddenPaths.Add(path);
            }
        }
        var criteria = new List<string>();
        if (request.ExcludedDirectories.Count > 0 || hiddenPaths.Count > 0)
        {
            var excluded = new SearchDirectoryFilter(request.ExcludedDirectories.Concat(hiddenPaths));
            db.CreateFunction<string, bool>("EXCLUDED_DIR", excluded.Excludes, isDeterministic: true);
            cmd.CommandText = "CREATE TEMP TABLE excluded_dirs(id INTEGER PRIMARY KEY); INSERT INTO excluded_dirs SELECT id FROM live_dirs WHERE EXCLUDED_DIR(path)";
            if (cmd.ExecuteNonQuery() > 0)
                criteria.Add("(e.parent IN (SELECT id FROM excluded_dirs) OR (e.kind=1 AND e.id IN (SELECT id FROM excluded_dirs)))");
        }
        if (!request.ShowHidden) criteria.Add("(e.id>0 AND (e.attrs&6)<>0)");
        if (criteria.Count > 0)
        {
            cmd.CommandText = "SELECT e.id FROM entries e WHERE " + string.Join(" OR ", criteria);
            using var rows = cmd.ExecuteReader();
            while (rows.Read()) { token.ThrowIfCancellationRequested(); result.Exclude(rows.GetInt32(0)); }
        }
        result.Freeze();
        Volatile.Write(ref lastVisibility, result);
        return result.Count == 0 ? null : result;
    }

    private void FillLiveVisibility(SqliteConnection db, SqliteTransaction tx, SearchRequest request, SearchVisibility result, CancellationToken token)
    {
        using var roots = Command(db, tx, "CREATE TEMP TABLE blocked_roots(id INTEGER PRIMARY KEY)");
        roots.ExecuteNonQuery();
        bool blockRoot = new SearchDirectoryFilter(request.ExcludedDirectories).Excludes(root);
        if (blockRoot) Run(db, tx, "INSERT INTO blocked_roots VALUES(0)");
        else
        {
            if (!request.ShowHidden)
            {
                using var hidden = Command(db, tx, "SELECT id FROM entries WHERE (attrs&6)<>0 AND id>0");
                using var rows = hidden.ExecuteReader();
                while (rows.Read()) { token.ThrowIfCancellationRequested(); result.Exclude(rows.GetInt32(0)); }
                // The previous path-based filter treats case-equivalent paths as one
                // exclusion. Preserve that behavior for case-sensitive directories.
                Run(db, tx, """
                    WITH hidden(id) AS (
                      SELECT id FROM entries WHERE (attrs&6)<>0 AND kind=1
                      UNION SELECT id FROM live_dirs WHERE (own_flags&32)<>0)
                    INSERT OR IGNORE INTO blocked_roots
                    SELECT other.id FROM hidden h JOIN live_dirs d ON d.id=h.id
                    JOIN live_dirs other ON other.path=d.path COLLATE WINORD WHERE h.id<>0 OR $p0
                    """, !string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase));
            }
            foreach (string rule in request.ExcludedDirectories)
            {
                token.ThrowIfCancellationRequested();
                string name = rule.Replace('/', '\\').TrimEnd('\\');
                if (name.Length == 0) continue;
                if (name.Contains('\\'))
                    Run(db, tx, "INSERT OR IGNORE INTO blocked_roots SELECT id FROM live_dirs WHERE path=$p0 COLLATE WINORD", name);
                else
                    Run(db, tx, """
                        INSERT OR IGNORE INTO blocked_roots
                        SELECT id FROM entries INDEXED BY live_name_order WHERE name=$p0 COLLATE WINORD AND kind=1
                        """, name);
            }
        }
        using var excluded = Command(db, tx, """
            WITH RECURSIVE blocked(id) AS (
              SELECT id FROM blocked_roots
              UNION SELECT d.id FROM blocked b JOIN entries d INDEXED BY live_directory_children ON d.parent=b.id AND d.kind=1)
            SELECT id FROM blocked_roots
            UNION ALL SELECT e.id FROM blocked b CROSS JOIN entries e INDEXED BY directory_items ON e.parent=b.id
            """);
        using var items = excluded.ExecuteReader();
        while (items.Read()) { token.ThrowIfCancellationRequested(); result.Exclude(items.GetInt32(0)); }
    }
}
