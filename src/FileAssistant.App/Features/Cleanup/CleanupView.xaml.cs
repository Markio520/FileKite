using FileAssistant.App.Controls;
using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.Cleanup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace FileAssistant.App.Features.Cleanup;

public sealed partial class CleanupView : UserControl
{
    private CleanupViewModel Model => App.Cleanup;
    private bool rendering;
    private bool viewingResults;
    private bool confirmingUninstall;
    private string actionScope = "";
    private object? softwareSource, pathsSource, planSource, resultsSource, residuesSource, locksSource;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? radarTimer;
    private long radarStartTicks;
    private long lastAnimatedBytes = -1;
    public CleanupView()
    {
        InitializeComponent();
        preferredFoldersWidth = App.PaneWidths.GetWidth("Cleanup.Folders");
        Search.RegisterPropertyChangedCallback(FilterSearchBox.TextProperty, Search_Changed);
        Search.RegisterPropertyChangedCallback(FilterSearchBox.SelectedValueProperty, Search_Changed);
        Method.ItemsSource = new[] { new SelectionOption(0, "移入回收站"), new SelectionOption(1, "永久删除") };
        radarTimer = DispatcherQueue.CreateTimer();
        radarTimer.Interval = TimeSpan.FromMilliseconds(24);
        radarTimer.IsRepeating = true;
        radarTimer.Tick += (_, _) =>
        {
            var t = (System.Diagnostics.Stopwatch.GetElapsedTime(radarStartTicks).TotalSeconds % 1.35) / 1.35;
            var eased = 1.0 - Math.Pow(1.0 - t, 2.4);
            RadarOuterScale.ScaleX = RadarOuterScale.ScaleY = 0.45 + 0.65 * eased;
            RadarOuterRing.Opacity = Math.Clamp(0.95 * (1.0 - eased), 0, 1);
        };
        Loaded += (_, _) => { Model.Changed += Model_Changed; Render(); };
        Unloaded += (_, _) => { radarTimer?.Stop(); Model.Changed -= Model_Changed; };
        var paste = new KeyboardAccelerator { Key = Windows.System.VirtualKey.V, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        paste.Invoked += async (_, e) => { if (!Model.FilesMode || Model.IsBusy || FocusManager.GetFocusedElement(XamlRoot) is TextBox) return; e.Handled = true; await PasteAsync(); };
        KeyboardAccelerators.Add(paste);
        Render();
    }
    public async void SetActive(bool active) { if (active && !Model.HasLoaded && !Model.FilesMode && !Model.IsBusy) await Model.LoadSoftwareAsync(); }
    public void FocusQuery() { if (Model.FilesMode) Search.FocusQuery(); else if (!Model.ShowResidues) SoftwareQuery.Focus(FocusState.Programmatic); }
    private void Model_Changed(object? sender, EventArgs e)
    {
        if (rendering) return;
        if (!Model.IsBusy && !ReferenceEquals(pathsSource, Model.Paths) && Model.Results.Length == 0)
            viewingResults = false;
        Render();
    }
    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Render()
    {
        if (Status is null) return;
        rendering = true;
        try
        {
            var busy = Model.IsBusy; var files = Model.FilesMode; var residue = !files && Model.ShowResidues;
            var results = !busy && viewingResults;
            UpdateListChrome(files, residue);
            SoftwareTab.IsChecked = !files; FilesTab.IsChecked = files;
            SoftwareTab.IsEnabled = FilesTab.IsEnabled = Refresh.IsEnabled = !busy;
            Refresh.Visibility = Visible(!files); SoftwareArea.Visibility = Visible(!files && !residue);
            FilesArea.Visibility = Visible(files); ResidueArea.Visibility = Visible(residue);
            SoftwareOptions.Visibility = Visible(!busy && !files && (!results || Model.CanScanResidue));
            CleanupOptions.Visibility = Visible(!busy && !results && (files || residue));
            OptionsTitle.Text = busy ? "正在处理" : results ? "本次处理结果" : files || residue ? "清理设置" : "卸载设置";
            FilesList.IsEnabled = SoftwareList.IsEnabled = ResidueList.IsEnabled = Back.IsEnabled = !busy;
            Method.IsEnabled = Administrator.IsEnabled = !busy; Method.SelectedValue = (int)Model.Method; Administrator.IsChecked = Model.Elevated;
            MethodNote.Text = Model.Method == CleanupMethod.Recycle ? "文件移入回收站，不支持时会停止并报告。" : "永久删除无法通过回收站恢复。";
            if (residue) MethodNote.Text += Model.BackupRegistry ? "注册表项会备份后移除。" : "注册表备份已关闭。";
            Status.Text = busy ? Model.ProgressText : string.Empty;
            IdleStatusText.Text = busy ? string.Empty : Model.Message;
            IdleStatusText.Visibility = Visible(!busy && Model.Message.Length > 0);
            RadarScanBanner.Visibility = Progress.Visibility = Cancel.Visibility = Visible(busy);
            if (busy) { if (radarTimer is { IsRunning: false }) { radarStartTicks = System.Diagnostics.Stopwatch.GetTimestamp(); radarTimer.Start(); } }
            else radarTimer?.Stop();

            var estimatedBytes = files && Model.Plan is { } plan ? plan.Roots.Sum(r => r.Bytes)
                : (!files && !residue && Model.SelectedSoftware is { EstimatedBytes: > 0 } sw ? sw.EstimatedBytes : 0);
            FreedBytesCard.Visibility = Visible(!busy && estimatedBytes > 0);
            if (!busy && estimatedBytes > 0 && estimatedBytes != lastAnimatedBytes)
            {
                lastAnimatedBytes = estimatedBytes;
                WorkspaceMotion.AnimateRollingBytes(FreedBytesValue, estimatedBytes, WorkspaceFile.FormatSize);
            }
            else if (estimatedBytes <= 0) lastAnimatedBytes = -1;

            Cancel.IsEnabled = !Model.IsCancelling;
            Cancel.Content = !files && !residue ? "停止等待 / 检查" : "停止";
            Details.IsEnabled = Model.Results.Length + Model.Warnings.Length + (Model.Plan?.Issues.Count ?? 0) > 0;
            Details.Visibility = Visible(Details.IsEnabled);
            RenderLists(files, residue);
            var targets = FileTargets();
            RemoveFiles.IsEnabled = !busy && FilesList.SelectedItems.Count > 0;
            if (!ReferenceEquals(residuesSource, Model.Residues)) { residuesSource = Model.Residues; ResidueList.ItemsSource = Model.Residues.Select(item => new ResidueRow(item)).ToArray(); }
            if (!ReferenceEquals(locksSource, Model.Locks)) { locksSource = Model.Locks; LocksList.ItemsSource = Model.Locks.Select(item => new CleanupLockRow(item)).ToArray(); }
            LocksPanel.Visibility = Visible(!busy && files && Model.Locks.Length > 0); LocksList.IsEnabled = !busy;
            CloseLocks.IsEnabled = TerminateLocks.IsEnabled = !busy && SelectedLocks().Length > 0 && LocksList.SelectedItems.Cast<CleanupLockRow>().All(row => row.Item.CanClose);
            SoftwareName.Text = Model.SelectedSoftware?.Name ?? "选择一个软件";
            SoftwareDetails.Text = Model.SelectedSoftware is { } selected ? string.Join("\n", new[] { selected.Publisher, selected.Version,
                "登记大小 " + (selected.EstimatedBytes > 0 ? WorkspaceFile.FormatSize(selected.EstimatedBytes) : "未提供"), selected.InstallLocation,
                Model.SelectedIsInstalled ? selected.IsProtected ? "受保护，不能卸载" : "当前仍有软件登记" : "软件登记已移除，可检查残留" }.Where(text => text.Length > 0)) : "卸载后可继续检查文件和注册表残留。";
            ScanResidue.IsEnabled = Model.CanScanResidue;
            ResidueHint.Visibility = Visible(Model.SelectedSoftware is not null && Model.SelectedIsInstalled);
            Execute.Content = files ? $"{(Model.Method == CleanupMethod.Recycle ? "移入回收站" : "永久删除")} {targets.Length:N0} 项…" : residue ? $"清理选中的 {ResidueList.SelectedItems.Count} 项…" : Model.SelectedSoftware is { CanUninstall: true, CanUninstallQuietly: false } ? "打开卸载向导…" : "卸载所选软件…";
            Execute.IsEnabled = files ? !busy && targets.Length > 0 : residue ? !busy && ResidueList.SelectedItems.Count > 0 : Model.CanUninstall && SoftwareList.SelectedItem is SoftwareRow;
            Execute.Visibility = Visible(!busy && !results);
            Execute.Style = (Style)Application.Current.Resources[(files || residue) && Model.Method == CleanupMethod.Permanent ? "WorkspaceDangerActionStyle" : "WorkspacePrimaryActionStyle"];
            ContinueSettings.Visibility = Visible(results);
            ActionNote.Visibility = Visible(!results);
            var scope = files ? (FilesList.SelectedItems.Count > 0 ? $"已选 {targets.Length:N0} 项" : $"当前筛选列表 · {targets.Length:N0} 项") + " · 含文件夹内全部内容"
                : residue ? $"已勾选 {ResidueList.SelectedItems.Count:N0} 项残留"
                : Model.SelectedSoftware is { } target ? "卸载 " + target.Name : "请选择要卸载的软件";
            ActionNote.Text = busy && actionScope.Length > 0 ? actionScope : scope;
            ToolTipService.SetToolTip(ActionNote, ActionNote.Text);
        }
        finally { rendering = false; }
    }
    private string FileDetail(string path)
    {
        if (Model.Results.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } result) return result.Message;
        if (Model.Plan?.Issues.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } issue) return issue.Message;
        if (Model.Plan?.Roots.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } root) return $"可处理 · {WorkspaceFile.FormatSize(root.Bytes)} · {root.Nodes.Count:N0} 个项目";
        if (Model.Plan?.Roots.Any(root => path.StartsWith(root.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) == true) return "已包含在上级目标中";
        return Model.Method == CleanupMethod.Recycle ? "拟移入回收站" : "拟永久删除";
    }
    private void Software_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering) return;
        viewingResults = false;
        Model.SelectSoftware((SoftwareList.SelectedItem as SoftwareRow)?.Item);
        Render();
    }
    private void FileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || MarqueeSelection.GetIsSelecting(FilesList)) return;
        Files_MarqueeCommitted(sender, EventArgs.Empty);
    }
    private void Files_MarqueeCommitted(object? sender, EventArgs e)
    {
        if (!Model.IsBusy) viewingResults = false;
        Render();
    }
    private void Residue_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!rendering) { viewingResults = false; Render(); } }
    private void Locks_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!rendering) Render(); }
    private async void SoftwareTab_Click(object sender, RoutedEventArgs e) { viewingResults = false; Model.SetMode(false); if (!Model.HasLoaded) await Model.LoadSoftwareAsync(); }
    private void FilesTab_Click(object sender, RoutedEventArgs e) { viewingResults = false; Model.SetMode(true); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Model.LoadSoftwareAsync();
    private void Back_Click(object sender, RoutedEventArgs e) { viewingResults = false; Model.BackToSoftware(); }
    private void Collapse_Click(object sender, RoutedEventArgs e) => DetailsPane.IsPaneOpen = false;
    private void Expand_Click(object sender, RoutedEventArgs e) => DetailsPane.IsPaneOpen = true;
    private void Options_Changed(object? sender, EventArgs e) { if (!rendering) Model.SetOptions((CleanupMethod)Method.SelectedValue, Administrator.IsChecked == true); }
    private void Options_Checked(object sender, RoutedEventArgs e) => Options_Changed(sender, EventArgs.Empty);
    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await AddAsync(FileInputSources.PickFilesAsync);
    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await AddAsync(FileInputSources.PickFolderAsync);
    private async Task AddAsync(Func<Task<IReadOnlyList<string>>> read) { try { viewingResults = false; Model.AddPaths(await read()); } catch (Exception error) { Model.Feedback(error.Message); } }
    private async Task PasteAsync() => await AddAsync(() => FileInputSources.ReadAsync(Clipboard.GetContent()));
    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteAsync();
    private void Files_DragOver(object sender, DragEventArgs e) { e.AcceptedOperation = !Model.IsBusy && FileInputSources.CanRead(e.DataView) ? DataPackageOperation.Copy : DataPackageOperation.None; e.Handled = true; }
    private async void Files_Drop(object sender, DragEventArgs e) { var deferral = e.GetDeferral(); try { await AddAsync(() => FileInputSources.ReadAsync(e.DataView)); } finally { deferral.Complete(); } }
    private void RemoveFiles_Click(object sender, RoutedEventArgs e) => Model.RemovePaths(FilesList.SelectedItems.Cast<CleanupFileRow>().Select(item => item.Path).ToArray());
    private async void FromStaging_Click(object sender, RoutedEventArgs e)
    {
        var bundle = await FileCollectionDialogs.PickFromStagingAsync(App.Workspace, this, "从暂存区导入清理清单", "导入");
        if (bundle is { Files.Count: > 0 }) Model.AddPaths(bundle.Files.Select(file => file.FullPath));
    }
    private void FromStaging_QuickImportRequested(object? sender, StagingQuickImportEventArgs e)
    {
        if (e.Bundle.Files.Count > 0) Model.AddPaths(e.Bundle.Files.Select(file => file.FullPath));
    }
    private async void ScanResidue_Click(object sender, RoutedEventArgs e) { viewingResults = false; await Model.ScanResidueAsync(); }
    private void ContinueSettings_Click(object sender, RoutedEventArgs e) { viewingResults = false; Render(); OptionsPanel.ScrollToTop(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Model.Cancel();
    private CleanupLock[] SelectedLocks() => LocksList.SelectedItems.Cast<CleanupLockRow>().Select(item => item.Item).ToArray();
    private async void CloseLocks_Click(object sender, RoutedEventArgs e) => await CloseLocksAsync(false);
    private async void TerminateLocks_Click(object sender, RoutedEventArgs e) => await CloseLocksAsync(true);
    private async Task CloseLocksAsync(bool terminate)
    {
        var selected = SelectedLocks(); if (selected.Length == 0) return;
        var note = terminate ? "将结束以下进程，未保存的工作可能丢失。退出后会重新核对删除目标，等待你确认。" : "将请求以下程序关闭，请先保存工作。退出后会重新核对删除目标，等待你确认。";
        if (await ConfirmAsync(terminate ? "结束占用进程" : "关闭占用程序", note, selected.Select(item => $"{item.Name} · PID {item.ProcessId}"), terminate ? "结束进程" : "请求关闭"))
        {
            await Model.CloseLocksAsync(selected, terminate);
            if (Model.Results.Length > 0 && Model.Results.All(item => item.Status == CleanupStatus.Succeeded))
                await ExecuteFilesAsync();
        }
    }
    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (Model.IsBusy) return;
        if (Model.FilesMode) await ExecuteFilesAsync();
        else if (Model.ShowResidues)
        {
            var selected = ResidueList.SelectedItems.Cast<ResidueRow>().Select(row => row.Item).ToArray();
            if (await ConfirmAsync("清理选中残留", (Model.BackupRegistry ? "注册表项先备份。文件将" : "注册表项将直接移除。文件将") + (Model.Method == CleanupMethod.Recycle ? "移入回收站。" : "永久删除，无法通过回收站恢复。"), selected.Select(item => item.Path + (item.ValueName is null ? "" : " → " + item.ValueName)), "清理选中项"))
                await ShowOperationResultAsync(() => Model.RemoveResidueAsync(selected));
        }
        else if (Model.SelectedSoftware is { } software)
            await ConfirmUninstallAsync(software, software.CanUninstallQuietly);
    }

    private async void SoftwareRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SoftwareRow row } || Model.IsBusy || confirmingUninstall) return;
        e.Handled = true;
        SoftwareList.SelectedItem = row;
        Model.SelectSoftware(row.Item);
        if (row.Item.CanUninstallQuietly) await ConfirmUninstallAsync(row.Item, true);
        else
        {
            confirmingUninstall = true;
            try
            {
                var dialog = DialogDismiss.Attach(new ContentDialog { Title = row.Item.Name, Content = Model.SoftwareSelectionHint, CloseButtonText = "知道了", XamlRoot = XamlRoot, RequestedTheme = ActualTheme });
                await dialog.ShowAsync();
            }
            finally { confirmingUninstall = false; }
        }
    }

    private async Task ConfirmUninstallAsync(InstalledSoftware software, bool quiet)
    {
        if (confirmingUninstall || !Model.CanUninstall) return;
        confirmingUninstall = true;
        try
        {
            var note = quiet ? "确认后将在后台卸载此软件。完成后可以检查残留。" : "此软件只提供交互式卸载。确认后会打开它的卸载向导。";
            if (await ConfirmAsync("卸载 " + software.Name, note, new[] { software.Publisher, software.InstallLocation }.Where(s => !string.IsNullOrWhiteSpace(s)), quiet ? "确认卸载" : "打开卸载向导")
                && Model.SelectedSoftware?.Fingerprint == software.Fingerprint && Model.SelectedSoftware.Id == software.Id)
                await ShowOperationResultAsync(() => Model.UninstallAsync(quiet));
        }
        finally { confirmingUninstall = false; }
    }

    private async Task ShowOperationResultAsync(Func<Task> run)
    {
        actionScope = ActionNote.Text;
        OptionsPanel.ScrollToTop();
        await run();
        actionScope = "";
        viewingResults = true;
        Render();
        OptionsPanel.ScrollToTop();
    }
    private async Task ExecuteFilesAsync()
    {
        if (Model.IsBusy || Model.Paths.Length == 0) return;
        var targets = FileTargets();
        if (targets.Length == 0) return;
        actionScope = ActionNote.Text;
        OptionsPanel.ScrollToTop();
        await Model.PlanAsync(targets);
        actionScope = "";
        if (!Model.CanRunFiles || Model.Plan is not { } plan) return;
        var method = Model.Method; var elevated = Model.Elevated;
        var permanent = method == CleanupMethod.Permanent;
        var note = permanent ? "此操作无法通过回收站恢复。将删除以下目标。" : "将把以下目标移入回收站；不支持回收时会保留并报告。";
        if (plan.Issues.Count > 0) note += $"\n另有 {plan.Issues.Count} 个目标无法处理，将保留，可在详情中查看原因。";
        if (await ConfirmAsync(permanent ? "永久删除所列目标" : "移入回收站", note, plan.Roots.Select(item => item.Path), permanent ? "永久删除" : "移入回收站") &&
            ReferenceEquals(Model.Plan, plan) && Model.Method == method && Model.Elevated == elevated)
            await ShowOperationResultAsync(Model.RunFilesAsync);
    }
    private async Task<bool> ConfirmAsync(string title, string note, IEnumerable<string> paths, string action)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 620 };
        panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { MaxHeight = 320, Content = new TextBlock { Text = string.Join("\n", paths), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
        var dialog = FileCollectionDialogs.Dialog(this, title, panel, action); dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        var lines = Model.Results.Select(item => item.Path + "\n" + item.Message).Concat(Model.Plan?.Issues.Select(item => item.Path + "\n" + item.Message) ?? []).Concat(Model.Warnings);
        var dialog = DialogDismiss.Attach(new ContentDialog { Title = "处理详情", XamlRoot = XamlRoot, RequestedTheme = ActualTheme, CloseButtonText = "关闭",
            Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock { Text = string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 620 } } });
        await dialog.ShowAsync();
    }
}
