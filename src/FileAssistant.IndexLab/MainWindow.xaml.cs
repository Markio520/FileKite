using System.Diagnostics;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Indexing;
using FileAssistant.Infrastructure.FileSystem.Ntfs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace FileAssistant.IndexLab;

public sealed partial class MainWindow : Window
{
    private readonly IndexCatalog catalog;
    private IndexInfo? info;
    private CancellationTokenSource? scan;
    private CancellationTokenSource? query;
    private Task activeScan = Task.CompletedTask;
    private bool initialized, replacingScopes, closing;
    private int folder, parent = -1, offset;
    private SpaceKind category;
    private IndexMap? map;

    public MainWindow() : this(new IndexCatalog(Path.Combine(FileAssistant.Core.AppProfile.Directory, "IndexLab", "Indexes")))
    { }

    internal MainWindow(IndexCatalog catalog)
    {
        this.catalog = catalog;
        InitializeComponent();
        AppWindow.Resize(new(1050, 760));
        AppWindow.Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (initialized) return;
        initialized = true;
        try { await ReloadScopesAsync(null); }
        catch (Exception ex) { ScanMessage(ex.Message); }
    }

    private async Task ReloadScopesAsync(string? select)
    {
        var unreadable = new List<string>();
        var scopes = await Task.Run(() => catalog.List(unreadable.Add));
        if (closing) return;
        replacingScopes = true;
        try
        {
            SavedScopes.ItemsSource = scopes;
            SavedScopes.SelectedItem = scopes.FirstOrDefault(item => string.Equals(item.Root, select, StringComparison.OrdinalIgnoreCase))
                ?? scopes.FirstOrDefault();
        }
        finally { replacingScopes = false; }
        await SelectAsync(SavedScopes.SelectedItem as IndexInfo);
        if (unreadable.Count > 0) ScanMessage($"有 {unreadable.Count} 份索引无法读取。{unreadable[0]}");
    }

