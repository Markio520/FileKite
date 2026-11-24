using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.FileSystem.Ntfs;

namespace FileAssistant.Indexing.Windows;

/// <summary>Owns the local index lifecycle for the full application. No shell service is installed.</summary>
public sealed partial class SharedIndexEngine : IFileSearch, ILiveSpaceScanner, IAsyncDisposable
{
    private readonly string storage;
    private readonly Func<IReadOnlyList<string>> roots;
    private readonly Func<AppSettings>? settings;
    private readonly SemaphoreSlim volumeGate = new(1, 1);
    private readonly SemaphoreSlim pathGate = new(1, 1);
    private readonly HashSet<string> changedPaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? volumeRun;
    private readonly IVolumeJournal journal;
    private readonly bool discoverSaved;
    private bool discovered;
    private readonly HashSet<string> manualRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan interval;
    private readonly SpaceScanner ordinary;
    private readonly IStreamingSpaceScanner explicitScanner;
    private int refreshFolder;
    private readonly IndexCatalog catalog;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken shutdown;
    private readonly object sync = new();
    private readonly Dictionary<string, TrackedLocation> locations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> readyVolumes = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource firstVolume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string[] online = [];
    private Task? initialization, monitor, closing;
    private long lastForegroundUse = Environment.TickCount64;
    private bool storagePrepared;
    /// <summary>Enable housekeeping for the application's dedicated Indexes directory.</summary>
    public bool MaintainIndexStorage { get; init; }
    private readonly Dictionary<TrackedLocation, Task> spaceUpdates = [];
    private bool globalRequested, disposed;
    private SearchProgress? lastProgress;
    private event Action<SearchProgress>? ProgressChanged;
    public event Action<string>? Changed;
    public SharedIndexEngine(string storage, string helperPath, Func<IReadOnlyList<string>>? roots = null, IVolumeJournal? journal = null, TimeSpan? interval = null,
        Func<AppSettings>? settings = null)
    {
        shutdown = lifetime.Token;
        this.storage = Path.GetFullPath(storage);
        this.roots = roots ?? FixedDrives;
        this.settings = settings;
        discoverSaved = roots is null;
        this.journal = journal ?? new NtfsJournal();
        this.interval = interval ?? TimeSpan.FromSeconds(2);
        catalog = new(this.storage);
        ordinary = new(this.storage);
        explicitScanner = new FastSpaceScanner(helperPath, this.storage);
    }

