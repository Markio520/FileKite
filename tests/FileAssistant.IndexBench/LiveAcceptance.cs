using System.Diagnostics;
using System.Text.Json;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Indexing;
using FileAssistant.Indexing.Windows;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.Windows;

namespace FileAssistant.IndexBench;

internal static class LiveAcceptance
{
    public static async Task RunAsync(string[] args)
    {
        using var limit = new BenchmarkMemoryLimit(int.Parse(args[1]));
        using var process = Process.GetCurrentProcess();
        var timer = Stopwatch.StartNew();
        if (args[0] is "trial-million" or "trial-million-space")
        {
            var store = new IndexCatalog(args[2]).ForRoot(Sample.Root);
            var generated = new SpaceSnapshot(Sample.Root, new Sample(), Sample.Files, Sample.Folders, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
            var metadata = generated.Metadata;
            int count = generated.Entries.Count;
            IEnumerable<SpaceEntry> sourceRows = generated.Entries;
            string? excluded = null;
            if (args[0] == "trial-million-space")
            {
                // Leave a real gap just before the last record through the normal
                // index-directory exclusion used on a volume that contains its own cache.
                var folder = generated.Entries[0] with { Parent = 0, CountedBytes = 0, LogicalBytes = 0, AllocatedBytes = 0 };
                sourceRows = sourceRows.Append(folder with { Name = "索引缓存", Stamp = folder.Stamp with { Identity = new(55, (ulong)count + 1, 0) } })
                    .Append(folder with { Name = "末目录", Stamp = folder.Stamp with { Identity = new(55, (ulong)count + 2, 0) } });
                excluded = Path.Join(Sample.Root, "索引缓存");
                metadata = metadata with { Folders = metadata.Folders + 2 };
                count += 2;
            }
            var info = store.ReplaceLive(metadata, count, sourceRows, default, null, excludedDirectory: excluded);
            var build = timer.ElapsedMilliseconds;
            timer.Restart();
            var samples = new List<double>();
            SearchPage? result = null;
            for (int i = 0; i < 5; i++)
            {
                timer.Restart();
                result = IndexedFileSearch.Query([store], new() { Text = "素材" }, default);
                samples.Add(timer.Elapsed.TotalMilliseconds);
            }

            if (result!.Total != 67942 || result.Items.Count != 200)
                throw new InvalidDataException("Wrong million-row result");
            var sorted = IndexedFileSearch.Query([store], new() { Text = "素材 ext:mp4", Sort = SearchSort.Size, Descending = true, Offset = 200 }, default);
            if (sorted.Items.Count != 200 || sorted.Items.Zip(sorted.Items.Skip(1)).Any(p => p.First.Size < p.Second.Size))
                throw new InvalidDataException("Wrong sorted page");
            var space = excluded is null ? null : MeasureSpace(store, info);
            process.Refresh();
            Print(new { mode = args[0], entries = info.Files + info.Folders, buildMs = build, queryMs = samples, hits = result.Total, page = result.Items.Count, info.CountedBytes, space, dbBytes = new FileInfo(store.DatabasePath).Length, peakWorkingSet = process.PeakWorkingSet64, peakGroupPrivate = limit.PeakBytes(), budgetMiB = int.Parse(args[1]) });
            return;
        }

        var roots = args.Skip(3).Select(Path.GetFullPath).ToArray();
        if (roots.Length == 0)
            throw new ArgumentException("Pass an isolated cache, then one or more read-only input scopes.");
        if (args[0] == "trial-legacy")
        {
            var exe = Environment.GetEnvironmentVariable("FILEASSISTANT_TEST_ENGINE") ?? throw new ArgumentException("Missing fixture Everything executable");
            var instance = "FileAssistantTrialCompare-" + Guid.NewGuid().ToString("N");
            await using var runtime = new EverythingRuntime(exe, args[2], instance, false, roots);
            await runtime.EnsureReadyAsync(null, default);
            var client = new EverythingSearchClient(instance);
            var requests = FixtureQueries();
            var outputs = new List<object>();
            foreach (var request in requests)
            {
                outputs.Add(await MeasureQuery(request, r => client.SearchAsync(r, default)));
            }

            process.Refresh();
            Print(new { mode = args[0], outputs, peakWorkingSet = process.PeakWorkingSet64, peakGroupPrivate = limit.PeakBytes(), budgetMiB = int.Parse(args[1]) });
            return;
        }

        await using var engine = new SharedIndexEngine(args[2], "folder-scan-only", () => roots, interval: TimeSpan.FromMinutes(10));
        var ready = await engine.SearchAsync(new() { PageSize = 1 }, [], null, default);
        var buildMs = timer.ElapsedMilliseconds;
        var rows = new List<object>();
        if (args[0] == "trial-real")
        {
            foreach (var request in FixtureQueries())
            {
                rows.Add(await MeasureQuery(request, r => engine.SearchAsync(r, [], null, default)));
            }
        }

        int directories = 0;
        long counted = 0;
        foreach (var scope in roots)
        {
            var store = new IndexCatalog(args[2]).ForRoot(scope);
            var state = store.GetLiveState() ?? throw new InvalidDataException("Root was not indexed");
            counted += state.Info.CountedBytes;
            directories += state.Info.Folders;
        }

        process.Refresh();
        Print(new { mode = args[0], roots = roots.Select(Path.GetPathRoot), items = ready.Total, buildMs, outputs = rows, folders = directories, countedBytes = counted, dbBytes = Directory.GetFiles(args[2], "*.sqlite3").Sum(p => new FileInfo(p).Length), peakWorkingSet = process.PeakWorkingSet64, peakGroupPrivate = limit.PeakBytes(), budgetMiB = int.Parse(args[1]) });
    }

    private static object MeasureSpace(IndexStore store, IndexInfo info)
    {
        var clock = Stopwatch.StartNew();
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var view = store.OpenSpace(Sample.Root, default);
        long openAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double openMs = clock.Elapsed.TotalMilliseconds;
        string last = Path.Join(Sample.Root, "末目录");
        if (view.Find(last) != view.Entries.Count - 1 || view.PathOf(view.Entries.Count - 1) != last)
            throw new InvalidDataException("Missing or misnumbered folder following the gap");
        clock.Restart();
        before = GC.GetAllocatedBytesForCurrentThread();
        var list = SpaceQueries.List(view, 0, SpaceView.LargeFiles, new(), SpaceSort.Name, false, default);
        long listAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double listMs = clock.Elapsed.TotalMilliseconds;
        if (list.Indices.Length != info.Files || list.Bytes != info.CountedBytes || list.Unknown != 0)
            throw new InvalidDataException("Wrong file count or physical size");
        return new { openMs, openAllocated, listMs, listAllocated, files = list.Indices.Length, list.Bytes };
    }

    private static async Task<object> MeasureQuery(SearchRequest request, Func<SearchRequest, Task<SearchPage>> query)
    {
        var timer = Stopwatch.StartNew();
        var sample = await query(request);
        var elapsed = timer.Elapsed.TotalMilliseconds;
        var paths = new List<string>();
        for (int offset = 0; offset < sample.Total;)
        {
            var page = await query(request with { Offset = offset, PageSize = 100 });
            if (page.Items.Count == 0 || page.Total != sample.Total)
                throw new InvalidDataException("分页总数发生变化。");
            paths.AddRange(page.Items.Select(f => f.FullPath));
            offset += page.Items.Count;
        }

        if (paths.Distinct(StringComparer.Ordinal).Count() != sample.Total)
            throw new InvalidDataException("分页包含重复项或遗漏。");
        paths.Sort(StringComparer.Ordinal);
        var signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", paths))));
        return new
        {
            request.Text,
            hits = sample.Total,
            items = sample.Items.Select(i => i.FullPath).ToArray(),
            signature,
            ms = elapsed
        };
    }

    private static SearchRequest[] FixtureQueries() => [new()
    {
        Text = "项目素材",
        Sort = SearchSort.Name,
        PageSize = 15
    }, new()
    {
        Text = "ext:png size:>=1kb",
        Sort = SearchSort.Size,
        Descending = true,
        Offset = 3,
        PageSize = 12
    }, new()
    {
        Text = "报价 | 产品",
        Sort = SearchSort.Path,
        PageSize = 12
    }, new()
    {
        Text = "folder: 目录",
        Sort = SearchSort.Name,
        PageSize = 8
    }, new()
    {
        Text = "!ext:txt dm:today",
        Sort = SearchSort.Name,
        PageSize = 8
    }

    ];
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
