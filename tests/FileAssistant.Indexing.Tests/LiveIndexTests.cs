using System.Runtime.InteropServices;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;
using FileAssistant.Indexing.Windows;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Indexing.Tests;

public sealed class LiveIndexTests : IDisposable
{
    private readonly string temp = Directory.CreateTempSubdirectory("FileAssistant-live-index-").FullName;
    private string? visibleFixture;
    private string Root(string name) => Directory.CreateDirectory(Path.Combine(temp, name)).FullName;
    private string Cache => Path.Combine(temp, "db");

    [Fact]
    public async Task Idle_maintenance_reclaims_a_committed_index_without_rebuilding_or_blocking_first_search()
    {
        var root = Root("documents");
        File.WriteAllText(Path.Join(root, "paper-kite.txt"), "data");
        var cache = Root("Indexes");
        await using var engine = new SharedIndexEngine(cache, "unused", () => [root], new NoJournal(), TimeSpan.FromMilliseconds(120))
            { MaintainIndexStorage = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await engine.EnsureReadyAsync(null, timeout.Token);
        var store = new IndexCatalog(cache).ForRoot(root);
        var before = store.GetLiveState()!;
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = store.DatabasePath, Pooling = false }.ToString()))
        {
            db.Open();
            using var marker = db.CreateCommand();
            marker.CommandText = "CREATE TABLE vacuum_pending(id INTEGER PRIMARY KEY)";
            marker.ExecuteNonQuery();
        }
        var request = new SearchRequest { Text = "paper-kite.txt" };
        var first = await engine.SearchAsync(request, [], null, timeout.Token);
        Assert.Single(first.Items);
        Assert.True(store.NeedsStorageCompaction());
        while (store.NeedsStorageCompaction()) await Task.Delay(200, timeout.Token);
        Assert.Equal(before.Info.Generation, store.GetLiveState()!.Info.Generation);
        var last = await engine.SearchAsync(request, [], null, timeout.Token);
        Assert.Equal(first.Items[0], Assert.Single(last.Items));
    }

    private SharedIndexEngine Engine(string[] scopes, IVolumeJournal? journal = null, TimeSpan? interval = null) => new(Cache, "unused-for-folder-scans", () => scopes, journal, interval ?? TimeSpan.FromHours(1));
    private static Task<SearchPage> Search(SharedIndexEngine engine, SearchRequest request) => engine.SearchAsync(request, [], null, default);
    [Fact]
    public async Task SearchPreferencesWorkForBootstrapCompleteFolderAndBatchWithoutRemovingSpaceEntries()
    {
        // AppData (including the ordinary TEMP directory) is itself hidden on Windows.
        var root = visibleFixture = Directory.CreateDirectory(Path.Join(AppContext.BaseDirectory, "visibility-" + Guid.NewGuid().ToString("N"))).FullName;
        string Add(string directory, string name, string text)
        {
            var path = Path.Join(Directory.CreateDirectory(Path.Join(root, directory)).FullName, name);
            File.WriteAllText(path, text);
            return path;
        }
        var visible = Add("docs", "合同报告.txt", new string('a', 8190) + "交付清单");
        var hiddenFolder = Directory.CreateDirectory(Path.Join(root, "private")).FullName;
        File.SetAttributes(hiddenFolder, File.GetAttributes(hiddenFolder) | FileAttributes.Hidden);
        Add(@"private\deep", "合同报告.txt", "交付清单");
        Add(@"NODE_MODULES\sub", "合同报告.txt", "交付清单");
        var hiddenFile = Add("docs", "隐藏.txt", "交付清单");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.System);
        Add("docs", "large.txt", new string('a', 2 * 1024 * 1024) + "交付清单");
        var modes = new SearchRequest { PinyinSearch = true, ShowHidden = false, ExcludedDirectories = ["node_modules"], ContentLimitMB = 1 };
        var store = new IndexCatalog(Cache).ForRoot(root);
        Assert.True(store.PublishSearchNames(Cache, default));
        void Verify(SearchPage page) => Assert.Equal(visible, Assert.Single(page.Items).FullPath);
        foreach (var text in new[] { "htbg", "hetongbaogao", "content:交付清单", "ext:txt htbg" })
            Verify(IndexedFileSearch.Query([store], modes with { Text = text }, default));
        var prefs = new AppSettings { PinyinSearch = true, ShowHidden = false, ExcludedDirectories = ["node_modules"], ContentLimitMB = 1 };
        await using var engine = new SharedIndexEngine(Cache, "unused", () => [root], new NoJournal(), TimeSpan.FromHours(1), () => prefs);
        await engine.EnsureReadyAsync(null, default);
        using var space = await engine.ReadCurrentAsync(root, null, default);
        Assert.NotNull(space.Find(Path.Join(root, @"NODE_MODULES\sub\合同报告.txt")));
        Assert.NotNull(space.Find(hiddenFile));
        foreach (var scope in Enum.GetValues<SearchScope>())
            foreach (var text in new[] { "htbg", "hetongbaogao", "content:交付清单" })
                Verify(await engine.SearchAsync(new() { Scope = scope, Text = text, Folder = root }, Reference(root), null, default));
        prefs = prefs with { PinyinSearch = false, ExcludedDirectories = [], ShowHidden = true, ContentLimitMB = 0 };
        Assert.Empty((await Search(engine, new() { Text = "htbg" })).Items);
        Assert.Equal(3, (await Search(engine, new() { Text = "合同报告" })).Total);
        Assert.Equal(5, (await Search(engine, new() { Text = "content:交付清单" })).Total);
    }

    [Fact]
    public async Task DeselectedVolumesStopUpdatingAndEmptySelectionSurvivesOpeningSettingsAndRestart()
    {
        var a = Root("volume-a"); var b = Root("volume-b");
        File.WriteAllText(Path.Join(a, "a.txt"), "a");
        File.WriteAllText(Path.Join(b, "b.txt"), "b");
        var prefs = new AppSettings { IndexedVolumes = [a], ExcludedDirectories = [], PrewarmIndex = false };
        SharedIndexEngine Open() => new(Cache, "unused", () => [a, b], new NoJournal(), TimeSpan.FromHours(1), () => prefs);
        await using (var engine = Open())
        {
            Assert.Equal((0L, 0, 1, false), engine.GetStatus());
            Assert.False(Directory.Exists(Cache));
            Assert.Equal("a.txt", Assert.Single((await Search(engine, new() { Text = "ext:txt" })).Items).Name);
            Assert.Equal((2L, 1, 1, false), engine.GetStatus());
            var oldGeneration = Assert.Single(new IndexCatalog(Cache).List()).Generation;
            prefs = prefs with { IndexedVolumes = [b] };
            var changingVolumes = engine.ApplyVolumeSettingsAsync();
            Assert.Equal("b.txt", Assert.Single((await Search(engine, new() { Text = "ext:txt" })).Items).Name);
            await changingVolumes;
            File.WriteAllText(Path.Join(a, "offline.txt"), "");
            using (var committed = await engine.ReadCurrentAsync(a, null, default))
                Assert.Null(committed.Find(Path.Join(a, "offline.txt")));
            await engine.SynchronizeAsync();
            Assert.Equal("b.txt", Assert.Single((await Search(engine, new() { Text = "ext:txt" })).Items).Name);
            Assert.Equal(oldGeneration, new IndexCatalog(Cache).List().Single(x => x.Root == a).Generation);
            var before = new IndexCatalog(Cache).List().Single(x => x.Root == b).Generation;
            await engine.RebuildAsync();
            Assert.NotEqual(before, new IndexCatalog(Cache).List().Single(x => x.Root == b).Generation);
            prefs = prefs with { IndexedVolumes = [] };
            await engine.ApplyVolumeSettingsAsync();
            Assert.Empty((await Search(engine, new())).Items);
            var explicitFolder = await Search(engine, new() { Scope = SearchScope.Folder, Folder = a, IncludeSubfolders = false });
            Assert.Equal(2, explicitFolder.Total);
            Assert.Equal((0L, 0, 0, false), engine.GetStatus());
        }
        await using (var again = Open())
        {
            Assert.Empty((await Search(again, new())).Items);
            prefs = prefs with { IndexedVolumes = [a] };
            await again.ApplyVolumeSettingsAsync();
            Assert.Equal(2, (await Search(again, new() { Text = "ext:txt" })).Total);
        }
    }
    private static SearchFile[] Reference(params string[] roots)
    {
        var scanner = new SpaceScanner();
        return roots.SelectMany(root =>
        {
            var snap = scanner.Scan(root, null, default);
            return Enumerable.Range(0, snap.Entries.Count).Select(snap.FileOf);
        }).ToArray();
    }

    [Fact]
    public async Task ExplicitPathNotificationsRefreshSearchAndSpaceWithoutWaitingForAWatcherTick()
    {
        var root = Root("external-changes");
        var old = Directory.CreateDirectory(Path.Join(root, "old")).FullName;
        File.WriteAllText(Path.Join(old, "keep.txt"), "first");
        var removed = Path.Join(root, "remove.txt");
        File.WriteAllText(removed, "remove");
        await using var engine = Engine([root], new NoJournal(), TimeSpan.FromHours(1));
        await engine.EnsureReadyAsync(null, default);
        var next = Path.Join(root, "new");
        Directory.Move(old, next);
        File.Delete(removed);
        var keep = Path.Join(next, "keep.txt");
        engine.InvalidatePaths([old, next, removed]);
        Assert.Equal(keep, Assert.Single((await Search(engine, new() { Text = "ext:txt" })).Items).FullPath);
        using var snapshot = await engine.RefreshPathsAsync(root, [old, next, removed], null, default);
        Assert.Null(snapshot.Find(old));
        Assert.Null(snapshot.Find(removed));
        Assert.NotNull(snapshot.Find(keep));
        Assert.Equal(keep, Assert.Single((await Search(engine, new() { Text = "ext:txt" })).Items).FullPath);
    }

    [Fact]
    public async Task StatusCountsCommittedFolderIndexesOnceWhenAVolumeIsAdded()
    {
        var root = Root("status-volume");
        var folder = Directory.CreateDirectory(Path.Join(root, "subtree")).FullName;
        File.WriteAllText(Path.Join(folder, "item.txt"), "fixture");
        var prefs = new AppSettings { IndexedVolumes = [root], ExcludedDirectories = [], PrewarmIndex = false };
        await using var engine = new SharedIndexEngine(Cache, "unused", () => [root], new NoJournal(), TimeSpan.FromHours(1), () => prefs);
        Assert.Single((await Search(engine, new() { Scope = SearchScope.Folder, Folder = folder })).Items);
        Assert.Equal((2L, 0, 1, false), engine.GetStatus());
        await engine.EnsureReadyAsync(null, default);
        Assert.Equal((3L, 1, 1, false), engine.GetStatus());
        prefs = prefs with { IndexedVolumes = [] };
        await engine.ApplyVolumeSettingsAsync();
        Assert.Equal((0L, 0, 0, false), engine.GetStatus());
    }

    [Fact]
    public async Task CancelledRebuildAndShutdownWaitForTheWriterAndPreserveTheLastGeneration()
    {
        var root = Root("maintenance");
        File.WriteAllText(Path.Join(root, "unchanged.txt"), "keep");
        using var entered = new SemaphoreSlim(0);
        using var resume = new ManualResetEventSlim();
        int pauseNext = 0;
        IReadOnlyList<string> Roots()
        {
            if (Interlocked.Exchange(ref pauseNext, 0) == 1)
            {
                entered.Release();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return [root];
        }
        await using var engine = new SharedIndexEngine(Cache, "unused", Roots, new NoJournal(), TimeSpan.FromHours(1));
        await engine.EnsureReadyAsync(null, default);
        var committed = Assert.Single(new IndexCatalog(Cache).List()).Generation;
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref pauseNext, 1);
        var rebuild = engine.RebuildAsync(cancellation.Token);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        var queuedSync = engine.SynchronizeAsync();
        Assert.False(queuedSync.IsCompleted);
        cancellation.Cancel(); resume.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rebuild);
        await queuedSync;
        Assert.Equal(committed, Assert.Single(new IndexCatalog(Cache).List()).Generation);

        resume.Reset(); Interlocked.Exchange(ref pauseNext, 1);
        var active = engine.RebuildAsync();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        var closing = engine.DisposeAsync().AsTask();
        var concurrentClose = engine.DisposeAsync().AsTask();
        Assert.Same(closing, concurrentClose);
        Assert.False(closing.IsCompleted);
        resume.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await closing;
        Assert.Equal(committed, Assert.Single(new IndexCatalog(Cache).List()).Generation);
    }

    [Fact]
    public async Task NameIndexPreservesEveryVisiblePathAndDoesNotPublishUnmeasuredSpace()
    {
        var root = Root("名称");
        var child = Directory.CreateDirectory(Path.Join(root, "隐藏")).FullName;
        File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.Hidden);
        Directory.CreateDirectory(Path.Join(child, "空"));
        // NTFS may leave this old timestamp in the parent's enumeration after a file is created below child.
        Directory.SetLastWriteTimeUtc(child, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.WriteAllText(Path.Join(child, "资料😀.TXT"), "文件");
        File.WriteAllText(Path.Join(root, "资料😀.TXT"), "different size");
        var store = new IndexCatalog(Cache).ForRoot(root);
        Assert.True(store.PublishSearchNames(Cache, default));
        Assert.Null(store.GetLiveState());
        Assert.Throws<InvalidOperationException>(() => store.OpenSpace(root, default));
        var reference = Reference(root);
        foreach (var text in new[] { "", "资料", "ext:txt", "folder:" })
            foreach (var sort in Enum.GetValues<SearchSort>())
            {
                var request = new SearchRequest { Text = text, Sort = sort };
                var expected = SearchRules.Filter(reference, request, "reference");
                var page = IndexedFileSearch.Query([store], request, default);
                Assert.Equal(expected.Total, page.Total);
                Assert.Equal(expected.Items.ToArray(), page.Items.ToArray());
            }
        var dirs = new SearchRequest { Kind = SearchKind.Folders, IncludeDirectoryUsage = true };
        Assert.All(IndexedFileSearch.Query([store], dirs, default).Items, f => Assert.Null(f.DirectoryUsage!.Value.Bytes));
        await using var engine = Engine([root], new NoJournal());
        await engine.EnsureReadyAsync(null, default);
        Assert.False(store.PublishSearchNames(Cache, default));
        using var space = store.OpenSpace(root, default);
        foreach (var item in (await Search(engine, dirs)).Items)
            Assert.Equal(space.Entries[space.Find(item.FullPath)!.Value].AllocatedBytes, item.DirectoryUsage!.Value.Bytes);
    }

    [Fact]
    public void FailedNameEnumerationLeavesThePreviousSearchGenerationReadable()
    {
        var root = Root("removable");
        File.WriteAllText(Path.Join(root, "可核对.txt"), "content");
        var store = new IndexCatalog(Cache).ForRoot(root);
        Assert.True(store.PublishSearchNames(Cache, default));
        var request = new SearchRequest { Text = "可核对" };
        var before = IndexedFileSearch.Query([store], request, default);
        Directory.Move(root, Path.Join(temp, "disconnected"));
        Assert.ThrowsAny<IOException>(() => store.PublishSearchNames(Cache, default));
        var after = IndexedFileSearch.Query([store], request, default);
        Assert.Equal(before.IndexGeneration, after.IndexGeneration);
        Assert.Equal(before.Items.Single(), after.Items.Single());
    }

    [Fact]
    public async Task RetryingFailedSpaceScanKeepsSearchAndDoesNotRepeatedlyRefreshThePage()
    {
        var root = Root("available");
        File.WriteAllText(Path.Join(root, "保留.txt"), "");
        await using var engine = Engine([root, Path.Join(temp, "missing-volume")], new UnreadableJournal());
        int notifications = 0;
        engine.Changed += _ => Interlocked.Increment(ref notifications);
        var request = new SearchRequest { Text = "保留" };
        var first = await Search(engine, request);
        Assert.True(first.IsPartialIndex);
        Assert.Contains("空间统计暂未完成", first.Source);
        int before = notifications;
        await engine.SynchronizeAsync();
        await engine.SynchronizeAsync();
        Assert.Equal(before, notifications);
        Assert.Equal("保留.txt", Assert.Single((await Search(engine, request)).Items).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleVolumeCanServeRealResultsBeforeAllItsNamesAreReady(bool rebuildFromSettings)
    {
        var root = Root("single-volume");
        for (int i = 0; i < 1200; i++) File.WriteAllText(Path.Join(root, $"item-{i:D4}.txt"), "");
        await using var engine = Engine([root], new NoJournal());
        using var resume = new ManualResetEventSlim();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += path =>
        {
            if (path == root && published.TrySetResult() && !resume.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Name scan was not released.");
        };
        var request = new SearchRequest { Text = "ext:txt" };
        var rebuild = rebuildFromSettings ? engine.RebuildAsync() : Task.CompletedTask;
        if (rebuildFromSettings) await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var complete = Search(engine, request);
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var early = await Search(engine, request with { AllowPartialIndex = true }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(early.IsPartialIndex);
            Assert.InRange(early.Total, 200, 1199);
            Assert.Equal(200, early.Items.Count);
            Assert.All(early.Items, item => Assert.True(File.Exists(item.FullPath)));
            var status = engine.GetStatus();
            Assert.Equal(0, status.Ready);
            Assert.Equal(1, status.Selected);
            Assert.True(status.Updating);
            Assert.False(complete.IsCompleted);
        }
        finally { resume.Set(); }
        await rebuild;
        var result = await complete;
        Assert.False(result.IsPartialIndex);
        Assert.Equal(1200, result.Total);
        Assert.Equal(1, engine.GetStatus().Ready);
    }

    [Fact]
    public void InterruptedNamePreviewIsReadableAndCanBeRebuiltAfterOfflineChanges()
    {
        var root = Root("interrupted");
        var sub = Directory.CreateDirectory(Path.Join(root, "before")).FullName;
        for (int i = 0; i < 1100; i++) File.WriteAllText(Path.Join(sub, $"data-{i}.txt"), "");
        var store = new IndexCatalog(Cache).ForRoot(root);
        using var stop = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => store.PublishSearchNames(Cache, stop.Token, stop.Cancel));
        var preview = IndexedFileSearch.Query([store], new() { Text = "ext:txt" }, default);
        Assert.True(preview.IsPartialIndex);
        Assert.InRange(preview.Total, 1, 1099);
        Assert.Null(store.GetLiveState());
        Assert.Throws<InvalidOperationException>(() => store.OpenSpace(root, default));

        var renamed = Path.Join(root, "after");
        Directory.Move(sub, renamed);
        File.Delete(Path.Join(renamed, "data-0.txt"));
        // Reopen through a new catalog just as a new process would.
        var reopened = new IndexCatalog(Cache).ForRoot(root);
        Assert.True(reopened.PublishSearchNames(Cache, default));
        var final = IndexedFileSearch.Query([reopened], new() { Text = "ext:txt", PageSize = 1200 }, default);
        Assert.False(final.IsPartialIndex);
        Assert.NotEqual(preview.IndexGeneration, final.IndexGeneration);
        Assert.Equal(1099, final.Total);
        Assert.All(final.Items, item => Assert.Equal(renamed, item.Directory));
        Assert.DoesNotContain(final.Items, item => item.Name == "data-0.txt");
        Assert.True(reopened.PublishSearchNames(Cache, default, () => throw new InvalidOperationException("A complete generation was replaced by a preview.")));
    }

    [Fact]
    public async Task InteractiveSearchCanUseOneCommittedVolumeWhileFullSearchWaits()
    {
        var first = Root("first");
        var other = Root("other");
        File.WriteAllText(Path.Join(first, "a.txt"), "a");
        File.WriteAllText(Path.Join(other, "b.txt"), "b");
        await using var engine = Engine([first, other], new NoJournal());
        using var release = new ManualResetEventSlim();
        engine.Changed += path =>
        {
            if (path == first && !release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Second volume was not released.");
        };
        var query = new SearchRequest { Text = "ext:txt" };
        var complete = Search(engine, query);
        try
        {
            var early = await Search(engine, query with { AllowPartialIndex = true }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(early.IsPartialIndex);
            Assert.Equal("a.txt", Assert.Single(early.Items).Name);
            Assert.False(complete.IsCompleted);
        }
        finally { release.Set(); }
        var all = await complete;
        Assert.False(all.IsPartialIndex);
        Assert.Equal(2, all.Total);
    }

    [Fact]
    public async Task StoredNameOrderMatchesOrdinalIgnoreCaseForAsciiAndUnicode()
    {
        var root = Root("order");
        string[] names = ["a", "A", "Z", "z", "_", "[", "`", "ß", "ſ", "K", "İ", "ı", "ς", "Σ", "中", "😀", "𐐀", "𐐨", "é", "é"];
        for (int i = 0; i < names.Length; i++)
        {
            var dir = Directory.CreateDirectory(Path.Join(root, i.ToString())).FullName;
            foreach (var prefix in new[] { "", "mixedPrefix_", "中文A", "wide😀" })
                File.WriteAllText(Path.Join(dir, prefix + names[i] + ".txt"), "");
        }
        await using var engine = Engine([root]);
        var request = new SearchRequest { Text = "ext:txt", PageSize = 200 };
        var expected = SearchRules.Filter(Reference(root), request, "reference");
        Assert.Equal(expected.Items, (await Search(engine, request)).Items);
    }

    [Fact]
    public async Task AllFiltersAndSortsProduceOneGloballyOrderedPageAcrossScopes()
    {
        var a = Root("first");
        var b = Root("second");
        foreach (var root in new[]
        {
            a,
            b
        }

        )
        {
            var child = Directory.CreateDirectory(Path.Join(root, "子目录")).FullName;
            File.WriteAllBytes(Path.Join(root, "产品 图.png"), new byte[3000]);
            File.WriteAllBytes(Path.Join(root, "报价.pdf"), new byte[4000]);
            File.WriteAllText(Path.Join(child, "报价.txt"), "文件");
            File.WriteAllText(Path.Join(child, "图标[02].JPG"), "例");
            File.WriteAllText(Path.Join(child, "😀.png"), "例");
        }

        await using var engine = Engine([b, a, Path.Join(a, "子目录")]);
        var all = Reference(a, b);
        var conditions = new[]
        {
            new SearchRequest(),
            new()
            {
                Text = "产品 | 报价 !ext:pdf"
            },
            new()
            {
                Text = "ext:jpg;png !size:>1kb"
            },
            new()
            {
                Text = "dm:today folder:"
            },
            new()
            {
                Text = "!dm:7days | file: size:>=3kb"
            },
            new()
            {
                Text = "图标[02].*"
            },
            new()
            {
                Text = "\"产品 图\""
            },
            new()
            {
                Text = "子目录",
                MatchPath = true
            },
            new()
            {
                Text = "??.png"
            },
            new()
            {
                Kind = SearchKind.Documents,
                MinimumSize = 0,
                MaximumSize = 4000,
                ModifiedFrom = DateTimeOffset.Now.AddDays(-1),
                ModifiedBefore = DateTimeOffset.Now.AddDays(1)
            }
        };
        foreach (var condition in conditions)
            foreach (var order in Enum.GetValues<SearchSort>())
                foreach (var descending in new[]
                {
                    false,
                    true
                }

                )
                {
                    var req = condition with
                    {
                        Sort = order,
                        Descending = descending,
                        Offset = 1,
                        PageSize = 3
                    };
                    var expected = SearchRules.Filter(all, req, "reference");
                    var actual = await Search(engine, req);
                    Assert.Equal(expected.Total, actual.Total);
                    Assert.Equal(expected.Items.ToArray(), actual.Items.ToArray());
                }

        await Assert.ThrowsAsync<SearchException>(() => Search(engine, new() { Text = "bad:option" }));
    }

    [JournalFact]
    public async Task NativeJournalReplaysMovesWritesLinksAndOfflineChangesWithoutRebuilding()
    {
        var root = Root("volume-fixture");
        var left = Directory.CreateDirectory(Path.Join(root, "甲")).FullName;
        var right = Directory.CreateDirectory(Path.Join(root, "乙")).FullName;
        var first = Path.Join(left, "原片.mp4");
        File.WriteAllBytes(first, new byte[20000]);
        var n = new NtfsJournal();
        Assert.NotNull(n.Head(root));
        var measured = new CountingJournal(n);
        await using (var engine = Engine([root], measured))
        {
            Assert.Equal(1, (await Search(engine, new() { Text = "原片" })).Total);
            var moved = Path.Join(right, "成品.mp4");
            File.Move(first, moved);
            File.WriteAllBytes(moved, new byte[90000]);
            var alias = Path.Join(left, "副本.mp4");
            Assert.True(CreateHardLinkW(alias, moved, 0));
            await engine.SynchronizeAsync();
            Assert.Equal(0, (await Search(engine, new() { Text = "原片" })).Total);
            var hits = await Search(engine, new() { Text = "ext:mp4", Sort = SearchSort.Path });
            Assert.Equal(2, hits.Total);
            Assert.All(hits.Items, f => Assert.Equal(90000, f.Size));
            using var snap = await engine.ReadCurrentAsync(root, null, default);
            var reference = new SpaceScanner().Scan(root, null, default);
            Assert.Equal(reference.Entries[0].CountedBytes, snap.Entries[0].CountedBytes);
            Assert.Equal(reference.Entries[0].LogicalBytes, snap.Entries[0].LogicalBytes);
            Assert.Equal(reference.Entries.Select(x => x.Stamp.Identity).OrderBy(i => i.Low).ToArray(), snap.Entries.Select(x => x.Stamp.Identity).OrderBy(i => i.Low).ToArray());
            Assert.Equal(SpaceQueries.Types(reference, default), SpaceQueries.Types(snap, default));
            Directory.Move(right, Path.Join(root, "改名目录"));
            File.Delete(alias);
            await engine.SynchronizeAsync();
            var last = Assert.Single((await Search(engine, new() { Text = "ext:mp4" })).Items);
            Assert.Contains("改名目录", last.FullPath);
            using var changed = await engine.ReadCurrentAsync(root, null, default);
            Assert.Equal(0, changed.Entries[changed.Find(left)!.Value].CountedBytes);
            Assert.True(changed.Entries[changed.Find(Path.Join(root, "改名目录"))!.Value].CountedBytes > 0);
        }

        File.WriteAllText(Path.Join(root, "关机期间.txt"), "catch up");
        File.Move(Path.Join(root,"改名目录","成品.mp4"),Path.Join(root,"离线更名.mp4"));
        var saved = new IndexCatalog(Cache).ForRoot(root).GetLiveState()!;
        await using var restarted = Engine([root], measured);
        Assert.Equal(1, (await Search(restarted, new() { Text = "关机期间" })).Total);
        Assert.Equal(1,(await Search(restarted,new(){Text="离线更名"})).Total);
        Assert.Equal(0,(await Search(restarted,new(){Text="成品.mp4"})).Total);
        Assert.True(measured.Records > 0);
        Assert.True(new IndexCatalog(Cache).ForRoot(root).GetLiveState()!.Cursor!.Value.Next > saved.Cursor!.Value.Next);
    }

    [Fact]
    public async Task RepeatedJournalWritesKeepBothMoveNamesAndTheHardLinkReason()
    {
        var root = Root("journal-burst");
        var left = Directory.CreateDirectory(Path.Join(root, "left")).FullName;
        var right = Directory.CreateDirectory(Path.Join(root, "right")).FullName;
        var anchor = Path.Join(left, "anchor.txt");
        var before = Path.Join(left, "before.txt");
        File.WriteAllText(anchor, "alias");
        File.WriteAllText(before, "move");
        var journal = new BurstJournal();
        await using var engine = Engine([root], journal);
        await engine.EnsureReadyAsync(null, default);
        var from = IndexEntryReader.Read(left)!.Value.Stamp.Identity;
        var to = IndexEntryReader.Read(right)!.Value.Stamp.Identity;
        var file = IndexEntryReader.Read(before)!.Value.Stamp.Identity;
        var shared = IndexEntryReader.Read(anchor)!.Value.Stamp.Identity;

        var moved = Path.Join(right, "after.txt");
        var alias = Path.Join(right, "alias.txt");
        File.Move(before, moved);
        Assert.True(CreateHardLinkW(alias, anchor, 0));
        var writes = new JournalChange(from.Low, from.High, shared.Low, shared.High, "anchor.txt", 1);
        journal.Events.Add(writes with { Reason = 0x10000 }); // The new alias is reported against the existing name.
        journal.Events.Add(new(from.Low, from.High, file.Low, file.High, "before.txt", 0x1000));
        journal.Events.AddRange(Enumerable.Repeat(writes, 4096));
        journal.Events.Add(new(to.Low, to.High, file.Low, file.High, "after.txt", 0x2000));

        await engine.SynchronizeAsync();
        var result = await Search(engine, new() { Text = "ext:txt" });
        Assert.Equal(new[] { anchor, moved, alias }.Order(StringComparer.Ordinal),
            result.Items.Select(hit => hit.FullPath).Order(StringComparer.Ordinal));
        Assert.Equal(journal.Events.Count + 1, new IndexCatalog(Cache).ForRoot(root).GetLiveState()!.Cursor!.Value.Next);
    }

    private sealed class BurstJournal : IVolumeJournal
    {
        public List<JournalChange> Events { get; } = [];
        public JournalHead? Head(string root) => new(1, 1, 1 + Events.Count);
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token)
        {
            var count = (int)Math.Min(512, through - cursor.Next);
            return new(new(1, cursor.Next + count), Events.GetRange((int)cursor.Next - 1, count));
        }
    }

    [Fact]
    public async Task MissingJournalOrWatcherOverflowCanRecoverAndRestartWithoutReturningOldNames()
    {
        var root = Root("fallback");
        var item = Path.Join(root, "a.txt");
        File.WriteAllText(item, "a");
        await using (var engine = Engine([root], new NoJournal(), TimeSpan.FromMilliseconds(100)))
        {
            Assert.Equal(1, (await Search(engine, new() { Text = "a.txt" })).Total);
            File.Move(item, Path.Join(root, "b.txt"));
            using var until = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while ((await Search(engine, new() { Text = "b.txt" })).Total != 1)
            {
                await Task.Delay(60, until.Token);
            }

            Assert.Equal(0, (await Search(engine, new() { Text = "a.txt" })).Total);
        }

        File.Delete(Path.Join(root, "b.txt"));
        await using var restarted = Engine([root], new NoJournal());
        Assert.Equal(0, (await Search(restarted, new() { Text = "b.txt" })).Total);
    }

    [Fact]
    public async Task JournalGapRebuildsOnlyAffectedScopeAndCancellationIsRetryable()
    {
        var a = Root("a");
        var b = Root("b");
        File.WriteAllText(Path.Join(a, "original"), "a");
        var journal = new FakeJournal(a);
        await using var engine = Engine([a, b], journal);
        Assert.Equal(1, (await Search(engine, new() { Text = "original" })).Total);
        var unchanged = new IndexCatalog(Cache).ForRoot(b).GetLiveState()!.Info.Generation;
        File.Move(Path.Join(a, "original"), Path.Join(a, "renamed"));
        journal.Reset = true;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.SynchronizeAsync(cancel.Token));
        await engine.SynchronizeAsync();
        Assert.Equal(1, (await Search(engine, new() { Text = "renamed" })).Total);
        Assert.Equal(0, (await Search(engine, new() { Text = "original" })).Total);
        Assert.Equal(unchanged, new IndexCatalog(Cache).ForRoot(b).GetLiveState()!.Info.Generation);
    }

    [Fact]
    public void JournalParserRejectsMalformedRecordsAndSupports128BitIds()
    {
        var bytes = new byte[88];
        BitConverter.GetBytes(35L).CopyTo(bytes, 0);
        BitConverter.GetBytes(80).CopyTo(bytes, 8);
        bytes[12] = 3;
        BitConverter.GetBytes(7L).CopyTo(bytes, 16);
        BitConverter.GetBytes(99L).CopyTo(bytes, 24);
        BitConverter.GetBytes(4L).CopyTo(bytes, 32);
        BitConverter.GetBytes(32L).CopyTo(bytes, 40);
        BitConverter.GetBytes(30L).CopyTo(bytes, 48);
        bytes[64] = 1;
        bytes[80] = 2;
        bytes[82] = 76;
        bytes[84] = 65;
        var change = Assert.Single(NtfsJournal.Decode(new(9, 28), 35, bytes).Changes);
        Assert.Equal(new JournalChange(4, 32, 7, 99, "A", 1), change);
        bytes[82] = 79;
        Assert.Throws<InvalidDataException>(() => NtfsJournal.Decode(new(9, 28), 35, bytes));
    }

    [Fact]
    public async Task DiskBackedViewsMatchSpaceQueriesAndHoldTheirGenerationDuringWrites()
    {
        var root = Root("空间");
        var child = Directory.CreateDirectory(Path.Join(root, "子目录")).FullName;
        for (int i = 0; i < 44; i++)
            File.WriteAllBytes(Path.Combine(child, $"资源{i:D2}." + (i % 3 == 0 ? "png" : "mp4")), new byte[4097 + i * 189]);
        File.WriteAllText(Path.Combine(root, "在外层.txt"), "内容");
        await using var engine = Engine([root]);
        await engine.EnsureReadyAsync(null, default);
        using var view = await engine.ReadCurrentAsync(child, null, default);
        var reference = new SpaceScanner().Scan(child, null, default);
        Assert.IsAssignableFrom<IIndexedSpaceView>(view.Entries);
        Assert.Equal(reference.Files, view.Files);
        Assert.Equal(reference.Folders, view.Folders);
        Assert.Equal(reference.Children(0).Select(reference.PathOf).Order(), view.Children(0).Select(view.PathOf).Order());
        foreach (var logical in new[] { false, true })
        {
            Assert.Equal(SpaceQueries.Types(reference, default, logical), SpaceQueries.Types(view, default, logical));
            var expectedMap = SpaceQueries.Map(reference, 0, default, logical);
            var map = SpaceQueries.Map(view, 0, default, logical);
            Assert.Equal(expectedMap.Count, map.Count);
            Assert.Equal(expectedMap.Bytes, map.Bytes);
            Assert.Equal(expectedMap.Largest.Select(x => (reference.PathOf(x.Id), x.Bytes)), map.Largest.Select(x => (view.PathOf(x.Id), x.Bytes)));
            foreach (var mode in new[] { SpaceView.Directory, SpaceView.LargeFiles })
            foreach (var sort in Enum.GetValues<SpaceSort>())
            foreach (var descending in new[] { false, true })
            {
                var filter = new SpaceFilter(SpaceKind.Image, 1, 30000, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
                var expected = SpaceQueries.List(reference, 0, mode, filter, sort, descending, default, logical);
                var found = SpaceQueries.List(view, 0, mode, filter, sort, descending, default, logical);
                Assert.Equal(expected.Bytes, found.Bytes);
                Assert.Equal(expected.Unknown, found.Unknown);
                Assert.Equal(expected.Indices.Select(reference.PathOf), found.Indices.Select(view.PathOf));
            }
        }

        var old = Path.Join(child, "资源00.png");
        File.Move(old, Path.Join(child, "新名称.png"));
        await engine.SynchronizeAsync();
        Assert.NotNull(view.Find(old));
        using var current = await engine.ReadCurrentAsync(child, null, default);
        Assert.Null(current.Find(old));
        Assert.NotNull(current.Find(Path.Join(child, "新名称.png")));
        Assert.Null(current.Find(Path.Join(root, "在外层.txt")));
    }

    [Fact]
    public async Task ANewWatchedSubtreeAndItsQueuedChildrenProduceExactlyOneRowPerName()
    {
        var root = Root("fallback-nested");
        await using var engine = Engine([root], new NoJournal(), TimeSpan.FromMilliseconds(160));
        await engine.EnsureReadyAsync(null, default);
        var parent = Directory.CreateDirectory(Path.Join(root, "nested")).FullName;
        var left = Directory.CreateDirectory(Path.Join(parent, "a")).FullName;
        var right = Directory.CreateDirectory(Path.Join(parent, "b")).FullName;
        var first = Path.Join(left, "同名.mp4");
        var alias = Path.Join(right, "同名.mp4");
        File.WriteAllBytes(first, new byte[3000]);
        Assert.True(CreateHardLinkW(alias, first, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<SearchPage> Until(int count, long? length = null)
        {
            while (true)
            {
                var page = await Search(engine, new() { Text = "同名" });
                if (page.Total == count && page.Items.All(f => !length.HasValue || f.Size == length))
                    return page;
                await Task.Delay(50, timeout.Token);
            }
        }

        var both = await Until(2, 3000);
        Assert.Equal(new[] { first, alias }, both.Items.Select(f => f.FullPath));
        var second = await Search(engine, new() { Text = "同名", Offset = 1, PageSize = 1 });
        Assert.Equal(alias, Assert.Single(second.Items).FullPath);
        File.WriteAllBytes(alias, new byte[13000]);
        await Until(2, 13000);
        File.Delete(first);
        Assert.Equal(alias, Assert.Single((await Until(1)).Items).FullPath);
        using var current = await engine.ReadCurrentAsync(parent, null, default);
        Assert.Equal(1, current.Files);
        Assert.Equal(new SpaceScanner().Scan(parent, null, default).Entries[0].CountedBytes, current.Entries[0].CountedBytes);
    }

    [Fact]
    public async Task ExplicitFolderRefreshReconcilesOnlyThatBranchOfAnExistingVolume()
    {
        var root = Root("scope");
        var branch = Directory.CreateDirectory(Path.Join(root, "当前")).FullName;
        var other = Directory.CreateDirectory(Path.Join(root, "其余")).FullName;
        await using var engine = Engine([root], new FakeJournal("no-changes"));
        await engine.EnsureReadyAsync(null, default);
        var file = Path.Join(branch, "手工刷新.txt");
        File.WriteAllText(file, "a");
        File.WriteAllText(Path.Join(other, "未请求刷新.txt"), "b");
        using var view = await engine.RescanAsync(branch, null, default);
        Assert.NotNull(view.Find(file));
        Assert.Equal(1, (await Search(engine, new() { Text = "手工刷新" })).Total);
        Assert.Equal(0, (await Search(engine, new() { Text = "未请求刷新" })).Total);
        Assert.Single(new IndexCatalog(Cache).List());
    }

    [Fact]
    public void PrecisionInputExcludesTheLiveIndexFilesFromAllPublishedTotals()
    {
        var root = Root("全部文件");
        var internalDir = Directory.CreateDirectory(Path.Join(root, "Indexes")).FullName;
        File.WriteAllBytes(Path.Join(internalDir, "不要收录.sqlite3"), new byte[16000]);
        var actual = Path.Join(root, "资源.png");
        File.WriteAllBytes(actual, new byte[6200]);
        var scan = new SpaceScanner().Scan(root, null, default);
        var store = new IndexCatalog(Cache).ForRoot(root);
        var published = store.ReplaceLive(scan.Metadata, scan.Entries.Count, scan.Entries, default, null, excludedDirectory: internalDir);
        Assert.Equal(1, published.Files);
        Assert.Equal(1, published.Folders);
        Assert.Equal(0, IndexedFileSearch.Query([store], new() { Text = "不要收录" }, default).Total);
        using var view = store.OpenSpace(root, default);
        Assert.Equal(2, view.Entries.Count);
        Assert.Equal(view.Entries[view.Find(actual)!.Value].AllocatedBytes, view.Entries[0].CountedBytes);
        Assert.Single(SpaceQueries.Types(view, default));
    }

    [Fact]
    public async Task FolderSearchIndexesRecursiveContentsOnceAndReadsOnlyOneLevelWhenRequested()
    {
        var root = Root("数据集");
        var folder = Directory.CreateDirectory(Path.Combine(root, "子")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(folder, "更深")).FullName;
        File.WriteAllText(Path.Combine(root, "文件.bin"), "root");
        File.WriteAllText(Path.Combine(folder, "文件A.bin"), "abc");
        File.WriteAllText(Path.Combine(child, "文件B.bin"), "abcd");
        var direct = new SearchRequest
        {
            Scope = SearchScope.Folder,
            Folder = root,
            IncludeSubfolders = false
        };
        await using var engine = Engine([root]);
        var early = await Search(engine, direct);
        Assert.Equal(2, early.Total);
        Assert.Empty(new IndexCatalog(Cache).List());
        Assert.Equal("当前目录", early.Source);
        await engine.EnsureReadyAsync(null, default);
        var reference = Reference(root);
        foreach (var scope in new[]
        {
            root,
            folder
        }

        )
            foreach (var recursive in new[]
            {
                false,
                true
            }

            )
                foreach (var sort in Enum.GetValues<SearchSort>())
                {
                    var request = direct with
                    {
                        Folder = scope,
                        IncludeSubfolders = recursive,
                        Sort = sort,
                        Offset = 1,
                        PageSize = 2
                    };
                    var eligible = reference.Where(f => f.FullPath != scope && (recursive ? IndexStore.Within(f.FullPath, scope) : f.Directory == scope)).ToArray();
                    var expected = SearchRules.Filter(eligible, request, "reference");
                    var indexed = await Search(engine, request);
                    Assert.Equal(expected.Total, indexed.Total);
                    Assert.Equal(expected.Items.ToArray(), indexed.Items.ToArray());
                    Assert.NotNull(indexed.IndexGeneration);
                }

        var added = Path.Combine(child, "新增.bin");
        File.WriteAllText(added, "change");
        await engine.SynchronizeAsync();
        var changed = await Search(engine, direct with { Folder = folder, IncludeSubfolders = true, Text = "新增" });
        Assert.Equal(added, Assert.Single(changed.Items).FullPath);
        Assert.Single(new IndexCatalog(Cache).List());
    }

    [Fact]
    public async Task ARedactedCreateCanReplaceAnAlreadyIndexedFileWithTheSameName()
    {
        var root = Root("轮换");
        var path = Path.Combine(root, "LOG.old");
        File.WriteAllText(path, "old");
        var journal = new ReplacementJournal();
        await using var engine = Engine([root], journal);
        Assert.Equal(3, Assert.Single((await Search(engine, new() { Text = "LOG.old" })).Items).Size);
        var replacement = Path.Combine(temp, "incoming");
        File.WriteAllBytes(replacement, new byte[2981]);
        File.Move(replacement, path, true);
        var parent = IndexEntryReader.Read(root)!.Value.Stamp.Identity;
        var item = IndexEntryReader.Read(path)!.Value.Stamp.Identity;
        journal.Change = new(parent.Low, parent.High, item.Low, item.High, "", 0x100);
        await engine.SynchronizeAsync();
        Assert.Equal(2981, Assert.Single((await Search(engine, new() { Text = "LOG.old" })).Items).Size);
        using var view = await engine.ReadCurrentAsync(root, null, default);
        Assert.Equal(1, view.Files);
        Assert.Equal(item, view.Entries[view.Find(path)!.Value].Stamp.Identity);
    }

    private sealed class ReplacementJournal : IVolumeJournal
    {
        public JournalChange? Change;
        public JournalHead? Head(string root) => new(1, 10, Change.HasValue ? 11 : 10);
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token) => new(new(cursor.JournalId, through), Change is { } change ? [change] : []);
    }

    private sealed class JournalFactAttribute : FactAttribute
    {
        public JournalFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || new NtfsJournal().Head(Path.GetTempPath()) is null)
                Skip = "The test volume must expose a readable NTFS change journal.";
        }
    }

    private sealed class CountingJournal(IVolumeJournal inner) : IVolumeJournal
    {
        public int Records;
        public JournalHead? Head(string root) => inner.Head(root);
        public JournalBatch Read(string root, JournalCursor c, long end, CancellationToken token)
        {
            var batch = inner.Read(root, c, end, token);
            Records += batch.Changes.Count;
            return batch;
        }
    }

    private sealed class UnreadableJournal : IVolumeJournal
    {
        public JournalHead? Head(string root) => throw new IOException("metadata unavailable");
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class NoJournal : IVolumeJournal
    {
        public JournalHead? Head(string root) => null;
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class FakeJournal(string first) : IVolumeJournal
    {
        public bool Reset;
        public JournalHead? Head(string root) => root == first && Reset ? new(2, 100, 101) : new(1, 10, 12);
        public JournalBatch Read(string root, JournalCursor cursor, long through, CancellationToken token) => new(new(cursor.JournalId, through), []);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, nint unused);
    public void Dispose()
    {
        Directory.Delete(temp, true);
        if (visibleFixture is not null) Directory.Delete(visibleFixture, true);
    }
}
