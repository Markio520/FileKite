namespace FileAssistant.Core.Features.Search;

public enum SearchScope { Everything, Folder, Batch }
public enum SearchKind { All, Images, Documents, Archives, Video, Audio, Folders, Files, Executables }
public enum SearchSort { Name, Path, Size, Modified }

public readonly record struct SearchDirectoryUsage(long? Bytes, bool Partial = false, bool Volume = false);

public sealed record SearchFile(string FullPath, bool IsFolder, long? Size, DateTimeOffset? Modified)
{
    public bool IsReparsePoint { get; init; }
    public bool IsHiddenOrSystem { get; init; }
    public SearchDirectoryUsage? DirectoryUsage { get; init; }
    public string Name => IsFolder && FullPath.Length == 3 && FullPath[1] == ':' && Path.EndsInDirectorySeparator(FullPath)
        ? FullPath : Path.GetFileName(FullPath.TrimEnd('\\', '/'));
    public string Directory => Path.GetDirectoryName(FullPath.TrimEnd('\\', '/')) ?? FullPath;
    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
}

public sealed record SearchRequest
{
    public string Text { get; init; } = string.Empty;
    public SearchScope Scope { get; init; }
    public string Folder { get; init; } = string.Empty;
    public bool IncludeSubfolders { get; init; } = true;
    public bool MatchPath { get; init; }
    public SearchKind Kind { get; init; }
    public long? MinimumSize { get; init; }
    public long? MaximumSize { get; init; }
    public DateTimeOffset? ModifiedFrom { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }
    public SearchSort Sort { get; init; }
    public bool Descending { get; init; }
    public int Offset { get; init; }
    public int PageSize { get; init; } = 200;
    public bool AllowPartialIndex { get; init; }
    public bool IncludeDirectoryUsage { get; init; }
    public bool PinyinSearch { get; init; }
    public bool ShowHidden { get; init; } = true;
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];
    // Null is a provider without local text search. Zero is an explicitly unlimited file size.
    public int? ContentLimitMB { get; init; }
}

public sealed record SearchPage(IReadOnlyList<SearchFile> Items, long Total, string Source, int Skipped = 0)
{
    public string? IndexGeneration { get; init; }
    public bool IsPartialIndex { get; init; }
    public bool HasMore(int loaded) => loaded < Total;
}

public sealed record SearchProgress(int Examined, string Location, string? Message = null);

/// <summary>A directory exclusion may be a component at any depth or an absolute subtree.</summary>
public sealed class SearchDirectoryFilter(IEnumerable<string> rules)
{
    private readonly HashSet<string> directories = rules.Select(rule => rule.Replace('/', '\\').TrimEnd('\\'))
        .Where(rule => rule.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public bool Excludes(string path)
    {
        if (directories.Count == 0) return false;
        var names = directories.GetAlternateLookup<ReadOnlySpan<char>>();
        var part = path.Replace('/', '\\').AsSpan().TrimEnd('\\');
        while (!part.IsEmpty)
        {
            int split = part.LastIndexOf('\\');
            if (names.Contains(part) || names.Contains(part[(split + 1)..])) return true;
            if (split < 0) break;
            part = part[..split];
        }
        return false;
    }
}

public sealed class SearchException(string message) : Exception(message);
