using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileAssistant.Core.Features.Search;

/// <summary>The supported query subset is shared by indexed, folder and batch search.</summary>
public static class SearchRules
{
    public static string Extensions(SearchKind kind) => kind switch
    {
        SearchKind.Images => "jpg;jpeg;png;webp;gif;bmp;tif;tiff;heic;heif;avif;svg;ico",
        SearchKind.Documents => "pdf;doc;docx;odt;xls;xlsx;ods;ppt;pptx;odp;txt;md;csv;tsv;rtf;log",
        SearchKind.Archives => "zip;7z;rar;tar;gz;bz2;xz",
        SearchKind.Video => "mp4;mov;mkv;avi;wmv;webm;m4v",
        SearchKind.Audio => "mp3;wav;flac;aac;m4a;ogg;opus;wma;aiff;aif",
        SearchKind.Executables => "exe;msi;com;bat;cmd",
        _ => string.Empty
    };

    public static CompiledSearch Compile(SearchRequest request, DateTimeOffset? now = null, Func<SearchFile, string, bool>? contentMatcher = null)
    {
        if (request.MinimumSize < 0 || request.MaximumSize < 0)
            throw new SearchException("文件大小不能为负数。");
        if (request.MinimumSize > request.MaximumSize)
            throw new SearchException("最小大小不能大于最大大小。");
        if (request.ModifiedFrom >= request.ModifiedBefore)
            throw new SearchException("开始日期不能晚于结束日期。");
        if (request.Text.Length > 2048) throw new SearchException("搜索内容过长，请缩短到 2048 个字符以内。");
        List<List<SearchTerm>> groups = [[]];
        foreach (var token in Tokenize(request.Text))
        {
            if (token == "|")
            {
                if (groups[^1].Count == 0) throw new SearchException("“|” 两侧都需要搜索条件。");
                groups.Add([]);
            }
            else groups[^1].Add(ParseTerm(token, request.MatchPath, now ?? DateTimeOffset.Now, request.PinyinSearch,
                request.ContentLimitMB.HasValue, contentMatcher));
        }
        if (groups.Count > 1 && groups[^1].Count == 0) throw new SearchException("“|” 后缺少搜索条件。");
        var filters = new List<SearchTerm>();
        var extensions = Extensions(request.Kind);
        if (extensions.Length > 0) filters.Add(ParseTerm("ext:" + extensions, false, now ?? DateTimeOffset.Now));
        if (request.Kind is SearchKind.Folders or SearchKind.Files)
            filters.Add(ParseTerm(request.Kind == SearchKind.Folders ? "folder:" : "file:", false, now ?? DateTimeOffset.Now));
        if (request.MinimumSize is { } min) filters.Add(new(f => !f.IsFolder && f.Size >= min, $"file: size:>={min}", new(SearchField.Size, ">=", min)));
        if (request.MaximumSize is { } max) filters.Add(new(f => !f.IsFolder && f.Size <= max, $"file: size:<={max}", new(SearchField.Size, "<=", max)));
        if (request.ModifiedFrom is { } date)
            filters.Add(new(f => f.Modified >= date, $"dm:>={date.LocalDateTime:yyyy-MM-dd}", new(SearchField.Modified, ">=", date)));
        if (request.ModifiedBefore is { } before)
            filters.Add(new(f => f.Modified < before, $"dm:<{before.LocalDateTime:yyyy-MM-dd}", new(SearchField.Modified, "<", before)));
        return new(groups, filters);
    }

