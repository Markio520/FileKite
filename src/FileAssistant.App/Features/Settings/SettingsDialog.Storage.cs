using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private StorageUsage? usage;
    private Action? storageUpdate;
    private int storageReadVersion;
    private bool readingStorage;
    private bool storageReadFailed;
    private DateTime lastStorageRead;
    private static readonly int[] StorageColors = [0x5B9BD5, 0x6CCB94, 0x9E8CD8, 0xE6A15C, 0x8892A4, 0xA09E94, 0xBD738C];

    private void BuildStorage()
    {
        AddSection("应用本地数据占用", "Local app storage", "磁盘 存储 缓存 备份 暂存索引 SQLite JSON 下载 空间 分段 字节 %LocalAppData% FileAssistant",
            cards =>
        {
            var totalText = Label(T("正在统计…", "Measuring…"), "SettingHeading");
            totalText.FontSize = 22;
            var caption = Label(T("本机已用空间", "Used on this device"), "SettingCaption");
            var retry = ActionButton(T("重试", "Retry"), RefreshStorageAsync);
            retry.Visibility = Visibility.Collapsed;
            var header = Flow(7, totalText, caption, retry);
            var path = Label(App.ProfileDirectory, "SettingCaption");
            path.IsTextSelectionEnabled = true;
            var track = new Grid { Height = 8, ColumnSpacing = 2 };
            var hero = new StackPanel { Spacing = 9 };
            hero.Children.Add(header); hero.Children.Add(path);
            hero.Children.Add(new Border { Height = 8, Style = LocalStyle("SettingInset"), CornerRadius = new(4), Child = track });
            var heroCard = Card(hero); heroCard.Padding = new(17, 13, 17, 15);
            cards.Children.Add(heroCard);

            var engineSize = Label("…", "SettingCaption");
            var previewSize = Label("…", "SettingCaption");
            var indexSize = Label("…", "SettingCaption");
            var legacySize = Label("…", "SettingCaption");
            var configurationSize = Label("…", "SettingCaption");
            var otherSize = Label("…", "SettingCaption");
            string engine = T("离线扩展引擎包", "Offline engine packages");
            string preview = T("预览与缩略图缓存", "Previews & thumbnail cache");
            string recovery = T("改名日志与覆盖恢复备份", "Rename history & recovery backups");
            string index = T("纸鸢搜索索引（SQLite）", "FileKite search index (SQLite)");
            string legacy = T("旧版搜索缓存", "Legacy search cache");
            string configuration = T("暂存分组与配置", "Staging groups & configuration");
            string other = T("其他本地数据", "Other local data");
            string[] segmentLabels = [engine, preview, index, recovery, configuration, other, legacy];

            var manage = ActionButton(T("管理引擎", "Manage engines"), () => { Navigate("engines"); return Task.CompletedTask; });
            cards.Children.Add(StorageCard(engine, T("本地数据目录中的 FFmpeg 与按需文档组件", "FFmpeg and document components installed in the data folder."),
                0, Flow(8, engineSize, manage)));
            var clearPreview = ActionButton(T("清理缓存", "Clear cache"), () => ClearStorageAsync(false));
            cards.Children.Add(StorageCard(preview, T("清理后，下次浏览文件时会重新生成预览", "Previews are regenerated the next time you browse those files."),
                1, Flow(8, previewSize, clearPreview)));
            string retentionTitle = T("恢复备份保留时间", "Recovery backup retention");
            var retention = Select(retentionTitle, s => s.BackupRetentionDays, days => Save(s => s with { BackupRetentionDays = days }),
            [
                (7, T("保留 7 天", "Keep 7 days")), (30, T("保留 30 天", "Keep 30 days")), (0, T("仅手动清理", "Manual cleanup"))
            ], 136);
            var clearRecovery = ActionButton(T("清理备份", "Clear backups"), () => ClearStorageAsync(true));
            cards.Children.Add(StorageCard(recovery, T("用于撤销最近的批量改名或恢复被覆盖的文件", "Restore overwritten files or undo recent renames."),
                3, Flow(6, retention, clearRecovery), 483));
            var indexDesc = Label("", "SettingCaption");
            var indexLabels = new StackPanel { Spacing = 2 };
            indexLabels.Children.Add(Label(index));
            indexLabels.Children.Add(indexDesc);
            cards.Children.Add(Card(Pair(Leading(Dot(2), indexLabels), indexSize, 405)));
            var legacyCard = StorageCard(legacy, T("Search 文件夹中的 Everything 数据库与设置，供旧版使用",
                "Everything database and settings in the Search folder, kept for earlier versions."), 6, legacySize);
            legacyCard.Visibility = Visibility.Collapsed;
            cards.Children.Add(legacyCard);
            cards.Children.Add(StorageCard(configuration, T("暂存区文件引用、分组与应用设置", "Staged file references, groups, and app settings."), 4, configurationSize));
            var otherCard = StorageCard(other, T("本地日志及其余应用数据", "Local logs and remaining application files."), 5, otherSize);
            otherCard.Visibility = Visibility.Collapsed;
            cards.Children.Add(otherCard);

            storageUpdate = () =>
            {
                retry.Visibility = Visible(storageReadFailed);
                if (usage is not { } u)
                {
                    if (storageReadFailed)
                    {
                        totalText.Text = FormatBytes(-1);
                        caption.Text = T("无法统计本地数据", "Could not measure local data");
                        engineSize.Text = previewSize.Text = indexSize.Text = configurationSize.Text = otherSize.Text = FormatBytes(-1);
                        legacyCard.Visibility = Visibility.Collapsed;
                        indexDesc.Text = "";
                        clearRecovery.Content = T("清理备份", "Clear backups");
                        track.Children.Clear(); track.ColumnDefinitions.Clear();
                    }
                    return;
                }
                long[] values = [u.EnginesBytes, u.PreviewBytes, u.IndexBytes, u.RecoveryBytes, u.ConfigurationBytes, u.OtherBytes, u.LegacySearchBytes ?? 0];
                long total = values.Where(v => v >= 0).Sum();
                totalText.Text = FormatBytes(total);
                caption.Text = values.Any(v => v < 0) ? T("可统计的本地数据", "Measured local data") : T("本机已用空间", "Used on this device");
                engineSize.Text = FormatBytes(u.EnginesBytes); previewSize.Text = FormatBytes(u.PreviewBytes);
                indexSize.Text = string.Format(T("占用磁盘 {0}", "{0} on disk"), FormatBytes(u.IndexBytes));
                indexDesc.Text = string.Format(T("文件大小 {0}，含 Indexes 中的数据库、日志及共享内存文件",
                    "File size {0}, including databases, journals and shared-memory files in Indexes."), FormatBytes(u.IndexLogicalBytes));
                legacyCard.Visibility = Visible(u.LegacySearchBytes.HasValue);
                if (u.LegacySearchBytes is { } legacyBytes) legacySize.Text = FormatBytes(legacyBytes);
                configurationSize.Text = FormatBytes(u.ConfigurationBytes);
                otherSize.Text = FormatBytes(u.OtherBytes); otherCard.Visibility = Visible(u.OtherBytes != 0);
                clearRecovery.Content = string.Format(T("清理（{0}）", "Clear ({0})"), FormatBytes(u.RecoveryBytes));
                track.Children.Clear(); track.ColumnDefinitions.Clear();
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] <= 0) continue;
                    var segment = new Border { Background = ColorBrush(StorageColors[i]), CornerRadius = new(1.5) };
                    var label = segmentLabels[i] + " · " + FormatBytes(values[i]);
                    NameControl(segment, label);
                    track.ColumnDefinitions.Add(new() { Width = new((double)values[i] / total * 1000, GridUnitType.Star) });
                    Place(track, segment, track.ColumnDefinitions.Count - 1);
                }
            };
            UpdateStorageFigures();
        }, () => ActionButton(T("打开数据文件夹 ↗", "Open data folder ↗"), () => { SettingsOperations.OpenDataFolder(); return Task.CompletedTask; }));
        AddSection("配置迁移、日志与重置", "Transfer settings, logs & reset", "导出 导入 恢复出厂 默认 配置 JSON 模板 创建大纲 templates backup import export diagnostics",
            cards =>
        {
            var export = ActionButton(T("导出…", "Export…"), ExportPreferencesAsync);
            var import = ActionButton(T("导入…", "Import…"), ImportPreferencesAsync);
            Row(cards, T("便携配置与创建大纲模板", "Portable settings & creation templates"), Flow(6, export, import),
                T("导出或还原个人设置、常用搜索条件与目录创建模板", "Transfer preferences, saved searches, and folder creation templates."));
            Row(cards, T("运行错误诊断日志", "Diagnostic error logs"),
                ActionButton(T("打开日志目录", "Open log folder"), () => { SettingsOperations.OpenLogFolder(); return Task.CompletedTask; }),
                T("错误记录保存在本机 Logs\\app-errors.log", "Errors are recorded locally in Logs\\app-errors.log."));
            Row(cards, T("恢复出厂默认设置", "Restore factory defaults"), ActionButton(T("重置所有设置…", "Reset all settings…"), async () =>
            {
                if (!await ConfirmAsync(T("重置所有设置？", "Reset all settings?"),
                    T("外观、快捷键、索引和任务偏好将恢复默认。已安装引擎、暂存分组与磁盘文件将保留。",
                      "Appearance, shortcuts, indexing, and task preferences will return to their defaults. Installed engines, staging groups, and files are kept."),
                    T("重置设置", "Reset settings"))) return;
                await SettingsOperations.ResetPreferencesAsync();
                Notify(T("已恢复默认设置", "Default settings restored"), "", InfoBarSeverity.Success);
                await RefreshStorageAsync();
            }, danger: true), T("恢复偏好与快捷键，保留已装引擎和暂存分组", "Reset preferences and shortcuts, keeping installed engines and staging groups."));
        });
    }

    private static Border Dot(int color) => new() { Width = 8, Height = 8, Background = ColorBrush(StorageColors[color]), CornerRadius = new(2) };
    private Border StorageCard(string title, string description, int color, FrameworkElement action, double inlineAt = 405)
        => Card(Pair(Leading(Dot(color), Texts(title, description)), action, inlineAt), 54);
    private string FormatBytes(long bytes) => bytes < 0 ? T("未知", "Unknown") : FileDisplay.Size(bytes, App.Preferences.Current.Units);
    private void UpdateStorageFigures() => storageUpdate?.Invoke();
    private async void LoadStorage()
    {
        if (readingStorage || usage is not null && DateTime.UtcNow - lastStorageRead < TimeSpan.FromSeconds(8)) return;
        try { await RefreshStorageAsync(); }
        catch (Exception error)
        {
            if (opened) Notify(T("无法统计本地数据占用", "Could not measure local storage"), error.Message, InfoBarSeverity.Warning);
        }
    }
    private async Task RefreshStorageAsync()
    {
        int request = ++storageReadVersion;
        readingStorage = true;
        try
        {
            var measured = await SettingsOperations.GetStorageUsageAsync();
            if (request != storageReadVersion || !opened) return;
            usage = measured; storageReadFailed = false; lastStorageRead = DateTime.UtcNow;
            UpdateStorageFigures();
        }
        catch
        {
            if (opened && request == storageReadVersion)
            {
                usage = null; storageReadFailed = true;
                UpdateStorageFigures();
            }
            throw;
        }
        finally { if (request == storageReadVersion) readingStorage = false; }
    }

    private async Task ClearStorageAsync(bool recovery)
    {
        var prompt = recovery ? T("清理恢复备份？", "Clear recovery backups?") : T("清理预览与缩略图缓存？", "Clear the preview cache?");
        var detail = recovery
            ? T("清理后，相应的历史任务无法再通过这些备份撤销。原始文件、已装引擎和暂存分组将保留。",
                "Tasks that depend on the cleared backups can no longer be undone. Source files, installed engines, and staging groups are kept.")
            : T("将移除已生成的预览和缩略图，下次浏览时会重新生成。原始文件将保留。",
                "Generated previews and thumbnails will be removed and recreated on demand. Source files are kept.");
        if (!await ConfirmAsync(prompt, detail, T("确认清理", "Clear"))) return;
        if (recovery) await SettingsOperations.ClearRecoveryAsync(); else await SettingsOperations.ClearPreviewAsync();
        await RefreshStorageAsync();
        Notify(recovery ? T("已清理恢复备份", "Recovery backups cleared") : T("已清理预览缓存", "Preview cache cleared"), "", InfoBarSeverity.Success);
    }
    private async Task ExportPreferencesAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "FileAssistant-settings-" + DateTime.Now.ToString("yyyyMMdd") };
        picker.FileTypeChoices.Add(T("设置备份", "Settings backup"), new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        await SettingsOperations.ExportSettingsAsync(file.Path);
        Notify(T("已导出配置", "Settings exported"), file.Path, InfoBarSeverity.Success);
    }
    private async Task ImportPreferencesAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        if (!await ConfirmAsync(T("导入并应用备份配置？", "Import and apply settings?"),
            string.Format(T("将使用 {0} 中的设置、常用搜索条件与创建模板。", "Preferences, saved searches, and creation templates will be restored from {0}."), file.Name),
            T("导入配置", "Import settings"))) return;
        await SettingsOperations.ImportSettingsAsync(file.Path);
        Notify(T("已导入配置", "Settings imported"), file.Name, InfoBarSeverity.Success);
        await RefreshStorageAsync();
    }
}
