using System.Buffers;
using System.Text;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.Indexing;

public static class LocalSearchFiles
{
    internal const FileAttributes Hidden = FileAttributes.Hidden | FileAttributes.System;
    private const FileAttributes Unavailable = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00440000;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".tsv", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".toml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py", ".sql", ".ps1", ".srt" };

    public static SearchPage Filter(IReadOnlyList<SearchFile> files, SearchRequest request, string source, CancellationToken token)
        => SearchRules.Filter(files, request, source, cancellationToken: token, contentMatcher: ContentMatcher(request, token), visible: Visibility(request));

    public static Func<SearchFile, bool> Visibility(SearchRequest request)
    {
        if (request.ShowHidden && request.ExcludedDirectories.Count == 0) return _ => true;
        var excluded = new SearchDirectoryFilter(request.ExcludedDirectories);
        var directories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool AllowedDirectory(string path)
        {
            if (directories.TryGetValue(path, out var known)) return known;
            bool visible = !excluded.Excludes(path);
            if (visible && !request.ShowHidden)
            {
                for (var current = path; current != null; current = Path.GetDirectoryName(current))
                {
                    if (directories.TryGetValue(current, out visible)) break;
                    visible = VisibleAttributes(current);
                    if (!visible || request.Scope == SearchScope.Folder && string.Equals(current, request.Folder, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
            if (directories.Count >= 8192) directories.Clear();
            directories[path] = visible;
            return visible;
        }

        return item => item.IsFolder ? AllowedDirectory(item.FullPath) :
            (request.ShowHidden || VisibleAttributes(item.FullPath)) && AllowedDirectory(item.Directory);
    }

    private static bool VisibleAttributes(string path)
    {
        // Windows marks the drive root Hidden/System. Its contents still follow their own attributes.
        if (Path.GetPathRoot(path) == path) return true;
        try { return (File.GetAttributes(path) & Hidden) == 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public static Func<SearchFile, string, bool> ContentMatcher(SearchRequest request, CancellationToken token) =>
        (file, term) => !file.IsFolder && !file.IsReparsePoint && ContainsText(file.FullPath, term, request.ContentLimitMB, token);

    internal static bool ContainsText(string path, string term, int? limitMB, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (limitMB is null or < 0 || term.Length == 0 || !TextExtensions.Contains(Path.GetExtension(path))) return false;
        long limit = limitMB == 0 ? long.MaxValue : (long)limitMB * 1024 * 1024;
        try
        {
            if ((File.GetAttributes(path) & Unavailable) != 0) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);
            if (stream.Length > limit) return false;
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var buffer = ArrayPool<char>.Shared.Rent(8192 + term.Length);
            try
            {
                int carry = 0, read;
                while ((read = reader.Read(buffer, carry, 8192)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (stream.Position > limit) return false;
                    var text = buffer.AsSpan(0, carry + read);
                    if (text.Contains('\0')) return false;
                    if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
                    carry = Math.Min(text.Length, term.Length - 1);
                    text[^carry..].CopyTo(buffer);
                }
            }
            finally { ArrayPool<char>.Shared.Return(buffer); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }
}
