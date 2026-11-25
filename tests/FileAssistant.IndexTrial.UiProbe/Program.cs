using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FileAssistant.App.Features.CreateOrRename;
using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.IndexBench;
#if LEGACY_WORKBENCH
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.FileSystem.Ntfs;
using FileAssistant.Infrastructure.Windows;
#else
using FileAssistant.Indexing.Windows;
#endif
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MainApp = FileAssistant.App.App;

namespace FileAssistant.IndexTrial.UiProbe;

internal sealed class Probe : MainApp
{
    private static readonly string[] Args = Environment.GetCommandLineArgs();
    private static readonly List<object> Samples = [];
    private static BenchmarkMemoryLimit limit = null!;
    private static void Log(string s) => File.AppendAllText(Args[1], s + Environment.NewLine);
    [STAThread]
    private static int Main()
    {
        try
        {
            using var budget = limit = new(int.Parse(Args[2]));
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new Probe();
            });
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            return 1;
        }
    }

    private static void Sample(string stage)
    {
        using var p = Process.GetCurrentProcess();
        Samples.Add(new { stage, workingSet = p.WorkingSet64, processPrivate = p.PrivateMemorySize64, peakWorkingSet = p.PeakWorkingSet64, peakGroupPrivate = limit.PeakBytes() });
    }

    private static async Task Until(Func<bool> condition, Func<string> status, int seconds = 360)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (Search.HasError)
                throw new InvalidDataException(Search.Message);
            if (Space.Message.StartsWith("无法完成操作。", StringComparison.Ordinal))
                throw new InvalidDataException(Space.Message);
            if (clock.Elapsed > TimeSpan.FromSeconds(seconds))
                throw new TimeoutException(status());
            await Task.Delay(50);
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            bool verifyChanges = Args[3] == "fixture";
            bool fixture = verifyChanges || Args[3] is "cold-fixture" or "cached-fixture";
            var roots = Args.Skip(6).ToArray();
            if (roots.Length == 0)
                throw new ArgumentException("Supply a cache, a temporary profile, and read-only roots.");
#if LEGACY_WORKBENCH
            if (verifyChanges || !fixture)
                throw new ArgumentException("The legacy comparison requires the two isolated fixture folders.");
            var instance = "FileAssistantCompare-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Args[4])))[..16];
            var engine = new EverythingRuntime(EverythingDistribution.BundledExecutable, Args[4], instance, false, roots);
            IFileSearch search = new FileSearchService(new EverythingSearchClient(instance), new DirectorySearchIndex(), engine);
            ISpaceScanner scanner = new FastSpaceScanner(Path.Join(AppContext.BaseDirectory, "FileAssistant.SpaceScan.exe"));
#else
            var helper = Args[3] == "cold-space-authorized" ? Path.Join(AppContext.BaseDirectory, "FileAssistant.SpaceScan.exe") : "no-elevated-helper";
            // For repeatable typing measurements use the exact same saved entries on both builds.
            var snapshotJournal = Args[3] == "typing" ? new SnapshotJournal(Args[4], roots) : null;
            var defaults = new FileAssistant.Core.Settings.AppSettings();
            var productDefaults = snapshotJournal is not null || Args[3] == "cold-search" && Path.GetFileName(Args[4]) == "Indexes";
            var engine = new SharedIndexEngine(Args[4], helper, () => roots, journal: snapshotJournal,
                interval: verifyChanges ? TimeSpan.FromMilliseconds(350) : TimeSpan.FromMinutes(10),
                settings: productDefaults ? () => defaults : null);
            if (Args[3] == "cold-search" && Path.GetFileName(Args[4]) == "Indexes")
                typeof(SharedIndexEngine).GetProperty("MaintainIndexStorage")?.SetValue(engine, true);
            IFileSearch search = engine;
            ISpaceScanner scanner = engine;
