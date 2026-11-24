using FileAssistant.App.Features.SpaceAnalysis;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Tests;

public sealed class SpaceCleanupRefreshTests
{
    private sealed class Scanner : ISpaceScanner
    {
        public TaskCompletionSource<SpaceSnapshot> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public CancellationToken FirstToken { get; private set; }
        public IReadOnlyList<SpaceDrive> GetDrives() => [];
        public Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        {
            if (++Calls == 1) { FirstToken = token; token.Register(() => First.TrySetCanceled(token)); return First.Task; }
            return Task.FromResult(Snapshot(root));
        }
    }
    private sealed class Recycler : ISpaceRecycler
    {
        public void OpenRecycleBin() { }
        public Task<SpaceRecycleReport> RecycleAsync(SpaceSnapshot snapshot, IReadOnlyList<int> selected, IProgress<SpaceRecycleProgress>? progress, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Interaction : IFileInteraction
    { public void Open(SearchFile file) { } public void Reveal(SearchFile file) { } public void CopyText(string text) { } }
    private static SpaceSnapshot Snapshot(string root) => new(root,
        [new(root, -1, new(new(1, 1, 0), 1, 1, FileAttributes.Directory, 1), 0, 0, 0)], 0, 1, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);

    private sealed class LiveScanner : ILiveSpaceScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpaceSnapshot> Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<SpaceDrive> GetDrives() => [];
        public Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token) =>
            Task.FromResult(Snapshot(root));
        public Task<SpaceSnapshot> ReadCurrentAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        {
            Assert.Null(progress);
            Started.TrySetResult();
            return Updated.Task.WaitAsync(token);
        }
    }

