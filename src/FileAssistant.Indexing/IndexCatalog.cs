using System.Security.Cryptography;
using System.Text;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Data.Sqlite;

namespace FileAssistant.Indexing;

public sealed record IndexInfo(string Root, string Generation, DateTimeOffset Updated, int Files, int Folders,
    long CountedBytes, string Method, string Notice, SpaceVolume? Volume,
    IReadOnlyDictionary<SpaceIssueKind, int> IssueCounts, IReadOnlyList<SpaceIssue> Issues)
{
    public bool IsBuildingNames => Method == IndexStore.NameIndexMethod && Notice == IndexStore.PartialNameNotice;
    public override string ToString() => Root;
}

public enum IndexView { Directory, Search, LargeFiles }

public sealed class IndexGenerationChangedException() : InvalidOperationException("扫描位置已有新一代索引。");

public sealed record IndexRow(int Id, string Path, SpaceEntry Entry);
public sealed record MapItem(int Id, string Name, bool IsFolder, long Bytes);
public sealed record IndexMap(IReadOnlyList<MapItem> Largest, int Count, long Bytes);
public sealed record IndexPage(IReadOnlyList<IndexRow> Rows, int Total, string FolderPath, int Parent, IndexMap? Map);

/// <summary>A scope gets its own versioned database. Two volumes never share local row IDs.</summary>
public sealed class IndexCatalog(string directory)
{
    public IndexStore ForRoot(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())));
        return new IndexStore(Path.Combine(directory, key + ".sqlite3"), normalized);
    }

    public IReadOnlyList<IndexInfo> List() => List(null);

    public IReadOnlyList<IndexInfo> List(Action<string>? reportUnreadable)
    {
        if (!Directory.Exists(directory)) return [];
        var results = new List<IndexInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.sqlite3"))
        {
            try
            {
                using var db = IndexStore.Open(path, false);
                if (IndexStore.CheckVersion(db) == 0) continue; // First build was interrupted before publishing.
                if (IndexStore.ReadInfo(db, null) is { } info) results.Add(info);
            }
            catch (Exception ex) when (reportUnreadable is not null &&
                ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
            {
                reportUnreadable(ex.Message);
            }
        }
        return results.OrderByDescending(i => i.Updated).ToArray();
    }
}
