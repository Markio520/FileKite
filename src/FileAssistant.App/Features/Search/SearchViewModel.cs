using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.FileInput;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Search;

public partial class SearchViewModel : ObservableObject
{
    private readonly IFileSearch search;
    private readonly Func<IReadOnlyList<SearchFile>> batch;
    private readonly Func<IReadOnlyList<SearchFile>, Task<FileImportReport>> addToStaging;
    private readonly IFileInteraction? interaction;
    private readonly ISettingsStore? settings;
    private readonly Func<AppSettings>? preferences;
    internal SynchronizationContext? InputDispatcher { get; init; }
    private AppSettings? appliedPreferences;

    public SearchViewModel(IFileSearch search, Func<IReadOnlyList<SearchFile>> batch,
        Func<IReadOnlyList<SearchFile>, Task<FileImportReport>> addToStaging, IFileInteraction? interaction = null, ISettingsStore? settings = null,
        IDirectoryContentReader? directoryReader = null, Func<AppSettings>? preferences = null)
    {
        this.search = search; this.batch = batch; this.addToStaging = addToStaging;
        this.interaction = interaction; this.settings = settings;
        this.directoryReader = directoryReader;
        this.preferences = preferences;
        appliedPreferences = preferences?.Invoke();
        ColumnWidths.PropertyChanged += (_, _) => { if (ColumnWidths.HasUserWidths) QueuePreferencesSave(); };
    }

