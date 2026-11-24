using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Indexing.Windows;

internal sealed class TrackedLocation(string root, IndexStore store, string excluded, IVolumeJournal journal, Func<bool> track) : IAsyncDisposable
{
    private readonly WindowsIndexFiles files = new();
    private readonly SemaphoreSlim writer = new(1);
    private readonly object queueLock = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private CancellationTokenSource? backgroundScan;
    private FileSystemWatcher? watcher;
    private bool overflow, journalDisabled, initialized, stopped;
    private JournalCursor? cursor;
    private long lastCheckpoint = Environment.TickCount64;
    private long nextStorageCheck;
    private JournalCursor? savedCursor;
    private SpaceIdentity identity;
    private IndexInfo? current;
    public string Root => root;
    public IndexStore Store => store;
    public IndexInfo? Current => Volatile.Read(ref current);
    public bool IsInitialized => Volatile.Read(ref initialized);
    private volatile bool searchNamesReady;
    public bool HasSearchNames => searchNamesReady;

    public event Action<string>? Changed;

    public void CompactWhenIdle(CancellationToken token)
    {
        if (Environment.TickCount64 < nextStorageCheck || !writer.Wait(0, token)) return;
        try
        {
            if (stopped || !initialized) return;
            nextStorageCheck = Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
            if (store.NeedsStorageCompaction())
            {
                store.CompactStorage(token);
                if (store.NeedsStorageCompaction()) return; // A reader may still be pinning the WAL.
            }
            nextStorageCheck = Environment.TickCount64 + (long)TimeSpan.FromDays(1).TotalMilliseconds;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidDataException
            or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            System.Diagnostics.Trace.TraceInformation("Index storage maintenance: {0}", ex.Message);
        }
        finally { writer.Release(); }
    }
    public async Task<bool> PublishSearchNamesAsync(CancellationToken token, Action? firstPageAvailable = null)
    {
        if (IsInitialized) return false;
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (initialized) return false;
            if (!searchNamesReady && store.GetLiveState() is null &&
                files.Read(root) is { IsDirectory: true, IsSkipped: false, IsCloud: false })
                searchNamesReady = store.PublishSearchNames(excluded, token, firstPageAvailable);
            return searchNamesReady;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        finally { writer.Release(); }
    }

