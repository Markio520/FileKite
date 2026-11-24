using System.ComponentModel;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Persistence;

public sealed record StorageUsage(long EnginesBytes, long PreviewBytes, long RecoveryBytes, long IndexBytes, long ConfigurationBytes, long OtherBytes)
{
    public long IndexLogicalBytes { get; init; }
    // Null means no legacy files were found. A resident or shared file can occupy zero additional bytes.
    public long? LegacySearchBytes { get; init; }
}

public static class ProfileStorage
{
    public static Task<StorageUsage> MeasureAsync(string profile, CancellationToken token) => Task.Run(() =>
    {
        const int index = 3, configuration = 4, other = 5, legacy = 6;
        long[] totals = new long[7];
        long indexLogical = 0;
        bool hasLegacy = false;
        var hardLinks = new Dictionary<SpaceIdentity, (int Category, long Bytes)>();
        var root = new DirectoryInfo(profile);
        var pending = new Stack<(DirectoryInfo Folder, int Category)>();
        pending.Push((root, -1));
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                SpaceScanner.CheckAncestors(next.Folder.FullName);
                foreach (var entry in next.Folder.EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var category = next.Category >= 0 ? next.Category : entry.Name.ToLowerInvariant() switch
                    {
                        "engines" => 0, "previews" => 1, "copyrecovery" or "namingrecovery" or "cleanup-backups" => 2,
                        "indexes" when entry is DirectoryInfo => index, "search" when entry is DirectoryInfo => legacy, "collections" => configuration,
                        _ => entry is FileInfo && entry.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? configuration : other
                    };
                    if (entry is DirectoryInfo directory) pending.Push((directory, category));
                    else
                    {
                        if (category == index && !IsSqliteFile(entry.Name))
                            category = entry.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? configuration : other;
                        MeasureFile(entry, category);
                    }
                }
            }
            catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException) { /* Removed during measurement. */ }
            catch (Exception e) when (next.Category >= 0 && e is (IOException or UnauthorizedAccessException or Win32Exception)) { MarkUnknown(next.Category); }
        }
        foreach (var (category, bytes) in hardLinks.Values) AddAllocation(category, bytes);
        return new StorageUsage(totals[0], totals[1], totals[2], totals[index], totals[configuration], totals[other])
            { IndexLogicalBytes = indexLogical, LegacySearchBytes = hasLegacy ? totals[legacy] : null };

        void MeasureFile(FileSystemInfo entry, int category)
        {
            try
            {
                var file = SpaceFileMetadata.ReadForScan(entry.FullName, entry.Attributes);
                if (file.Stamp.IsReparsePoint || file.Stamp.IsDirectory) return;
                if (category == legacy) hasLegacy = true;
                if (category == index && indexLogical >= 0) indexLogical += file.Length;
                if (file.Stamp.Links > 1 && file.Stamp.Identity.IsKnown)
                {
                    // A real index owns its allocation ahead of a legacy alias. A legacy
                    // cache similarly owns its allocation ahead of an alias elsewhere.
                    if (!hardLinks.TryGetValue(file.Stamp.Identity, out var prior) || category == index ||
                        category == legacy && prior.Category != index)
                        hardLinks[file.Stamp.Identity] = (category, file.Allocation);
                }
                else AddAllocation(category, file.Allocation);
            }
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3 or 303) { /* A sidecar or cache entry expired. */ }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception) { MarkUnknown(category); }
        }

        void AddAllocation(int category, long bytes)
        {
            if (bytes < 0) totals[category] = -1;
            else if (totals[category] >= 0) totals[category] += bytes;
        }

        void MarkUnknown(int category)
        {
            totals[category] = -1;
            if (category == index) indexLogical = -1;
            if (category == legacy) hasLegacy = true;
        }
    }, token);

    private static bool IsSqliteFile(ReadOnlySpan<char> name)
    {
        if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        else if (name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
            name = name[..^8];
        var extension = Path.GetExtension(name);
        return extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase) || extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase);
    }
}
