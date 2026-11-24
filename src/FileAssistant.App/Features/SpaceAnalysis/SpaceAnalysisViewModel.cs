using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.App.Features.SpaceAnalysis;

public sealed class SpaceAnalysisViewModel(ISpaceScanner scanner, ISpaceRecycler recycler, IFileInteraction interaction, string programDirectory,
    Func<AppSettings>? preferences = null)
{
    private AppSettings? appliedPreferences = preferences?.Invoke();
    public bool LogicalSize => appliedPreferences?.LogicalSpaceSize == true;
    public int DisplayRevision { get; private set; }
    public void ApplyPreferences()
    {
        if (preferences?.Invoke() is not { } current) return;
        var previous = appliedPreferences;
        appliedPreferences = current;
        if (previous?.LogicalSpaceSize == current.LogicalSpaceSize && previous.ShowExtensions == current.ShowExtensions && previous.Units == current.Units) return;
        DisplayRevision++;
        if (previous?.LogicalSpaceSize != current.LogicalSpaceSize)
        {
            if (Sort is SpaceSort.Allocated or SpaceSort.Logical) Sort = LogicalSize ? SpaceSort.Logical : SpaceSort.Allocated;
            Map = null;
            CancelTypes();
            TypeTotals = [];
            typesReady = false;
            StartQuery();
        }
        else Notify();
    }
    private CancellationTokenSource? operation;
    private CancellationTokenSource? query;
    private Task activeOperation = Task.CompletedTask;
    private Task activeQuery = Task.CompletedTask;
    private CancellationTokenSource? typeQuery;
    private Task activeTypes = Task.CompletedTask;
    private bool typesReady;
    private int[] pending = [];
    private string? scanRoot;
    private Task cleanupRefresh = Task.CompletedTask;
    private sealed record PathUpdate(IReadOnlyList<PathChange> Changes, IReadOnlyList<string> Removed, bool RefreshAll);
    private readonly record struct PendingTarget(int Id, string Path, SpaceIdentity Identity);
    private readonly Queue<PathUpdate> pathUpdates = new();
    private bool indexViewActive, indexDirty;
    private Task indexRefresh = Task.CompletedTask;
    private CancellationTokenSource? indexDelay;
    private CancellationTokenSource? indexRead;
    private Task releasingView = Task.CompletedTask;
    private string? resumeFolder;
    private bool viewUnloaded;
    public void SetIndexViewActive(bool active)
    {
        indexViewActive = active;
        viewUnloaded = !active;
        if (!active) { indexDelay?.Cancel(); indexRead?.Cancel(); SuspendIndexView(); return; }
        if (resumeFolder is { } path && scanRoot is { } root && !IsBusy)
        {
            resumeFolder = null;
            activeOperation = RunAsync(token => ScanCoreAsync(root, path, token, liveRefresh: true));
            return;
        }
        if (active && indexDirty && indexRefresh.IsCompleted) indexRefresh = RefreshIndexAsync();
    }
    private void SuspendIndexView()
    {
        if (!viewUnloaded || IsBusy || pending.Length != 0 || Snapshot?.Entries is not IIndexedSpaceView) return;
        resumeFolder = Snapshot.PathOf(Folder);
        ClearSnapshot();
        Notify();
    }
    public async Task ReleaseIndexViewAsync()
    {
        if (Snapshot?.Entries is IIndexedSpaceView) ClearSnapshot();
        await releasingView;
    }
    public void IndexChanged(string scope)
    {
        var root = Snapshot?.Root ?? (Scanning ? scanRoot : null);
        if (scanner is not ILiveSpaceScanner || root is null || scope.Length > 0 && !IsWithin(root, scope)) return;
        indexDirty = true;
        if (indexViewActive && indexRefresh.IsCompleted) indexRefresh = RefreshIndexAsync();
    }
    private async Task RefreshIndexAsync()
    {
        int version = stopVersion;
        while (indexDirty && indexViewActive && version == stopVersion)
        {
            using (var delay = new CancellationTokenSource())
            {
                indexDelay = delay;
                try { await Task.Delay(Snapshot?.Entries.Count > 250_000 ? 4500 : 600, delay.Token); }
                catch (OperationCanceledException) when (delay.IsCancellationRequested) { if (!indexViewActive || version != stopVersion) return; }
                finally { indexDelay = null; }
            }
            if (version != stopVersion || !indexViewActive || Snapshot is not { } snapshot || pending.Length != 0 || IsBusy) return;
            var path = snapshot.PathOf(Folder);
            indexDirty = false;
            var view = View;
            var filter = Filter;
            var sort = Sort;
            var descending = Descending;
            var logical = LogicalSize;
            using var read = new CancellationTokenSource();
            indexRead = read;
            SpaceSnapshot? next = null;
            try
            {
                next = await ((ILiveSpaceScanner)scanner).ReadCurrentAsync(snapshot.Root, null, read.Token);
                if (snapshot.Entries is IIndexedSpaceView { Generation: { } previous } &&
                    next.Entries is IIndexedSpaceView current && current.Generation == previous) continue;
                var candidate = next;
                var updated = await Task.Run(() =>
                {
                    var folder = FindSurvivingFolder(candidate, path);
                    var program = candidate.Find(programDirectory);
                    var map = SpaceQueries.Map(candidate, folder, read.Token, logical);
                    var rows = view == SpaceView.Types ? null : SpaceQueries.List(candidate, folder, view, filter, sort, descending, read.Token, logical);
                    var types = SpaceQueries.Types(candidate, read.Token, logical);
                    return (folder, program, map, rows, types);
                }, read.Token);
                if (read.IsCancellationRequested || !indexViewActive || !ReferenceEquals(Snapshot, snapshot) ||
                    pending.Length != 0 || IsBusy) continue;
                var readers = Task.WhenAll(releasingView, activeQuery, activeTypes);
                CancelQuery();
                CancelTypes();
                Snapshot = next;
                next = null;
                Folder = updated.folder;
                ProgramFolder = updated.program;
                Map = updated.map;
                Result = updated.rows;
                if (!TypeTotals.SequenceEqual(updated.types)) TypeTotals = updated.types;
                typesReady = true;
                try { Notify(); }
                finally { releasingView = ReleaseAfterReadersAsync(snapshot, readers); }
            }
            catch (OperationCanceledException) when (read.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!read.IsCancellationRequested && ReferenceEquals(Snapshot, snapshot))
                {
                    Message = "索引更新未完成。" + error.Message;
                    Notify();
                }
            }
            finally
            {
                indexRead = null;
                next?.Dispose();
            }
        }
    }
    private int stopVersion;

    public event EventHandler? Changed;
    public event EventHandler<IReadOnlyList<string>>? FilesRemoved;

    public SpaceSnapshot? Snapshot { get; private set; }
    public SpaceScanProgress? ScanProgress { get; private set; }
    public SpaceRecycleProgress? RecycleProgress { get; private set; }
    public SpaceRecycleReport? RecycleReport { get; private set; }
    public SpaceQueryResult? Result { get; private set; }
    public IReadOnlyList<SpaceTypeTotal> TypeTotals { get; private set; } = [];
    public SpaceMapData? Map { get; private set; }
    public SpaceView View { get; private set; }
    public SpaceFilter Filter { get; private set; } = new();
    public SpaceSort Sort { get; private set; } = preferences?.Invoke().LogicalSpaceSize == true ? SpaceSort.Logical : SpaceSort.Allocated;
    public bool Descending { get; private set; } = true;
    public int Folder { get; private set; }
    public int? ProgramFolder { get; private set; }
    public int? Highlight { get; private set; }
    public bool Scanning { get; private set; }
    public bool Recycling { get; private set; }
    public bool IsBusy => operation is not null;
    public bool IsQuerying => query is not null || typeQuery is not null;
    public bool IsLoadingView => query is not null || (View == SpaceView.Types && typeQuery is not null);
    public bool CanEndAnalysis => Snapshot is not null && !IsBusy && pending.Length == 0;
    public bool IsCancelling => operation?.IsCancellationRequested == true;
    public string Message { get; private set; } = string.Empty;
    public IReadOnlyList<int> Pending => pending;

    public IReadOnlyList<SpaceDrive> GetDrives() => scanner.GetDrives();

    public Task ScanAsync(string root,bool rescan=false)
    {
        if (IsBusy) return activeOperation;
        scanRoot = root;
        var priorFolder = Snapshot is { } snapshot && string.Equals(root, snapshot.Root, StringComparison.OrdinalIgnoreCase)
            ? snapshot.PathOf(Folder) : null;
        RecycleReport = null;
        pending = [];
        return activeOperation = RunAsync(token => ScanCoreAsync(root, priorFolder, token, rescan:rescan));
    }

    public Task RecycleAsync()
    {
        if (IsBusy || !cleanupRefresh.IsCompleted || Snapshot is not { } snapshot || pending.Length == 0) return activeOperation;
        var selection = pending;
        return activeOperation = RunAsync(async token =>
        {
            Recycling = true;
            RecycleReport = null;
            RecycleProgress = new(0, selection.Length, "");
            CancelQuery();
            Notify();
            try
            {
                var progress = new Progress<SpaceRecycleProgress>(value =>
                {
                    if (!Recycling || operation?.Token != token || token.IsCancellationRequested) return;
                    RecycleProgress = value;
                    Notify();
                });
                var report = await recycler.RecycleAsync(snapshot, selection, progress, token);
                RecycleReport = report;
                var removed = report.Items.Where(item => item.Status == SpaceRecycleStatus.Recycled).Select(item => item.Path).ToArray();
                pending = selection.Where(id => !removed.Contains(snapshot.PathOf(id), StringComparer.Ordinal)).ToArray();
                if (removed.Length == 0)
                {
                    StartQuery();
                    return;
                }

                var keep = CapturePending(snapshot);
                pending = [];
                FilesRemoved?.Invoke(this, removed);
                var path = snapshot.PathOf(Folder);
                Recycling = false;
                if (!token.IsCancellationRequested)
                {
                    await ScanCoreAsync(snapshot.Root, path, token, liveRefresh: true, changedPaths: removed);
                    if (Snapshot is { } updated) RestorePending(updated, keep);
                }
                else
                {
                    ClearSnapshot();
                    Message = "处理已停止。请重新扫描以查看当前目录。";
                }
            }
            finally { Recycling = false; }
        });
    }

    public void Cancel()
    {
        operation?.Cancel();
        Notify();
    }

    public Task RefreshAfterCleanupAsync(IReadOnlyList<string>? removed = null) =>
        QueuePathUpdate(new([], removed ?? [], removed is null));

    public Task ApplyPathChangesAsync(IReadOnlyList<PathChange>? changes = null, IReadOnlyList<string>? removed = null) =>
        QueuePathUpdate(new(changes ?? [], removed ?? [], false));

    private Task QueuePathUpdate(PathUpdate update)
    {
        var root = Snapshot?.Root ?? scanRoot;
        if (root is null || cleanupRefresh.IsCompleted && !AffectsRoot(update, root)) return Task.CompletedTask;
        pathUpdates.Enqueue(update);
        indexRead?.Cancel();
        if (cleanupRefresh.IsCompleted) cleanupRefresh = RefreshQueuedAsync(stopVersion);
        return cleanupRefresh;
    }

    private static bool AffectsRoot(PathUpdate update, string root) => update.RefreshAll ||
        update.Changes.Any(change => Overlap(change.Before, root) || Overlap(change.After, root)) ||
        update.Removed.Any(path => Overlap(path, root));
    private static bool Overlap(string a, string b) => IsWithin(a, b) || IsWithin(b, a);

    internal static bool IsWithin(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task RefreshQueuedAsync(int version)
    {
        await Task.Yield();
        while (pathUpdates.Count > 0 && version == stopVersion)
        {
            await activeOperation; // Do not interrupt another feature's recycle operation or an ongoing scan.
            if (version != stopVersion) return;
            if (scanRoot is not { } root) { pathUpdates.Clear(); return; }
            var prior = Snapshot;
            var folder = prior?.PathOf(Folder) ?? resumeFolder ?? root;
            var targets = prior == null ? [] : CapturePending(prior);
            var touched = new HashSet<string>(StringComparer.Ordinal);
            bool refresh = false, removedRoot = false;
            while (pathUpdates.TryDequeue(out var change))
            {
                if (!AffectsRoot(change, root)) continue;
                refresh = true;
                root = NamingRulesEngine.MapPath(root, change.Changes);
                folder = NamingRulesEngine.MapPath(folder, change.Changes);
                targets = targets.Select(target => target with { Path = NamingRulesEngine.MapPath(target.Path, change.Changes) })
                    .Where(target => !change.Removed.Any(path => IsWithin(target.Path, path))).ToArray();
                foreach (var renamed in change.Changes) { touched.Add(renamed.Before); touched.Add(renamed.After); }
                touched.UnionWith(change.Removed);
                removedRoot |= change.Removed.Any(path => IsWithin(root, path));
            }
            if (!refresh) continue;
            if (removedRoot || !Directory.Exists(root))
            {
                pending = []; scanRoot = resumeFolder = null;
                ClearSnapshot();
                Message = "当前扫描位置已移除，请重新选择磁盘或文件夹。";
                Notify();
                continue;
            }
            scanRoot = root;
            if (prior == null && resumeFolder != null) { resumeFolder = folder; continue; }
            var retained = prior == null ? [] : targets.Where(target => prior.PathOf(target.Id) == target.Path)
                .Select(target => target.Id).ToArray();
            pending = [];
            activeOperation = RunAsync(async token =>
            {
                try
                {
                    await ScanCoreAsync(root, folder, token, liveRefresh: true, changedPaths: touched.ToArray());
                    if (Snapshot is not { } current) return;
                    RestorePending(current, targets);
                }
                finally { if (ReferenceEquals(Snapshot, prior)) pending = retained; }
            });
            await activeOperation;
        }
    }

    private PendingTarget[] CapturePending(SpaceSnapshot snapshot) => pending.Select(id =>
        new PendingTarget(id, snapshot.PathOf(id), snapshot.Entries[id].Stamp.Identity)).ToArray();

    private void RestorePending(SpaceSnapshot snapshot, IReadOnlyList<PendingTarget> targets)
    {
        // A reused path with another file ID must never inherit an earlier recycle choice.
        var ids = targets.Select(target => (Id: snapshot.Find(target.Path), target.Identity))
            .Where(target => target.Id is { } id && snapshot.Entries[id].Stamp.Identity == target.Identity && CanQueue(id))
            .Select(target => target.Id!.Value);
        pending = snapshot.CollapseSelection(ids);
    }

    public async Task StopAsync()
    {
        stopVersion++; pathUpdates.Clear();
        indexDelay?.Cancel();
        indexRead?.Cancel();
        operation?.Cancel();
        CancelQuery();
        await activeOperation;
        await cleanupRefresh;
        CancelQuery();
        CancelTypes();
        await Task.WhenAll(activeQuery, activeTypes);
        await indexRefresh;
    }

    private async Task RunAsync(Func<CancellationToken, Task> run)
    {
        var version = stopVersion;
        indexRead?.Cancel();
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        Message = string.Empty;
        try { await run(cancellation.Token); }
        catch (OperationCanceledException) { Message = "操作已取消。"; }
        catch (Exception error)
        {
            Message = "无法完成操作。" + error.Message;
        }
        finally
        {
            operation = null;
            SuspendIndexView();
            Notify();
            if (version == stopVersion && indexViewActive && indexDirty && pending.Length == 0 && Snapshot is not null && indexRefresh.IsCompleted)
                indexRefresh = RefreshIndexAsync();
        }
    }

    private async Task ScanCoreAsync(string root, string? priorFolder, CancellationToken token, bool liveRefresh = false, bool rescan=false,
        IReadOnlyList<string>? changedPaths = null)
    {
        SpaceSnapshot? unpublished = null;
        if (!liveRefresh) ClearSnapshot();
        Scanning = true;
        ScanProgress = new(0, 0, 0, 0, TimeSpan.Zero, []);
        Notify();
        try
        {
            var progress = new Progress<SpaceScanProgress>(value =>
            {
                if (!Scanning || operation?.Token != token || token.IsCancellationRequested) return;
                ScanProgress = value;
                Notify();
            });
            var snapshot = unpublished = scanner is ILiveSpaceScanner live && (rescan || liveRefresh)
                ? await (changedPaths != null ? live.RefreshPathsAsync(root, changedPaths, progress, token) :
                    rescan ? live.RescanAsync(root,progress,token) : live.ReadCurrentAsync(root,progress,token))
                : await scanner.ScanAsync(root,progress,token);
            // Initial navigation can index a whole volume. Keep that work off the window thread.
            var locations = await Task.Run(() =>
                (Folder: FindSurvivingFolder(snapshot, priorFolder), Program: snapshot.Find(programDirectory)));
            token.ThrowIfCancellationRequested();
            if (liveRefresh) ClearSnapshot();
            Snapshot = snapshot;
            unpublished = null;
            Message = snapshot.ScanNotice;
            Folder = locations.Folder;
            ProgramFolder = locations.Program;
            StartQuery();
        }
        finally
        {
            unpublished?.Dispose();
            Scanning = false;
            ScanProgress = null;
        }
    }

    private static int FindSurvivingFolder(SpaceSnapshot snapshot, string? path)
    {
        for (var part = path; part is not null; part = Path.GetDirectoryName(part))
            if (snapshot.Find(part) is { } id && snapshot.Entries[id].IsDirectory && !snapshot.Entries[id].IsSkipped) return id;
        return 0;
    }

    public Task EndAnalysisAsync()
    {
        if (!CanEndAnalysis) return Task.CompletedTask;
        var readers = Task.WhenAll(activeQuery, activeTypes);
        stopVersion++;
        indexDelay?.Cancel();
        pathUpdates.Clear();
        scanRoot = null;
        resumeFolder = null;
        RecycleReport = null;
        ClearSnapshot();
        Message = "本次分析已结束。";
        Notify();
        return Task.WhenAll(readers, releasingView, indexRefresh);
    }

    private void ClearSnapshot()
    {
        indexRead?.Cancel();
        CancelQuery();
        CancelTypes();
        typesReady = false;
        if (Snapshot is { Entries: IIndexedSpaceView } prior)
            releasingView = ReleaseAfterReadersAsync(prior, Task.WhenAll(releasingView, activeQuery, activeTypes));
        Snapshot = null;
        Result = null;
        TypeTotals = [];
        Map = null;
        Folder = 0;
        ProgramFolder = null;
        Highlight = null;
    }

    private static async Task ReleaseAfterReadersAsync(SpaceSnapshot view,Task readers)
    {
        try { await readers.ConfigureAwait(false); }
        finally { await Task.Run(view.Dispose).ConfigureAwait(false); }
    }

    public void Browse(int folder, int? highlight = null)
    {
        if (IsBusy || Snapshot is not { } snapshot || !snapshot.Entries[folder].IsDirectory) return;
        if (Folder == folder && View == SpaceView.Directory && Result is not null)
        {
            Highlight = highlight;
            Notify();
            return;
        }
        Folder = folder;
        View = SpaceView.Directory;
        Highlight = highlight;
        StartQuery();
    }

    public void Show(SpaceView view)
    {
        if (IsBusy || View == view) return;
        View = view;
        Highlight = null;
        StartQuery();
    }

    public void SetFilter(SpaceFilter filter)
    {
        if (IsBusy) return;
        Filter = filter;
        View = SpaceView.LargeFiles;
        Highlight = null;
        StartQuery();
    }

    public void SortBy(SpaceSort sort)
    {
        if (IsBusy) return;
        Descending = Sort == sort ? !Descending : sort != SpaceSort.Name;
        Sort = sort;
        Highlight = null;
        StartQuery();
    }

    public int? TakeHighlight()
    {
        var current = Highlight;
        Highlight = null;
        return current;
    }

    private void StartQuery()
    {
        if (indexRead is not null) { indexDirty = true; indexRead.Cancel(); }
        CancelQuery();
        Result = null;
        if (Snapshot is not { } snapshot)
        {
            Notify();
            return;
        }

        query = new CancellationTokenSource();
        activeQuery = QueryAsync(snapshot, Folder, View, Filter, Sort, Descending, query);
        Notify();
    }

    private async Task QueryAsync(SpaceSnapshot snapshot, int folder, SpaceView view, SpaceFilter filter, SpaceSort sort,
        bool descending, CancellationTokenSource request)
    {
        using (request)
        {
            var token = request.Token;
            var logical = LogicalSize;
            var currentMap = Map?.Folder == folder ? Map : null;
            try
            {
                var (result, map) = await Task.Run(() =>
                {
                    var map = currentMap ?? SpaceQueries.Map(snapshot, folder, token, logical);
                    var rows = view == SpaceView.Types ? null : SpaceQueries.List(snapshot, folder, view, filter, sort, descending, token, logical);
                    return (rows, map);
                }, token);
                if (!ReferenceEquals(query, request) || token.IsCancellationRequested) return;
                Result = result;
                Map = map;
                if (!typesReady && typeQuery is null)
                {
                    typeQuery = new CancellationTokenSource();
                    activeTypes = LoadTypesAsync(snapshot, typeQuery);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (ReferenceEquals(query, request)) Message = "无法显示本次结果。" + error.Message;
            }
            finally
            {
                if (ReferenceEquals(query, request))
                {
                    query = null;
                    Notify();
                }
            }
        }
    }

    private async Task LoadTypesAsync(SpaceSnapshot snapshot, CancellationTokenSource request)
    {
        using (request)
        {
            try
            {
                var logical = LogicalSize;
                var totals = await Task.Run(() => SpaceQueries.Types(snapshot, request.Token, logical), request.Token);
                if (!ReferenceEquals(typeQuery, request)) return;
                TypeTotals = totals;
                typesReady = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (ReferenceEquals(typeQuery, request)) Message = "无法汇总文件类型。" + error.Message;
            }
            finally
            {
                if (ReferenceEquals(typeQuery, request)) { typeQuery = null; Notify(); }
            }
        }
    }

    private void CancelTypes()
    {
        var previous = typeQuery;
        typeQuery = null;
        previous?.Cancel();
    }

    private void CancelQuery()
    {
        var previous = query;
        query = null;
        previous?.Cancel();
    }

    public void AddToPending(IReadOnlyList<int> items)
    {
        if (Snapshot is not { Cancelled: false } snapshot || IsBusy) return;
        var next = snapshot.CollapseSelection(pending.Concat(items.Where(CanQueue)));
        var excluded = items.Count(id => !CanQueue(id));
        Message = excluded > 0 ? $"有 {excluded:N0} 项无法完整核对，已保留在原位置。" : string.Empty;
        pending = next;
        if (pending.Length > 0 && indexRead is not null) { indexDirty = true; indexRead.Cancel(); }
        Notify();
    }

    public bool CanQueue(int id) => Snapshot is { Cancelled: false } snapshot && (uint)id < (uint)snapshot.Entries.Count && snapshot.Entries[id].CanRecycle &&
        (ProgramFolder is not { } program || !snapshot.Contains(id, program) && !snapshot.Contains(program, id));

    public string ProgramLabel(int id) => ProgramFolder is { } program && Snapshot is { } snapshot
        ? id == program ? "当前运行目录" : snapshot.Contains(id, program) ? "包含当前运行目录" : ""
        : "";

    public void RemoveFromPending(IEnumerable<int> items)
    {
        if (IsBusy) return;
        pending = pending.Except(items).ToArray();
        if(pending.Length==0)
        {
            SuspendIndexView();
            if(indexViewActive && indexDirty && indexRefresh.IsCompleted) indexRefresh=RefreshIndexAsync();
        }
        Notify();
    }

    public void Open(int id) => RunInteraction(() => interaction.Open(Snapshot!.FileOf(id)));
    public void Reveal(int id) => RunInteraction(() => interaction.Reveal(Snapshot!.FileOf(id)));
    public void CopyPaths(IEnumerable<int> ids) => RunInteraction(() => interaction.CopyText(
        string.Join(Environment.NewLine, ids.Select(Snapshot!.PathOf))));
    public void OpenRecycleBin() => RunInteraction(recycler.OpenRecycleBin);

    private void RunInteraction(Action action)
    {
        try { action(); Message = string.Empty; }
        catch (Exception error) { Message = error is FileInteractionException ? error.Message : "Windows 无法完成此操作。" + error.Message; }
        Notify();
    }

    public void ShowMessage(string text)
    {
        Message = text;
        Notify();
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
