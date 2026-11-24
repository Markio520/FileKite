using System.Text.RegularExpressions;
using System.Text.Json;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

/// <summary>Each online volume contributes one ordered cursor. Memory depends on the volume and page counts.</summary>
public static class IndexedFileSearch
{
    public static SearchPage Query(IEnumerable<IndexStore> stores, SearchRequest request, CancellationToken token, string? notice = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageSize);
        var expression = SearchRules.Compile(request);
        var sources = stores.DistinctBy(s => s.DatabasePath, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.DatabasePath, StringComparer.Ordinal).ToArray();
        var readers = new List<SearchCursor>();
        try
        {
            long total = 0;
            var heap = new PriorityQueue<SearchCursor?, SearchFile>(new FileOrder(request));
            foreach (var store in sources)
            {
                token.ThrowIfCancellationRequested();
                var c = new SearchCursor(store, expression, request, sources.Length == 1 ? request.Offset : 0, token);
                readers.Add(c);
                total += c.Total;
                if (c.RootItem is { } root)
                {
                    heap.Enqueue(null, root);
                    total++;
                }

                if (c.Next())
                    heap.Enqueue(c, c.Current);
            }

            var page = new List<SearchFile>(Math.Min(request.PageSize, 512));
            int skip = sources.Length == 1 && readers[0].RootItem is null ? 0 : request.Offset;
            while (page.Count < request.PageSize && heap.TryDequeue(out var c, out var item))
            {
                token.ThrowIfCancellationRequested();
                if (skip > 0)
                    skip--;
                else
                {
                    if (request.IncludeDirectoryUsage && item.IsFolder)
                    {
                        var owner = c ?? readers.First(r => r.RootItem == item);
                        item = item with { DirectoryUsage = owner.Usage(item.FullPath) };
                    }
                    page.Add(item);
                }
                if (c is not null && c.Next())
                    heap.Enqueue(c, c.Current);
            }

            var generation = string.Join("/", readers.Select(r => r.Generation));
            var label = request.Scope == SearchScope.Folder ? "目录 · 自建共享索引" : $"自建索引 · {sources.Length} 个位置";
            bool partial = readers.Any(reader => reader.IsBuildingNames);
            if (partial) label += " · " + IndexStore.PartialNameNotice;
            return new(page, total, label + (string.IsNullOrEmpty(notice) ? "" : " · " + notice), readers.Sum(r => r.AccessErrors))
            {
                IndexGeneration = generation, IsPartialIndex = partial
            };
        }
        catch (SqliteException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        finally
        {
            foreach (var c in readers)
                c.Dispose();
        }
    }

    public static IComparer<SearchFile> Order(SearchRequest request) => new FileOrder(request);
    private sealed class FileOrder(SearchRequest request) : IComparer<SearchFile>
    {
        public int Compare(SearchFile? x, SearchFile? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var names = StringComparer.OrdinalIgnoreCase;
            int value = request.Sort switch
            {
                SearchSort.Path => names.Compare(x.Directory, y.Directory),
                SearchSort.Size => Nullable.Compare(x.Size, y.Size),
                SearchSort.Modified => Nullable.Compare(x.Modified, y.Modified),
                _ => names.Compare(x.Name, y.Name)};
            if (value != 0)
                return request.Descending ? -Math.Sign(value) : value;
            value = names.Compare(x.FullPath, y.FullPath);
            return value != 0 ? value : StringComparer.Ordinal.Compare(x.FullPath, y.FullPath);
        }
    }

    private sealed class SearchCursor : IDisposable
    {
        private readonly SqliteConnection db;
        private readonly SqliteTransaction tx;
        private readonly CancellationTokenRegistration stop;
        private SqliteCommand? query;
        private SqliteCommand? usageQuery;
        private readonly bool namesOnly;
        private SqliteDataReader? reader;
        private IEnumerator<SearchFile>? fastRows;
        public SearchFile? RootItem { get; }
        public long Total { get; }
        public string Generation { get; }
        public int AccessErrors { get; }
        public bool IsBuildingNames { get; }
        public SearchFile Current { get; private set; } = null!;

