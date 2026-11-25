using System.Diagnostics;
using System.Text.Json;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.IndexBench;
using FileAssistant.Indexing;
using FileAssistant.Infrastructure.FileSystem.Ntfs;

if (args.Length > 0 && args[0] is "typing" or "typing-common" or "typing-everything")
{
    await TypingAcceptance.RunAsync(args);
    return;
}
if (args.Length >= 3 && args[0] == "compact-index")
{
    StorageAcceptance.Compact(args[1], args[2..]);
    return;
}
if (args is ["directory-scan", var directoryRoot])
{
    var watch = Stopwatch.StartNew();
    using var scanLimit = new BenchmarkMemoryLimit(256);
    var scanner = new FileAssistant.Infrastructure.FileSystem.SpaceScanner();
    var result = scanner.ScanTo(directoryRoot, null, default, (scan, count, rows) =>
    {
        long bytes = 0, lengths = 0;
        int read = 0;
        foreach (var row in rows)
        {
            read++;
            if (row.IsDirectory) continue;
            bytes += row.CountedBytes;
            lengths += Math.Max(0, row.LogicalBytes);
        }
        if (read != count || read != scan.Files + scan.Folders) throw new InvalidDataException("Incomplete scan.");
        return new { scan.Files, scan.Folders, issues = scan.IssueCounts, bytes, lengths,
            scanMs = (long)scan.Elapsed.TotalMilliseconds, totalMs = watch.ElapsedMilliseconds, peakPrivateMiB = scanLimit.PeakBytes() / 1048576.0 };
    });
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
if (args.Length > 0 && args[0] is "engine-startup" or "engine-replay" or "journal-range")
{
    await StartupAcceptance.RunAsync(args);
    return;
}
if (args.Length > 0 && args[0].StartsWith("trial-", StringComparison.Ordinal))
{
    await LiveAcceptance.RunAsync(args);
    return;
}
if (args.Length < 2) throw new ArgumentException("fixture|stream|space|folder-search|indexed <wire> [database-directory], or reopen <database-directory>");
if (args[0] == "search-preferences")
{
    var cache = new IndexCatalog(args[1]);
    var store = cache.ForRoot(Sample.Root);
    var sample = new Sample();
    using var replay = new SpaceSnapshot(Sample.Root, sample, Sample.Files, Sample.Folders, null,
        new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
    var build = Stopwatch.StartNew();
    if (!File.Exists(store.DatabasePath)) store.ReplaceLive(replay.Metadata, sample.Count, sample, default, null);
    var builtMs = build.ElapsedMilliseconds;
    var queries = new List<object>();
    foreach (var term in new[] { "", "asset-0008", "项目素材", "xmsc", "xiangmusucai", "missingname" })
    {
        var request = new SearchRequest { Text = term, PinyinSearch = true, ShowHidden = false, ExcludedDirectories = ["分组-001"] };
        for (int pass = 0; pass < 2; pass++)
        {
            var watch = Stopwatch.StartNew();
            var page = IndexedFileSearch.Query([store], request, default);
            queries.Add(new { term, pass, elapsedMs = watch.ElapsedMilliseconds, page.Total, rows = page.Items.Count });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { entries = sample.Count, builtMs,
        dbBytes = new FileInfo(store.DatabasePath).Length, queries }));
    return;
}
if (args[0] == "search-startup")
{
    if (args.Length < 3 || Directory.Exists(args[1])) throw new ArgumentException("Supply a fresh cache and read-only roots.");
    var catalog = new IndexCatalog(args[1]);
    var stores = new List<IndexStore>();
    var watch = Stopwatch.StartNew();
    foreach (var path in args.Skip(2))
    {
        var store = catalog.ForRoot(path);
        var start = watch.ElapsedMilliseconds;
        if (!store.PublishSearchNames(args[1], default)) throw new InvalidDataException("Expected an empty index.");
        stores.Add(store);
        var publish = watch.ElapsedMilliseconds;
        var page = IndexedFileSearch.Query(stores, new() { Text = "ext:txt" }, default);
        Console.WriteLine(JsonSerializer.Serialize(new { root = path, buildMs = publish - start, elapsedMs = watch.ElapsedMilliseconds,
            queryMs = watch.ElapsedMilliseconds - publish, hits = page.Total, rows = page.Items.Count }));
    }
    return;
}
if (args[0] == "reopen")
{
    var watch = Stopwatch.StartNew();
    var catalog = new IndexCatalog(args[1]);
    var info = catalog.List().Single();
    var store = catalog.ForRoot(info.Root);
    var root = store.Page(info, IndexView.Directory, 0, "", SpaceKind.All, 0, default);
    var opened = watch.ElapsedMilliseconds;
    watch.Restart();
    var result = store.Page(info, IndexView.Search, 0, "素材", SpaceKind.All, 0, default);
    var searched = watch.ElapsedMilliseconds;
    await Task.Delay(3000);
    using var current = Process.GetCurrentProcess();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        rootItems = root.Total,
        opened,
        searched,
        total = result.Total,
        idleWorkingSet = current.WorkingSet64,
        idlePrivate = current.PrivateMemorySize64
    }));
    return;
}
if (args[0] == "fixture")
{
    using var output = new BinaryWriter(new BufferedStream(File.Create(args[1]), 1024 * 1024));
    var snapshot = new SpaceSnapshot(Sample.Root, new Sample(), Sample.Files, Sample.Folders, null,
        new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false)
    { ScanMethod = "固定回放" };
    SpaceScanWire.Write(output, snapshot);
    return;
}
if (args[0] == "stream")
{
    using var stream = File.OpenRead(args[1]);
    using var destination = Console.OpenStandardOutput();
    stream.CopyTo(destination, 1024 * 1024);
    return;
}
int? budgetMiB = args.Length > 3 ? int.Parse(args[3]) : null;
using var limit = budgetMiB is { } budget ? new BenchmarkMemoryLimit(budget) : null;
var processPath = Environment.ProcessPath!;
var childStart = new ProcessStartInfo(processPath) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    childStart.ArgumentList.Add(typeof(Sample).Assembly.Location);
childStart.ArgumentList.Add("stream"); childStart.ArgumentList.Add(args[1]);
using var child = Process.Start(childStart)!;
limit?.VerifyChild(child);
using var input = new BinaryReader(new BufferedStream(child.StandardOutput.BaseStream, 128 * 1024));
using var self = Process.GetCurrentProcess();
using var stop = new CancellationTokenSource();
long maxGroupWs = 0, maxGroupPrivate = 0, maxMainWs = 0, maxChildWs = 0;
void Poll()
{
    self.Refresh();
    var ws = self.WorkingSet64; var priv = self.PrivateMemorySize64;
    long childWs = 0, childPriv = 0;
    try { child.Refresh(); if (!child.HasExited) { childWs = child.WorkingSet64; childPriv = child.PrivateMemorySize64; } }
    catch (InvalidOperationException) { }
    maxMainWs = Math.Max(maxMainWs, ws); maxChildWs = Math.Max(maxChildWs, childWs);
    maxGroupWs = Math.Max(maxGroupWs, ws + childWs); maxGroupPrivate = Math.Max(maxGroupPrivate, priv + childPriv);
}
var sampling = Task.Factory.StartNew(() => { do { Poll(); } while (!stop.Token.WaitHandle.WaitOne(15)); }, TaskCreationOptions.LongRunning);
var sw = Stopwatch.StartNew();
if (input.ReadByte() != 2) throw new InvalidDataException();
var terms = new[] { "素材", "草稿", "00157" };
long readyMs = 0, readMs = 0, largeMs = 0, bytes = 0, typeBytes = 0, dbBytes = 0;
int folderItems = 0;
var timings = new Dictionary<string, List<double>>();
var signatures = new Dictionary<string, string>();
void TimeQuery(string term, Func<(int Total, string? First)> get)
{
    var times = new List<double>();
    for (int n = 0; n < 4; n++)
    {
        var q = Stopwatch.StartNew(); var found = get(); times.Add(q.Elapsed.TotalMilliseconds);
        signatures[term] = found.Total + "|" + found.First;
    }
    timings[term] = times;
}
switch (args[0])
{
    case "space":
        {
            var snap = SpaceScanWire.Read(input); readMs = sw.ElapsedMilliseconds;
            bytes = snap.Entries[0].CountedBytes;
            var dir = SpaceQueries.List(snap, 0, SpaceView.Directory, new(), SpaceSort.Allocated, true, default);
            folderItems = dir.Indices.Length;
            _ = SpaceQueries.Map(snap, 0, default);
            typeBytes = SpaceQueries.Types(snap, default).Sum(x => x.Bytes);
            readyMs = sw.ElapsedMilliseconds;
            var q = Stopwatch.StartNew();
            _ = SpaceQueries.List(snap, 0, SpaceView.LargeFiles, new(), SpaceSort.Allocated, true, default).Indices.Take(200).ToArray();
            largeMs = q.ElapsedMilliseconds;
            GC.KeepAlive(snap);
            break;
        }
    case "folder-search":
        {
            var snap = SpaceScanWire.Read(input); readMs = sw.ElapsedMilliseconds;
            // Existing folder search keeps full SearchFile rows, then runs the shared SearchRules.
            var files = Enumerable.Range(1, snap.Entries.Count - 1).Select(snap.FileOf).ToArray();
            readyMs = sw.ElapsedMilliseconds;
            foreach (var term in terms) TimeQuery(term, () =>
            {
                var page = SearchRules.Filter(files, new SearchRequest { Text = term }, "目录快照");
                return ((int)page.Total, page.Items.FirstOrDefault()?.FullPath);
            });
            GC.KeepAlive(files);
            break;
        }
    case "indexed":
        {
            var cat = new IndexCatalog(args[2]); var store = cat.ForRoot(Sample.Root);
            var info = SpaceScanWire.Read(input, (s, n, rows) => store.Replace(s, n, rows, default), default);
            readMs = sw.ElapsedMilliseconds; bytes = info.CountedBytes;
            folderItems = store.Page(info, IndexView.Directory, 0, "", SpaceKind.All, 0, default).Total;
            typeBytes = store.Types(info, default).Sum(x => x.Bytes);
            readyMs = sw.ElapsedMilliseconds;
            var q = Stopwatch.StartNew();
            _ = store.Page(info, IndexView.LargeFiles, 0, "", SpaceKind.All, 0, default);
            largeMs = q.ElapsedMilliseconds;
            foreach (var term in terms) TimeQuery(term, () =>
            {
                var page = store.Page(info, IndexView.Search, 0, term, SpaceKind.All, 0, default);
                return (page.Total, page.Rows.FirstOrDefault()?.Path);
            });
            dbBytes = new FileInfo(store.DatabasePath).Length;
            break;
        }
    default: throw new ArgumentException("Unknown measurement mode");
}
child.WaitForExit();
if (child.ExitCode != 0) throw new IOException("Replay sender failed.");
stop.Cancel(); await sampling; Poll();
Console.WriteLine(JsonSerializer.Serialize(new
{
    mode = args[0],
    rows = Sample.Files + Sample.Folders,
    readMs,
    readyMs,
    largeMs,
    bytes,
    typeBytes,
    folderItems,
    dbBytes,
    maxMainWs,
    maxChildWs,
    maxGroupWs,
    maxGroupPrivate,
    timings,
    signatures,
    budgetMiB,
    jobPeakBytes = limit?.PeakBytes()
}));