#endif
            try
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var app = typeof(MainApp);
#if !LEGACY_WORKBENCH
                var binaries = typeof(FileAssistant.Infrastructure.FileSystem.SpaceScanner).Assembly;
                var hasLegacy = new[]{"EverythingRuntime","EverythingSearchClient","EverythingServiceSetup"}
                    .Any(name=>binaries.GetType("FileAssistant.Infrastructure.Windows."+name)!=null);
                if (hasLegacy || File.Exists(Path.Join(AppContext.BaseDirectory, "SearchEngine", "Everything.exe")))
                    throw new InvalidDataException("Legacy dependency found");
#endif
                await (Task)app.GetMethod("InitializeWorkspaceAsync", flags)!.Invoke(this, [Args[5], search, scanner])!;
                app.GetMethod("CreateWorkspaceWindow", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
#if !LEGACY_WORKBENCH
                app.GetMethod("ObserveIndex", flags)!.Invoke(this, [engine]);
#endif
                var root = (FrameworkElement)Window.Content;
                var workspace = (WorkspacePage)((Frame)root.FindName("RootFrame")).Content;
                // Startup navigation is restored on Loaded. A synthetic click before then can be cancelled by it.
                await Until(() => workspace.IsLoaded, () => "Waiting for the workspace to load.", 20);
                void Show(string name)
                {
                    typeof(WorkspacePage).GetMethod("NavigateTo", flags)!.Invoke(workspace, [name]);
                    root.UpdateLayout();
                }

                if (new object? []
                {
                    Workspace,
                    Copy,
                    Naming,
                    Conversion,
                    Cleanup,
                    Outputs,
                    Jobs
                }.Any(s => s is null))
                    throw new InvalidDataException("Missing workspace feature");
                Sample("workspace");
#if !LEGACY_WORKBENCH
                if (Args[3] is "cold-space" or "cold-space-authorized" or "cached-space" or "cached-space-changes")
                {
                    Show("space");
                    var panel = (FrameworkElement)((ContentPresenter)workspace.FindName("SpaceHost")).Content;
                    ((TextBox)panel.FindName("ScopeBox")).Text = roots[0];
                    var changedFolder = Args[3] == "cached-space-changes" ? Directory.CreateTempSubdirectory("FileAssistant-space-probe-") : null;
                    if (changedFolder is not null)
                        for (int i = 0; i < 256; i++) File.WriteAllText(Path.Join(changedFolder.FullName, i + ".txt"), "change");
                    try
                    {
                        var watch = Stopwatch.StartNew();
                        var scan = (Task)panel.GetType().GetMethod("ScanFromInputAsync", flags)!.Invoke(panel, [false])!;
                        long combined = 0, helperPeak = 0;
                        bool helperMemoryReadable = true;
                        using var appProcess = Process.GetCurrentProcess();
                        while (!scan.IsCompleted)
                        {
                            appProcess.Refresh();
                            long workers = 0;
                            foreach (var process in Process.GetProcessesByName("FileAssistant.SpaceScan"))
                                using (process) try { workers += process.PrivateMemorySize64; } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { helperMemoryReadable = false; }
                            helperPeak = Math.Max(helperPeak, workers);
                            combined = Math.Max(combined, appProcess.PrivateMemorySize64 + workers);
                            await Task.Delay(100);
                        }
                        await scan;
                        await Until(() => Space.Snapshot != null && Space.Map != null && !Space.IsQuerying, () => Space.Message);
                        var readyMs = watch.ElapsedMilliseconds;
                        var s = Space.Snapshot!;
                        var sample = new[] { "pagefile.sys","hiberfil.sys","Recovery","System Volume Information" }
                            .Select(n => (n, id:s.Find(Path.Join(s.Root,n)))).Where(v=>v.id.HasValue)
                            .Select(v => new {v.n, allocated=s.Entries[v.id!.Value].AllocatedBytes, counted=s.Entries[v.id.Value].CountedBytes}).ToArray();
                        Sample("directoryList");
                        long? refreshedMs = null;
                        if (changedFolder is not null)
                        {
                            var witness = Path.Join(changedFolder.FullName, "255.txt");
                            await Until(() => Space.Snapshot?.Find(witness) is not null, () => Space.Message, 240);
                            refreshedMs = watch.ElapsedMilliseconds;
                        }
                        var displayed = Space.Snapshot!;
                        Log(JsonSerializer.Serialize(new {mode=Args[3],readyMs,refreshedMs,s.Files,s.Folders,s.ScanMethod,s.ScanNotice,used=s.Volume!.UsedBytes,
                            counted=displayed.Entries[0].CountedBytes,summary=((TextBlock)panel.FindName("VolumeSummary")).Text,sample,helperPeak,helperMemoryReadable,combined,samples=Samples}));
                        await Space.ReleaseIndexViewAsync();
                        await engine.DisposeAsync();
                        Log("PASS");
                    }
                    finally { changedFolder?.Delete(recursive: true); }
                    Environment.Exit(0);
                }
#endif
                if (Args[3] == "typing")
                {
#if !LEGACY_WORKBENCH
                    await engine.EnsureReadyAsync(null, default);
                    Show("search");
                    Search.ScopeIndex = 0;
                    await Until(() => !Search.IsBusy && Search.CanUseResults, () => Search.Status, 90);
                    await MeasureTypingAsync(root, workspace);
                    Search.Deactivate();
                    await engine.DisposeAsync();
                    Log("PASS");
                    Environment.Exit(0);
#endif
                }
                if (verifyChanges)
                {
                    Show("naming");
                    VerifyOutline(workspace);
                    Show("workspace");
                }
                var queryClock=Stopwatch.StartNew();
                Show("search");
                Search.ScopeIndex = 0;
                Search.QueryText = fixture ? "项目素材" : "ext:txt";
                await Until(() => Search.CanUseResults && Search.Total > 0, () => Search.Status + " " + Search.Message);
                var firstQueryMs = queryClock.ElapsedMilliseconds;
                var firstHits = Search.Total;
                Sample("firstSearchPage");
                var firstStatus = Search.Status;
                var partial = Search.GetType().GetProperty("IsPartialIndex");
                await Until(() => Search.CanUseResults && Search.Total >= 200 && partial?.GetValue(Search) is not true,
                    () => Search.Status + " " + Search.Message);
                var allVolumesMs = queryClock.ElapsedMilliseconds;
                Task<long>? spaceTiming = null;
#if !LEGACY_WORKBENCH
                if (!fixture && Args[3] != "cold-search") spaceTiming = TimeSpaceCompletionAsync(engine, queryClock);
#endif
                Sample("allVolumesReady");
                var allVolumesStatus = Search.Status;
#if !LEGACY_WORKBENCH
                if (!allVolumesStatus.Contains($"自建索引 · {roots.Length} 个位置", StringComparison.Ordinal))
                    throw new InvalidDataException($"Incomplete search coverage after {allVolumesMs} ms: {allVolumesStatus}. Peak {limit.PeakBytes()}");
#endif
                var searchView = (FrameworkElement)workspace.FindName("SearchPanel");
                var results = (ListView)searchView.FindName("ResultsList");
                root.UpdateLayout();
                if (results.Items.Count != 200)
                    throw new InvalidDataException("First native page = " + results.Items.Count);
                long searchHits = Search.Total;
#if LEGACY_WORKBENCH
                limit.VerifyChild((Process)typeof(EverythingRuntime).GetField("process", flags)!.GetValue(engine)!);
#endif
                await Search.LoadMoreCommand.ExecuteAsync(null);
                if (results.Items.Count != Math.Min(searchHits, 400))
                    throw new InvalidDataException($"Second native page = {results.Items.Count}, model rows = {Search.Results.Count}, " +
                        $"total = {Search.Total}, first = {firstQueryMs}/{firstHits}, all = {allVolumesMs}/{searchHits}, " +
                        $"canLoad = {Search.CanLoadMore}, busy = {Search.IsBusy}, status = {Search.Status}, " +
                        $"message = {Search.Message}, feedback = {Search.Feedback}, peak = {limit.PeakBytes()}");
                if (fixture && (searchHits != 240 || Search.Results.Select(r => Path.GetPathRoot(r.FullPath)).Distinct().Count() != 2))
                    throw new InvalidDataException("Cross-volume data missing");
                var indexedQueryClock=Stopwatch.StartNew();
                var repeat = new SearchRequest { Text = Search.QueryText };
#if !LEGACY_WORKBENCH
                if (!fixture) repeat = repeat with { AllowPartialIndex = true };
#endif
                var warmSearch=await search.SearchAsync(repeat,[],null,default);
                var repeatedQueryMs=indexedQueryClock.Elapsed.TotalMilliseconds;
                if(warmSearch.Items.Count!=200) throw new InvalidDataException("Warm search page is incomplete");
                object? rootCoverage = null, diskAccounting = null;
#if !LEGACY_WORKBENCH
                // The independent root-coverage check includes even normally excluded directories.
                defaults = defaults with { ShowHidden = true, ExcludedDirectories = [] };
                if (!fixture) rootCoverage = await VerifyRootCoverageAsync(search, roots);
#endif
                Sample("twoSearchPages");
#if !LEGACY_WORKBENCH
                if (Args[3] == "cold-search")
                {
                    Search.Deactivate();
                    await engine.DisposeAsync();
                    var committed = new FileAssistant.Indexing.IndexCatalog(Args[4]).List();
                    if (committed.Count != roots.Length) throw new InvalidDataException("Interrupted space scan lost a name index.");
                    Log(JsonSerializer.Serialize(new { mode = Args[3], firstQueryMs, allVolumesMs, repeatedQueryMs, searchHits,
                        indexed = committed.Sum(i => i.Files + i.Folders), rootCoverage, samples = Samples }));
                    Log("PASS");
                    Environment.Exit(0);
                }
                if (verifyChanges && partial is not null)
                {
                    var rows = Search.Results;
                    var selected = rows[rows.Count - 1];
                    results.SelectedItems.Add(selected);
                    var touched = Path.Combine(roots[0], "unrelated-" + Guid.NewGuid().ToString("N") + ".tmp");
                    int busyTransitions = 0, rowChanges = 0;
                    void BusyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
                    {
                        if (e.PropertyName == nameof(Search.IsBusy) && Search.IsBusy) busyTransitions++;
                    }
                    void RowsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => rowChanges++;
                    Search.PropertyChanged += BusyChanged;
                    rows.CollectionChanged += RowsChanged;
                    try
                    {
                        for (int i = 0; i < 12; i++) await File.AppendAllTextAsync(touched, "x");
                        await engine.SynchronizeAsync();
                        await Task.Delay(2600);
                        if (busyTransitions != 0 || rowChanges != 0 || !ReferenceEquals(Search.Results, rows) ||
                            rows.Count != searchHits || !results.SelectedItems.Contains(selected) || !Search.CanUseResults)
                            throw new InvalidDataException("Unrelated changes disturbed the native search list.");
                    }
                    finally
                    {
                        Search.PropertyChanged -= BusyChanged;
                        rows.CollectionChanged -= RowsChanged;
                        File.Delete(touched);
                    }
                    Search.SortIndex = (int)SearchSort.Size * 2;
                    await Until(() => Search.CanUseResults && Search.Results.Count == 200, () => Search.Status);
                    await Search.LoadMoreCommand.ExecuteAsync(null);
                    var ranked = Search.Results;
                    var movedRow = ranked[^1];
                    var previousLength = new FileInfo(movedRow.FullPath).Length;
                    var modified = File.GetLastWriteTimeUtc(movedRow.FullPath);
                    results.SelectedItems.Add(movedRow);
                    try
                    {
                        await SetFixtureLengthAsync(movedRow.FullPath, 1);
                        await engine.SynchronizeAsync();
                        await Until(() => ranked[0] == movedRow && movedRow.File.Size == 1, () => Search.Status, 18);
                        if (ranked.Count != 240 || !results.SelectedItems.Contains(movedRow) || Search.IsBusy)
                            throw new InvalidDataException("Updating the size sort displaced the selected file.");
                    }
                    finally
                    {
                        await SetFixtureLengthAsync(movedRow.FullPath, previousLength);
                        File.SetLastWriteTimeUtc(movedRow.FullPath, modified);
                        await engine.SynchronizeAsync();
                        Search.SortIndex = 0;
                    }
                }
#endif
                if (fixture)
                {
                    Search.UseFolder(roots[0]);
                    Search.QueryText = "项目素材";
                    await Until(() => Search.CanUseResults && Search.Total == 120, () => Search.Status + " " + Search.Message);
                    Search.IncludeSubfolders = false;
                    await Until(() => Search.CanUseResults && Search.Total == 0, () => Search.Status);
                    Search.ScopeIndex = 0;
                    Search.IncludeSubfolders = true;
                }

                Show("space");
#if !LEGACY_WORKBENCH
                if (verifyChanges)
                {
                    var panel = (FrameworkElement)((ContentPresenter)workspace.FindName("SpaceHost")).Content;
                    await SelectDriveAsync(panel, Path.GetPathRoot(roots[1])!);
                    ((TextBox)panel.FindName("ScopeBox")).Text = roots[0];
                }
#endif
                await Space.ScanAsync(roots[0]);
                await Until(() => Space.Snapshot != null && Space.Map is not null && !Space.IsQuerying, () => Space.Message);
#if !LEGACY_WORKBENCH
                if (Space.Snapshot!.Entries is not IIndexedSpaceView)
                    throw new InvalidDataException("Space analysis did not use the shared index.");
#endif
                var spaceView = (FrameworkElement)((ContentPresenter)workspace.FindName("SpaceHost")).Content;
                var spaceList = (ListView)spaceView.FindName("Rows");
                root.UpdateLayout();
                if (spaceList.Items.Count == 0)
                    throw new InvalidDataException("Native directory rows are empty.");
                int entries = Space.Snapshot!.Entries.Count, visibleRows = spaceList.Items.Count;
                long bytes = Space.Snapshot.Entries[0].CountedBytes;
                long logicalBytes = Space.Snapshot.Entries[0].LogicalBytes;
                if (fixture && logicalBytes != 606480)
                    throw new InvalidDataException("Incorrect fixture logical size: " + logicalBytes);
                Space.Show(SpaceView.Types);
                await Until(() => !Space.IsQuerying && Space.TypeTotals.Count > 0, () => Space.Message);
                int types = Space.TypeTotals.Count;
                Sample("spaceSummaryAndTypes");
                Space.Show(SpaceView.Directory);
                if (verifyChanges)
                {
                    var marker = Path.Combine(roots[0], "UI-" + Guid.NewGuid().ToString("N") + ".txt");
                    var moved = Path.Combine(roots[1],Path.GetFileName(marker));
                    try
                    {
                        await File.WriteAllTextAsync(marker, "verification");
                        await Until(() => Space.Snapshot?.Find(marker) != null, () => Space.Message);
                        Show("search");
                        Search.QueryText = Path.GetFileName(marker);
                        await Until(() => Search.CanUseResults && Search.Total == 1, () => Search.Status);
                        File.Move(marker,moved);
                        await Until(() => Search.CanUseResults && Search.Total==1 && Search.Results.Count==1 &&
                            string.Equals(Search.Results[0].FullPath,moved,StringComparison.OrdinalIgnoreCase),()=>Search.Status);
                        File.Delete(moved);
                        await Until(() => Search.CanUseResults && Search.Total == 0, () => Search.Status);
                    }
                    finally
                    {
                        if (File.Exists(marker)) File.Delete(marker);
                        if (File.Exists(moved)) File.Delete(moved);
                    }
                }

#if !LEGACY_WORKBENCH
                if (!fixture) diskAccounting = await VerifyDiskAccountingAsync(search, roots, spaceView);
#endif
                Show("workspace");
                await Space.ReleaseIndexViewAsync();
#if !LEGACY_WORKBENCH
                if (Space.Snapshot is not null)
                    throw new InvalidDataException("The hidden space view retained its snapshot");
#endif
                Search.Deactivate();
                await Space.StopAsync();
                await Task.Delay(350);
                Sample("backAtWorkspace");
                if (Window.AppWindow.IsVisible)
                    throw new InvalidOperationException("The test window must stay hidden");
                var indexed=(await search.SearchAsync(new(){PageSize=1},[],null,default)).Total;
                object? generations = null;
                bool verifiedOfflineRename = false;
#if !LEGACY_WORKBENCH
                if (fixture) generations = new FileAssistant.Indexing.IndexCatalog(Args[4]).List()
                    .Select(i => new { root = i.Root, generation = i.Generation }).ToArray();
                if (Args[3] == "cached-fixture")
                {
                    var oldName = await search.SearchAsync(new() { Text = "产品00001.png" }, [], null, default);
                    var newName = await search.SearchAsync(new() { Text = "离线改名00001.png" }, [], null, default);
                    if (oldName.Total != 0 || newName.Total != 1 ||
                        newName.Items[0].FullPath != Path.Combine(roots[0], "目录0-1", "离线改名00001.png"))
                        throw new InvalidDataException("The reopened workbench did not reconcile the offline rename.");
                    verifiedOfflineRename = true;
                }
#endif
                long? allSpaceMs = spaceTiming is null ? null : await spaceTiming;
                Log(JsonSerializer.Serialize(new { mode = Args[3], verifiedCrossVolumeMove=verifyChanges, verifiedV4Outline=verifyChanges,
                    verifiedOfflineRename, verifiedStableSearch=verifyChanges && partial is not null, scopes=roots.Length, indexed, generations,
                    firstQueryMs, firstHits, firstStatus, allVolumesMs, allVolumesStatus, repeatedQueryMs, budgetMiB = int.Parse(Args[2]),
                    searchHits, entries, visibleRows, types, bytes, logicalBytes, allSpaceMs, rootCoverage, diskAccounting, samples = Samples }));
            }
            finally
            {
                await engine.DisposeAsync();
            }

            Log("PASS");
            Environment.Exit(0);
        }
        catch (Exception error)
        {
            Log(error.ToString());
            Environment.Exit(1);
        }
    }