    private sealed class PathScanner(Func<string, SpaceSnapshot> read) : ISpaceScanner
    {
        public int Calls { get; private set; }
        public IReadOnlyList<SpaceDrive> GetDrives() => [];
        public Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(read(root));
        }
    }

    [Fact]
    public async Task ExternalPathChangesRemapCurrentFolderAndRetainOnlyTheOriginalPendingFiles()
    {
        var root = Directory.CreateTempSubdirectory("FileAssistant-path-queue-").FullName;
        try
        {
            bool changed = false;
            var scanner = new PathScanner(location =>
            {
                var folder = Snapshot(location).Entries[0];
                var stamp = new SpaceStamp(new(1, 3, 0), 1, 1, FileAttributes.Normal, 1);
                var entries = new List<SpaceEntry> { folder, folder with { Name = changed ? "new" : "old", Parent = 0, Stamp = folder.Stamp with { Identity = new(1, 2, 0) } } };
                entries.Add(new("keep.txt", 1, stamp, 1, 1, 1));
                entries.Add(new("replaced.txt", 1, stamp with { Identity = new(1, changed ? 9UL : 4UL, 0) }, 1, 1, 1));
                if (!changed) entries.Add(new("removed.txt", 1, stamp with { Identity = new(1, 5, 0) }, 1, 1, 1));
                return new(location, entries, entries.Count - 2, 2, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false);
            });
            var model = new SpaceAnalysisViewModel(scanner, new Recycler(), new Interaction(), @"Q:\Other");
            await model.ScanAsync(root);
            model.Browse(1);
            model.AddToPending([2, 3, 4]);
            Assert.Equal(3, model.Pending.Count);
            await model.ApplyPathChangesAsync(removed: [@"Q:\unrelated"]);
            Assert.Equal(1, scanner.Calls);
            changed = true;
            var old = Path.Join(root, "old"); var moved = Path.Join(root, "new");
            await model.ApplyPathChangesAsync([new(old, moved, true)], [Path.Join(moved, "removed.txt")]);
            var updated = model.Snapshot!;
            Assert.Equal(moved, updated.PathOf(model.Folder));
            Assert.Equal(Path.Join(moved, "keep.txt"), updated.PathOf(Assert.Single(model.Pending)));
            await model.ApplyPathChangesAsync(removed: [root]);
            Assert.Null(model.Snapshot); Assert.Empty(model.Pending);
            Assert.Equal(2, scanner.Calls);
            await model.StopAsync();
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task LiveIndexReadKeepsTheCurrentViewUsableUntilTheCompleteReplacementIsReady()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var scanner = new LiveScanner();
        var model = new SpaceAnalysisViewModel(scanner, new Recycler(), new Interaction(), @"Q:\Other");
        model.SetIndexViewActive(true);
        await model.ScanAsync(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (model.IsQuerying) await Task.Delay(10, timeout.Token);
        var old = model.Snapshot;
        var rows = model.Result;
        var map = model.Map;
        for (int i = 0; i < 10; i++) model.IndexChanged(root);
        await scanner.Started.Task.WaitAsync(timeout.Token);
        Assert.False(model.IsBusy);
        Assert.False(model.Scanning);
        Assert.False(model.IsLoadingView);
        Assert.Same(rows, model.Result);
        Assert.Same(map, model.Map);
        var next = Snapshot(root);
        scanner.Updated.SetResult(next);
        while (ReferenceEquals(model.Snapshot, old)) await Task.Delay(10, timeout.Token);
        Assert.Same(next, model.Snapshot);
        Assert.NotNull(model.Result);
        Assert.NotNull(model.Map);
        await model.StopAsync();
    }

    [Fact] public async Task CleanupOnVolumeWaitsForCurrentScanThenRefreshesWithoutCancellingIt()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!; var scanner = new Scanner();
        var model = new SpaceAnalysisViewModel(scanner, new Recycler(), new Interaction(), @"C:\Application");
        var initial = model.ScanAsync(root); var refresh = model.RefreshAfterCleanupAsync([Path.Combine(root, "fixture", "file.txt")]);
        Assert.False(scanner.FirstToken.IsCancellationRequested); Assert.Equal(1, scanner.Calls);
        scanner.First.SetResult(Snapshot(root)); await initial; await refresh;
        Assert.Equal(2, scanner.Calls); Assert.NotNull(model.Snapshot); await model.StopAsync();
    }
    [Fact] public async Task EndingAnalysisReleasesTheSnapshotAndPreservesPendingItemsUntilRemoved()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var scanner = new Scanner(); var model = new SpaceAnalysisViewModel(scanner, new Recycler(), new Interaction(), @"Q:\Other");
        var scan = model.ScanAsync(root);
        var file = new SpaceStamp(new(2, 3, 0), 1, 1, FileAttributes.Normal, 1);
        scanner.First.SetResult(new(root, [Snapshot(root).Entries[0], new("sample", 0, file, 1, 1, 1)], 1, 1, null,
            new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false));
        await scan;
        model.AddToPending([1]);
        Assert.False(model.CanEndAnalysis);
        await model.EndAnalysisAsync();
        Assert.NotNull(model.Snapshot); Assert.Equal([1], model.Pending);
        model.RemoveFromPending([1]); await model.EndAnalysisAsync();
        Assert.Null(model.Snapshot); Assert.Null(model.Map); Assert.Null(model.Result); Assert.Empty(model.TypeTotals);
        await model.RefreshAfterCleanupAsync(); await model.StopAsync();
        Assert.Equal(1, scanner.Calls);
    }

    [Fact] public async Task ClosingCancelsQueuedRefreshAndDoesNotStartAnotherScan()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!; var scanner = new Scanner();
        var model = new SpaceAnalysisViewModel(scanner, new Recycler(), new Interaction(), @"C:\Application");
        var initial = model.ScanAsync(root); var refresh = model.RefreshAfterCleanupAsync([Path.Combine(root, "fixture")]);
        await model.StopAsync(); await initial; await refresh;
        Assert.Equal(1, scanner.Calls); Assert.True(scanner.FirstToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangingSizeMetricRequeriesListMapAndTypesWithoutScanningAgain()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var settings = new AppSettings();
        var scanner = new Scanner();
        var model = new SpaceAnalysisViewModel(scanner, new Recycler(), new Interaction(), @"Q:\Other", () => settings);
        var scan = model.ScanAsync(root);
        var file = new SpaceStamp(new(2, 3, 0), 1, 1, FileAttributes.Normal, 2);
        scanner.First.SetResult(new(root, [
            Snapshot(root).Entries[0] with { LogicalBytes = 200, AllocatedBytes = 4096, CountedBytes = 4096 },
            new("one.txt", 0, file, 100, 4096, 4096, SpaceEntryFlags.Shared),
            new("two.txt", 0, file, 100, 4096, 0, SpaceEntryFlags.Shared)
        ], 2, 1, null, new Dictionary<SpaceIssueKind, int>(), [], TimeSpan.Zero, false));
        await scan;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Check(long expected)
        {
            while (model.IsQuerying) await Task.Delay(5, timeout.Token);
            Assert.Equal(expected, model.Map!.Bytes);
            Assert.Equal(expected, model.Result!.Bytes);
            Assert.Equal(expected, Assert.Single(model.TypeTotals).Bytes);
        }
        await Check(4096);
        settings = settings with { LogicalSpaceSize = true };
        model.ApplyPreferences();
        await Check(200);
        Assert.Equal(SpaceSort.Logical, model.Sort);
        settings = settings with { LogicalSpaceSize = false };
        model.ApplyPreferences();
        await Check(4096);
        Assert.Equal(1, scanner.Calls);
        await model.StopAsync();
    }
}
