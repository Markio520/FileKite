using FileAssistant.Core.Features.Search;
using Microsoft.Data.Sqlite;
using System.Text;

namespace FileAssistant.Indexing;

public sealed partial class IndexStore
{
    private BootstrapLookup? bootstrapLookup;
    private void ReleaseBootstrapLookup() => Volatile.Write(ref bootstrapLookup, null);

    internal BootstrapLookup? LookupFor(string generation)
    {
        var lookup = Volatile.Read(ref bootstrapLookup);
        return lookup?.Generation == generation ? lookup : null;
    }

    // The walk already knows every parent's consecutive child IDs. Keep those ranges and
    // directory-name hashes so a visibility change only needs paths for possible rule matches.
    // SQLite remains the complete snapshot and also serves readers in another process.
    internal sealed class BootstrapLookup(string root, string generation)
    {
        private readonly List<(int Id, int NameHash)> directories = [];
        private readonly List<(int First, int Parent)> ranges = [];
        private readonly List<int> hidden = [];
        private readonly PriorityQueue<int, NameRank> leadingNames = new(HighestNameFirst);
        private int[] firstIds = [];
        private int count;
        public string Generation => generation;

        public void Observe(int id, int parent, string name, string parentPath, FileAttributes attributes)
        {
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 &&
                (id != 0 || !string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)))
                hidden.Add(id);
            if (id == 0) return;
            if ((attributes & FileAttributes.Directory) != 0)
                directories.Add((id, StringComparer.OrdinalIgnoreCase.GetHashCode(name)));
            if (ranges.Count == 0 || ranges[^1].Parent != parent)
                ranges.Add((id, parent));