#if !LEGACY_WORKBENCH
    private static async Task SetFixtureLengthAsync(string path, long length)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.SetLength(length);
                return;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 20)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<long> TimeSpaceCompletionAsync(SharedIndexEngine engine, Stopwatch clock)
    {
        await engine.EnsureReadyAsync(null, default).ConfigureAwait(false);
        return clock.ElapsedMilliseconds;
    }

    private static async Task<object[]> VerifyRootCoverageAsync(IFileSearch search, string[] roots)
    {
        var checks = new List<object>();
        foreach (var root in roots)
        {
            var options = new EnumerationOptions { AttributesToSkip = 0 };
            var expected = Directory.GetDirectories(root, "*", options);
            var page = await search.SearchAsync(new() { Scope = SearchScope.Folder, Folder = root, IncludeSubfolders = false,
                Kind = SearchKind.Folders, AllowPartialIndex = true, PageSize = 10000 }, [], null, default);
            var paths = page.Items.Select(f => f.FullPath).ToHashSet(StringComparer.Ordinal);
            var stable = expected.Intersect(Directory.GetDirectories(root, "*", options), StringComparer.Ordinal).ToArray();
            var missing = stable.Except(paths, StringComparer.Ordinal).ToArray();
            if (missing.Length != 0) throw new InvalidDataException("Root directories missing from search: " + string.Join(", ", missing));
            checks.Add(new { root, expected = stable.Length, found = paths.Count, missing = missing.Length });
        }
        return checks.ToArray();
    }

    private static async Task<long> SelectDriveAsync(FrameworkElement view, string root)
    {
        var clock = Stopwatch.StartNew();
        var box = (TextBox)view.FindName("ScopeBox");
        var label = (TextBlock)view.FindName("ScopeSummary");
        box.Text = root;
        await Until(() => label.Text.StartsWith(root + " 磁盘已用"),
            () => $"Drive selection {box.Text}, header {label.Text}", 2);
        return clock.ElapsedMilliseconds;
    }

    private static async Task<object[]> VerifyDiskAccountingAsync(IFileSearch search, string[] roots, FrameworkElement view)
    {
        var checks = new List<object>();
        foreach (var root in roots)
        {
            var selectMs = await SelectDriveAsync(view, root);
            await Space.ScanAsync(root);
            await Until(() => Space.Snapshot?.Root == root && Space.Map != null && !Space.IsQuerying, () => Space.Message);
            view.UpdateLayout();
            var snapshot = Space.Snapshot!;
            var disk = snapshot.Volume!;
            var primary = ((TextBlock)view.FindName("ScopeSummary")).Text;
            var detail = ((TextBlock)view.FindName("VolumeSummary")).Text;
            if (!primary.StartsWith(root + " 磁盘已用") || !detail.Contains("已归属文件"))
                throw new InvalidDataException("Drive summaries: " + primary + " / " + detail);
            var drive = new DriveInfo(root);
            long before = drive.TotalSize - drive.TotalFreeSpace;
            var driveHit = await search.SearchAsync(new() { Text = root[..2], Kind = SearchKind.Folders, IncludeDirectoryUsage = true }, [], null, default);
            var used = driveHit.Items.Single(f => f.FullPath == root).DirectoryUsage;
            long after = drive.TotalSize - drive.TotalFreeSpace;
            if (used is not { Volume: true, Bytes: long bytes } || bytes < Math.Min(before, after) - 1048576 || bytes > Math.Max(before, after) + 1048576)
                throw new InvalidDataException("Drive search result differs from Windows.");
            var folderPage = await search.SearchAsync(new() { Scope = SearchScope.Folder, Folder = root, IncludeSubfolders = false,
                Kind = SearchKind.Folders, IncludeDirectoryUsage = true, PageSize = 10000 }, [], null, default);
            var samples = new List<object>();
            foreach (var name in new[] { "Recovery", "System Volume Information" })
            {
                var path = Path.Join(root, name);
                if (!Directory.GetDirectories(root, name, new EnumerationOptions { AttributesToSkip = 0 }).Any()) continue;
                int id = snapshot.Find(path) ?? throw new InvalidDataException("Missing folder in space analysis: " + path);
                var entry = snapshot.Entries[id];
                var row = new FileAssistant.App.Features.SpaceAnalysis.SpaceRow(snapshot, id);
                var hit = folderPage.Items.Single(f => f.FullPath == path);
                var searchRow = new FileAssistant.App.Features.Search.SearchResultItem(hit);
                bool denied = false;
                try { _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault(); } catch (UnauthorizedAccessException) { denied = true; }
                if (denied && (row.Allocated != "未知" || searchRow.SizeLabel != "占用未知" || !entry.IsPartial))
                    throw new InvalidDataException("Unreadable directory displayed a measured size: " + path);
                bool preciseAction = false;
                if (denied && drive.DriveFormat == "NTFS")
                {
                    var list = (ListView)view.FindName("Rows");
                    var visible = list.Items.OfType<FileAssistant.App.Features.SpaceAnalysis.SpaceRow>().Single(r => r.Path == path);
                    list.SelectedItems.Clear();
                    list.SelectedItems.Add(visible);
                    var button = (Button)view.FindName("VerifyVolume");
                    var stats = (TextBlock)view.FindName("InspectorStats");
                    await Until(() => button.Visibility == Visibility.Visible && stats.Text.Contains("占用空间 未知"),
                        () => $"Protected directory selection {path}: {stats.Text}, action {button.Visibility}", 2);
                    preciseAction = true;
                }
                samples.Add(new { path, denied, search = searchRow.SizeLabel, allocation = row.Allocated, note = row.Note, preciseAction });
            }
            long attributed = snapshot.Entries[0].CountedBytes;
            checks.Add(new { root, primary, detail, selectMs, windowsBefore = before, windowsAfter = after, searchUsed = used.Value.Bytes,
                spaceUsed = disk.UsedBytes, attributed, difference = disk.UsedBytes - attributed, samples });
        }
        return checks.ToArray();
    }
