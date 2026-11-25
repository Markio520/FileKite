using System.Diagnostics;
using System.Text.Json;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using FileAssistant.Indexing;
using FileAssistant.Indexing.Windows;

namespace FileAssistant.IndexBench;

internal static class StartupAcceptance
{
    public static async Task RunAsync(string[] args)
    {
        bool replay = args[0] is "engine-replay" or "journal-range";
        if (args.Length < (replay ? 4 : 3)) throw new ArgumentException("engine-startup <cache> <root> [root...] or engine-replay|journal-range <cache> <journal-heads.json> <root> [root...]");
        var cache = Path.GetFullPath(args[1]);
        var roots = args.Skip(replay ? 3 : 2).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Any(path => roots.Any(other => other.Length < path.Length && IndexStore.Within(path, other))))
            throw new ArgumentException("Pass non-overlapping roots.");
        var catalog = new IndexCatalog(cache);
        var preferences = new AppSettings();
        var journal = replay ? FreezeJournal(args[2], roots, catalog) : null;
        if (args[0] == "journal-range")
        {
            foreach (var root in roots)
            {
                var from = catalog.ForRoot(root).GetLiveState()!.Cursor!.Value;
                var head = journal!.Head(root)!.Value;
                var cursor = from;
                long records = 0;
                int reads = 0;
                while (cursor.Next < head.Next)
                {
                    var batch = journal.Read(root, cursor, head.Next, default);
                    if (batch.End.Next <= cursor.Next || batch.End.Next > head.Next) throw new InvalidDataException("Invalid journal range.");
                    cursor = batch.End;
                    records += batch.Changes.Count;
                    reads++;
                }
                Console.WriteLine(JsonSerializer.Serialize(new { root, from = from.Next, through = cursor.Next, records, reads }));
            }
            return;
        }
        using var limit = new BenchmarkMemoryLimit(256);
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        await using var engine = new SharedIndexEngine(cache, "ordinary-scan-only", () => roots, journal,
            interval: TimeSpan.FromMinutes(15), settings: () => preferences)
        { MaintainIndexStorage = Path.GetFileName(cache).Equals("Indexes", StringComparison.OrdinalIgnoreCase) };
        var ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allNames = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += root =>
        {
            lock (ready)
            {
                if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase) || ready.Contains(root)) return;
                var info = catalog.List().FirstOrDefault(item => item.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (info is null) return;
                if (info.IsBuildingNames)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { stage = "partialNames", root, ms = watch.ElapsedMilliseconds, entries = info.Files + info.Folders }));
                    return;
                }
                ready.Add(root);
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "volumeAvailable", root, ms = watch.ElapsedMilliseconds }));
                if (ready.Count == roots.Length) allNames.TrySetResult(watch.ElapsedMilliseconds);
            }
        };
        var request = new SearchRequest { AllowPartialIndex = true, IncludeDirectoryUsage = true };
        var first = await engine.SearchAsync(request, [], null, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "firstPage", ms = watch.ElapsedMilliseconds, first.Total, rows = first.Items.Count, first.IsPartialIndex }));
        var reported = await allNames.Task.WaitAsync(timeout.Token);
        var page = await engine.SearchAsync(request, [], null, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "allNames", publishedMs = reported,
            ms = watch.ElapsedMilliseconds, page.Total, rows = page.Items.Count, page.IsPartialIndex, page.Source }));
        await engine.EnsureReadyAsync(null, timeout.Token);
        var metadata = catalog.List();
        var status = engine.GetStatus();
        if (status.Ready != roots.Length || metadata.Count != roots.Length || roots.Any(root => catalog.ForRoot(root).GetLiveState() is null))
        {
            var diagnostic = await engine.SearchAsync(request, [], null, timeout.Token);
            throw new InvalidDataException($"Only {status.Ready}/{roots.Length} roots are ready. {diagnostic.Source}");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "spaceReady", ms = watch.ElapsedMilliseconds,
            files = metadata.Sum(info => (long)info.Files), directories = metadata.Sum(info => (long)info.Folders),
            peakPrivateMiB = limit.PeakBytes() / 1048576.0 }));
    }

    private static IVolumeJournal FreezeJournal(string file, string[] roots, IndexCatalog catalog)
    {
        var journal = new NtfsJournal();
        var heads = roots.ToDictionary(root => root, root => journal.Head(root) ?? throw new IOException("A readable journal is required."),
            StringComparer.OrdinalIgnoreCase);
        if (File.Exists(file))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, JournalHead>>(File.ReadAllText(file))
                ?? throw new InvalidDataException("The journal range file is empty.");
            heads = new(saved, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(output, heads);
        }
        foreach (var root in roots)
        {
            var cursor = catalog.ForRoot(root).GetLiveState()?.Cursor;
            var live = journal.Head(root);
            if (cursor is null || live is null || !heads.TryGetValue(root, out var target) ||
                !live.Value.Contains(cursor.Value) || !target.Contains(cursor.Value) || target.Next > live.Value.Next)
                throw new IOException("The saved snapshot and recorded journal range must still be readable.");
        }
        return new FixedJournal(journal, heads);
    }

    private sealed class FixedJournal(IVolumeJournal journal, Dictionary<string, JournalHead> heads) : IVolumeJournal
    {
        public JournalHead? Head(string root) => heads[root];
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token) => journal.Read(root, cursor, through, token);
    }
}