        public SearchCursor(IndexStore store, CompiledSearch match, SearchRequest request, int offset, CancellationToken token)
        {
            db = IndexStore.Open(store.DatabasePath, false);
            stop = IndexStore.InterruptOnCancel(db, token);
            try
            {
                tx = db.BeginTransaction(deferred: true);
                var info = IndexStore.ReadInfo(db, tx) ?? throw new InvalidDataException("索引未完成。");
                Generation = info.Generation;
                IsBuildingNames = info.IsBuildingNames;
                AccessErrors = info.IssueCounts.GetValueOrDefault(SpaceIssueKind.Access);
                namesOnly = info.Method == IndexStore.NameIndexMethod;
                var visibility = store.Visibility(db, tx, request, Generation, token);
                if (visibility != null) db.CreateFunction<int, bool>("SEARCH_VISIBLE", visibility.Allows, isDeterministic: true);
                bool rootVisible = visibility?.Allows(0) != false;
                if (request.Scope != SearchScope.Folder && rootVisible)
                    using (var root = db.CreateCommand())
                    {
                        root.Transaction = tx;
                        root.CommandText = "SELECT modified,attrs FROM entries WHERE id=0";
                        using var row = root.ExecuteReader();
                        row.Read();
                        var stamp = row.GetInt64(0);
                        var file = new SearchFile(info.Root, true, null, stamp is > 0 and <= 2650467743999999999 ? new DateTimeOffset(DateTime.FromFileTimeUtc(stamp)) : null)
                        {
                            IsReparsePoint = ((FileAttributes)row.GetInt32(1) & FileAttributes.ReparsePoint) != 0
                        };
                        if (match.Matches(file))
                        {
                            RootItem = file;
                            offset = 0;
                        }
                    }

                int? folder = null;
                if (request.Scope == SearchScope.Folder)
                {
                    using var location = db.CreateCommand();
                    location.Transaction = tx;
                    location.CommandText = "SELECT id FROM live_dirs WHERE path=$path COLLATE WINORD AND (own_flags&2)=0 ORDER BY path=$path COLLATE BINARY DESC LIMIT 1";
                    location.Parameters.AddWithValue("$path", request.Folder);
                    if (location.ExecuteScalar() is not long id)
                        throw new DirectoryNotFoundException(request.Folder);
                    if (id != 0 || !request.IncludeSubfolders)
                        folder = checked((int)id);
                }

                int take = (int)Math.Min(int.MaxValue, (long)request.Offset - offset + request.PageSize);
                string? term = match.Filters.Count == 0 && match.Groups.Count == 1 ? match.Groups[0] switch
                {
                    [] => "",
                    [{ Condition: { Field: SearchField.Name, Operation: "contains", Negated: false, Value: string text } }] => text,
                    _ => null
                } : null;
                string? extension = null;
                if (term == null && !namesOnly && match.Groups.Count == 1)
                {
                    var conditions = match.Groups[0].Concat(match.Filters).ToArray();
                    if (conditions is [{ Condition: { Field: SearchField.Extension, Negated: false, Value: string[] { Length: 1 } extensions } }]
                        && extensions[0].Length <= 8 && IndexStore.HasPackedNames(db, tx))
                        extension = extensions[0].ToUpperInvariant();
                }
                if (!folder.HasValue && request.Sort == SearchSort.Name && !request.Descending && (term != null || extension != null) && (long)take + offset <= 4096)
                {
                    var pinyin = term != null && request.PinyinSearch && SearchPinyin.IsQuery(term);
                    long visibleTotal = (long)info.Files + info.Folders - (visibility?.Count ?? 0) - (rootVisible ? 1 : 0);
                    if (!namesOnly)
                    {
                        var fast = store.LiveNames(db, tx, term ?? "", offset, take, token, visibility, pinyin, Generation,
                            term == "" ? checked((int)visibleTotal) : null, extension);
                        Total = fast.Total;
                        fastRows = fast.Rows.GetEnumerator();
                        return;
                    }
                    Total = visibleTotal;
                    var ids = term!.Length == 0 ? store.LookupFor(Generation)?.FirstPage(offset, take, Total, visibility) : null;
                    if (ids is null)
                    {
                        ids = IndexStore.SelectBootstrapNames(db, tx, term, offset, take, visibility, pinyin, token, out int count);
                        Total = count;
                    }
                    if (ids.Count == 0) return;
                    query = db.CreateCommand();
                    query.Transaction = tx;
                    query.CommandText = "SELECT d.prefix||e.name,e.attrs," + SqlFilter.Size + "," + SqlFilter.Time +
                        " FROM json_each($ids) i JOIN entries e ON e.id=i.value JOIN live_dirs d ON d.id=e.parent ORDER BY i.key";
                    query.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
                    reader = query.ExecuteReader();
                    return;
                }

                query = db.CreateCommand();
                query.Transaction = tx;
                var filter = new SqlFilter(db, query, namesOnly, request, token);
                var where = filter.Build(match);
                if (folder is { } folderId)
                {
                    query.Parameters.AddWithValue("$folder", folderId);
                    var parents = request.IncludeSubfolders ? """
                        e.parent IN (WITH RECURSIVE dirs(id) AS
                            (SELECT $folder UNION ALL SELECT ch.id FROM entries ch JOIN dirs ON ch.parent=dirs.id WHERE ch.kind=1 AND (ch.flags&2)=0)
                            SELECT id FROM dirs)
                        """ : "e.parent=$folder";
                    where = parents + " AND " + where;
                }

                const string joined = " FROM entries e JOIN live_dirs d ON d.id=e.parent";
                bool unfiltered = where == "1";
                if (visibility != null) where = filter.ReadsContent ? "SEARCH_VISIBLE(e.id) AND " + where : where + " AND SEARCH_VISIBLE(e.id)";
                if (filter.ReadsContent)
                {
                    // Share the set between count and page reads. Never reread a file to produce the same result page.
                    query.CommandText = "CREATE TEMP TABLE content_hits(id INTEGER PRIMARY KEY); INSERT INTO content_hits SELECT e.id" + joined + " WHERE e.id>0 AND " + where;
                    query.ExecuteNonQuery();
                    where = "e.id IN (SELECT id FROM content_hits)";
                }
                if (unfiltered)
                    Total = (long)info.Files + info.Folders - (visibility?.Count ?? 0) - (rootVisible ? 1 : 0);
                else
                {
                    query.CommandText = "SELECT count(*)" + (filter.NeedsPath ? joined : " FROM entries e") + " WHERE e.id>0 AND " + where;
                    Total = (long)query.ExecuteScalar()!;
                }

                if (Total <= offset)
                    return;
                var order = request.Sort switch
                {
                    SearchSort.Path => "d.path COLLATE WINORD",
                    SearchSort.Size => SqlFilter.Size,
                    SearchSort.Modified => SqlFilter.Time,
                    _ => "e.name COLLATE WINORD"
                };
                query.CommandText = "SELECT d.prefix||e.name,e.attrs," + SqlFilter.Size + "," + SqlFilter.Time + joined + " WHERE e.id>0 AND " + where + " ORDER BY " + order + (request.Descending ? " DESC" : "") + ",(d.prefix||e.name) COLLATE WINORD,(d.prefix||e.name) COLLATE BINARY LIMIT $limit OFFSET $skip";
                query.Parameters.AddWithValue("$limit", (long)request.Offset - offset + request.PageSize);
                query.Parameters.AddWithValue("$skip", offset);
                reader = query.ExecuteReader();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public SearchDirectoryUsage Usage(string path)
        {
            if (SpaceVolume.ReadLocalRoot(path) is { } disk) return new(disk.UsedBytes, Volume: true);
            if (namesOnly) return new(null);
            if (usageQuery is null)
            {
                usageQuery = db.CreateCommand();
                usageQuery.Transaction = tx;
                usageQuery.CommandText = "SELECT e.allocated,e.flags FROM live_dirs d JOIN entries e ON e.id=d.id WHERE d.path=$path COLLATE WINORD ORDER BY d.path=$path COLLATE BINARY DESC LIMIT 1";
                usageQuery.Parameters.Add("$path", SqliteType.Text);
                usageQuery.Prepare();
            }
            usageQuery.Parameters[0].Value = path;
            using var row = usageQuery.ExecuteReader();
            if (!row.Read()) return new(null);
            long bytes = row.GetInt64(0);
            bool partial = ((SpaceEntryFlags)row.GetInt32(1) & SpaceEntryFlags.Partial) != 0;
            return new(bytes < 0 || partial && bytes == 0 ? null : bytes, partial);
        }

        public bool Next()
        {
            if (fastRows is not null)
            {
                if (!fastRows.MoveNext())
                    return false;
                Current = fastRows.Current;
                return true;
            }

            if (reader is null || !reader.Read())
                return false;
            var attr = (FileAttributes)reader.GetInt32(1);
            Current = new(reader.GetString(0), (attr & FileAttributes.Directory) != 0, reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(reader.GetInt64(3))))
            {
                IsReparsePoint = (attr & FileAttributes.ReparsePoint) != 0
            };
            return true;
        }

        public void Dispose()
        {
            usageQuery?.Dispose();
            fastRows?.Dispose();
            reader?.Dispose();
            query?.Dispose();
            tx?.Dispose();
            stop.Dispose();
            db.Dispose();
        }
    }

