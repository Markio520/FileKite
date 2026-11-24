using System.Globalization;
using System.IO;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.FileInput;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileAssistant.App.Features.SpaceAnalysis;

public sealed partial class SpaceAnalysisView : UserControl
{
    private const int PageSize = 250;
    private const double MinimumMapHeight = 96;
    private const double DefaultMapHeight = 174;
    private readonly SpaceAnalysisViewModel model = App.Space;
    private IReadOnlyList<SpaceDrive> drives = [];
    private SpaceVolume? selectedVolume;
    private string measuredRoot = "";
    private long volumeReadAt;
    private bool active;
    private bool rendering;
    private bool showingDialog;
    private bool foldersOpen = true;
    private double? preferredFolderWidth = App.PaneWidths.GetWidth(FolderWidthKey);
    private double mapHeight = DefaultMapHeight;
    private double reopenMapHeight = DefaultMapHeight;
    private double centerViewportHeight = double.PositiveInfinity;
    private SpaceSnapshot? shownSnapshot;
    private SpaceQueryResult? shownResult;
    private IReadOnlyList<SpaceTypeTotal>? shownTypes;
    private readonly List<SpaceUsageSegment> segments = [];
    private SpaceRow[] pageRows = [];
    private int shownFolder = -1;
    private int displayRevision = -1;
    private int page;
    private int? inspecting;

    public event EventHandler<string>? NavigateRequested;

    public SpaceAnalysisView()
    {
        InitializeComponent();
        Center.EffectiveViewportChanged += (_, e) =>
        {
            if (centerViewportHeight == e.EffectiveViewport.Height) return;
            centerViewportHeight = e.EffectiveViewport.Height;
            UpdateMapHeight();
        };
        KindMenu.ItemsSource = Enum.GetValues<SpaceKind>().Where(kind => kind != SpaceKind.Folder)
            .Select(kind => new SelectionOption((int)kind, SpaceKinds.Label(kind))).ToArray();
        Loaded += (_, _) => { model.Changed += ModelChanged; model.SetIndexViewActive(active); Render(); };
        Unloaded += (_, _) => { model.SetIndexViewActive(false); model.Changed -= ModelChanged; FilePreview.File = null; };
        ActualThemeChanged += (_, _) => { shownTypes = null; Render(); };
    }