    public async Task UpdateAsync(IStreamingSpaceScanner scanner, IProgress<SpaceScanProgress>? progress, CancellationToken token, bool force = false, bool preferPrecision = false, bool background = false)
    {
        if (preferPrecision) lock (queueLock) backgroundScan?.Cancel();
        await writer.WaitAsync(token).ConfigureAwait(false);
        using var backgroundToken = background && searchNamesReady ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
        var callerToken = token;
        if (backgroundToken is not null)
        {
            lock (queueLock) backgroundScan = backgroundToken;
            token = backgroundToken.Token;
        }
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var currentIdentity = files.Read(root)?.Stamp.Identity ?? throw new DirectoryNotFoundException(root);
            if (!initialized)
            {
                var saved = store.GetLiveState();
                if(saved is not null) store.PrepareLiveSearch(token);
                current = saved?.Info;
                identity = saved?.Identity ?? default;
                cursor = savedCursor = saved?.Cursor;
            }

            var head = journalDisabled ? null : journal.Head(root);
            bool rebuild = force || current is null || identity != currentIdentity || (head is { } h ? !cursor.HasValue || !h.Contains(cursor.Value) : !initialized || cursor.HasValue);
            // Upgrade an ordinary search cache once on the first foreground drive scan.
            // A declined/unavailable helper persists its fallback notice with the generation.
            rebuild |= preferPrecision && current is { Method: "目录扫描", Notice.Length: 0 } &&
                new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            if (head is null && track())
                StartWatcher();
            var pending = Drain();
            rebuild |= pending.Lost;
            var oldGeneration = current?.Generation;
            if (rebuild)
            {
                await BuildAsync(scanner, progress, head?.Tail, token).ConfigureAwait(false);
                identity = currentIdentity;
            }

            if (head is not null)
            {
                try
                {
                    CatchUp(token);
                }
                catch (JournalUnavailableException)
                {
                    // An ID change, journal wrap or unsupported read cannot safely advance the old checkpoint.
                    var fresh = journal.Head(root);
                    if (fresh is null || fresh.Value.Id == head.Value.Id && cursor is { } c && fresh.Value.Contains(c))
                    {
                        journalDisabled = true;
                        if (track()) StartWatcher();
                        fresh = null;
                    }

                    var alreadyRequested = rebuild && preferPrecision;
                    await BuildAsync(alreadyRequested ? new SpaceScanner(excluded) : scanner, progress, fresh?.Tail, token,
                        alreadyRequested ? "磁盘日志在扫描期间中断，已自动按目录重新核对。" : null).ConfigureAwait(false);
                    if (fresh.HasValue)
                        CatchUp(token);
                }
            }
            else if (!rebuild && pending.Paths.Length > 0)
                current = store.ApplyChanges(current!, [], pending.Paths, null, excluded, files, token);
            Volatile.Write(ref initialized, true);
            searchNamesReady = false;
            if (oldGeneration is not null && oldGeneration != current!.Generation)
                Changed?.Invoke(root);
        }
        catch (Exception ex) when (backgroundToken?.IsCancellationRequested == true && !callerToken.IsCancellationRequested &&
            ex is OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            Volatile.Write(ref initialized, false);
        }
        catch
        {
            Volatile.Write(ref initialized, false);
            throw;
        }
        finally
        {
            lock (queueLock) backgroundScan = null;
            writer.Release();
        }
    }

    public async Task RefreshPathsAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        if (paths.Count == 0) return;
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (current is null) return;
            var prior = current.Generation;
            current = store.ApplyChanges(current, [], paths, null, excluded, files, token);
            if (current.Generation != prior) Changed?.Invoke(root);
        }
        finally { writer.Release(); }
    }

    public async Task RefreshDirectoryAsync(string directory, CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var item = files.Read(directory) ?? throw new DirectoryNotFoundException(directory);
            var parent = files.Read(Path.GetDirectoryName(directory)!) ?? throw new DirectoryNotFoundException(directory);
            var id = item.Stamp.Identity;
            var parentId = parent.Stamp.Identity;
            // A directory security change already requires reconciling the entire branch.
            var change = new JournalChange(parentId.Low, parentId.High, id.Low, id.High, Path.GetFileName(directory), 0x800);
            current = store.ApplyChanges(current!, [change], [directory], null, excluded, files, token);
            Changed?.Invoke(root);
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task BuildAsync(IStreamingSpaceScanner scanner, IProgress<SpaceScanProgress>? progress, JournalCursor? start, CancellationToken token, string? notice = null)
    {
        progress?.Report(new(0, 0, 0, 0, TimeSpan.Zero, []) { Stage = "正在建立 " + root + " 的共享索引" });
        current = await scanner.ScanToAsync(root, progress, token, (meta, count, rows) => store.ReplaceLive(notice is null ? meta : meta with { ScanNotice = notice }, count, rows, token, start, excludedDirectory: excluded)).ConfigureAwait(false);
        cursor = savedCursor = start;
        lastCheckpoint = Environment.TickCount64;
    }

    private sealed class JournalUnavailableException(string message, Exception? cause = null) : IOException(message, cause);
    private void CatchUp(CancellationToken token)
    {
        var target = journal.Head(root) ?? throw new JournalUnavailableException("变更日志不可用。");
        if (cursor is not { } from || !target.Contains(from))
            throw new JournalUnavailableException("变更日志已替换或早期记录已过期。");
        while (from.Next < target.Next)
        {
            // Reconciliation reads the current disk state. Repeated writes to the same name
            // need one lookup, while both parents/names of a move and all reason bits matter.
            var reasons = new Dictionary<JournalChange, uint>();
            do
            {
                token.ThrowIfCancellationRequested();
                JournalBatch batch;
                try
                {
                    batch = journal.Read(root, from, target.Next, token);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new JournalUnavailableException("无法读取连续的磁盘变更。", ex);
                }

                if (batch.End.JournalId != from.JournalId || batch.End.Next <= from.Next || batch.End.Next > target.Next)
                    throw new JournalUnavailableException("变更日志返回了无效的读取位置。");
                foreach (var change in batch.Changes)
                {
                    var key = change with { Reason = 0 };
                    reasons[key] = reasons.GetValueOrDefault(key) | change.Reason;
                }
                from = batch.End;
            }
            // The frozen head bounds the read. Repeated writes consume no additional retained
            // memory. Split only when distinct identities/names fill a bounded working set.
            while (from.Next < target.Next && reasons.Count < 16384);
            if (reasons.Count != 0 || !initialized)
            {
                var changes = reasons.Select(pair => pair.Key with { Reason = pair.Value }).ToArray();
                var previous = current!;
                current = store.ApplyChanges(previous, changes, [], from, excluded, files, token, checkpointIdle: !initialized);
                if (!initialized || previous.Generation != current.Generation) savedCursor = from;
            }
            cursor = from;
        }

        if (Environment.TickCount64 - lastCheckpoint >= 60000)
            SaveCursor();
    }

    private void SaveCursor()
    {
        if (cursor is { } end && savedCursor != end && current is not null)
        {
            store.ApplyChanges(current, [], [], end, excluded, files, default, true);
            savedCursor = end;
        }

        lastCheckpoint = Environment.TickCount64;
    }

    private void StartWatcher()
    {
        if (watcher is not null)
            return;
        var w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.Security
        };
        w.Created += OnChange;
        w.Deleted += OnChange;
        w.Changed += OnChange;
        w.Renamed += (_, e) =>
        {
            Enqueue(e.OldFullPath);
            Enqueue(e.FullPath);
        };
        w.Error += (_, _) =>
        {
            lock (queueLock)
                overflow = true;
        };
        try
        {
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    public async Task StopMonitoringAsync(CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (track() || stopped) return;
            watcher?.Dispose();
            watcher = null;
            try { if (initialized) SaveCursor(); }
            finally { Volatile.Write(ref initialized, false); Drain(); }
        }
        finally { writer.Release(); }
    }

    private void OnChange(object sender, FileSystemEventArgs args) => Enqueue(args.FullPath);
    private void Enqueue(string path)
    {
        if (IndexStore.Within(path, excluded))
            return;
        lock (queueLock)
        {
            if (overflow)
                return;
            if (queued.Count >= 8192)
            {
                overflow = true;
                queued.Clear();
                return;
            }

            queued.Add(path);
        }
    }

    private (string[] Paths, bool Lost) Drain()
    {
        lock (queueLock)
        {
            var result = (queued.ToArray(), overflow);
            queued.Clear();
            overflow = false;
            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (stopped)
                return;
            stopped = true;
            watcher?.Dispose();
            if (initialized)
                SaveCursor();
        }
        finally
        {
            writer.Release();
        }
    }
}