    public static SearchPage Filter(IReadOnlyList<SearchFile> files, SearchRequest request, string source, int skipped = 0,
        CancellationToken cancellationToken = default, Func<SearchFile, string, bool>? contentMatcher = null,
        Func<SearchFile, bool>? visible = null)
    {
        var expression = Compile(request, contentMatcher: contentMatcher);
        var offset = Math.Max(0, request.Offset);
        var size = Math.Max(0, request.PageSize);
        int keep = size == 0 || offset >= files.Count ? 0 : (int)Math.Min(files.Count, (long)offset + size);
        bool Matches(SearchFile file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (visible == null || visible(file)) && expression.Matches(file);
        }
        // Deep pages and whole-list exports are faster with precomputed sort keys and a bulk sort.
        if (keep > 256 && keep > files.Count / 4)
        {
            var matches = files.Where(Matches).ToList();
            var items = SortPage(matches, request, offset, size);
            cancellationToken.ThrowIfCancellationRequested();
            return new(items, matches.Count, source, skipped);
        }
        // The worst retained hit is at the top. Count every match, but retain only the requested prefix.
        var order = Comparer<PageKey>.Create((left, right) => Compare(right, left));
        var best = new PriorityQueue<int, PageKey>(Math.Min(keep, 256), order);
        int total = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!Matches(file)) continue;
            total++;
            if (keep == 0) continue;
            var key = new PageKey(i, request.Sort == SearchSort.Path ? file.Directory : null);
            if (best.Count < keep) best.Enqueue(i, key);
            else best.EnqueueDequeue(i, key);
        }
        var page = new SearchFile[Math.Max(0, best.Count - offset)];
        for (int i = page.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page[i] = files[best.Dequeue()];
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(page, total, source, skipped);

        int Compare(PageKey left, PageKey right)
        {
            var a = files[left.Index];
            var b = files[right.Index];
            int primary = request.Sort switch
            {
                SearchSort.Path => StringComparer.OrdinalIgnoreCase.Compare(left.Directory, right.Directory),
                SearchSort.Size => Nullable.Compare(a.Size, b.Size),
                SearchSort.Modified => Nullable.Compare(a.Modified, b.Modified),
                _ => a.NameSpan.CompareTo(b.NameSpan, StringComparison.OrdinalIgnoreCase)
            };
            if (primary != 0) return request.Descending ? -Math.Sign(primary) : primary;
            int path = StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
            return path != 0 ? path : left.Index.CompareTo(right.Index);
        }
    }

    private readonly record struct PageKey(int Index, string? Directory);

    private static SearchFile[] SortPage(List<SearchFile> matches, SearchRequest request, int offset, int size)
    {
        IOrderedEnumerable<SearchFile> Order<T>(Func<SearchFile, T> key, IComparer<T>? comparer = null) =>
            request.Descending ? matches.OrderByDescending(key, comparer) : matches.OrderBy(key, comparer);
        var sorted = request.Sort switch
        {
            SearchSort.Path => Order(file => file.Directory, StringComparer.OrdinalIgnoreCase),
            SearchSort.Size => Order(file => file.Size),
            SearchSort.Modified => Order(file => file.Modified),
            _ => Order(file => file.Name, StringComparer.OrdinalIgnoreCase)
        };
        return sorted.ThenBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).Skip(offset).Take(size).ToArray();
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var token = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; token.Append(c); }
            else if (!quoted && (char.IsWhiteSpace(c) || c == '|'))
            {
                if (token.Length > 0) { yield return token.ToString(); token.Clear(); }
                if (c == '|') yield return "|";
            }
            else token.Append(c);
        }
        if (quoted) throw new SearchException("引号尚未闭合，请补上右侧双引号。");
        if (token.Length > 0) yield return token.ToString();
    }

    private static SearchTerm ParseTerm(string token, bool matchPath, DateTimeOffset now, bool pinyin = false,
        bool supportsContent = false, Func<SearchFile, string, bool>? contentMatcher = null)
    {
        if (token.StartsWith('!'))
        {
            int count = 0;
            while (count < token.Length && token[count] == '!') count++;
            if (count == token.Length) throw new SearchException("“!” 后需要排除的名称或条件。");
            var inner = ParseTerm(token[count..], matchPath, now, pinyin, supportsContent, contentMatcher);
            if (count % 2 == 0) return inner;
            return new(f => !inner.Matches(f), "!<" + inner.Expression + ">", inner.Condition with { Negated = !inner.Condition.Negated });
        }
        var colon = token.IndexOf(':');
        if (colon > 1 && !token.StartsWith('"'))
        {
            var name = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..].Trim('"');
            switch (name)
            {
                case "content" when supportsContent:
                    if (string.IsNullOrEmpty(value)) throw new SearchException("请在 content: 后填写要查找的文本。");
                    return new(f => !f.IsFolder && contentMatcher?.Invoke(f, value) == true, "content:\"" + value + "\"",
                        new(SearchField.Content, "contains", value));
                case "ext":
                    var exts = value.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet();
                    if (exts.Count == 0 || exts.Any(e => !Regex.IsMatch(e, "^[a-z0-9]+$"))) throw new SearchException("扩展名请使用 ext:jpg;png 这样的格式。");
                    return new(f => !f.IsFolder && exts.Contains(f.Extension), "ext:" + string.Join(';', exts), new(SearchField.Extension, "in", exts.ToArray()));
                case "file" when value.Length == 0: return new(f => !f.IsFolder, "file:", new(SearchField.Folder, "=", false));
                case "folder" when value.Length == 0: return new(f => f.IsFolder, "folder:", new(SearchField.Folder, "=", true));
                case "size":
                    var size = Regex.Match(value, @"^(>=|<=|>|<|=)?(\d+(?:\.\d+)?)(b|kb|mb|gb)?$", RegexOptions.IgnoreCase);
                    if (!size.Success) throw new SearchException("大小条件请使用 size:>100mb，例如 size:<=2gb。");
                    if (!decimal.TryParse(size.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) throw new SearchException("大小数值过大。");
                    var unit = size.Groups[3].Value.ToLowerInvariant();
                    var factor = unit switch { "kb" => 1024L, "mb" => 1048576L, "gb" => 1073741824L, _ => 1L };
                    if (amount > long.MaxValue / factor) throw new SearchException("大小数值过大。");
                    var bytes = (long)(amount * factor);
                    var op = size.Groups[1].Value;
                    return new(f => !f.IsFolder && f.Size is { } s && (op switch { ">" => s > bytes, ">=" => s >= bytes, "<" => s < bytes, "<=" => s <= bytes, _ => s == bytes }), $"file: size:{op}{bytes}", new(SearchField.Size, op.Length == 0 ? "=" : op, bytes));
                case "dm":
                    DateTimeOffset from, to;
                    try
                    {
                        var today = new DateTimeOffset(now.Date, now.Offset);
                        if (value.Equals("today", StringComparison.OrdinalIgnoreCase)) { from = today; to = today.AddDays(1); }
                        else if (value.Equals("7days", StringComparison.OrdinalIgnoreCase)) { from = today.AddDays(-6); to = today.AddDays(1); }
                        else if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) { from = new(day, now.Offset); to = from.AddDays(1); }
                        else throw new SearchException("日期条件支持 dm:today、dm:7days 或 dm:2026-10-30。");
                    }
                    catch (ArgumentOutOfRangeException) { throw new SearchException("日期超出可查询范围，请选择其他日期。"); }
                    return new(f => f.Modified >= from && f.Modified < to, $"dm:>={from:yyyy-MM-dd} dm:<{to:yyyy-MM-dd}", new(SearchField.Modified, "range", from, to));
                default: throw new SearchException($"暂不支持“{name}:”。可用 ext:、size:、dm:、file:、folder:，或给名称加双引号。");
            }
        }
        var text = token.Replace("\"", string.Empty);
        if (text.Length == 0) throw new SearchException("引号内需要填写名称。");
        var usePath = matchPath || text.Contains('\\') || text.Contains('/');
        Regex? wildcard = text.IndexOfAny(['*', '?']) >= 0
            ? new("^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) : null;
        bool alsoPinyin = pinyin && !usePath && wildcard is null && SearchPinyin.IsQuery(text);
        bool Matches(SearchFile file)
        {
            ReadOnlySpan<char> candidate = usePath ? file.FullPath.AsSpan() : file.NameSpan;
            if (wildcard != null) return wildcard.IsMatch(candidate);
            return candidate.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                alsoPinyin && !Ascii.IsValid(candidate) && SearchPinyin.Matches(file.Name, text);
        }
        return new(Matches,
            (usePath ? "path:" : "") + "\"" + text + "\"", new(usePath ? SearchField.Path : SearchField.Name, wildcard is null ? "contains" : "glob", (object?)wildcard ?? text));
    }
}

public enum SearchField { Name, Path, Extension, Folder, Size, Modified, Content }

public sealed record SearchCondition(SearchField Field, string Operation, object Value, object? Upper = null, bool Negated = false);

public sealed record SearchTerm(Func<SearchFile, bool> Matches, string Expression, SearchCondition Condition);

public sealed class CompiledSearch(List<List<SearchTerm>> groups, List<SearchTerm> filters)
{
    public IReadOnlyList<IReadOnlyList<SearchTerm>> Groups => groups;
    public IReadOnlyList<SearchTerm> Filters => filters;
    public bool Matches(SearchFile file)
    {
        foreach (var group in groups)
            if (MatchesAll(group, file)) return MatchesAll(filters, file);
        return false;
    }

    private static bool MatchesAll(List<SearchTerm> terms, SearchFile file)
    {
        foreach (var term in terms)
            if (!term.Matches(file)) return false;
        return true;
    }
    public string EverythingExpression => string.Join(' ', new[]
    {
        groups.All(g => g.Count == 0) ? string.Empty : "<" + string.Join(" | ", groups.Select(group => string.Join(' ', group.Select(t => t.Expression)))) + ">",
        string.Join(' ', filters.Select(t => t.Expression))
    }.Where(s => s.Length > 0));
}