    private sealed class SqlFilter(SqliteConnection db, SqliteCommand cmd, bool namesOnly, SearchRequest request, CancellationToken token)
    {
        public const string Size = "(CASE WHEN e.kind<>1 AND e.logical>=0 THEN e.logical END)";
        public const string Time = "(CASE WHEN e.modified BETWEEN 1 AND 2650467743999999999 THEN e.modified END)";
        public bool NeedsPath { get; private set; }
        public bool ReadsContent { get; private set; }

        private string Bind(object value)
        {
            var p = "$v" + cmd.Parameters.Count;
            cmd.Parameters.AddWithValue(p, value);
            return p;
        }

        public string Build(CompiledSearch match)
        {
            var clauses = match.Filters.Select(t => Term(t.Condition)).ToList();
            if (match.Groups.Any(g => g.Count > 0))
                clauses.Add("(" + string.Join(" OR ", match.Groups.Select(g => "(" + string.Join(" AND ", g.Select(t => Term(t.Condition))) + ")")) + ")");
            return clauses.Count == 0 ? "1" : string.Join(" AND ", clauses);
        }

        private int functions;
        private string Term(SearchCondition c)
        {
            string result;
            switch (c.Field)
            {
                case SearchField.Content:
                    NeedsPath = ReadsContent = true;
                    var content = "contentmatch" + functions++;
                    db.CreateFunction<string, bool>(content, path => LocalSearchFiles.ContainsText(path, (string)c.Value, request.ContentLimitMB, token));
                    result = "(e.kind<>1 AND " + content + "(d.prefix||e.name))";
                    break;
                case SearchField.Folder:
                    result = (bool)c.Value ? "e.kind=1" : "e.kind<>1";
                    break;
                case SearchField.Extension:
                    var extensions = ((string[])c.Value).Select(x => x.ToUpperInvariant());
                    // The parser accepts only ASCII alphanumerics in an extension.
                    // A literal dot and fixed-length suffix give the same answer before the full expression index exists.
                    var predicate = namesOnly
                        ? string.Join(" OR ", extensions.GroupBy(x => x.Length).Select(g =>
                            $"substr(e.name_key,-{g.Key + 1}) IN ({string.Join(",", g.Select(x => Bind("." + x)))})"))
                        : $"WINEXT(e.name_key) IN ({string.Join(",", extensions.Select(Bind))})";
                    result = $"(e.kind<>1 AND ({predicate}))";
                    return c.Negated ? "NOT "+result : result;
                case SearchField.Size:
                    result = Size + c.Operation + Bind(c.Value);
                    break;
                case SearchField.Modified:
                    var from = FileTime((DateTimeOffset)c.Value);
                    result = c.Operation == "range" ? Time + ">=" + Bind(from) + " AND " + Time + "<" + Bind(FileTime((DateTimeOffset)c.Upper!)) : Time + c.Operation + Bind(from);
                    break;
                default:
                    var path = c.Field == SearchField.Path;
                    NeedsPath |= path;
                    var column = path ? "(d.prefix||e.name)" : "e.name";
                    if (c.Value is Regex regex)
                    {
                        var name = "wild" + functions++;
                        db.CreateFunction<string, bool>(name, regex.IsMatch, isDeterministic: true);
                        result = name + "(" + column + ")";
                    }
                    else if (path)
                    {
                        var name = "pathmatch" + functions++;
                        db.CreateFunction<string, bool>(name, value => value.Contains((string)c.Value, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
                        result = name + "(" + column + ")";
                    }
                    else
                    {
                        var term = (string)c.Value;
                        result = "instr(e.name_key," + Bind(term.ToUpperInvariant()) + ")>0";
                        if (request.PinyinSearch && SearchPinyin.IsQuery(term))
                        {
                            var name = "pinyin" + functions++;
                            db.CreateFunction<string, bool>(name, value => SearchPinyin.Matches(value, term), isDeterministic: true);
                            result = "(" + result + " OR (length(CAST(e.name AS BLOB))<>length(e.name) AND " + name + "(e.name)))";
                        }
                    }
                    break;
            }

            // The managed predicate returns false for missing dates and sizes, before negation.
            return (c.Negated ? "NOT " : "") + "coalesce((" + result + "),0)";
        }

        private static long FileTime(DateTimeOffset date) => date.UtcTicks - DateTime.FromFileTimeUtc(0).Ticks;
    }
}
