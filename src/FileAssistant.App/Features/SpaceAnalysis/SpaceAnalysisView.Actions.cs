using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileAssistant.App.Features.SpaceAnalysis;

public sealed partial class SpaceAnalysisView
{
    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!active || rendering) return;
        if (e.AddedItems.OfType<SpaceRow>().LastOrDefault() is { } row) inspecting = row.Id;
        RenderInspector();
        RenderFooter();
    }

    private void Rows_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (model.IsBusy || shownSnapshot is not { } snapshot) return;
        var container = FindContainer<ListViewItem>(ContextTarget(Rows, e), Rows);
        var target = container?.Content as SpaceRow ?? (e.TryGetPosition(Rows, out _) ? null : Rows.SelectedItem as SpaceRow);
        if (target is null) return;
        if (!Rows.SelectedItems.Contains(target))
        {
            Rows.SelectedItems.Clear();
            Rows.SelectedItems.Add(target);
        }
        ShowActions(Rows, e, snapshot, target.Id, CurrentSelection());
    }

    private void ShowActions(FrameworkElement anchor, ContextRequestedEventArgs e, SpaceSnapshot snapshot, int id, int[] selected)
    {
        var entry = snapshot.Entries[id];
        var menu = new MenuFlyout();
        void Add(string title, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = title, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                if (!model.IsBusy && ReferenceEquals(snapshot, model.Snapshot)) action();
            };
            menu.Items.Add(item);
        }
        Add(entry.IsDirectory ? "打开文件夹" : "打开文件", "\uE8E5", () => model.Open(id),
            selected.Length == 1 && !entry.IsSkipped && !entry.IsCloud);
        Add("在资源管理器中定位", "\uED25", () => model.Reveal(id), selected.Length == 1);
        Add("复制完整路径", "\uE8C8", () => model.CopyPaths(selected));
        if (model.View == SpaceView.LargeFiles && !entry.IsDirectory)
            Add("在目录内容中显示", "\uE8B7", () => model.Browse(entry.Parent, id), selected.Length == 1);
        menu.Items.Add(new MenuFlyoutSeparator());
        var transfer = new MenuFlyoutSubItem { Text = "使用到…", IsEnabled = selected.Any(item => !snapshot.Entries[item].IsSkipped) };
        foreach (var item in TransferItems(snapshot, selected)) transfer.Items.Add(item);
        menu.Items.Add(transfer);
        Add("加入待处理清单", "\uE710", () => model.AddToPending(selected),
            selected.Any(model.CanQueue));
        if (e.TryGetPosition(anchor, out var position)) menu.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(anchor);
        e.Handled = true;
    }

    private async void Rows_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FindContainer<ListViewItem>(e.OriginalSource, Rows)?.Content is not SpaceRow row) return;
        e.Handled = true;
        if (model.IsBusy || shownSnapshot is not { } snapshot) return;
        if (row.IsDirectory) model.Browse(row.Id);
        else if (!snapshot.Entries[row.Id].IsCloud && !snapshot.Entries[row.Id].IsSkipped)
            await FileActivation.OpenAsync(this, snapshot.FileOf(row.Id));
    }

    private void Rows_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || Rows.SelectedItems.Count != 1 || Rows.SelectedItem is not SpaceRow row) return;
        OpenRow(row);
        e.Handled = true;
    }

    private void OpenRow(SpaceRow row)
    {
        if (model.IsBusy || shownSnapshot is not { } snapshot) return;
        if (row.IsDirectory) model.Browse(row.Id);
        else if (!snapshot.Entries[row.Id].IsCloud && !snapshot.Entries[row.Id].IsSkipped) model.Open(row.Id);
    }

    public async Task RunShortcutAsync(ShortcutAction action)
    {
        if (model.IsBusy) return;
        switch (action)
        {
            case ShortcutAction.QuickLook or ShortcutAction.Compare when shownSnapshot is { } snapshot:
                var files = CurrentSelection().Where(id => !snapshot.Entries[id].IsDirectory && !snapshot.Entries[id].IsSkipped && !snapshot.Entries[id].IsCloud)
                    .Select(snapshot.FileOf).ToArray();
                if (files.Length > 0) await Preview.QuickLookDialog.ShowAsync(this, files, 0, compare: action == ShortcutAction.Compare);
                break;
            case ShortcutAction.Filter:
                if (model.View == SpaceView.LargeFiles && model.Snapshot is not null) FilterFlyout.ShowAt(FilterButton);
                else FocusScope();
                break;
            case ShortcutAction.Refresh:
                await ScanFromInputAsync();
                break;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSelection() is [var id]) model.Open(id);
    }
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (inspecting is { } id) model.Reveal(id);
        else if (CurrentSelection().LastOrDefault(-1) is var current && current >= 0) model.Reveal(current);
    }
    private void Copy_Click(object sender, RoutedEventArgs e) => model.CopyPaths(CurrentSelection());
    private void Stage_Click(object sender, RoutedEventArgs e) => model.AddToPending(CurrentSelection());
    private void RecycleBin_Click(object sender, RoutedEventArgs e) => model.OpenRecycleBin();

    private void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (shownSnapshot is not { } snapshot || model.IsBusy) return;
        var menu = new MenuFlyout();
        foreach (var item in TransferItems(snapshot, CurrentSelection())) menu.Items.Add(item);
        menu.ShowAt(TransferButton);
    }

    private MenuFlyoutItem[] TransferItems(SpaceSnapshot snapshot, int[] selected)
    {
        var cleanup = new MenuFlyoutItem { Text = "加入清理清单", IsEnabled = !App.Cleanup.IsBusy };
        cleanup.Click += (_, _) =>
        {
            if (model.IsBusy || !ReferenceEquals(snapshot, model.Snapshot)) return;
            App.Cleanup.AddPaths(selected.Where(id => !snapshot.Entries[id].IsSkipped).Select(snapshot.PathOf), navigate: true);
        };
        return [Destination("文件暂存区", "workspace", App.Workspace), Destination("复制与整理", "organize", App.CopyInput),
            Destination("格式转换", "convert", App.Conversion.Input), Destination("批量改名", "naming", App.Naming.Input), cleanup];

        MenuFlyoutItem Destination(string label, string page, WorkspaceViewModel target)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = target.CanReceiveFiles };
            item.Click += async (_, _) =>
            {
                if (showingDialog || model.IsBusy || !ReferenceEquals(snapshot, model.Snapshot)) return;
                var files = selected.Where(id => !snapshot.Entries[id].IsSkipped).Select(snapshot.FileOf).ToArray();
                showingDialog = true;
                try
                {
                    var report = await FileCollectionDialogs.ImportAsync(target, FileCollectionBundle.FromFiles(files), this);
                    if (report is not null)
                    {
                        model.ShowMessage(target.InputFeedback);
                        NavigateRequested?.Invoke(this, page);
                    }
                }
                catch (Exception error) { model.ShowMessage("无法转交项目。" + error.Message); }
                finally { showingDialog = false; }
            };
            return item;
        }
    }

    private StackPanel ReviewPanel() => new() { Spacing = 10, MinWidth = Math.Clamp(XamlRoot.Size.Width - 96, 0, 440) };

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (model.IsBusy || shownSnapshot is not { } snapshot || model.Pending.Count == 0 || showingDialog) return;
        var list = ReviewList(selectable: true);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var note = Caption("移入回收站后可通过 Windows 还原。系统可用空间通常要等清空回收站后才会增加。");
        var remove = new Button { Content = "移出选中项", IsEnabled = false };
        var panel = ReviewPanel();
        panel.Children.Add(summary);
        panel.Children.Add(list);
        panel.Children.Add(remove);
        panel.Children.Add(note);
        var dialog = FileCollectionDialogs.Dialog(this, "核对待处理清单", panel, "移入回收站");
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.CloseButtonText = "返回分析";

        void Refresh()
        {
            var items = model.Pending;
            var bytes = items.Sum(id => snapshot.Entries[id].CountedBytes);
            summary.Text = $"将处理 {items.Count:N0} 项 · 本次统计中归入这些位置的占用 {SpaceLabels.Size(bytes)}";
            list.ItemsSource = items.Select(id => new SpaceReviewRow(id, snapshot.PathOf(id),
                SpaceKinds.Label(SpaceKinds.Of(snapshot.Entries[id])) + " · 移入回收站" +
                (snapshot.Entries[id].IsShared ? " · 含共享数据" : ""), SpaceLabels.Allocated(snapshot.Entries[id]))).ToArray();
            dialog.IsPrimaryButtonEnabled = items.Count > 0;
        }

        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItems.Count > 0;
        remove.Click += (_, _) =>
        {
            model.RemoveFromPending(list.SelectedItems.OfType<SpaceReviewRow>().Select(row => row.Id).ToArray());
            Refresh();
        };
        Refresh();
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || !ReferenceEquals(snapshot, model.Snapshot)) return;
        await model.RecycleAsync();
    }

    private async void Result_Click(object sender, RoutedEventArgs e)
    {
        if (model.RecycleReport is not { } report || showingDialog) return;
        var panel = ReviewPanel();
        panel.Children.Add(new TextBlock { Text = ReportSummary(report), TextWrapping = TextWrapping.Wrap });
        if (report.FreeBefore is { } before && report.FreeAfter is { } after)
            panel.Children.Add(Caption($"操作前可用 {SpaceLabels.Size(before)} · 操作后可用 {SpaceLabels.Size(after)}"));
        var list = ReviewList();
        list.ItemsSource = report.Items.Select((item, i) => new SpaceReviewRow(i, item.Path,
            item.Status switch
            {
                SpaceRecycleStatus.Recycled => "已移入回收站",
                SpaceRecycleStatus.Cancelled => "已取消",
                _ => "未移入回收站 · " + item.Message
            }, "")).ToArray();
        panel.Children.Add(list);
        await ShowDialogAsync(InfoDialog("处理结果", panel));
    }

    private async void Issues_Click(object sender, RoutedEventArgs e)
    {
        if (shownSnapshot is not { } snapshot || showingDialog) return;
        var panel = ReviewPanel();
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", snapshot.IssueCounts.Select(pair =>
                pair.Key == SpaceIssueKind.Cancelled ? SpaceLabels.IssueKind(pair.Key) : $"{SpaceLabels.IssueKind(pair.Key)} {pair.Value:N0}")),
            TextWrapping = TextWrapping.Wrap
        });
        var list = ReviewList();
        list.ItemsSource = snapshot.Issues.Select((issue, i) => new SpaceReviewRow(i, issue.Path, issue.Message, "")).ToArray();
        panel.Children.Add(list);
        if (snapshot.IssueCounts.Values.Sum() > snapshot.Issues.Count)
            panel.Children.Add(Caption($"显示前 {snapshot.Issues.Count:N0} 条记录。"));
        if (snapshot.IsWholeVolume)
            panel.Children.Add(Caption("未归类空间可能包含系统元数据、无法访问的位置和扫描期间的变化。"));
        await ShowDialogAsync(InfoDialog("扫描详情", panel));
    }

    private static string ReportSummary(SpaceRecycleReport report)
    {
        var moved = report.Items.Count(item => item.Status == SpaceRecycleStatus.Recycled);
        var failed = report.Items.Count(item => item.Status == SpaceRecycleStatus.Failed);
        var cancelled = report.Items.Count - moved - failed;
        var change = report.FreeAfter - report.FreeBefore;
        var delta = change switch
        {
            > 0 => "+" + SpaceLabels.Size(change.Value),
            < 0 => "−" + SpaceLabels.Size(-change.Value),
            0 => "0 B",
            _ => "未知"
        };
        return $"已移出原位置 {moved:N0} 项 · 失败 {failed:N0} 项 · 取消 {cancelled:N0} 项\n系统可用空间变化 {delta}";
    }

    private ListView ReviewList(bool selectable = false) => new()
    {
        MaxHeight = 322,
        SelectionMode = selectable ? ListViewSelectionMode.Extended : ListViewSelectionMode.None,
        ItemContainerStyle = (Style)Application.Current.Resources["WorkspaceListItemStyle"],
        ItemTemplate = (DataTemplate)Resources["SpaceReviewItemTemplate"]
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["WorkspaceCaptionStyle"]
    };

    private ContentDialog InfoDialog(string title, object content) => new()
    {
        Title = title, Content = content, CloseButtonText = "关闭", XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
        DefaultButton = ContentDialogButton.Close
    };

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (showingDialog || XamlRoot is null) return ContentDialogResult.None;
        Controls.DialogDismiss.Attach(dialog);
        showingDialog = true;
        try { return await dialog.ShowAsync(); }
        catch (Exception error)
        {
            model.ShowMessage("无法打开此窗口。" + error.Message);
            return ContentDialogResult.None;
        }
        finally { showingDialog = false; }
    }
}
