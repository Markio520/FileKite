using System.Diagnostics;
using System.Text.Json;
using FileAssistant.Indexing;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.IndexBench;

internal static class StorageAcceptance
{
    // Explicitly named, closed FileKite cache files only. Never scan or rebuild a volume here.
    internal static void Compact(string directory, string[] roots)
    {
        if (!Path.IsPathFullyQualified(directory) || !Path.GetFileName(directory).Equals("Indexes", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Supply an absolute path to the dedicated Indexes directory.");
        using var limit = new BenchmarkMemoryLimit(256);
        var catalog = new IndexCatalog(directory);
        var stores = roots.Select(catalog.ForRoot).ToArray();
        if (stores.Any(store => store.GetLiveState() is null))
            throw new InvalidDataException("Every requested location must already have a complete index.");
        bool inherited = IndexStorageAllocation.TryEnableCompressionForNewFiles(directory);
        foreach (var store in stores)
        {
            var before = store.GetLiveState()!;
            var sizeBefore = IndexEntryReader.Read(store.DatabasePath) ?? throw new FileNotFoundException("Missing index", store.DatabasePath);
            var clock = Stopwatch.StartNew();
            bool migrated = store.CompactStorage(default);
            double sqlSeconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            bool compressed = IndexStorageAllocation.TryCompressDatabase(store.DatabasePath);
            double ntfsSeconds = clock.Elapsed.TotalSeconds;
            var after = store.GetLiveState()!;
            var sizeAfter = IndexEntryReader.Read(store.DatabasePath) ?? throw new FileNotFoundException("Missing index", store.DatabasePath);
            if (store.NeedsStorageCompaction() || before.Identity != after.Identity || before.Cursor != after.Cursor ||
                before.Info.Generation != after.Info.Generation || before.Info.Files != after.Info.Files ||
                before.Info.Folders != after.Info.Folders || before.Info.CountedBytes != after.Info.CountedBytes)
                throw new InvalidDataException("Index state changed or a reader still holds the old snapshot.");
            Console.WriteLine(JsonSerializer.Serialize(new { before.Info.Root, before.Info.Files, before.Info.Folders,
                before.Info.Generation, before.Cursor, inherited, migrated, compressed, sqlSeconds, ntfsSeconds,
                logicalBefore = sizeBefore.LogicalBytes, logicalAfter = sizeAfter.LogicalBytes,
                allocatedBefore = sizeBefore.AllocatedBytes, allocatedAfter = sizeAfter.AllocatedBytes,
                peakPrivateMiB = limit.PeakBytes() / 1048576.0 }));
        }
    }
}