    private CancellationTokenSource? pending;
    private int generation;
    private bool active;
    private bool composing;
    private bool needsSearch = true;
    private SearchRequest? completedRequest;
    private IReadOnlyList<SearchFile>? completedBatch;
    private int fetched;
    private string? resultGeneration;
    private string? resultSource;
    private CancellationTokenSource? indexRefresh;
    private bool indexDirty;
    private const int NormalRefreshMs = 1800;
    private int refreshDelayMs = NormalRefreshMs;
    private long lastIndexNotice;
    internal event Action? IndexRowsChanging, IndexRowsChanged;
    public void IndexChanged(string? scope = null)
    {
        if (ScopeIndex == (int)SearchScope.Batch) return;
        if (IsFolderScope && !string.IsNullOrEmpty(scope) &&
            !IsInFolder(Folder,scope) && !IsInFolder(scope,Folder)) return;
        var now = Environment.TickCount64;
        if (now - lastIndexNotice > 10_000) refreshDelayMs = NormalRefreshMs;
        lastIndexNotice = now;
        indexDirty = true;
        QueueIndexRefresh();
    }
    private static bool IsInFolder(string path,string directory) => string.Equals(path,directory,StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

    public void FilePathsChanged(IReadOnlyList<PathChange>? changes = null, IReadOnlyList<string>? removed = null)
    {
        var paths = (changes ?? []).SelectMany(change => new[] { change.Before, change.After }).Concat(removed ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) search.InvalidateFolder(); else search.InvalidatePaths(paths);
        if (changes?.Any(change => change.IsDirectory) == true || removed?.Count > 0) InvalidateLocations();
        if (pending is not null) { Cancel(); IsCancelled = false; }
        if (paths.Length > 0) UpdateChangedRows(changes ?? [], removed ?? []);
        if (IsFolderScope && changes is { Count: > 0 } && Folder.Length > 0)
        {
            var mapped = NamingRulesEngine.MapPath(Folder, changes);
            if (mapped != Folder) { UseFolder(mapped); return; }
        }
        if (ScopeIndex != (int)SearchScope.Batch && completedRequest != null && completedRequest == Request())
        {
            indexDirty = true;
            QueueIndexRefresh();
        }
        else
        {
            needsSearch = true;
            if (active && !composing) _ = RunAsync(false, true);
        }
        NotifyResultActions();
    }

    private void UpdateChangedRows(IReadOnlyList<PathChange> changes, IReadOnlyList<string> removed)
    {
        if (Results.Count == 0) return;
        var comparer = PathComparer((SearchScope)ScopeIndex);
        var moves = Results.Select(row => (Row: row, Path: NamingRulesEngine.MapPath(row.FullPath, changes)))
            .Where(item => item.Path != item.Row.FullPath).ToDictionary(item => item.Row, item => item.Path);
        var destinations = moves.Values.ToHashSet(comparer);
        var folder = IsFolderScope ? NamingRulesEngine.MapPath(Folder, changes) : null;
        var excluded = new SearchDirectoryFilter(appliedPreferences?.ExcludedDirectories ?? []);
        var rule = completedRequest == null ? null : SearchRules.Compile(completedRequest with { PinyinSearch = appliedPreferences?.PinyinSearch == true });
        // Content stays with the file on a rename. The live query verifies compound content rules and metadata.
        bool hasContent = rule?.Groups.SelectMany(group => group).Any(term => term.Condition.Field == SearchField.Content) == true;
        int count = Results.Count;
        IndexRowsChanging?.Invoke();
        try
        {
            for (int i = Results.Count - 1; i >= 0; i--)
            {
                var row = Results[i];
                bool moved = moves.TryGetValue(row, out var destination);
                var path = destination ?? row.FullPath;
                if (removed.Any(item => IsInFolder(path, item)) || !moved && destinations.Contains(path))
                {
                    Results.RemoveAt(i);
                    continue;
                }
                if (!moved) continue;
                var file = row.File with { FullPath = path };
                if (folder != null && (!IsInFolder(path, folder) || !IncludeSubfolders && !comparer.Equals(file.Directory, folder)) ||
                    excluded.Excludes(file.IsFolder ? path : file.Directory) || !hasContent && rule != null && !rule.Matches(file))
                    Results.RemoveAt(i);
                else if (row.Update(file) && ReferenceEquals(row, SelectedItem)) NotifySelectedDetails();
            }
        }
        finally { IndexRowsChanged?.Invoke(); }
        fetched = Math.Max(0, fetched - (count - Results.Count));
        Total = Math.Max(0, Total - (count - Results.Count));
        if (SelectedItem != null && !Results.Contains(SelectedItem))
        {
            SelectedItem = null;
            SelectionCount = Math.Clamp(SelectionCount - 1, 0, Results.Count);
        }
        OnPropertyChanged(nameof(ShowEmpty));
    }
    public SearchColumnWidths ColumnWidths { get; } = new();
    public void ResetColumnWidths() { ColumnWidths.Reset(); QueuePreferencesSave(); }

    public void ApplyPreferences()
    {
        if (preferences?.Invoke() is not { } current) return;
        var previous = appliedPreferences;
        appliedPreferences = current;
        bool unitsChanged = previous?.Units != current.Units;
        if (unitsChanged)
        {
            OnPropertyChanged(nameof(SizeOptions)); OnPropertyChanged(nameof(SizeUnitOptions)); OnPropertyChanged(nameof(SizeUnitHint));
            OnPropertyChanged(nameof(SizeLabel)); OnPropertyChanged(nameof(FilterError)); OnPropertyChanged(nameof(HasFilterError));
        }
        if (previous?.ShowExtensions != current.ShowExtensions || previous.Units != current.Units)
        {
            foreach (var row in Results) row.RefreshDisplay();
            OnPropertyChanged(nameof(DetailName)); OnPropertyChanged(nameof(DetailSize));
        }
        bool visibilityChanged = previous == null || previous.ShowHidden != current.ShowHidden ||
            !previous.ExcludedDirectories.SequenceEqual(current.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);
        if (visibilityChanged) InvalidateLocations();
        if (visibilityChanged || unitsChanged && SizeIndex > 0 || previous == null || previous.PinyinSearch != current.PinyinSearch || previous.ContentLimitMB != current.ContentLimitMB ||
            (previous.IndexedVolumes == null) != (current.IndexedVolumes == null) ||
            !(previous.IndexedVolumes ?? []).SequenceEqual(current.IndexedVolumes ?? [], StringComparer.OrdinalIgnoreCase))
        {
            needsSearch = true;
            if (active && !composing) _ = RunAsync(false, false);
        }
    }

    [ObservableProperty] public partial string QueryText { get; set; } = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFolderScope), nameof(ScopeLabel))] public partial int ScopeIndex { get; set; }
    [ObservableProperty] public partial string Folder { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IncludeSubfolders { get; set; } = true;
    [ObservableProperty] public partial bool MatchPath { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KindLabel))] public partial int KindIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeLabel), nameof(IsCustomSize))] public partial int SizeIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DateLabel), nameof(IsCustomDate))] public partial int DateIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NameSortGlyph), nameof(PathSortGlyph), nameof(SizeSortGlyph), nameof(ModifiedSortGlyph),
        nameof(NameSortDescription), nameof(PathSortDescription), nameof(SizeSortDescription), nameof(ModifiedSortDescription))] public partial int SortIndex { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowSearchError), nameof(ShowInlineError), nameof(ResultCountLabel))] public partial ObservableCollection<SearchResultItem> Results { get; set; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanLoadMore), nameof(CanAdd), nameof(CanUseResults), nameof(ShowEmpty), nameof(ResultCountLabel))] public partial bool IsBusy { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUseResults), nameof(CanAdd))] public partial bool IsLoadingMore { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(CanLoadMore), nameof(CanAdd), nameof(CanUseResults), nameof(ShowSearchError), nameof(ShowInlineError))] public partial bool HasError { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowInlineError), nameof(InlineErrorTitle), nameof(LoadMoreLabel))] public partial bool HasPageError { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial string Status { get; set; } = "正在准备文件列表…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyDescription))]
    public partial bool IsPartialIndex { get; set; }
    [ObservableProperty] public partial string Feedback { get; set; } = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAdd))] public partial bool IsAddingToStaging { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EmptyTitle), nameof(EmptyDescription))] public partial bool IsCancelled { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanLoadMore), nameof(ResultCountLabel))] public partial long Total { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanAdd), nameof(ResultCountLabel), nameof(IslandSummary))] public partial int SelectionCount { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowSelectionHint), nameof(DetailName), nameof(DetailPath), nameof(DetailType), nameof(DetailSize), nameof(DetailModified), nameof(DetailGlyph))]
    public partial SearchResultItem? SelectedItem { get; set; }

    public bool IsFolderScope => ScopeIndex == 1;
    public string ScopeLabel => SearchFilterOptions.Scopes.First(item => item.Value == ScopeIndex).Label;
    public string KindLabel => SearchFilterOptions.Kinds.First(item => item.Value == KindIndex).Label;
    public string SizeLabel => SizeOptions.First(item => item.Value == SizeIndex).Label;
    public string DateLabel => SearchFilterOptions.Dates.First(item => item.Value == DateIndex).Label;
    public bool CanUseResults => (!IsBusy || IsLoadingMore) && !HasError && completedRequest is not null && completedRequest == Request();
    public bool CanLoadMore => !IsBusy && CanUseResults && fetched < Total;
    public bool CanAdd => CanUseResults && SelectionCount > 0 && !IsAddingToStaging;
    public bool ShowSearchError => HasError && Results.Count == 0;
    public bool ShowInlineError => (HasError || HasPageError) && Results.Count > 0;
    public string InlineErrorTitle => HasPageError ? "后续结果加载失败，已加载文件仍可使用" : "搜索未完成，列表保留上次结果";
    public string LoadMoreLabel => HasPageError ? "重试加载" : "加载更多";
    public bool HasSearchSource => !IsFolderScope || !string.IsNullOrWhiteSpace(Folder);
    public bool ShowEmpty => HasSearchSource && !IsBusy && !HasError && !HasFilterError && Results.Count == 0;
    public bool HasSelection => SelectedItem is not null;
    public bool ShowSelectionHint => !HasSelection;
    public string IslandSummary => $"已选 {SelectionCount:N0} 项";
    public string EmptyTitle => IsCancelled ? "搜索已取消" : IsPartialIndex ? "已索引的文件中暂无匹配" : "没有匹配结果";
    public string EmptyDescription => IsCancelled ? "已停止本次查找，可修改条件或刷新重新搜索。" : IsPartialIndex
        ? "索引仍在更新，新匹配会自动出现。" : "调整关键词或筛选条件，也可以更换搜索范围。";
    public string ResultCountLabel => (completedRequest is not null && !CanUseResults ? "上次结果 · " : string.Empty)
        + $"匹配 {(completedRequest is null ? "—" : Total.ToString("N0"))} 项 · 已加载 {Results.Count:N0} 项 · 已选 {SelectionCount:N0} 项";
    public string DetailName => SelectedItem?.Name ?? "选择文件查看信息";
    public string DetailPath => SelectedItem?.FullPath ?? string.Empty;
    public string DetailType => SelectedItem?.TypeLabel ?? string.Empty;
    public string DetailSize => SelectedItem?.SizeLabel ?? string.Empty;
    public string DetailModified => SelectedItem?.ModifiedLabel ?? string.Empty;
    public string DetailGlyph => SelectedItem?.Glyph ?? "\uE721";

    partial void OnQueryTextChanged(string value) => Schedule();
    partial void OnScopeIndexChanged(int value) => Schedule();
    partial void OnFolderChanged(string value) => Schedule();
    partial void OnIncludeSubfoldersChanged(bool value) => Schedule();
    partial void OnMatchPathChanged(bool value) => Schedule();
    partial void OnKindIndexChanged(int value) => Schedule();
    partial void OnSizeIndexChanged(int value) => Schedule();
    partial void OnDateIndexChanged(int value) => Schedule();
    partial void OnSortIndexChanged(int value) { Schedule(); QueuePreferencesSave(); }

    public void Activate()
    {
        active = true;
        if (ScopeIndex == (int)SearchScope.Batch && completedBatch is not null && !completedBatch.SequenceEqual(batch())) needsSearch = true;
        if (needsSearch && !composing) _ = RunAsync(false, false);
        else QueueIndexRefresh();
    }
    public void Deactivate()
    {
        active = false;
        CancelIndexRefresh();
        if (pending is null) return;
        // Resume an interrupted replacement on return; a cancelled next page can be requested again.
        needsSearch = !IsLoadingMore;
        Cancel();
    }
    private void Schedule()
    {
        needsSearch = true;
        if (updatingConditions) return;
        PresetIndex = -1;
        OnPropertyChanged(nameof(FilterError)); OnPropertyChanged(nameof(HasFilterError));
        NotifyResultActions();
        if (active && !composing) _ = RunAsync(false, true);
    }

    public void SetInputComposition(bool composing)
    {
        this.composing = composing;
        if (composing) Cancel();
        else Schedule();
    }

    [RelayCommand] private async Task RefreshAsync()
    {
        search.InvalidateFolder();
        if (ScopeIndex == 0) search.RetryIndexInitialization();
        await RunAsync(false, false);
    }
    [RelayCommand] private Task LoadMoreAsync() => CanLoadMore ? RunAsync(true, false) : Task.CompletedTask;
    [RelayCommand] private Task RetryAsync() => HasError ? RefreshAsync() : LoadMoreAsync();
    [RelayCommand] private void Cancel()
    {
        CancelIndexRefresh();
        if (pending is null) return;
        ++generation;
        pending.Cancel();
        pending = null;
        IsCancelled = true;
        Status = IsLoadingMore ? "已停止加载，已加载文件仍可使用" : Results.Count > 0
            ? "搜索已取消 · 列表保留上次结果，可刷新重试" : "已取消搜索 · 可修改条件或刷新";
        IsBusy = false;
        IsLoadingMore = false;
    }
    [RelayCommand] private void ResetFilters()
    {
        updatingConditions = true;
        QueryText = string.Empty; KindIndex = 0; SizeIndex = 0; DateIndex = 0; MatchPath = false;
        CustomMinimum = string.Empty; CustomMaximum = string.Empty; CustomDateFrom = null; CustomDateThrough = null;
        updatingConditions = false;
        Schedule();
    }

    public async Task AddSelectionAsync(IReadOnlyList<SearchResultItem> selection)
    {
        if (!CanAdd || selection.Count == 0 || selection.Any(item => !Results.Contains(item))) return;
        IsAddingToStaging = true;
        Feedback = "正在检查文件并加入暂存区…";
        try
        {
            var report = await addToStaging(selection.Select(i => i.File).ToArray());
            Feedback = report.Summary + (report.Items.Count > 0 ? "；打开文件暂存区可查看明细。" : string.Empty);
        }
        catch (Exception) { Feedback = "无法加入暂存区，请检查文件路径后重试。"; }
        finally { IsAddingToStaging = false; }
    }

    public void OpenFile(SearchResultItem item) { if (CanUseResults && Results.Contains(item)) Interact(() => interaction!.Open(item.File)); }
    public void OpenLocation(SearchResultItem item) { if (CanUseResults && Results.Contains(item)) Interact(() => interaction!.Reveal(item.File)); }
    public void CopySelection(IReadOnlyList<SearchResultItem> selection, bool namesOnly)
    {
        if (!CanUseResults || selection.Count == 0 || selection.Any(item => !Results.Contains(item))) return;
        Interact(() =>
        {
            interaction!.CopyText(string.Join(Environment.NewLine, selection.Select(item => namesOnly ? item.File.Name : item.FullPath)));
            Feedback = $"已复制 {selection.Count} 项的{(namesOnly ? "名称" : "完整路径")}";
        });
    }
    private void Interact(Action action)
    {
        Feedback = string.Empty;
        try { action(); }
        catch (Exception e) { Feedback = e is FileInteractionException ? e.Message : "无法完成此操作，请检查文件访问权限后重试。"; }
    }

    private SearchRequest? Request()
    {
        if (!TryGetCustomRanges(out var minimum, out var maximum, out var from, out var before, out _)) return null;
        if (!IsCustomSize) (minimum, maximum) = PresetSize(SizeIndex, FilterUnits);
        return new()
        {
            Text = QueryText, Scope = (SearchScope)ScopeIndex, Folder = Folder, AllowPartialIndex = true, IncludeDirectoryUsage = true,
            ContentLimitMB = preferences?.Invoke().ContentLimitMB,
            IncludeSubfolders = IncludeSubfolders, MatchPath = MatchPath, Kind = (SearchKind)KindIndex,
            MinimumSize = minimum, MaximumSize = maximum,
            ModifiedFrom = DateIndex switch { 1 => DateTimeOffset.Now.Date, 2 => DateTimeOffset.Now.Date.AddDays(-6), 3 => DateTimeOffset.Now.Date.AddDays(-29), 4 => from, _ => null },
            ModifiedBefore = IsCustomDate ? before : null,
            Sort = (SearchSort)(SortIndex / 2), Descending = SortIndex % 2 == 1
        };
    }

    private async Task RunAsync(bool append, bool debounce)
    {
        using var cancellation = new CancellationTokenSource();
        var previous = pending;
        pending = cancellation;
        // Cancel can synchronously resume the old finally block. Publish its replacement first.
        var version = ++generation;
        CancelIndexRefresh();
        if (!append) { indexDirty = false; refreshDelayMs = NormalRefreshMs; }
        previous?.Cancel();
        if (version != generation) return;
        var token = cancellation.Token;
        var request = append && completedRequest is not null ? completedRequest with { Offset = fetched } : Request();
        needsSearch = false;
        HasError = false; HasPageError = false; IsCancelled = false; Message = string.Empty; Feedback = string.Empty;
        IsLoadingMore = append; IsBusy = true;
        if (request is null)
        {
            pending = null; IsBusy = false; IsLoadingMore = false;
            Status = Results.Count > 0 ? "请修正筛选条件 · 列表保留上次结果" : "请填写有效的筛选区间";
            NotifyResultActions(); OnPropertyChanged(nameof(ShowEmpty));
            return;
        }
        if (!HasSearchSource)
        {
            Results = []; fetched = 0; Total = 0; SelectedItem = null; SelectionCount = 0; completedRequest = null; completedBatch = null;
            pending = null; IsBusy = false; Status = "选择文件夹以查看内容";
            OnPropertyChanged(nameof(ShowEmpty));
            return;
        }
        Status = append ? "正在加载更多，已加载文件仍可使用…" : Results.Count > 0 ? "正在更新搜索 · 列表暂时显示上次结果…" : "正在搜索…";
        try
        {
            if (debounce)
            {
                // Merge changes posted in one UI turn without making every indexed keystroke
                // wait for a timer. Give filesystem walks and content scans time to settle.
                var indexedNames = request.Scope == SearchScope.Everything &&
                    !request.Text.Contains("content:", StringComparison.OrdinalIgnoreCase);
                if (indexedNames && InputDispatcher is { } dispatcher)
                {
                    var nextTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dispatcher.Post(static state => ((TaskCompletionSource)state!).TrySetResult(), nextTurn);
                    await nextTurn.Task;
                }
                else await Task.Delay(indexedNames ? 16 : 150, token);
            }
            if (version != generation || token.IsCancellationRequested) return;
            var stopwatch = Stopwatch.StartNew();
            var progress = new ThrottledSearchProgress(new Progress<SearchProgress>(p =>
            {
                if (version == generation && !token.IsCancellationRequested && IsBusy)
                    Status = (!append && Results.Count > 0 ? "正在更新搜索（列表为上次结果） · " : string.Empty)
                        + (p.Message ?? $"正在扫描 · 已发现 {p.Examined:N0} 项");
            }));
            var batchFiles = request.Scope == SearchScope.Batch ? batch().ToArray() : Array.Empty<SearchFile>();
            var page = await search.SearchAsync(request, batchFiles, progress, token);
            if (version != generation || token.IsCancellationRequested) return;
            if (append && page.IndexGeneration is not null && resultGeneration != page.IndexGeneration)
            {
                page = await search.SearchAsync(request with { Offset = 0, PageSize = checked(fetched + request.PageSize) },
                    batchFiles, null, token);
                if (version != generation || token.IsCancellationRequested) return;
                ReconcileRows(page.Items, PathComparer(request.Scope));
                fetched = page.Items.Count;
            }
            else if (append)
            {
                var paths = Results.Select(i => i.FullPath).ToHashSet(PathComparer(request.Scope));
                foreach (var file in page.Items)
                    if (paths.Add(file.FullPath)) Results.Add(new(file, ColumnWidths));
                fetched += page.Items.Count;
            }
            else
            {
                // Publish a complete page once: typing and waiting never empty the visible list.
                Results = new(page.Items.DistinctBy(file => file.FullPath, PathComparer(request.Scope)).Select(file => new SearchResultItem(file, ColumnWidths)));
                fetched = page.Items.Count; SelectedItem = null; SelectionCount = 0;
            }
            resultGeneration = page.IndexGeneration;
            resultSource = SourceLabel(page);
            IsPartialIndex = page.IsPartialIndex;
            Total = page.Total;
            completedRequest = request with { Offset = 0 };
            completedBatch = request.Scope == SearchScope.Batch ? batchFiles : null;
            Status = $"{resultSource} · {stopwatch.ElapsedMilliseconds:N0} ms";
            if (page.Items.Count == 0 && fetched < Total) { fetched = (int)Math.Min(Total, int.MaxValue); Feedback = "索引在查询期间发生变化，请刷新获取最新结果。"; }
        }
        catch (OperationCanceledException) { if (version == generation) { IsCancelled = true; Status = "已取消搜索 · 可修改条件或刷新"; } }
        catch (Exception e)
        {
            if (version != generation) return;
            Message = e is SearchException ? e.Message : "无法完成搜索，请检查目录权限后重试。";
            HasPageError = append;
            HasError = !append;
            Status = append ? $"后续结果加载失败 · 已载入 {Results.Count:N0} / {Total:N0} 项 · 可重试加载"
                : Results.Count > 0 ? "搜索未完成 · 列表保留上次结果" : "搜索未完成";
        }
        finally
        {
            if (version == generation)
            {
                pending = null; IsBusy = false; IsLoadingMore = false;
                if (needsSearch && active && !composing) _ = RunAsync(false, true);
                else QueueIndexRefresh();
                NotifyResultActions(); OnPropertyChanged(nameof(ShowEmpty));
            }
        }
    }

    private void CancelIndexRefresh()
    {
        var previous = indexRefresh;
        indexRefresh = null;
        if (previous is not null) { indexDirty = true; previous.Cancel(); }
    }

    private void QueueIndexRefresh()
    {
        if (!indexDirty || !active || composing || pending is not null || indexRefresh is not null ||
            ScopeIndex == (int)SearchScope.Batch || IsCancelled || HasFilterError || !HasSearchSource) return;
        if (completedRequest is null || completedRequest != Request())
        {
            if (HasError) _ = RunAsync(false, true);
            return;
        }
        indexRefresh = new();
        _ = RefreshIndexAsync(indexRefresh);
    }

    private async Task RefreshIndexAsync(CancellationTokenSource refresh)
    {
        using (refresh)
        {
            var token = refresh.Token;
            try
            {
                // A fixed window also makes progress while the disk changes continuously.
                await Task.Delay(IsPartialIndex ? 150 : refreshDelayMs, token);
                indexDirty = false;
                var request = completedRequest;
                if (request is null || request != Request()) return;
                var page = await search.SearchAsync(request with { PageSize = Math.Max(request.PageSize, fetched) },
                    [], null, token);
                if (token.IsCancellationRequested || !active || completedRequest != request) return;
                var source = SourceLabel(page);
                bool changed = ReconcileRows(page.Items, PathComparer(request.Scope)) || Total != page.Total || resultSource != source;
                refreshDelayMs = changed ? NormalRefreshMs : Math.Min(6000, 2 * refreshDelayMs);
                fetched = page.Items.Count;
                Total = page.Total;
                resultGeneration = page.IndexGeneration;
                IsPartialIndex = page.IsPartialIndex;
                if (HasError) { HasError = false; Message = string.Empty; }
                if (resultSource != source)
                {
                    resultSource = source;
                    Status = source;
                }
                if (changed)
                {
                    NotifyResultActions();
                    OnPropertyChanged(nameof(ShowEmpty));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    Status = error is SearchException ? error.Message : "索引更新未完成，可刷新重试。";
                    Message = Status;
                    HasError = true;
                    resultSource = null;
                }
            }
            finally
            {
                if (ReferenceEquals(indexRefresh, refresh))
                {
                    indexRefresh = null;
                    QueueIndexRefresh();
                }
            }
        }
    }

    // Keep the ItemsSource and surviving row objects so a background change
    // does not reset scrolling, selection or the pages the user already loaded.
    private static string SourceLabel(SearchPage page) => page.Skipped > 0 ? $"{page.Source} · {page.Skipped:N0} 项无法读取" : page.Source;
    private static StringComparer PathComparer(SearchScope scope) => scope == SearchScope.Batch ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private bool ReconcileRows(IReadOnlyList<SearchFile> files, StringComparer comparer)
    {
        if (files.Count == Results.Count && files.Select((file, i) => file == Results[i].File).All(equal => equal))
            return false;
        IndexRowsChanging?.Invoke();
        try { ApplyIndexRows(files, comparer); }
        finally { IndexRowsChanged?.Invoke(); }
        return true;
    }

    private void ApplyIndexRows(IReadOnlyList<SearchFile> files, StringComparer comparer)
    {
        var wanted = files.DistinctBy(f => f.FullPath, comparer).ToArray();
        var remaining = wanted.Select(f => f.FullPath).ToHashSet(comparer);
        for (int i = Results.Count - 1; i >= 0; i--)
            if (!remaining.Contains(Results[i].FullPath)) Results.RemoveAt(i);
        var existing = Results.ToDictionary(r => r.FullPath, comparer);
        for (int i = 0; i < wanted.Length; i++)
        {
            var file = wanted[i];
            if (!existing.TryGetValue(file.FullPath, out var row))
            {
                Results.Insert(i, new(file, ColumnWidths));
                continue;
            }
            if (!ReferenceEquals(Results[i], row)) Results.Move(Results.IndexOf(row), i);
            if (row.Update(file) && ReferenceEquals(row, SelectedItem)) NotifySelectedDetails();
        }
        if (SelectedItem is { } selected && !remaining.Contains(selected.FullPath))
        {
            SelectedItem = null;
            SelectionCount = Math.Clamp(SelectionCount - 1, 0, Results.Count);
        }
    }

    private void NotifySelectedDetails()
    {
        OnPropertyChanged(nameof(DetailName)); OnPropertyChanged(nameof(DetailPath)); OnPropertyChanged(nameof(DetailSize));
        OnPropertyChanged(nameof(DetailModified)); OnPropertyChanged(nameof(DetailType)); OnPropertyChanged(nameof(DetailGlyph));
    }

    private void NotifyResultActions()
    {
        OnPropertyChanged(nameof(CanUseResults));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(ResultCountLabel));
    }
}