#endif

    private static void VerifyOutline(WorkspacePage workspace)
    {
        Naming.ModeValue = 1;
        Naming.Destination = Directory.CreateDirectory(Path.Join(Args[5], "naming")).FullName;
        var view = (NamingView)workspace.FindName("NamingPanel");
        var input = (FileInputView)view.FindName("InputView");
        if (input.AlternateContent is not NamingCreationPreviewView outline ||
            input.AlternateFolderContent is not NamingDirectoryTreeView folders ||
            outline.FindName("PromoteSelectedOutlineButton") is not Button ||
            outline.FindName("TemplateDropDownButton") is not DropDownButton ||
            folders.FindName("CreationTree") is not TreeView { CanDragItems: true })
            throw new InvalidDataException("Missing v4 outline or directory drag controls.");
        var draft = NamingViewModel.ParseOutlineText("交付\n  图片\n    封面.png\n  清单.txt");
        var moved = NamingViewModel.MoveOutlineSelectionToRoot(draft, [1, 2]);
        if (moved is null || Naming.SyncOutlineDrafts(moved).Length != 0 ||
            !Naming.ExportCurrentOutline().Select(line => (line.Indent, line.Name)).SequenceEqual(
                new[] { (0, "交付"), (1, "清单.txt"), (0, "图片"), (1, "封面.png") }))
            throw new InvalidDataException("The v4 workbench lost or misplaced a child when moving an outline selection.");
    }