            if (leadingNames.Count == FirstPageRows)
            {
                leadingNames.TryPeek(out _, out var worst);
                var comparison = StringComparer.OrdinalIgnoreCase.Compare(name, worst.Name);
                if (comparison > 0) return;
                var candidate = new NameRank(name, Path.Join(parentPath, name));
                if (comparison == 0 && ComparePaths(candidate.Path, worst.Path) >= 0) return;
                leadingNames.DequeueEnqueue(id, candidate);
            }
            else leadingNames.Enqueue(id, new(name, Path.Join(parentPath, name)));
        }

        public void Finish(int total)
        {
            count = total;
            firstIds = new int[leadingNames.Count];
            for (int i = firstIds.Length - 1; i >= 0; i--) firstIds[i] = leadingNames.Dequeue();
        }

        public List<int>? FirstPage(int offset, int take, long visibleRows, SearchVisibility? visibility)
        {
            var ids = new List<int>(Math.Min(firstIds.Length, take));
            int skip = offset;
            foreach (var id in firstIds)
            {
                if (visibility?.Allows(id) == false) continue;
                if (skip > 0) skip--;
                else if (ids.Count < take) ids.Add(id);
                if (ids.Count == take) break;
            }
            // Exclusions may hide the whole prefix. Consult the complete table for the rest.
            return ids.Count == take || (long)offset + ids.Count >= visibleRows ? ids : null;
        }

        public void FillVisibility(SqliteConnection db, SqliteTransaction tx, SearchRequest request, SearchVisibility result, CancellationToken token)
        {
            var rules = new SearchDirectoryFilter(request.ExcludedDirectories);
            if (rules.Excludes(root))
            {
                result.ExcludeRange(0, count);
                return;
            }
            if (!request.ShowHidden)
                foreach (int id in hidden) result.Exclude(id);

            if (request.ExcludedDirectories.Count > 0)
            {
                var hashes = new HashSet<int>();
                foreach (var rule in request.ExcludedDirectories)
                {
                    var trimmed = rule.Replace('/', '\\').AsSpan().TrimEnd('\\');
                    if (!trimmed.IsEmpty)
                        hashes.Add(string.GetHashCode(trimmed[(trimmed.LastIndexOf('\\') + 1)..], StringComparison.OrdinalIgnoreCase));
                }
                using var path = Command(db, tx, "SELECT path FROM live_dirs WHERE id=$p0", 0);
                path.Prepare();
                foreach (var (id, nameHash) in directories)
                {
                    if (!hashes.Contains(nameHash)) continue;
                    token.ThrowIfCancellationRequested();
                    path.Parameters[0].Value = id;
                    // Verify the whole path for absolute rules, overlapping subtrees and hash collisions.
                    if (path.ExecuteScalar() is string location && rules.Excludes(location)) result.Exclude(id);
                }
            }
            if (result.Count == 0) return;
            for (int i = 0; i < ranges.Count; i++)
            {
                var (first, parent) = ranges[i];
                if (result.Allows(parent)) continue;
                token.ThrowIfCancellationRequested();
                result.ExcludeRange(first, i + 1 < ranges.Count ? ranges[i + 1].First : count);
            }
        }
    }

    private readonly record struct NameRank(string Name, string Path);
    // Name-only snapshots have no global name-order index. Count in the same pass as
    // selecting the page, and only fetch a directory path if a name can enter that page.
    internal static List<int> SelectBootstrapNames(SqliteConnection db, SqliteTransaction tx, string term, int offset,
        int take, SearchVisibility? visibility, bool pinyin, CancellationToken token, out int total)
    {
        using var query = Command(db, tx, "SELECT id,name,parent FROM entries WHERE id>0");
        if (term.Length != 0)
        {
            string predicate = "instr(name_key,$key)>0";
            query.Parameters.AddWithValue("$key", term.ToUpperInvariant());
            if (pinyin)
            {
                db.CreateFunction<string, bool>("BOOTSTRAP_PINYIN", name => SearchPinyin.Matches(name, term), isDeterministic: true);
                predicate += " OR (length(CAST(name AS BLOB))<>length(name) AND BOOTSTRAP_PINYIN(name))";
            }
            query.CommandText += " AND (" + predicate + ")";
        }
        using var directory = Command(db, tx, "SELECT prefix FROM live_dirs WHERE id=$p0", 0);
        directory.Prepare();
        int lastParent = -1, worstId = -1, needed = checked(offset + take);
        string prefix = "";
        byte[] worstName = [];
        var best = new PriorityQueue<int, NameRank>(HighestNameFirst);
        total = 0;
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested();
            int id = rows.GetInt32(0);
            if (visibility?.Allows(id) == false) continue;
            total++;
            if (needed == 0) continue;
            var bytes = SQLitePCL.raw.sqlite3_column_blob(rows.Handle, 1);
            bool full = best.Count == needed;
            NameRank worst = default;
            int comparison = -1;
            if (full)
            {
                best.TryPeek(out var first, out worst);
                if (first != worstId)
                {
                    worstId = first;
                    worstName = Encoding.UTF8.GetBytes(worst.Name);
                }
                comparison = CompareNames(bytes, worstName);
                if (comparison > 0) continue;
            }
            int parent = rows.GetInt32(2);
            if (parent != lastParent)
            {
                lastParent = parent;
                directory.Parameters[0].Value = parent;
                prefix = (string)directory.ExecuteScalar()!;
            }
            var name = Encoding.UTF8.GetString(bytes);
            var candidate = new NameRank(name, prefix + name);
            if (comparison == 0 && ComparePaths(candidate.Path, worst.Path) >= 0) continue;
            if (full) best.DequeueEnqueue(id, candidate);
            else best.Enqueue(id, candidate);
        }
        var ids = new int[Math.Max(0, best.Count - offset)];
        for (int i = ids.Length - 1; i >= 0; i--) ids[i] = best.Dequeue();
        return [.. ids];
    }

    private static readonly IComparer<NameRank> HighestNameFirst = Comparer<NameRank>.Create((a, b) =>
    {
        int order = StringComparer.OrdinalIgnoreCase.Compare(b.Name, a.Name);
        return order == 0 ? ComparePaths(b.Path, a.Path) : order;
    });
    private static int ComparePaths(string a, string b)
    {
        int order = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return order == 0 ? StringComparer.Ordinal.Compare(a, b) : order;
    }
}
