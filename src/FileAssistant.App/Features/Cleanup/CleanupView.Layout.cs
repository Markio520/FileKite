using FileAssistant.App.Controls;
using FileAssistant.Core.Features.Cleanup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileAssistant.App.Features.Cleanup;

public sealed partial class CleanupView
{
    private int softwareCategory, filesCategory;
    private bool? lastFilesMode;
    private CleanupMethod? displayedMethod;
    private string softwareQuery = "", filesQuery = "";
    private readonly HashSet<string> fileProblemPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool foldersOpen = true;
    private bool? lastFoldersOpen;
    private double? preferredFoldersWidth;
    private double DesiredFoldersWidth => Math.Clamp(preferredFoldersWidth ?? 194, 176, 360);
    private double AvailableFoldersWidth => Math.Clamp(InputSurface.ActualWidth - 380, 176, 360);
    private bool? wasCompact;
    private void InputSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ActualWidth > 0)
        {
            var compact = ActualWidth < 1050;
            if (wasCompact != compact) { wasCompact = compact; foldersOpen = !compact; }
        }
        UpdateFoldersWidth();
    }
    private void UpdateFoldersWidth()
    {
        if (FoldersResizeHandle is null) return;
        var open = foldersOpen;
        var toggled = lastFoldersOpen is not null && lastFoldersOpen != open;
        lastFoldersOpen = open;
        DetailsPane.MinimumContentWidth = 380 + (open ? DesiredFoldersWidth : 48);

        if (toggled && IsLoaded && InputSurface.ActualWidth > 0)
        {
            WorkspaceMotion.AnimateSidePaneToggle(
                this,
                FoldersColumn,
                FoldersGapColumn,
                FolderSidebar,
                CollapsedFolders,
                FoldersResizeHandle,
                MainListBorder,
                opening: open,
                expandedWidth: Math.Min(DesiredFoldersWidth, AvailableFoldersWidth),
                collapsedWidth: 48,
                applyRestingGeometry: () => ApplyFoldersRestingGeometry(open));
            return;
        }

        WorkspaceMotion.CancelWidthAnimation(this);
        FolderSidebar.Opacity = 1;
        ApplyFoldersRestingGeometry(open);
    }
    private void ApplyFoldersRestingGeometry(bool open)
    {
        FolderSidebar.Visibility = FoldersResizeHandle.Visibility = Visible(open);
        CollapsedFolders.Visibility = Visible(!open);
        if (open)
        {
            var width = Math.Min(DesiredFoldersWidth, AvailableFoldersWidth);
            FoldersGapColumn.Width = new(ResizeHandle.HitTargetThickness);
            FoldersColumn.Width = new(Math.Max(0, width - ResizeHandle.HitTargetOverlap));
            Grid.SetColumn(FolderSidebar, 0);
            Grid.SetColumnSpan(FolderSidebar, 2);
            FolderSidebar.Margin = new(0, 0, ResizeHandle.HitTargetOverlap, 0);
            Grid.SetColumn(MainListBorder, 1);
            Grid.SetColumnSpan(MainListBorder, 2);
            MainListBorder.Margin = new(ResizeHandle.HitTargetOverlap, 0, 0, 0);
        }
        else
        {
            FoldersGapColumn.Width = new(0);
            FoldersColumn.Width = new(48);
            Grid.SetColumn(FolderSidebar, 0);
            Grid.SetColumnSpan(FolderSidebar, 1);
            FolderSidebar.Margin = new(0);
            Grid.SetColumn(MainListBorder, 2);
            Grid.SetColumnSpan(MainListBorder, 1);
            MainListBorder.Margin = new(0);
        }
    }
    private void CollapseFolders_Click(object sender, RoutedEventArgs e) { foldersOpen = false; UpdateFoldersWidth(); }
    private void ExpandFolders_Click(object sender, RoutedEventArgs e) { foldersOpen = true; UpdateFoldersWidth(); }
    private void Folders_ResizeRequested(object? sender, double delta)
    {
        if (!foldersOpen || !double.IsFinite(delta)) return;
        preferredFoldersWidth = Math.Clamp(Math.Min(DesiredFoldersWidth, AvailableFoldersWidth) + delta, 176, AvailableFoldersWidth);
        UpdateFoldersWidth(); App.PaneWidths.SetWidth("Cleanup.Folders", preferredFoldersWidth);
    }
    private void Folders_ResetRequested(object? sender, EventArgs e)
    { preferredFoldersWidth = null; UpdateFoldersWidth(); App.PaneWidths.SetWidth("Cleanup.Folders", null); }
    private void Category_Click(object sender, RoutedEventArgs e)
    {
        if (Model.IsBusy) return;
        var value = int.Parse(((ToggleButton)sender).Tag.ToString()!);
        if (Model.FilesMode) filesCategory = value; else { softwareCategory = value; Model.BackToSoftware(); }
        Render();
    }
    private void Search_Changed(DependencyObject sender, DependencyProperty property) { if (!rendering) Render(); }
    private void UpdateListChrome(bool files, bool residue)
    {
        if (lastFilesMode != files)
        {
            lastFilesMode = files; Search.Text = ""; Search.SelectedValue = 0;
            Search.ItemsSource = files
                ? new[] { new SelectionOption(0, "所有类型"), new SelectionOption(1, "文件夹"), new SelectionOption(2, "文件") }
                : new[] { new SelectionOption(0, "不限登记大小"), new SelectionOption(1, "有登记大小"), new SelectionOption(2, "无登记大小") };
            Search.PlaceholderText = files ? "搜索目标名称或路径" : "搜索软件名称或发布者";
        }
        var category = files ? filesCategory : softwareCategory;
        CategoryTitle.Text = files ? "清理目标" : "软件分类";
        AllCategoryLabel.Text = files ? "全部目标" : "全部软件";
        AvailableCategoryLabel.Text = files ? "未完成" : "可卸载";
        ProtectedCategory.Visibility = Visible(!files);
        AllCategory.IsChecked = category == 0; AvailableCategory.IsChecked = category == 1; ProtectedCategory.IsChecked = category == 2;
        foreach (var button in Categories.Children.OfType<Control>()) button.IsEnabled = !Model.IsBusy;
        Search.IsEnabled = SoftwareQuery.IsEnabled = SoftwareFilter.IsEnabled = !Model.IsBusy;
        Search.Visibility = Visible(files && !residue);
        SoftwareQueryField.Visibility = SoftwareFilter.Visibility = Visible(!files && !residue);
        SoftwareFilter.Content = Search.SelectedValue > 0 ? "筛选 · 1" : "筛选";
        Back.Visibility = Visible(residue); FileActions.Visibility = AddButtons.Visibility = Visible(files);
        FromStaging.IsEnabled = AddButtons.IsEnabled = !Model.IsBusy;
        ListTitle.Text = residue ? "核对残留" : files ? category == 0 ? "全部目标" : "未完成" : category switch { 1 => "可卸载", 2 => "受保护 / 无入口", _ => "已安装软件" };
    }
    private void RenderLists(bool files, bool residue)
    {
        var query = $"{Search.SelectedValue}:{Search.Text}";
        if (!files && !residue && (!ReferenceEquals(softwareSource, Model.Software) || softwareQuery != softwareCategory + query))
        {
            softwareSource = Model.Software; softwareQuery = softwareCategory + query;
            var rows = Model.Software.Where(item =>
                (softwareCategory == 0 || (softwareCategory == 1 ? item.CanUninstall && !item.IsProtected : !item.CanUninstall || item.IsProtected)) &&
                (Search.SelectedValue == 0 || (Search.SelectedValue == 1 ? item.EstimatedBytes > 0 : item.EstimatedBytes <= 0)) &&
                (item.Name.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase) || item.Publisher.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase)))
                .Select(item => new SoftwareRow(item)).ToArray();
            SoftwareList.ItemsSource = rows; SoftwareList.SelectedItem = rows.FirstOrDefault(row => row.Item.Id == Model.SelectedSoftware?.Id);
            if (Model.SelectedIsInstalled && SoftwareList.SelectedItem is null) Model.ClearSoftwareSelection();
        }
        SoftwareEmpty.Visibility = Visible(SoftwareList.Items.Count == 0);
        SoftwareEmpty.Text = Model.IsBusy ? "正在读取软件…" : Model.HasLoaded ? "没有匹配的软件。" : "点击刷新，读取已安装软件。";
        if (files && (!ReferenceEquals(pathsSource, Model.Paths) || !ReferenceEquals(planSource, Model.Plan) || !ReferenceEquals(resultsSource, Model.Results) ||
            displayedMethod != Model.Method || filesQuery != filesCategory + query))
        {
            var selected = FilesList.SelectedItems.Cast<CleanupFileRow>().Select(row => row.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            pathsSource = Model.Paths; planSource = Model.Plan; resultsSource = Model.Results; filesQuery = filesCategory + query; displayedMethod = Model.Method;
            // Preparing a retry or closing a lock clears operation results. Keep failed
            // targets in this category until they are actually handled or removed.
            fileProblemPaths.IntersectWith(Model.Paths);
            fileProblemPaths.UnionWith(Model.Results.Where(x => x.Status is CleanupStatus.Failed or CleanupStatus.Partial or CleanupStatus.NeedsAttention or CleanupStatus.Cancelled).Select(x => x.Path));
            fileProblemPaths.UnionWith(Model.Plan?.Issues.Select(x => x.Path) ?? []);
            var rows = Model.Paths.Where(path => (filesCategory == 0 || fileProblemPaths.Contains(path)) && path.Contains(Search.Text, StringComparison.CurrentCultureIgnoreCase))
                .Select(path => new CleanupFileRow(path, FileDetail(path)))
                .Where(row => Search.SelectedValue == 0 || (Search.SelectedValue == 1 ? row.IsDirectory : !row.IsDirectory)).ToArray();
            FilesList.ItemsSource = rows;
            foreach (var row in rows.Where(row => selected.Contains(row.Path))) FilesList.SelectedItems.Add(row);
        }
        FilesEmpty.Visibility = Visible(FilesList.Items.Count == 0);
        FilesEmptyTitle.Text = Model.Paths.Length == 0 ? "将文件或文件夹拖到这里" : "没有匹配的目标";
        FilesEmptyHint.Text = Model.Paths.Length == 0 ? "也可点击右上角添加文件，或从暂存区导入。" : "调整分类或清空搜索，即可查看其他目标。";
        ListCount.Text = $"{(files ? FilesList.Items.Count : residue ? Model.Residues.Length : SoftwareList.Items.Count):N0} 项";
        UpdateCategoryCounts(files);
    }

    private void UpdateCategoryCounts(bool files)
    {
        AllCategoryCount.Text = files ? Model.Paths.Length.ToString("N0") : Model.HasLoaded ? Model.Software.Length.ToString("N0") : "—";
        var available = Model.Software.Count(item => item.CanUninstall && !item.IsProtected);
        AvailableCategoryCount.Text = files ? fileProblemPaths.Count.ToString("N0") : Model.HasLoaded ? available.ToString("N0") : "—";
        ProtectedCategoryCount.Text = Model.HasLoaded ? (Model.Software.Length - available).ToString("N0") : "—";
        foreach (var (button, label, count) in new[] { (AllCategory, AllCategoryLabel, AllCategoryCount), (AvailableCategory, AvailableCategoryLabel, AvailableCategoryCount), (ProtectedCategory, ProtectedCategoryLabel, ProtectedCategoryCount) })
        {
            var text = label.Text + " · " + count.Text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, text);
            ToolTipService.SetToolTip(button, text);
        }
    }
    private string[] FileTargets() => (FilesList.SelectedItems.Count > 0 ? FilesList.SelectedItems.Cast<CleanupFileRow>() : FilesList.Items.Cast<CleanupFileRow>()).Select(row => row.Path).ToArray();
}
