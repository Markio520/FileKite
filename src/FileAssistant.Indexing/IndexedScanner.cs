using System.Diagnostics;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Indexing;

public sealed class IndexedScanner(IndexCatalog catalog, IStreamingSpaceScanner scanner)
{
    public Task<IndexInfo> BuildAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
    {
        var store = catalog.ForRoot(root);
        return scanner.ScanToAsync<IndexInfo>(root, progress, token, Save);

        IndexInfo Save(SpaceScanMetadata scan, int count, IEnumerable<SpaceEntry> rows)
        {
            var elapsed = Stopwatch.StartNew();
            long last = -500;
            var updating = new InlineProgress(written =>
            {
                if (written != count && elapsed.ElapsedMilliseconds - last < 250) return;
                last = elapsed.ElapsedMilliseconds;
                var stage = written == count ? "正在建立查询索引" : $"正在写入索引 · {written:N0} / {count:N0}";
                progress?.Report(new(scan.Files, scan.Folders, scan.Skipped, 0, scan.Elapsed + elapsed.Elapsed, []) { Stage = stage });
            });
            return store.Replace(scan, count, rows, token, updating);
        }
    }

    private sealed class InlineProgress(Action<int> update) : IProgress<int>
    {
        public void Report(int value) => update(value);
    }
}