    private static IReadOnlyList<string> FixedDrives() => DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady).Select(d => d.Name).ToArray();
    public IReadOnlyList<SpaceDrive> GetDrives() => ordinary.GetDrives();
    private TrackedLocation Location(string path)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!locations.TryGetValue(path, out var scope))
            {
                if (MaintainIndexStorage && !storagePrepared)
                {
                    try
                    {
                        Directory.CreateDirectory(storage);
                        IndexStorageAllocation.TryEnableCompressionForNewFiles(storage);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Trace.TraceInformation("Index directory preparation: {0}", ex.Message);
                    }
                    storagePrepared = true;
                }
                scope = new(path, catalog.ForRoot(path), storage, journal, () => IsSelected(path));
                scope.Changed += p => Changed?.Invoke(p);
                locations.Add(path, scope);
            }

            return scope;
        }
    }

    private void StartMonitor()
    {
        lock (sync)
        {
            if (disposed)
                return;
            monitor ??= Task.Run(() => MonitorAsync(shutdown));
        }
    }

    public Task EnsureReadyAsync(IProgress<SearchProgress>? progress, CancellationToken token) =>
        WaitForVolumesAsync(progress, token, allowPartial: false);

    private async Task WaitForVolumesAsync(IProgress<SearchProgress>? progress, CancellationToken token, bool allowPartial)
    {
        Task bootstrap, ready;
        SearchProgress? initialProgress;
        Action<SearchProgress>? notify = progress is null ? null : progress.Report;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (notify is not null)
                ProgressChanged += notify;
            globalRequested = true;
            bootstrap = initialization ??= Task.Run(() => RefreshVolumesAsync(shutdown));
            ready = allowPartial && !bootstrap.IsCompleted ? Task.WhenAny(bootstrap, firstVolume.Task).Unwrap() : bootstrap;
            initialProgress = lastProgress;
        }

        try
        {
            if (initialProgress != null) progress?.Report(initialProgress);
            while (true)
            {
                await ready.WaitAsync(token).ConfigureAwait(false);
                lock (sync)
                {
                    if (initialization == null || ReferenceEquals(bootstrap, initialization)) return;
                    bootstrap = initialization;
                    ready = allowPartial && !bootstrap.IsCompleted ? Task.WhenAny(bootstrap, firstVolume.Task).Unwrap() : bootstrap;
                }
            }
        }
        finally
        {
            if (notify is not null)
                lock (sync)
                    ProgressChanged -= notify;
        }
    }

    public void InvalidateFolder() => Interlocked.Exchange(ref refreshFolder, 1);
    public void InvalidatePaths(IReadOnlyList<string> paths)
    {
        InvalidateFolder();
        lock (sync)
        {
            if (!disposed && locations.Count > 0) changedPaths.UnionWith(paths);
        }
    }

    private async Task RefreshChangedPathsAsync(CancellationToken token)
    {
        await pathGate.WaitAsync(token).ConfigureAwait(false);
        string[] paths = [];
        try
        {
            TrackedLocation[] scopes;
            lock (sync)
            {
                if (changedPaths.Count == 0) return;
                paths = changedPaths.ToArray();
                changedPaths.Clear();
                scopes = locations.Values.Where(scope => scope.IsInitialized && IsSelected(scope.Root)).ToArray();
            }
            await Task.Run(async () =>
            {
                foreach (var scope in scopes)
                {
                    var relevant = paths.Where(path => IndexStore.Within(path, scope.Root) &&
                        !string.Equals(path, scope.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
                    await scope.RefreshPathsAsync(relevant, token).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
        }
        catch
        {
            lock (sync) changedPaths.UnionWith(paths);
            throw;
        }
        finally { pathGate.Release(); }
    }
    public void RetryIndexInitialization()
    {
        lock (sync)
        {
            if (initialization?.IsCompleted == true)
            {
                initialization = null;
                firstVolume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public async Task<SearchPage> SearchAsync(SearchRequest request, IReadOnlyList<SearchFile> batch, IProgress<SearchProgress>? progress, CancellationToken token)
    {
        Volatile.Write(ref lastForegroundUse, Environment.TickCount64);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
        token = stop.Token;
        if (settings?.Invoke() is { } preferences)
            request = request with { PinyinSearch = preferences.PinyinSearch, ShowHidden = preferences.ShowHidden,
                ExcludedDirectories = preferences.ExcludedDirectories, ContentLimitMB = preferences.ContentLimitMB };
        _ = SearchRules.Compile(request);
        if (request.Scope == SearchScope.Batch)
            return await Task.Run(() => LocalSearchFiles.Filter(batch, request, "文件暂存区", token), token).ConfigureAwait(false);
        await RefreshChangedPathsAsync(token).ConfigureAwait(false);
        if (request.Scope == SearchScope.Folder)
            return await SearchFolderAsync(request, progress, token).ConfigureAwait(false);
        await WaitForVolumesAsync(progress, token, request.AllowPartialIndex).ConfigureAwait(false);
        TrackedLocation[] available;
        string notice;
        bool incomplete;
        lock (sync)
        {
            available = online.Where(p => readyVolumes.Contains(p) && IsSelected(p)).Select(p => locations[p]).ToArray();
            var notes = online.Where(errors.ContainsKey).Select(p => p + " " + errors[p]).ToList();
            incomplete = available.Length != online.Length;
            int building = online.Count(path => !readyVolumes.Contains(path) && !errors.ContainsKey(path));
            if (building > 0)
                notes.Add($"{building} 个位置正在建库");
            notice = string.Join(" · ", notes);
        }

        if (available.Length == 0 && settings?.Invoke().IndexedVolumes is { Length: 0 })
            return new([], 0, "未选择索引卷");
        if (available.Length == 0)
            throw new SearchException(string.IsNullOrEmpty(notice) ? "没有可查询的在线磁盘。" : notice);
        var page = await Task.Run(() => IndexedFileSearch.Query(available.Select(s => s.Store), request, token, notice), token).ConfigureAwait(false);
        return page with { IsPartialIndex = incomplete || page.IsPartialIndex };
    }

    /// <summary>Also used by explicit refresh and deterministic acceptance tests.</summary>
    public async Task SynchronizeAsync(CancellationToken token = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
        token = stop.Token;
        await EnsureReadyAsync(null, token).ConfigureAwait(false);
        Task? bootstrap;
        lock (sync)
            bootstrap = initialization;
        if (bootstrap != null)
            await bootstrap.WaitAsync(token).ConfigureAwait(false);
        await Task.Run(() => RefreshVolumesAsync(token, explicitRequest: true), token).ConfigureAwait(false);
    }

    public async Task RebuildAsync(CancellationToken token = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            globalRequested = true;
        }
        await Task.Run(() => RefreshVolumesAsync(token, rebuild: true, explicitRequest: true), token).ConfigureAwait(false);
    }

    private bool IsSelected(string path) => settings?.Invoke().IndexedVolumes is not { } selected ||
        selected.Any(root => !string.IsNullOrWhiteSpace(root) && IndexStore.Within(path, root));

    public async Task ApplyVolumeSettingsAsync(CancellationToken token = default)
    {
        Task update;
        lock (sync)
        {
            if (disposed) return;
            online = online.Where(IsSelected).ToArray();
            readyVolumes.IntersectWith(online);
            var disabled = locations.Values.Where(scope => !IsSelected(scope.Root)).ToArray();
            var active = globalRequested;
            volumeRun?.Cancel();
            update = Task.Run(async () =>
            {
                foreach (var scope in disabled) await scope.StopMonitoringAsync(shutdown).ConfigureAwait(false);
                if (active) await RefreshVolumesAsync(shutdown).ConfigureAwait(false);
            }, shutdown);
            if (active)
            {
                firstVolume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (readyVolumes.Count > 0) firstVolume.TrySetResult();
                initialization = update;
            }
        }
        Changed?.Invoke(string.Empty);
        await update.WaitAsync(token).ConfigureAwait(false);
    }

    public (long Items, int Ready, int Selected, bool Updating) GetStatus()
    {
        // Read only published metadata. Opening settings in on-demand mode never starts a scan.
        var selected = roots().Where(IsSelected).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = new List<IndexInfo>();
        foreach (var info in catalog.List(_ => { }).OrderBy(info => info.Root.Length))
            if (selected.Any(root => IndexStore.Within(info.Root, root)) && !known.Any(parent => IndexStore.Within(info.Root, parent.Root)))
                known.Add(info);
        lock (sync)
            return (known.Sum(info => (long)info.Files + info.Folders),
                known.Count(info => !info.IsBuildingNames && selected.Contains(info.Root) && readyVolumes.Contains(info.Root)), selected.Count,
                lastProgress != null || initialization is { IsCompleted: false } || volumeRun != null);
    }

    private async Task RefreshVolumesAsync(CancellationToken token, bool rebuild = false, bool explicitRequest = false)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
        await volumeGate.WaitAsync(run.Token).ConfigureAwait(false);
        try
        {
            lock (sync) volumeRun = run;
            await RefreshVolumesCoreAsync(run.Token, rebuild).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!explicitRequest && !token.IsCancellationRequested && !shutdown.IsCancellationRequested && run.IsCancellationRequested)
        {
            // The settings change schedules the replacement pass with the new selection.
        }
        finally
        {
            lock (sync) { volumeRun = null; lastProgress = null; }
            volumeGate.Release();
        }
    }

    private async Task RefreshVolumesCoreAsync(CancellationToken token, bool rebuild)
    {
        if (discoverSaved && !discovered)
        {
            var saved = catalog.List(_ =>
            {
            });
            lock (sync)
            {
                foreach (var item in saved)
                    if (!string.Equals(item.Root, Path.GetPathRoot(item.Root), StringComparison.OrdinalIgnoreCase))
                        manualRoots.Add(item.Root);
                discovered = true;
            }
        }

        string[] manual;
        lock (sync)
            manual = manualRoots.ToArray();
        var selected = roots().Concat(manual.Where(Directory.Exists)).Select(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)))
            .Where(IsSelected).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ToArray();
        var active = selected.Where(path => !selected.Any(other => other.Length < path.Length && IndexStore.Within(path, other))).ToArray();
        string[] previous;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            previous = online;
            online = active;
            readyVolumes.IntersectWith(active);
        }

        if (previous.Length != 0 && !previous.SequenceEqual(active, StringComparer.OrdinalIgnoreCase))
            Changed?.Invoke(string.Empty);
        // Publish names in priority order. Starting two name walks on partitions of one SSD
        // delayed both the first page and the system-drive result on a cold database.
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var ordered = active.OrderByDescending(p => File.Exists(catalog.ForRoot(p).DatabasePath))
            .ThenBy(p => string.Equals(Path.GetPathRoot(p), systemDrive, StringComparison.OrdinalIgnoreCase)).ToArray();
        var workers = new ParallelOptions { CancellationToken = token,
            MaxDegreeOfParallelism = ordered.Length > 1 && ordered.All(IsFixedDriveRoot) ? 2 : 1 };
        var awaitingSpace = new List<(TrackedLocation Scope, IProgress<SpaceScanProgress> Progress)>();
        foreach (var path in ordered)
        {
            token.ThrowIfCancellationRequested();
            var scope = Location(path);
            var progress = new InlineProgress<SpaceScanProgress>(p =>
            {
                var value = new SearchProgress(p.Files + p.Folders, path, p.Stage);
                Action<SearchProgress>? cb;
                lock (sync)
                {
                    lastProgress = value;
                    cb = ProgressChanged;
                }

                cb?.Invoke(value);
            });
            bool published = false;
            bool needFirstPage, previewPublished = false;
            lock (sync) needFirstPage = readyVolumes.Count == 0;
            try
            {
                published = await scope.PublishSearchNamesAsync(token, needFirstPage ? () =>
                {
                    previewPublished = true;
                    MarkAvailable(scope);
                } : null).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                    or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
            {
                // The normal scanner can still recover or provide a per-volume error.
            }
            if (published)
            {
                MarkAvailable(scope, previewPublished);
                awaitingSpace.Add((scope, progress));
            }
            else await FinishVolumeAsync(scope, progress, token).ConfigureAwait(false);
        }

        // The exact scans and SQLite snapshots for different local volumes are independent.
        await Parallel.ForEachAsync(awaitingSpace, workers, (item, run) =>
            new ValueTask(FinishVolumeAsync(item.Scope, item.Progress, run))).ConfigureAwait(false);

        lock (sync)
            lastProgress = null;
        StartMonitor();

        async Task FinishVolumeAsync(TrackedLocation scope, IProgress<SpaceScanProgress> progress, CancellationToken run)
        {
            if (rebuild)
            {
                await scope.UpdateAsync(ordinary, progress, run, force: true).ConfigureAwait(false);
                MarkAvailable(scope, changed: true);
            }
            else await UpdateSafeAsync(scope, progress, run).ConfigureAwait(false);
        }
    }

    private static bool IsFixedDriveRoot(string path)
    {
        if (path.Length != 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        try { return new DriveInfo(path).DriveType == DriveType.Fixed; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private void MarkAvailable(TrackedLocation scope, bool changed = false)
    {
        lock (sync)
        {
            if (!scope.HasSearchNames || !readyVolumes.Contains(scope.Root))
                changed |= errors.Remove(scope.Root);
            if (online.Contains(scope.Root, StringComparer.OrdinalIgnoreCase))
            {
                changed |= readyVolumes.Add(scope.Root);
                firstVolume.TrySetResult();
            }
        }
        if (changed) Changed?.Invoke(scope.Root);
    }

    private async Task UpdateSafeAsync(TrackedLocation scope, IProgress<SpaceScanProgress>? progress, CancellationToken token)
    {
        try
        {
            var hadNames = scope.HasSearchNames;
            await scope.UpdateAsync(ordinary, progress, token, background: true).ConfigureAwait(false);
            if (!scope.IsInitialized) return;
            MarkAvailable(scope, hadNames);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            bool first;
            lock (sync)
            {
                first = !errors.ContainsKey(scope.Root);
                errors[scope.Root] = scope.HasSearchNames ? "空间统计暂未完成 · " + ex.Message : ex.Message;
                if (!scope.HasSearchNames) readyVolumes.Remove(scope.Root);
            }

            if (first)
                Changed?.Invoke(scope.Root);
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await RefreshChangedPathsAsync(token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // The normal volume pass also reconciles the journal or watcher; retry the explicit paths on the next tick.
                }
                bool global;
                lock (sync)
                    global = globalRequested && initialization is not { IsCompleted: false };
                if (global)
                {
                    try
                    {
                        await RefreshVolumesAsync(token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }
                }

                TrackedLocation[] extra;
                lock (sync)
                    extra = locations.Values.Where(s => s.Current != null && IsSelected(s.Root) && !online.Any(root => IndexStore.Within(s.Root, root))).ToArray();
                foreach (var scope in extra)
                    if (Directory.Exists(scope.Root))
                        await UpdateSafeAsync(scope, null, token).ConfigureAwait(false);

                if (MaintainIndexStorage && Environment.TickCount64 - Volatile.Read(ref lastForegroundUse) >= 15000)
                {
                    TrackedLocation[] idle;
                    lock (sync) idle = locations.Values.Where(s => s.IsInitialized && IsSelected(s.Root)).ToArray();
                    foreach (var scope in idle)
                    {
                        if (Environment.TickCount64 - Volatile.Read(ref lastForegroundUse) < 15000) break;
                        scope.CompactWhenIdle(token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token) => OpenSpaceAsync(root, progress, token, false);
    public Task<SpaceSnapshot> RescanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token) => OpenSpaceAsync(root, progress, token, true);
    public Task<SpaceSnapshot> ReadCurrentAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token) => OpenSpaceAsync(root, progress, token, false, committedOnly: true);
    public async Task<SpaceSnapshot> RefreshPathsAsync(string root, IReadOnlyList<string> paths, IProgress<SpaceScanProgress>? progress, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var scope = ScopeFor(root);
        var relevant = paths.Where(path => IndexStore.Within(path, scope.Root) && !string.Equals(path, scope.Root, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).ToArray();
        return await Task.Run(async () =>
        {
            await scope.UpdateAsync(ordinary, progress, stop.Token).ConfigureAwait(false);
            await scope.RefreshPathsAsync(relevant, stop.Token).ConfigureAwait(false);
            return scope.Store.OpenSpace(root, stop.Token, IndexCacheAllocation(root));
        }, stop.Token).ConfigureAwait(false);
    }
    private async Task<SpaceSnapshot> OpenSpaceAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token, bool userScan, bool committedOnly = false)
    {
        Volatile.Write(ref lastForegroundUse, Environment.TickCount64);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
        token = stop.Token;
        var scope = ScopeFor(root);
        var fullDrive = !committedOnly && root.Length == 3 && string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase);
        return await Task.Run(async () =>
        {
            bool exactScope = string.Equals(scope.Root, root, StringComparison.OrdinalIgnoreCase);
            var tracked = IsSelected(scope.Root);
            if (fullDrive && tracked && !userScan && exactScope && scope.Store.GetLiveState() is { Cursor: { } checkpoint } saved &&
                (saved.Info.Method.Contains("NTFS", StringComparison.OrdinalIgnoreCase) || saved.Info.Notice.Length > 0) &&
                IndexEntryReader.Read(root)?.Stamp.Identity == saved.Identity && journal.Head(root) is { } head && head.Contains(checkpoint))
            {
                var snapshot = scope.Store.OpenSpace(root, token, IndexCacheAllocation(root));
                snapshot.ScanNotice = $"已打开 {saved.Info.Updated.ToLocalTime():MM-dd HH:mm} 的索引，磁盘变化将自动更新。" + snapshot.ScanNotice;
                lock (sync)
                {
                    if (!disposed && (!spaceUpdates.TryGetValue(scope, out var update) || update.IsCompleted))
                        spaceUpdates[scope] = Task.Run(() => UpdateSafeAsync(scope, null, shutdown));
                }
                StartMonitor();
                return snapshot;
            }
            if (!committedOnly || !scope.IsInitialized && scope.Store.GetLiveState() == null)
                await scope.UpdateAsync(fullDrive ? explicitScanner : ordinary, progress, token, force: userScan && exactScope,
                    preferPrecision: fullDrive).ConfigureAwait(false);
            if (userScan && !exactScope)
                await scope.RefreshDirectoryAsync(root, token).ConfigureAwait(false);
            if (tracked) StartMonitor();
            try
            {
                return scope.Store.OpenSpace(root, token, IndexCacheAllocation(root));
            }
            catch (DirectoryNotFoundException) when (!string.Equals(root, scope.Root, StringComparison.OrdinalIgnoreCase))
            {
                var exact = Location(root);
                await exact.UpdateAsync(ordinary, progress, token, force: true).ConfigureAwait(false);
                return exact.Store.OpenSpace(root, token);
            }
        }, token).ConfigureAwait(false);
    }

    private long IndexCacheAllocation(string root)
    {
        if (root.Length != 3 || !IndexStore.Within(storage, root)) return 0;
        long bytes = 0;
        try
        {
            foreach (var file in IndexEntryReader.Names(storage))
            {
                int dot = file.Name.IndexOf('.');
                if (dot != 64 || file.Name[dot..] is not (".sqlite3" or ".sqlite3-wal" or ".sqlite3-shm") ||
                    file.Name.AsSpan(0, dot).IndexOfAnyExcept("0123456789ABCDEF") >= 0) continue;
                var entry = IndexEntryReader.Read(Path.Join(storage, file.Name));
                if (entry is { IsDirectory: false, IsCloud: false, AllocatedBytes: > 0 } item &&
                    !item.Stamp.IsReparsePoint && item.Stamp.Links == 1) bytes += item.AllocatedBytes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // A concurrent WAL checkpoint may remove a cache file during enumeration.
        }
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (closing != null) return new(closing);
            disposed = true;
            Task[] active = [.. spaceUpdates.Values, initialization ?? Task.CompletedTask, monitor ?? Task.CompletedTask];
            var cancellation = lifetime.CancelAsync();
            closing = Task.Run(() => CloseAsync(active, cancellation));
            return new(closing);
        }
    }

    private async Task CloseAsync(Task[] active, Task cancellation)
    {
        try
        {
            await Task.WhenAll(active.Append(cancellation)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // A settings rebuild or explicit sync may be running independently of initialization.
            await volumeGate.WaitAsync().ConfigureAwait(false);
            volumeGate.Release();
            await pathGate.WaitAsync().ConfigureAwait(false);
            pathGate.Release();
            TrackedLocation[] scopes;
            lock (sync)
                scopes = locations.Values.ToArray();
            try
            {
                await Task.WhenAll(scopes.Select(location => location.DisposeAsync().AsTask())).ConfigureAwait(false);
                // Windows can stall live reads for seconds while applying NTFS compression.
                // Existing databases get this flag after close. New files inherit it at creation.
                if (MaintainIndexStorage)
                    foreach (var scope in scopes)
                        IndexStorageAllocation.TryCompressDatabase(scope.Store.DatabasePath);
            }
            finally
            {
                lifetime.Dispose();
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