    public void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        model.SetIndexViewActive(value);
        if (value)
        {
            try
            {
                drives = model.GetDrives();
                DriveMenu.ItemsSource = new[] { new SelectionOption(0, "选择磁盘") }.Concat(drives.Select((drive, index) =>
                    new SelectionOption(index + 1, drive.Label, drive.Root))).ToArray();
                UpdateDriveSelection();
            }
            catch (Exception error) { model.ShowMessage("无法列出磁盘。" + error.Message); }
            Render();
        }
        else FilePreview.File = null;
    }

    public void FocusScope()
    {
        if (model.IsBusy) return;
        ScopeBox.Focus(FocusState.Programmatic);
    }

    private async void EndAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanEndAnalysis) return;
        var largeSnapshot = model.Snapshot!.Entries is not IIndexedSpaceView && model.Snapshot.Entries.Count >= 250_000;
        await model.EndAnalysisAsync();
        if (largeSnapshot)
        {
            // Give the list, tree and inspector one dispatcher turn to drop their old item sources first.
            await Task.Yield();
            if (!model.IsBusy && model.Snapshot is null)
                await Task.Run(() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true));
        }
    }

    private void PathHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0)
            FolderHeader.Height = InspectorHeader.Height = e.NewSize.Height;
    }

    private void ModelChanged(object? sender, EventArgs e)
    {
        // Cleanup can replace a scan while this page is hidden. Its old visual tree must release that snapshot too.
        if (!active && shownSnapshot is not null && !ReferenceEquals(shownSnapshot, model.Snapshot))
        {
            Treemap.SetData(null, null);
            RebuildTree(null);
            shownSnapshot = null;
            shownResult = null;
            shownTypes = null;
            shownFolder = -1;
            inspecting = null;
            pageRows = [];
            Rows.ItemsSource = Types.ItemsSource = Crumbs.ItemsSource = null;
            RenderDistributionBar(null, []);
        }
        Render();
    }
    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static double Percentage(long part, long total) => total > 0 ? Math.Clamp(100.0 * part / total, 0, 100) : 0;

    private void Render()
    {
        if (!active || rendering || Feedback is null) return;
        rendering = true;
        try
        {
            var snapshot = model.Snapshot;
            var busy = model.IsBusy;
            DriveMenu.IsEnabled = ScopeBox.IsEnabled = BrowseButton.IsEnabled = !busy;
            ScanButton.IsEnabled = !busy && ReadScope().Length > 0;
            var rescanning = snapshot is not null && string.Equals(snapshot.Root, ReadScope(), StringComparison.OrdinalIgnoreCase);
            ScanButton.Content = rescanning ? "重新扫描" : "开始扫描";
            ScanButton.Visibility = Show(!busy);
            ScanButton.Style = (Style)Application.Current.Resources[rescanning ? "WorkspaceToolbarButtonStyle" : "WorkspaceToolbarAccentStyle"];
            EndAnalysisButton.Visibility = Show(snapshot is not null && !busy);
            EndAnalysisButton.IsEnabled = model.CanEndAnalysis;
            ToolTipService.SetToolTip(EndAnalysisButton, model.Pending.Count > 0 ? "处理或移出待处理项后可结束分析" : "释放本次扫描结果");
            CancelButton.Visibility = Show(busy);
            CancelButton.IsEnabled = !model.IsCancelling;
            CancelButton.Content = model.IsCancelling ? "正在停止…" : model.Recycling ? "停止后续项目" : "取消扫描";
            ScanIndicator.Visibility = Show(busy);
            ScanIndicator.IsIndeterminate = !model.Recycling;
            if (model.Recycling && model.RecycleProgress is { } work)
            {
                ScanIndicator.Maximum = Math.Max(1, work.Total);
                ScanIndicator.Value = work.Finished;
            }

            RenderSummary(snapshot);
            bool displayChanged = displayRevision != model.DisplayRevision;
            displayRevision = model.DisplayRevision;
            bool indexUpdate = !busy && shownSnapshot is not null && snapshot is not null &&
                !ReferenceEquals(shownSnapshot, snapshot);
            if (!ReferenceEquals(snapshot, shownSnapshot) || displayChanged)
            {
                var expanded = (indexUpdate || displayChanged) && shownSnapshot != null ? folderNodes.Where(pair => pair.Key != 0 && pair.Value.IsExpanded)
                    .Select(pair => shownSnapshot!.PathOf(pair.Key)).ToArray() : [];
                shownSnapshot = snapshot;
                shownFolder = -1;
                shownTypes = null;
                RebuildTree(snapshot);
                foreach (var path in expanded)
                    if (snapshot!.Find(path) is { } id && snapshot.Entries[id].IsDirectory)
                    {
                        ShowFolder(snapshot, id);
                        if (!folderBranches.ContainsKey(id)) LoadChildren(snapshot, id);
                        folderNodes[id].IsExpanded = true;
                    }
            }
            if (snapshot is not null && shownFolder != model.Folder)
            {
                shownFolder = model.Folder;
                ShowFolder(snapshot, model.Folder);
            }
            else if (snapshot is null) Crumbs.ItemsSource = null;
            ParentButton.IsEnabled = !busy && snapshot is not null && model.Folder > 0;
            Folders.IsEnabled = !busy;
            Rows.IsEnabled = !busy;
            DirectoryTab.IsChecked = model.View == SpaceView.Directory;
            LargeTab.IsChecked = model.View == SpaceView.LargeFiles;
            TypeTab.IsChecked = model.View == SpaceView.Types;
            DirectoryTab.IsEnabled = LargeTab.IsEnabled = TypeTab.IsEnabled = snapshot is not null && !busy;
            KindMenu.SelectedValue = (int)model.Filter.Kind;
            var hasBounds = model.Filter.Minimum.HasValue || model.Filter.Maximum.HasValue ||
                model.Filter.ModifiedFrom.HasValue || model.Filter.ModifiedBefore.HasValue;
            var metric = model.LogicalSize ? "大小" : "占用";
            FilterButton.Content = metric + "与日期" + (hasBounds ? " · 已筛选" : "");
            FilterSizeHeading.Text = (model.LogicalSize ? "文件逻辑大小" : "磁盘占用") + "（MiB）";
            Filters.Visibility = Show(snapshot is not null && model.View == SpaceView.LargeFiles);
            SummaryBanner.Visibility = Show(snapshot is not null || busy);
            ScanSummary.Visibility = Visibility.Visible;
            KindMenu.IsEnabled = FilterButton.IsEnabled = !busy;

            var showTypes = snapshot is not null && model.View == SpaceView.Types;
            Rows.Visibility = Show(snapshot is not null && !showTypes);
            Types.Visibility = Show(showTypes);
            ListHeader.Visibility = Show(!showTypes);
            var highlight = model.Result is not null && !busy ? model.TakeHighlight() : null;
            if (!ReferenceEquals(shownResult, model.Result) || displayChanged)
            {
                shownResult = model.Result;
                page = indexUpdate || displayChanged ? Math.Min(page, Math.Max(0, ((shownResult?.Indices.Length ?? 0) - 1) / PageSize)) : 0;
                if (highlight is { } id) SeekPage(id);
                FillPage(indexUpdate || displayChanged, displayChanged);
            }
            if (highlight is { } target)
            {
                var before = page;
                SeekPage(target);
                if (before != page) FillPage();
                if (pageRows.FirstOrDefault(row => row.Id == target) is { } row)
                {
                    Rows.SelectedItems.Clear();
                    Rows.SelectedItems.Add(row);
                    inspecting = target;
                    Rows.ScrollIntoView(row);
                }
            }
            if (!ReferenceEquals(shownTypes, model.TypeTotals))
            {
                shownTypes = model.TypeTotals;
                RenderDistributionBar(snapshot, shownTypes);
                if (snapshot is not null)
                {
                    var total = snapshot.Entries[0].TotalBytes(model.LogicalSize);
                    Types.ItemsSource = shownTypes.Select(group => new SpaceTypeRow(group.Kind, SpaceKinds.Label(group.Kind),
                        $"{group.Count:N0} 个文件", SpaceLabels.Size(group.Bytes), Percentage(group.Bytes, total),
                        group.Unknown == 0 ? "" : $"{group.Unknown:N0} 个占用未知", SpaceChartColors.SegmentBrush(group.Kind, ActualTheme))).ToArray();
                }
                else Types.ItemsSource = null;
            }

            StorageBarHost.IsHitTestVisible = SegmentLegend.IsHitTestVisible = !busy;
            foreach (var segment in segments)
            {
                segment.Bar.IsEnabled = segment.Legend.IsEnabled = !busy;
                segment.SetSelected(model.View == SpaceView.LargeFiles && model.Filter.Kind == segment.Kind);
            }
            RenderListState(snapshot, showTypes);
            RenderMap(snapshot);
            if (displayChanged) Treemap.RefreshDisplay();
            RenderInspector();
            RenderFooter();
        }
        finally { rendering = false; }
    }

    private void RenderSummary(SpaceSnapshot? snapshot)
    {
        var scope = ReadScope();
        if (!string.Equals(measuredRoot, scope, StringComparison.OrdinalIgnoreCase) || Environment.TickCount64 - volumeReadAt > 2000)
        {
            measuredRoot = scope;
            volumeReadAt = Environment.TickCount64;
            selectedVolume = SpaceVolume.ReadLocalRoot(scope);
        }
        IssuesButton.Visibility = Show(snapshot is { IsPartial: true });
        IssuesButton.Content = snapshot is { Skipped: > 0 } ? $"扫描详情 · {snapshot.Skipped:N0}" : "扫描详情";
        VolumeSummary.Visibility = Show(snapshot is not null || model.Scanning || model.Recycling || selectedVolume is not null);
        if (model.Scanning && model.ScanProgress is { } progress)
        {
            ScopeSummary.Text = selectedVolume is { } scanning ? Capacity(scanning) : progress.Stage;
            VolumeSummary.Text = $"{progress.Stage} · 已统计 {SpaceLabels.Size(progress.Bytes)} · {progress.Files:N0} 个文件 · {progress.Folders:N0} 个目录 · 跳过或变化 {progress.Skipped:N0} · {SpaceLabels.Elapsed(progress.Elapsed)}";
        }
        else if (model.Recycling && model.RecycleProgress is { } work)
        {
            ScopeSummary.Text = $"正在移入回收站 · {work.Finished:N0} / {work.Total:N0} 项";
            VolumeSummary.Text = work.Path;
        }
        else if (selectedVolume is { } selectedDisk && snapshot is not null &&
            !string.Equals(snapshot.Root, selectedDisk.Root, StringComparison.OrdinalIgnoreCase))
        {
            ScopeSummary.Text = Capacity(selectedDisk);
            VolumeSummary.Text = $"下方显示 {snapshot.Root} 的目录统计。扫描可查看 {selectedDisk.Root} 的目录占用。";
        }
        else if (snapshot is not null)
        {
            var state = snapshot.Cancelled ? "已取消 · 部分结果" : snapshot.IsPartial ? "部分结果" : "扫描完成";
            var totalSize = SpaceLabels.Size(snapshot.Entries[0].TotalBytes(model.LogicalSize));
            ScopeSummary.Text = $"{(model.LogicalSize ? "文件逻辑大小" : "已统计空间")} {totalSize}   ·   {state}";
            if (!string.Equals(snapshot.Root, ReadScope(), StringComparison.OrdinalIgnoreCase)) ScopeSummary.Text += "   ·   当前结果 " + snapshot.Root;
            ToolTipService.SetToolTip(ScopeSummary, snapshot.Root);
            var stats = $"{snapshot.ScanMethod} · {snapshot.Files:N0} 个文件 · {snapshot.Folders:N0} 个目录";
            if (snapshot.Entries is not IIndexedSpaceView) stats += " · " + SpaceLabels.Elapsed(snapshot.Elapsed);
            if (snapshot.IsWholeVolume && snapshot.Volume is { } savedDisk)
            {
                var disk = selectedVolume is { } selected && string.Equals(selected.Root, savedDisk.Root, StringComparison.OrdinalIgnoreCase) ? selected : savedDisk;
                ScopeSummary.Text = Capacity(disk);
                var attributed = snapshot.Entries[0].CountedBytes;
                var difference = disk.DifferenceFrom(attributed) - disk.ReservedBytes - savedDisk.NtfsAttributeBytes - savedDisk.IndexCacheBytes;
                var reserved = disk.ReservedBytes > 0 ? $" · Windows 预留 {SpaceLabels.Size(disk.ReservedBytes)}" : "";
                var cache = savedDisk.IndexCacheBytes > 0 ? $" · 本机索引缓存 {SpaceLabels.Size(savedDisk.IndexCacheBytes)}" : "";
                var attributes = savedDisk.NtfsAttributeBytes > 0 ? $" · NTFS 索引及附加流 {SpaceLabels.Size(savedDisk.NtfsAttributeBytes)}" : "";
                var remainder = difference == 0 ? "已核对全部已用空间" : difference > 0 ? $"尚未归属 {SpaceLabels.Size(difference)}" : $"快照比当前磁盘已用多 {SpaceLabels.Size(-difference)}";
                VolumeSummary.Text = $"已归属文件 {SpaceLabels.Size(attributed)}{reserved}{attributes}{cache} · {remainder} · {state} · {stats}";
                if (model.LogicalSize) VolumeSummary.Text = $"文件逻辑大小 {totalSize} · " + VolumeSummary.Text;
            }
            else if (snapshot.Volume is { } containing) VolumeSummary.Text = stats + "   |   " + Capacity(containing);
            else VolumeSummary.Text = stats;
        }
        else
        {
            ScopeSummary.Text = selectedVolume is { } disk ? Capacity(disk) : "选择要分析的磁盘或文件夹。";
            ToolTipService.SetToolTip(ScopeSummary, null);
            VolumeSummary.Text = selectedVolume is null ? "" : "开始扫描后可查看各目录的占用。";
        }
        ToolTipService.SetToolTip(VolumeSummary, VolumeSummary.Text);
    }

    private static string Capacity(SpaceVolume disk) =>
        $"{disk.Root} 磁盘已用 {SpaceLabels.Size(disk.UsedBytes)} / {SpaceLabels.Size(disk.TotalBytes)} · 可用 {SpaceLabels.Size(disk.FreeBytes)}";

    private void RenderDistributionBar(SpaceSnapshot? snapshot, IReadOnlyList<SpaceTypeTotal> groups)
    {
        segments.Clear();
        SegmentedStorageBar.Children.Clear();
        SegmentedStorageBar.ColumnDefinitions.Clear();
        SegmentLegend.Children.Clear();

        var counted = snapshot?.Entries[0].TotalBytes(model.LogicalSize) ?? 0;
        var nonEmpty = groups.Where(group => group.Bytes > 0).OrderByDescending(group => group.Bytes).ToArray();
        var remaining = !model.LogicalSize && snapshot is { IsWholeVolume: true, Volume: { } disk }
            ? Math.Max(0, (selectedVolume?.Root == disk.Root ? selectedVolume : disk).DifferenceFrom(counted)) : 0;
        long reserved = !model.LogicalSize && snapshot is { IsWholeVolume: true, Volume: { } saved } ? (selectedVolume?.Root == saved.Root ? selectedVolume : saved).ReservedBytes : 0;
        long attributes = !model.LogicalSize && snapshot is { IsWholeVolume: true, Volume: { } scanned } ? scanned.NtfsAttributeBytes : 0;
        long indexCache = !model.LogicalSize && snapshot is { IsWholeVolume: true, Volume: { } indexed } ? indexed.IndexCacheBytes : 0;
        long unresolved = Math.Max(0, remaining - reserved - attributes - indexCache);
        if (snapshot is null || nonEmpty.Length == 0 && remaining == 0)
        {
            StorageBarHost.Visibility = SegmentLegendViewport.Visibility = Visibility.Collapsed;
            return;
        }

        StorageBarHost.Visibility = SegmentLegendViewport.Visibility = Visibility.Visible;
        var style = (Style)Resources["SpaceTypeButtonStyle"];
        foreach (var group in nonEmpty)
        {
            var size = SpaceLabels.Size(group.Bytes);
            var summary = $"{SpaceKinds.Label(group.Kind)} · {size}（{Percentage(group.Bytes, counted + remaining):0.#}%）";
            var segment = new SpaceUsageSegment(group.Kind, SpaceChartColors.SegmentBrush(group.Kind, ActualTheme), size, summary, style, SelectSegment);
            Grid.SetColumn(segment.Bar, segments.Count);
            SegmentedStorageBar.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength((double)group.Bytes / (counted + remaining), GridUnitType.Star),
                MinWidth = 2
            });
            segments.Add(segment);
            SegmentedStorageBar.Children.Add(segment.Bar);
            SegmentLegend.Children.Add(segment.Legend);
        }
        AddVolumeSegment("Windows 预留", reserved, "由 Windows 报告的卷预留空间。");
        AddVolumeSegment("NTFS 索引及附加流", attributes, "包含 NTFS 目录索引、命名数据流和其他非驻留属性。");
        AddVolumeSegment("本机索引缓存", indexCache, "当前卷上自建索引数据库及日志的实际占用。");
        AddVolumeSegment("尚未归属", unresolved, "本次快照未核对到的已用空间，包含扫描期间的变化。");

        void AddVolumeSegment(string title, long amount, string explanation)
        {
            if (amount <= 0) return;
            var gap = new Border { Height = 9, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["WorkspaceBorderBrush"], CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(gap, SegmentedStorageBar.ColumnDefinitions.Count);
            SegmentedStorageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((double)amount / Math.Max(1, counted + remaining), GridUnitType.Star), MinWidth = 2 });
            SegmentedStorageBar.Children.Add(gap);
            var label = new TextBlock { Text = $"{title} {SpaceLabels.Size(amount)}", FontSize = 11.5 };
            ToolTipService.SetToolTip(label, explanation);
            SegmentLegend.Children.Add(label);
        }

        if (!WorkspaceMotion.Enabled) return;
        // Smooth left-to-right capacity bar growth animation
        try
        {
            var barVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SegmentedStorageBar);
            barVisual.CenterPoint = System.Numerics.Vector3.Zero;
            var comp = barVisual.Compositor;
            var ease = comp.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.22f, 1f), new System.Numerics.Vector2(0.36f, 1f));
            var grow = comp.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new System.Numerics.Vector3(0.05f, 1f, 1f));
            grow.InsertKeyFrame(1f, System.Numerics.Vector3.One, ease);
            grow.Duration = TimeSpan.FromMilliseconds(380);
            barVisual.StartAnimation("Scale", grow);
        }
        catch (Exception) { }
    }

    private void SelectSegment(SpaceKind kind)
    {
        if (!model.IsBusy) model.SetFilter(new(kind));
    }

    private void RenderListState(SpaceSnapshot? snapshot, bool showTypes)
    {
        var result = model.Result;
        var count = result?.Indices.Length ?? 0;
        var totalPages = Math.Max(1, (count + PageSize - 1) / PageSize);
        PageButtons.Visibility = Show(!showTypes && totalPages > 1);
        PrevPage.IsEnabled = page > 0;
        NextPage.IsEnabled = page + 1 < totalPages;
        PageNumber.Text = $"{page + 1:N0} / {totalPages:N0}";
        PageFooter.Visibility = Visibility.Visible;
        ResultsSummary.Text = snapshot is null
            ? (selectedVolume is { } disk ? $"{Capacity(disk)}   ·   点击「开始扫描」查看目录与大文件" : "0 项   ·   选择磁盘或文件夹后开始扫描")
            : showTypes ? "扫描范围内的类型分布 · 选择类型查看文件"
            : result is null ? "" : ResultLabel(result);
        ToolTipService.SetToolTip(ResultsSummary, ResultsSummary.Text);
        NameHeader.Content = SortLabel("名称", SpaceSort.Name);
        var primary = model.LogicalSize ? SpaceSort.Logical : SpaceSort.Allocated;
        var secondary = model.LogicalSize ? SpaceSort.Allocated : SpaceSort.Logical;
        SpaceHeader.Tag = primary.ToString();
        SizeHeader.Tag = secondary.ToString();
        SpaceHeader.Content = SortLabel(model.LogicalSize ? "逻辑大小" : "占用空间", primary);
        SizeHeader.Content = SortLabel(model.LogicalSize ? "占用空间" : "逻辑大小", secondary);
        DateHeader.Content = SortLabel("修改日期", SpaceSort.Modified);

        var busy = model.IsBusy;
        Empty.Visibility = Show(model.IsLoadingView || snapshot is null || (showTypes ? model.TypeTotals.Count == 0 : count == 0));
        EmptyActions.Visibility = Show(snapshot is null && !busy);
        EmptyScanButton.IsEnabled = !busy && ReadScope().Length > 0;
        if (model.Scanning)
        {
            EmptyTitle.Text = "正在统计目录占用";
            EmptyBody.Text = model.ScanProgress is { LargestFolders.Count: > 0 } progress
                ? string.Join("\n", progress.LargestFolders.Select(item => $"{item.Name}   {SpaceLabels.Size(item.Bytes)}"))
                : "文件和目录数量会随扫描更新。";
        }
        else if (snapshot is null)
        {
            EmptyTitle.Text = "开始空间分析";
            EmptyBody.Text = selectedVolume is { } selectedDisk
                ? $"{Capacity(selectedDisk)}\n点击「开始扫描」查看目录占用排行、大文件与空间矩形图。"
                : "选择磁盘或文件夹后开始扫描，查看彩色容量分布条、目录占用排行、大文件与空间矩形图。";
        }
        else if (model.IsLoadingView)
        {
            EmptyTitle.Text = "正在整理结果";
            EmptyBody.Text = "";
        }
        else
        {
            EmptyTitle.Text = model.View == SpaceView.LargeFiles ? "没有符合条件的文件" :
                model.View == SpaceView.Types ? "没有可统计的文件" :
                snapshot.Entries[model.Folder].IsPartial ? "未读取到目录内容" : "此目录没有内容";
            EmptyBody.Text = model.View == SpaceView.LargeFiles ? "可调整类型、占用或日期筛选。" :
                snapshot.Entries[model.Folder].IsPartial ? "可从扫描详情查看原因。" : "";
        }
    }

    private string ResultLabel(SpaceQueryResult result)
    {
        if (model.View == SpaceView.Directory && model.Snapshot is { } snapshot && snapshot.Entries[model.Folder].UnknownAllocation && result.Indices.Length == 0)
            return "此目录的占用未知";
        var scope = model.View == SpaceView.Directory ? "本层已统计" :
            model.Filter == new SpaceFilter() ? "扫描范围内文件占用" : "筛选结果占用";
        var unknown = result.Unknown > 0 ? $" · {result.Unknown:N0} 项不完整" : "";
        if (model.LogicalSize) scope = model.View == SpaceView.Directory ? "本层逻辑大小" : "文件逻辑大小";
        return $"{result.Indices.Length:N0} 项 · {scope} {SpaceLabels.Size(result.Bytes)}{unknown}";
    }

    private string SortLabel(string title, SpaceSort sort) => title + (model.Sort != sort ? "" : model.Descending ? " ↓" : " ↑");

    private void FillPage(bool preservePosition = false, bool forceDisplay = false)
    {
        SpaceRow[] next = shownSnapshot is { } snapshot && shownResult is { } result
            ? result.Indices.Skip(page * PageSize).Take(PageSize)
                .Select(id => new SpaceRow(snapshot, id, showFolder: model.View == SpaceView.LargeFiles, logicalSize: model.LogicalSize)).ToArray() : [];
        if (preservePosition && !forceDisplay && next.Length == pageRows.Length && next.Zip(pageRows).All(pair => pair.First.SameDisplay(pair.Second))) return;
        var selected = preservePosition ? Rows.SelectedItems.OfType<SpaceRow>().Select(row => (row.Path, row.Identity)).ToHashSet() : null;
        var scroll = preservePosition ? MarqueeSelection.FindScroller(Rows) : null;
        var offset = scroll?.VerticalOffset ?? 0;
        pageRows = next;
        inspecting = null;
        Rows.ItemsSource = pageRows;
        if (preservePosition)
        {
            if (selected?.Count > 0)
                foreach (var row in pageRows)
                    if (selected.Contains((row.Path, row.Identity))) Rows.SelectedItems.Add(row);
            if (scroll is not null) { Rows.UpdateLayout(); scroll.ChangeView(null, offset, null, true); }
        }
        else if (pageRows.Length > 0) Rows.ScrollIntoView(pageRows[0]);
    }

    private void SeekPage(int id)
    {
        if (shownResult is null) return;
        var index = Array.IndexOf(shownResult.Indices, id);
        if (index >= 0) page = index / PageSize;
    }

    private void MovePage(int delta)
    {
        if (model.IsBusy) return;
        page += delta;
        FillPage();
        Render();
    }

    private void RenderMap(SpaceSnapshot? snapshot)
    {
        var map = model.Map;
        var shown = snapshot is not null && map?.Folder == model.Folder;
        MapRegion.Visibility = Show(shown);
        MapSummary.Text = map is { Bytes: > 0 }
            ? $"本层 · {SpaceLabels.Size(map.Bytes)}"
            : "本层没有可绘制的已统计占用";
        ToolTipService.SetToolTip(MapSummary, "面积表示占用空间，颜色表示类型。单击定位，双击文件夹进入。");
        UpdateMapHeight();
    }

    private void UpdateMapHeight()
    {
        if (Treemap is null) return;
        var available = MaximumMapHeight;
        var canResize = MapRegion.Visibility == Visibility.Visible && model.Map is { Bytes: > 0 } && available >= MinimumMapHeight;
        var expanded = canResize && mapHeight >= MinimumMapHeight;
        MapResize.Visibility = Show(canResize);
        MapToggle.IsEnabled = canResize;
        MapToggle.Content = expanded ? "收起空间矩形图" : "展开空间矩形图";
        Treemap.Visibility = Show(expanded);
        MapRow.Height = expanded ? new GridLength(Math.Min(mapHeight, available)) : GridLength.Auto;
        Treemap.SetData(expanded ? model.Snapshot : null, expanded ? model.Map : null);
    }

    private double MaximumMapHeight
    {
        get
        {
            // A WinUI grid can arrange at its children's minimum height even when its viewport clips it.
            var height = Math.Min(Center.ActualHeight, Math.Min(Center.MaxHeight, centerViewportHeight));
            return Math.Clamp(height - (model.View == SpaceView.LargeFiles ? 250 : 212), 0, 380);
        }
    }
    private double CollapsedMapHeight => MapHeader.ActualHeight + MapHeader.Margin.Bottom + MapRegion.Margin.Bottom;

    private void RenderFooter()
    {
        var selection = CurrentSelection();
        var waiting = model.Pending.Count;
        SelectionSummary.Text = $"已选 {selection.Length:N0} 项   ·   待处理 {waiting:N0} 项";
        ReviewButton.Content = $"核对待处理 · {waiting:N0}";
        ReviewButton.IsEnabled = FooterReview.IsEnabled = !model.IsBusy && waiting > 0;
        FooterReview.Visibility = Show(!DetailsPane.IsPaneOpen && waiting > 0);
        ResultButton.Visibility = Show(model.RecycleReport is not null);
        CollapsedResultButton.Visibility = Show(!DetailsPane.IsPaneOpen && model.RecycleReport is not null);
        ResultButton.IsEnabled = CollapsedResultButton.IsEnabled = !model.Recycling;
        StageButton.IsEnabled = !model.IsBusy && selection.Any(model.CanQueue);
        Feedback.Text = model.Message.Length > 0 ? model.Message :
            model.RecycleReport is { } report ? ReportSummary(report) : "";
        Feedback.Visibility = Show(Feedback.Text.Length > 0);
        ToolTipService.SetToolTip(Feedback, Feedback.Text);
        FeedbackBar.Visibility = Show(Feedback.Visibility == Visibility.Visible || FooterReview.Visibility == Visibility.Visible || CollapsedResultButton.Visibility == Visibility.Visible);
    }

    private void RenderInspector()
    {
        var selection = CurrentSelection();
        if (shownSnapshot is not { } snapshot || selection.Length == 0)
        {
            inspecting = null;
            InspectorEmpty.Visibility = Visibility.Visible;
            InspectorItem.Visibility = Visibility.Collapsed;
            FilePreview.File = null;
            Treemap.Select(null);
            return;
        }

        var id = inspecting is { } value && selection.Contains(value) ? value : selection[^1];
        var row = new SpaceRow(snapshot, id, model.ProgramLabel(id));
        var entry = snapshot.Entries[id];
        InspectorEmpty.Visibility = Visibility.Collapsed;
        InspectorItem.Visibility = Visibility.Visible;
        InspectorItem.IsHitTestVisible = !model.IsBusy;
        InspectorName.Text = row.Name;
        InspectorPath.Text = row.Path;
        InspectorStats.Text = $"{row.Kind} · {row.DateAndTime}\n占用空间 {row.Allocated}\n文件大小 {row.Logical}";
        InspectorNote.Text = row.Note;
        InspectorNote.Visibility = Show(row.Note.Length > 0);
        VerifyVolume.Visibility = Show(!snapshot.ScanMethod.Contains("NTFS", StringComparison.OrdinalIgnoreCase) && entry.IsDirectory && entry.IsPartial && !entry.IsCloud && !entry.Stamp.IsReparsePoint &&
            snapshot.Volume is { } volume && CanVerifyVolume(volume));
        OpenItem.IsEnabled = selection.Length == 1 && !entry.IsSkipped && !entry.IsCloud;
        TransferButton.IsEnabled = selection.Any(item => !snapshot.Entries[item].IsSkipped);
        FilePreview.Visibility = Show(DetailsPane.IsPaneOpen && !entry.IsDirectory && !entry.IsSkipped && !entry.IsCloud && !entry.Stamp.IsReparsePoint && !model.IsBusy);
        FilePreview.Glyph = row.Glyph;
        var preview = FilePreview.Visibility == Visibility.Visible ? snapshot.FileOf(id) : null;
        if (FilePreview.File != preview) FilePreview.File = preview;
        Treemap.Select(id);
    }

    private int[] CurrentSelection() => Rows.SelectedItems.OfType<SpaceRow>().Select(row => row.Id).ToArray();
    private string ReadScope()
    {
        var path = ScopeBox.Text.Trim().Trim('"');
        return path.Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? path + "\\" : path;
    }

    private void UpdateDriveSelection()
    {
        var scope = ReadScope().Replace('/', '\\');
        var index = drives.Select((drive, i) => (drive, i)).FirstOrDefault(pair =>
            scope.StartsWith(pair.drive.Root, StringComparison.OrdinalIgnoreCase));
        DriveMenu.SelectedValue = index.drive is null ? 0 : index.i + 1;
    }

    private void Scope_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ScanButton is null) return;
        ToolTipService.SetToolTip(ScopeBox, ScopeBox.Text);
        UpdateDriveSelection();
        Render();
    }
    private void Drive_SelectionCommitted(object? sender, EventArgs e)
    {
        if (DriveMenu.SelectedValue is var value && value > 0 && value <= drives.Count) ScopeBox.Text = drives[value - 1].Root;
    }
    private async void Scope_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ScanFromInputAsync();
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanFromInputAsync();
    private static bool CanVerifyVolume(SpaceVolume volume)
    {
        try { return volume.Root.Length == 3 && new DriveInfo(volume.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    private async void VerifyVolume_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsBusy || model.Snapshot?.Volume is not { } volume) return;
        ScopeBox.Text = volume.Root;
        await ScanFromInputAsync(true);
    }
    private async Task ScanFromInputAsync(bool precise = false)
    {
        var scope = ReadScope();
        if (model.IsBusy || showingDialog || scope.Length == 0) return;
        if (model.Pending.Count > 0)
        {
            var dialog = FileCollectionDialogs.Dialog(this, "重新扫描",
                new TextBlock { Text = "本次扫描会清空待处理清单。需要处理的项目可从新结果中重新选择。", TextWrapping = TextWrapping.Wrap }, "重新扫描");
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        }
        var rescan = model.Snapshot is { } current && string.Equals(Path.TrimEndingDirectorySeparator(scope),current.Root,StringComparison.OrdinalIgnoreCase);
        await model.ScanAsync(scope, precise || rescan);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await FileInputSources.PickFolderAsync();
            if (!model.IsBusy && paths.Count > 0) ScopeBox.Text = paths[0];
        }
        catch (Exception error) { model.ShowMessage("无法打开文件夹选择器。" + error.Message); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => model.Cancel();
    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<SpaceView>(tag, out var view)) model.Show(view);
        Render();
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<SpaceSort>(tag, out var sort)) model.SortBy(sort);
    }
    private void Kind_SelectionCommitted(object? sender, EventArgs e) => model.SetFilter(model.Filter with { Kind = (SpaceKind)KindMenu.SelectedValue });
    private void Types_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpaceTypeRow row) model.SetFilter(new(row.Kind));
    }
    private void Filter_Opening(object sender, object e)
    {
        const long mib = 1024 * 1024;
        MinimumSize.Text = (model.Filter.Minimum / (decimal)mib)?.ToString("0.######", CultureInfo.CurrentCulture) ?? "";
        MaximumSize.Text = (model.Filter.Maximum / (decimal)mib)?.ToString("0.######", CultureInfo.CurrentCulture) ?? "";
        ModifiedFrom.Date = model.Filter.ModifiedFrom;
        ModifiedThrough.Date = model.Filter.ModifiedBefore?.AddDays(-1);
        FilterError.Text = "";
    }
    private void ApplyFilter_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBytes(MinimumSize.Text, out var min) || !TryBytes(MaximumSize.Text, out var max) || min > max)
        {
            FilterError.Text = "请输入有效的占用范围，最小值应小于或等于最大值。";
            return;
        }
        if (ModifiedFrom.Date?.Date > ModifiedThrough.Date?.Date)
        {
            FilterError.Text = "开始日期不能晚于结束日期。";
            return;
        }
        try
        {
            var from = ModifiedFrom.Date is { } a ? new DateTimeOffset(a.Date) : (DateTimeOffset?)null;
            var before = ModifiedThrough.Date is { } b ? new DateTimeOffset(b.Date.AddDays(1)) : (DateTimeOffset?)null;
            model.SetFilter(new((SpaceKind)KindMenu.SelectedValue, min, max, from, before));
            FilterFlyout.Hide();
        }
        catch (ArgumentException) { FilterError.Text = "该日期无法用于筛选，请选择其他日期。"; }
    }
    private static bool TryBytes(string input, out long? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (!decimal.TryParse(input, NumberStyles.Number, CultureInfo.CurrentCulture, out var mib) || mib < 0 || mib > long.MaxValue / 1048576m) return false;
        result = (long)(mib * 1048576m);
        return true;
    }
    private void ClearFilter_Click(object sender, RoutedEventArgs e) { model.SetFilter(new()); FilterFlyout.Hide(); }
    private void PrevPage_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => MovePage(1);
    private void Center_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMapHeight();
    private void Map_Resize(object? sender, double delta)
    {
        if (MapResize.Visibility != Visibility.Visible) return;
        // Thumb deltas can arrive before the grid's next arrange. Accumulate against the requested height.
        var maximum = MaximumMapHeight;
        var collapsed = Math.Min(CollapsedMapHeight, maximum);
        var next = Math.Clamp(Math.Min(mapHeight, maximum) - delta, collapsed, maximum);
        if (mapHeight >= MinimumMapHeight && next < MinimumMapHeight)
        {
            reopenMapHeight = Math.Max(mapHeight, DefaultMapHeight);
            next = collapsed;
        }
        mapHeight = next;
        UpdateMapHeight();
    }
    private void MapToggle_Click(object sender, RoutedEventArgs e)
    {
        if (mapHeight >= MinimumMapHeight)
        {
            reopenMapHeight = mapHeight;
            mapHeight = CollapsedMapHeight;
        }
        else mapHeight = reopenMapHeight;
        UpdateMapHeight();
    }
    private void ToggleDetails_Click(object sender, RoutedEventArgs e) { DetailsPane.IsPaneOpen = !DetailsPane.IsPaneOpen; Render(); }
    private void Treemap_TileInvoked(object? sender, int id)
    {
        model.Browse(model.Folder, id >= 0 ? id : null);
        if (id < 0) Rows.Focus(FocusState.Programmatic);
    }
    private void Treemap_FolderInvoked(object? sender, int id) => model.Browse(id);
}