#if !LEGACY_WORKBENCH
    private sealed class SnapshotJournal(string cache, string[] roots) : IVolumeJournal
    {
        private readonly Dictionary<string, JournalHead> heads = roots.ToDictionary(root => root, root =>
        {
            var cursor = new FileAssistant.Indexing.IndexCatalog(cache).ForRoot(root).GetLiveState()?.Cursor
                ?? throw new InvalidDataException("The typing probe requires a complete journal-backed index.");
            return new JournalHead(cursor.JournalId, cursor.Next, cursor.Next);
        }, StringComparer.OrdinalIgnoreCase);

        public JournalHead? Head(string root) => heads[root];
        public JournalBatch Read(string root, FileAssistant.Indexing.JournalCursor cursor, long through, CancellationToken token) =>
            throw new InvalidOperationException("The frozen cursor must never need a journal read.");
    }

    private static async Task MeasureTypingAsync(FrameworkElement root, WorkspacePage workspace)
    {
        var view = (FrameworkElement)workspace.FindName("SearchPanel");
        var box = (TextBox)view.FindName("QueryBox");
        var list = (ListView)view.FindName("ResultsList");
        string[] terms = ["n", "no", "node", "report", "报告", "ext:txt", "ext:dll", "filekite-no-such-name-7e92af21"];
        for (var pass = 0; pass < 6; pass++)
        foreach (var text in terms)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Check()
            {
                if (Search.HasError) ready.TrySetException(new InvalidDataException(Search.Message));
                else if (Search.QueryText == text && !Search.IsBusy && Search.CanUseResults) ready.TrySetResult();
            }
            void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Check();
            Search.PropertyChanged += Changed;
            try
            {
                var elapsed = Stopwatch.StartNew();
                box.Text = text;
                Check();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var readyMs = elapsed.Elapsed.TotalMilliseconds;
                root.UpdateLayout();
                var layoutMs = elapsed.Elapsed.TotalMilliseconds;
                if (list.Items.Count != Math.Min(200, Search.Total) || Search.IsPartialIndex)
                    throw new InvalidDataException("Incomplete native result list.");
                var paths = string.Join('\n', Search.Results.Select(item => item.FullPath));
                Log(JsonSerializer.Serialize(new { pass, text, readyMs, layoutMs, Search.Total, rows = list.Items.Count,
                    signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths))),
                    peakPrivateMiB = limit.PeakBytes() / 1048576.0 }));
            }
            finally { Search.PropertyChanged -= Changed; }
        }
    }
#endif
}