    private async void ScopeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (initialized && !replacingScopes) await SelectAsync(SavedScopes.SelectedItem as IndexInfo);
    }

    private async Task SelectAsync(IndexInfo? selected)
    {
        info = selected;
        folder = offset = 0;
        parent = -1;
        category = SpaceKind.All;
        if (selected is not null)
        {
            PathInput.Text = selected.Root;
            var uncertain = selected.IssueCounts.Values.Sum();
            SnapshotSummary.Text = $"{selected.Files:N0} 个文件 · {selected.Folders:N0} 个目录 · {DisplayRow.Bytes(selected.CountedBytes)} · {selected.Updated.ToLocalTime():MM/dd HH:mm} 扫描";
            if (uncertain > 0) SnapshotSummary.Text += $" · {uncertain:N0} 条扫描提示";
            var stamp = $"扫描于 {selected.Updated.ToLocalTime():g} · {selected.Method}";
            ToolTipService.SetToolTip(SnapshotSummary, string.Join("\n", new[] { stamp, selected.Notice }
                .Concat(selected.Issues.Take(6).Select(issue => issue.Path + " · " + issue.Message)).Where(s => s.Length != 0)));
        }
        await RefreshAsync();
    }

    private async void PickFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var picked = await picker.PickSingleFolderAsync();
            if (picked is not null && !closing) PathInput.Text = picked.Path;
        }
        catch (Exception ex) { ScanMessage(ex.Message); }
    }

    private async void StartScan(object sender, RoutedEventArgs args)
    {
        if (scan is not null || closing) return;
        var path = PathInput.Text.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) { ScanMessage("请输入可访问的磁盘或文件夹完整路径。"); return; }
        activeScan = BuildAsync(path);
        await activeScan;
    }

    private async Task BuildAsync(string path)
    {
        using var work = new CancellationTokenSource();
        scan = work;
        ScanButton.IsEnabled = false;
        ScanProgress.Visibility = CancelButton.Visibility = Visibility.Visible;
        ScanProgress.IsIndeterminate = true;
        ScanMessage("正在读取目录…");
        try
        {
            var scanner = new IndexedScanner(catalog, new FastSpaceScanner(Path.Combine(AppContext.BaseDirectory, "FileAssistant.SpaceScan.exe")));
            var progress = new Progress<SpaceScanProgress>(p =>
            {
                if (!closing && scan == work) ScanMessage($"{p.Stage}  ·  {p.Files:N0} 个文件  /  {p.Folders:N0} 个目录");
            });
            var result = await scanner.BuildAsync(path, progress, work.Token);
            if (closing) return;
            await ReloadScopesAsync(result.Root);
            ScanMessage(string.Join(" · ", new[] { "索引已保存", result.Method, result.Notice }.Where(s => s.Length > 0)));
        }
        catch (OperationCanceledException) { if (!closing) ScanMessage("已取消，本次扫描未替换已保存的索引。"); }
        catch (Exception ex) { if (!closing) ScanMessage("建立索引失败：" + ex.Message); }
        finally
        {
            scan = null;
            if (!closing)
            {
                ScanButton.IsEnabled = true;
                ScanProgress.IsIndeterminate = false;
                ScanProgress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ScanMessage(string message) { ScanState.Text = message; ScanState.Visibility = Visibility.Visible; }
    private void CancelScan(object sender, RoutedEventArgs args) => scan?.Cancel();

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        query?.Cancel();
        if (scan is null) { closing = true; return; }
        args.Cancel = true;
        if (closing) return;
        closing = true;
        scan.Cancel();
        await activeScan;
        Close();
    }

    private async void TabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!initialized) return;
        offset = 0;
        if (Tabs.SelectedItem != LargeTab) category = SpaceKind.All;
        await RefreshAsync();
    }
    private async void QueryChanged(object sender, TextChangedEventArgs args)
    {
        if (!initialized || Tabs.SelectedItem != SearchTab) return;
        offset = 0;
        await RefreshAsync(debounce: true);
    }

    private async void QueryKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter) return;
        args.Handled = true;
        offset = 0;
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool debounce = false)
    {
        if (closing) return;
        query?.Cancel();
        using var work = new CancellationTokenSource();
        query = work;
        var token = work.Token;
        var isSearch = Tabs.SelectedItem == SearchTab;
        var isTypes = Tabs.SelectedItem == TypeTab;
        var mode = isSearch ? IndexView.Search : Tabs.SelectedItem == LargeTab ? IndexView.LargeFiles : IndexView.Directory;
        Query.Visibility = isSearch ? Visibility.Visible : Visibility.Collapsed;
        Location.Visibility = isSearch ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = !isTypes && mode == IndexView.Directory ? Visibility.Visible : Visibility.Collapsed;
        NameHeader.Text = isTypes ? "文件类型" : "名称 / 位置";
        SizeHeader.Text = isTypes ? "文件数" : "逻辑大小";
        var selected = info;
        var requestedFolder = folder;
        var start = offset;
        var term = Query.Text;
        var kind = category;
        Next.IsEnabled = Previous.IsEnabled = false;
        try
        {
            if (selected is null) return;
            if (debounce) await Task.Delay(140, token);
            var timer = Stopwatch.StartNew();
            var store = catalog.ForRoot(selected.Root);
            if (isTypes)
            {
                var types = await Task.Run(() => store.Types(selected, token), token);
                token.ThrowIfCancellationRequested();
                map = null;
                Location.Text = selected.Root;
                Display(types.Select(DisplayRow.From).ToArray(), types.Count, 0, timer.Elapsed, "种类型", false);
                return;
            }
            var page = await Task.Run(() => store.Page(selected, mode, requestedFolder, term, kind, start, token), token);
            token.ThrowIfCancellationRequested();
            parent = page.Parent;
            BackButton.IsEnabled = mode == IndexView.Directory && parent >= 0;
            Location.Text = mode == IndexView.Directory ? page.FolderPath : kind == SpaceKind.All
                ? selected.Root : selected.Root + "  /  " + SpaceKinds.Label(kind);
            ToolTipService.SetToolTip(Location, Location.Text);
            map = page.Map;
            Display(page.Rows.Select(DisplayRow.From).ToArray(), page.Total, start, timer.Elapsed, "项", true);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (IndexGenerationChangedException) when (!closing && !work.IsCancellationRequested)
        {
            // Local row IDs belong to a single scan. Return to the new root before reusing any selection.
            try { await ReloadScopesAsync(selected?.Root); }
            catch (Exception ex) { ScanMessage(ex.Message); }
        }
        catch (Exception ex)
        {
            if (!work.IsCancellationRequested && !closing)
            {
                map = null; DrawMap(); Results.ItemsSource = null;
                Empty.Text = ex.Message; Empty.Visibility = Visibility.Visible;
                PageStatus.Text = "读取失败";
            }
        }
        finally { if (query == work) query = null; }
    }

    private void Display(DisplayRow[] rows, int total, int start, TimeSpan elapsed, string unit, bool paged)
    {
        Results.ItemsSource = rows;
        Empty.Text = "没有匹配的项目。";
        Empty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageStatus.Text = $"{total:N0} {unit}  ·  {elapsed.TotalMilliseconds:N0} ms";
        if (paged && rows.Length > 0) PageStatus.Text += $"  ·  {start + 1:N0}–{start + rows.Length:N0}";
        Previous.IsEnabled = paged && start > 0;
        Next.IsEnabled = paged && start + rows.Length < total;
        DrawMap();
    }

    private async void PreviousPage(object sender, RoutedEventArgs args) { offset = Math.Max(0, offset - IndexStore.PageSize); await RefreshAsync(); }
    private async void NextPage(object sender, RoutedEventArgs args) { offset += IndexStore.PageSize; await RefreshAsync(); }
    private async void GoUp(object sender, RoutedEventArgs args) { if (parent >= 0) await BrowseAsync(parent); }
    private async Task BrowseAsync(int id)
    {
        if (query is not null) return;
        folder = id; offset = 0;
        if (Tabs.SelectedItem != DirectoryTab) Tabs.SelectedItem = DirectoryTab;
        else await RefreshAsync();
    }

    private async Task OpenSelectedAsync()
    {
        if (query is not null) return;
        if (Results.SelectedItem is not DisplayRow selected) return;
        if (selected.File is { Entry.IsDirectory: true } directory) await BrowseAsync(directory.Id);
        else if (selected.Category is { } kind) { category = kind; Tabs.SelectedItem = LargeTab; }
        else if (selected.File is { } file) ScanMessage(file.Path + " · " + file.Entry.Modified?.ToLocalTime().ToString("g"));
    }
    private async void OpenResult(object sender, DoubleTappedRoutedEventArgs args) => await OpenSelectedAsync();
    private async void ResultKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) { args.Handled = true; await OpenSelectedAsync(); }
    }
}
