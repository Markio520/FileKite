using System.Diagnostics;
using FileAssistant.App.Controls;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private sealed record IndexDrive(string Root, DriveType Type, bool Ready, string VolumeName);
    private IReadOnlyList<IndexDrive> driveSnapshot = [];
    private TextBlock? indexTitle, indexDescription;
    private SettingsFlowPanel? volumeHost;
    private Button? indexSync, indexRebuild;
    private string? renderedVolumeSelection;
    private long driveCheckTime;
    private bool loadingDrives, loadedDrives, readingIndexStatus, indexActionPending, indexReadyForAction;

    private void BuildSearch()
    {
        AddSection("NTFS 全盘索引", "NTFS volume index", "全盘索引 NTFS USN 增量 实时 同步 监听 重建 预热 驻留内存 卷 盘符 磁盘 排除目录 node_modules .git $Recycle.Bin System Volume Information include exclude volumes memory",
            cards =>
        {
            var summary = new StackPanel { Spacing = 2 };
            indexTitle = Label(T("正在读取索引状态…", "Reading index status…"), "SettingHeading");
            indexDescription = Label("", "SettingCaption");
            summary.Children.Add(indexTitle); summary.Children.Add(indexDescription);
            indexSync = ActionButton(T("同步", "Sync"), () => RunIndexAction(false), canExecute: CanRunIndexAction);
            indexRebuild = ActionButton(T("重建索引", "Rebuild index"), () => RunIndexAction(true), canExecute: CanRunIndexAction);
            cards.Children.Add(Card(Pair(Leading(Icon("\uEDA2"), summary), Flow(6, indexSync, indexRebuild), 410), 60));
            volumeHost = Flow(5);
            var volumeRow = Card(Pair(Label(T("纳入实时索引的本地卷", "Local volumes included in the index")), volumeHost, 450));
            cards.Children.Add(volumeRow);
            renderedVolumeSelection = null;
            Bind(_ => RenderIndexDrives());
            SwitchRow(cards, T("随软件启动预热索引", "Warm up the index at startup"), s => s.PrewarmIndex, (s, v) => s with { PrewarmIndex = v },
                T("关闭后，首次使用全盘搜索时加载索引", "When off, the index is loaded on the first full-volume search."));
            cards.Children.Add(Exclusions());
        });
        AddSection("检索匹配、预览与空间口径", "Search matching, preview & size", "中文 拼音 首字母 隐藏文件 保护 全文 上限 空格 播放 静音 默认 空间统计 逻辑 分配 physical allocated content match",
            cards =>
        {
            SwitchRow(cards, T("中文文件名拼音与首字母检索", "Match Chinese names by pinyin or initials"), s => s.PinyinSearch, (s, v) => s with { PinyinSearch = v },
                T("例如输入「htbg」可匹配「合同报告.docx」", "For example, “htbg” matches “合同报告.docx”."));
            SwitchRow(cards, T("显示系统隐藏文件与受保护目录", "Show hidden files and protected directories"), s => s.ShowHidden, (s, v) => s with { ShowHidden = v });
            var limitTitle = T("文件内容搜索单文件跳过上限", "Maximum file size for content search");
            int[] limits = [20, 50, 200, 0];
            (int Value, string Label)[] LimitOptions(SizeUnits units) => limits.Select(mib =>
            {
                var size = FileDisplay.Size(mib * 1_048_576L, units);
                return (mib, mib == 0 ? T("不限制", "Unlimited") : mib == 50 ? string.Format(T("{0}（推荐）", "{0} (recommended)"), size) : size);
            }).ToArray();
            var displayedUnits = shown.Units;
            var limit = Select(limitTitle, s => s.ContentLimitMB, mib => Save(s => s with { ContentLimitMB = mib }), LimitOptions(displayedUnits));
            Bind(s =>
            {
                if (s.Units == displayedUnits) return;
                displayedUnits = s.Units;
                limit.ItemsSource = LimitOptions(s.Units).Select((option, i) => new SelectionOption(i, option.Label)).ToArray();
            });
            Row(cards, limitTitle, limit);
            ChoiceRow(cards, T("快速预览 (QuickLook) 音视频策略", "Quick Look media playback"), s => s.Playback, (s, v) => s with { Playback = v },
            [
                (MediaPlayback.Play, T("自动播放并保留音量", "Autoplay with sound")),
                (MediaPlayback.Muted, T("自动播放但默认静音", "Autoplay muted")),
                (MediaPlayback.Paused, T("暂停于首帧", "Pause on the first frame"))
            ]);
            ChoiceRow(cards, T("空间分析默认统计口径", "Default space analysis metric"), s => s.LogicalSpaceSize, (s, v) => s with { LogicalSpaceSize = v },
            [
                (false, T("磁盘实际分配大小", "Allocated size on disk")), (true, T("文件逻辑字节大小", "Logical file size"))
            ], T("分配大小统计文件在磁盘上占用的空间", "Allocated size measures how much disk space is assigned to files."));
        });
    }

    private bool CanRunIndexAction() => indexReadyForAction && !indexActionPending;
    private void UpdateIndexButtons()
    {
        if (indexSync is not null) indexSync.IsEnabled = CanRunIndexAction();
        if (indexRebuild is not null) indexRebuild.IsEnabled = CanRunIndexAction();
    }

    private async void RefreshIndexStatus()
    {
        if (!opened || readingIndexStatus || indexTitle is null || indexDescription is null ||
            !SectionVisible("search", T("NTFS 全盘索引", "NTFS volume index"))) return;
        var title = indexTitle;
        readingIndexStatus = true;
        try
        {
            var state = await Task.Run(SettingsOperations.GetIndexStatus);
            if (!opened || indexTitle != title) return;
            indexTitle.Text = string.Format(T("已索引 {0:N0} 个文件与目录", "{0:N0} files and folders indexed"), state.Items);
            string status = state.Updating && state.Selected > 0 && state.Ready == state.Selected
                ? T("文件名已可搜索，正在后台同步", "File names are searchable. Background sync is in progress.") :
                state.Updating ? T("正在更新索引", "Updating index") :
                state.Selected == 0 ? T("当前没有纳入的卷", "No volumes included") :
                state.Ready == state.Selected ? T("索引已就绪", "Index ready") :
                !shown.PrewarmIndex && state.Ready == 0 ? T("按需加载 · 首次搜索时启动", "On demand · starts on the first search") :
                T("正在等待索引就绪", "Waiting for the index");
            indexDescription.Text = status + " · " + string.Format(T("就绪卷 {0}/{1} · 应用工作集 {2}", "{0}/{1} volumes ready · app working set {2}"),
                state.Ready, state.Selected, FileDisplay.Size(Environment.WorkingSet, shown.Units));
            indexReadyForAction = !state.Updating && state.Selected > 0;
            UpdateIndexButtons();
        }
        catch (Exception error)
        {
            if (!opened || indexTitle != title) return;
            indexTitle.Text = T("暂时无法读取索引状态", "Index status is unavailable");
            indexDescription.Text = error.Message;
            indexReadyForAction = false;
            UpdateIndexButtons();
        }
        finally { readingIndexStatus = false; }
        LoadIndexDrives();
    }

    private async Task RunIndexAction(bool rebuild)
    {
        if (indexActionPending) return;
        if (rebuild && !await ConfirmAsync(T("重建本机索引？", "Rebuild the local index?"),
            T("将重新读取已选卷的文件和目录。重建期间，部分位置的搜索结果可能暂时不完整。",
              "FileAssistant will read the selected volumes again. Some locations may return partial results until rebuilding finishes."),
            T("重建索引", "Rebuild index"))) return;
        indexActionPending = true;
        UpdateIndexButtons();
        try
        {
            if (rebuild) await SettingsOperations.RebuildIndexAsync();
            else await SettingsOperations.SynchronizeIndexAsync();
            Notify(rebuild ? T("索引重建请求已完成", "Index rebuild request completed") : T("索引同步请求已完成", "Index sync request completed"),
                "", InfoBarSeverity.Success);
        }
        finally { indexActionPending = false; RefreshIndexStatus(); }
    }

    private async void LoadIndexDrives()
    {
        if (!opened || volumeHost is null || loadingDrives || driveCheckTime != 0 && Stopwatch.GetElapsedTime(driveCheckTime).TotalSeconds < 12) return;
        loadingDrives = true; driveCheckTime = Stopwatch.GetTimestamp();
        try
        {
            var drives = await Task.Run(() =>
            {
                var result = new List<IndexDrive>();
                foreach (var d in SettingsOperations.GetIndexDrives())
                {
                    var info = new DriveInfo(d.Root);
                    if (info.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    bool ready = info.IsReady;
                    string label = "";
                    try { if (ready) label = info.VolumeLabel; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    result.Add(new(info.Name, info.DriveType, ready, label));
                }
                return result.OrderBy(item => item.Root, StringComparer.OrdinalIgnoreCase).ToArray();
            });
            if (!opened) return;
            if (!loadedDrives || !driveSnapshot.SequenceEqual(drives)) renderedVolumeSelection = null;
            driveSnapshot = drives;
            loadedDrives = true;
            RenderIndexDrives();
        }
        catch (Exception error)
        {
            if (opened) Notify(T("无法读取本地卷", "Could not read local volumes"), error.Message, InfoBarSeverity.Warning);
        }
        finally { loadingDrives = false; }
    }

    private string[] DefaultIndexVolumes() => driveSnapshot.Where(d => d.Ready).Select(d => d.Root).ToArray();

    private void RenderIndexDrives()
    {
        if (volumeHost is null) return;
        var settings = App.Preferences.Current;
        var selected = new HashSet<string>(settings.IndexedVolumes ?? DefaultIndexVolumes(), StringComparer.OrdinalIgnoreCase);
        string fingerprint = string.Join('|', selected.Order(StringComparer.OrdinalIgnoreCase));
        if (fingerprint == renderedVolumeSelection) return;
        renderedVolumeSelection = fingerprint;
        volumeHost.Children.Clear();
        var candidates = driveSnapshot.ToList();
        candidates.AddRange(selected.Where(root => !candidates.Any(d => d.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
            .Select(root => new IndexDrive(root, DriveType.Unknown, false, "")));
        if (candidates.Count == 0)
        {
            volumeHost.Children.Add(Label(!loadedDrives ? T("正在读取本地卷…", "Reading local volumes…") : T("无可用本地卷", "No local volumes available"), "SettingCaption"));
            return;
        }
        foreach (var drive in candidates)
        {
            string kind = !drive.Ready ? T("未连接", "Offline") : drive.VolumeName.Length > 0 ? drive.VolumeName :
                drive.Root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase) ? T("系统盘", "System") :
                drive.Type == DriveType.Removable ? T("移动盘", "Removable") : T("本地磁盘", "Local disk");
            string name = drive.Root.TrimEnd('\\') + "  " + kind;
            var button = new ToggleButton
            {
                Style = LocalStyle("SettingVolume"), Content = (selected.Contains(drive.Root) ? "✓ " : "") + name, IsChecked = selected.Contains(drive.Root),
                MinHeight = 27, MinWidth = 0, Padding = new(8, 2, 8, 2), FontSize = 12,
                IsEnabled = drive.Ready || selected.Contains(drive.Root)
            };
            NameControl(button, name, "Settings.Volume." + drive.Root[0]);
            button.Click += async (_, _) =>
            {
                bool include = button.IsChecked == true;
                renderedVolumeSelection = null;
                await Save(s =>
                {
                    var roots = new HashSet<string>(s.IndexedVolumes ?? DefaultIndexVolumes(), StringComparer.OrdinalIgnoreCase);
                    if (include) roots.Add(drive.Root); else roots.Remove(drive.Root);
                    return s with { IndexedVolumes = roots.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
                });
                RenderIndexDrives();
            };
            volumeHost.Children.Add(button);
        }
    }

    private Expander Exclusions()
    {
        string title = T("搜索默认排除目录", "Excluded directories");
        Expander? disclosure = null;
        var input = new TextBox
        {
            PlaceholderText = T("输入目录名或完整路径…", "Directory name or full path…"), MinWidth = 0, FontSize = 12.5, MinHeight = 31
        };
        NameControl(input, title, "Settings.Exclude.Input");
        var tags = Flow(5);
        string[]? previous = null;
        Bind(s =>
        {
            if (previous is not null && previous.SequenceEqual(s.ExcludedDirectories)) return;
            previous = s.ExcludedDirectories;
            tags.Children.Clear();
            foreach (string rule in s.ExcludedDirectories)
            {
                var name = Label(rule, "SettingCaption");
                name.TextWrapping = TextWrapping.Wrap;
                var remove = ActionButton("×", () => Save(current => current with { ExcludedDirectories = current.ExcludedDirectories
                    .Where(p => !p.Equals(rule, StringComparison.OrdinalIgnoreCase)).ToArray() }));
                remove.Style = LocalStyle("SettingBareButton"); remove.MinHeight = 22; remove.MinWidth = 22;
                NameControl(remove, T("移除此排除项", "Remove this exclusion") + " · " + rule);
                var tag = new Grid { ColumnSpacing = 4 };
                tag.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                tag.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                Place(tag, name); Place(tag, remove, 1);
                var chip = Card(tag, 24);
                chip.Padding = new(7, 1, 1, 1);
                tags.Children.Add(chip);
            }
            if (tags.Children.Count == 0) tags.Children.Add(Label(T("没有排除目录", "No directories excluded"), "SettingCaption"));
        });
        async Task Add()
        {
            var candidate = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(candidate)) { input.Focus(FocusState.Programmatic); return; }
            if (App.Preferences.Current.ExcludedDirectories.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                Notify(T("此目录已在排除列表中", "This directory is already excluded"), candidate);
                return;
            }
            await Save(s => s with { ExcludedDirectories = s.ExcludedDirectories.Append(candidate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
            if (App.Preferences.Current.ExcludedDirectories.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                input.Text = "";
                if (disclosure is not null) disclosure.IsExpanded = true;
            }
        }
        var add = ActionButton(T("添加", "Add"), Add);
        input.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; await Add(); } };
        var entry = new Grid { ColumnSpacing = 7, MaxWidth = 200 };
        entry.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Place(entry, input); Place(entry, add, 1);
        var header = Pair(Texts(title, T("过滤开发依赖缓存与系统回收站，避免零碎缓存淹没结果", "Skip dependency caches and system folders in search results.")), entry, 418);
        header.MinHeight = 54;
        disclosure = Disclosure(title, header, tags, true);
        return disclosure;
    }
}
