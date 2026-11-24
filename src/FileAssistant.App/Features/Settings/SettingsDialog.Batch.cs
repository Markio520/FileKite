using FileAssistant.App.Features.FileInput;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private Task pendingQualitySave = Task.CompletedTask;

    private void BuildBatch()
    {
        AddSection("默认输出与冲突规则", "Output & conflict defaults", "转换 改名 复制 整理 冲突 同名 图像画质 输出 编号 CSV JPG JPEG WebP AVIF folder path destination",
            cards =>
        {
            var title = T("转换与整理默认输出位置", "Default output location for conversion and copying");
            Row(cards, title, Select(title, s => s.OutputLocation, PickOutputLocation,
            [
                (OutputLocation.Source, T("源文件所在目录", "Source file’s folder")),
                (OutputLocation.Subfolder, T("源文件下新建子文件夹", "Subfolder beside source")),
                (OutputLocation.Fixed, T("指定固定输出文件夹…", "Fixed output folder…"))
            ]), T("新建批次时的初始目标路径，可在单次任务的面板中调整", "Start each batch here. You can change the destination for an individual task."), "\uE8B7");
            var pathText = Label("", "SettingCaption");
            pathText.IsTextSelectionEnabled = true;
            var pathRow = Card(Pair(pathText, ActionButton(T("浏览…", "Browse…"), () => PickOutputLocation(OutputLocation.Fixed)), 235));
            Bind(s =>
            {
                pathText.Text = T("固定输出文件夹", "Fixed output folder") + "\n" + s.OutputFolder;
                pathRow.Visibility = Visible(s.OutputLocation == OutputLocation.Fixed);
            });
            cards.Children.Add(pathRow);
            ChoiceRow(cards, T("批量任务全部执行完成后", "When a batch finishes"), s => s.Completion, (s, v) => s with { Completion = v },
            [
                (CompletionBehavior.InWindow, T("仅窗口内更新状态", "Update status in the window")),
                (CompletionBehavior.Tray, T("后台时发送托盘通知", "Tray notification in the background")),
                (CompletionBehavior.OpenFolder, T("自动打开输出目录", "Open the output folder"))
            ]);
            ChoiceRow(cards, T("格式转换 · 同名文件处理", "Conversion · existing files"), s => s.ConversionConflict, (s, v) => s with { ConversionConflict = v },
            [
                (ConversionCollision.Number, T("自动追加编号保留 (1)", "Add a number (1)")),
                (ConversionCollision.Skip, T("跳过已存在文件", "Skip existing files")),
                (ConversionCollision.Overwrite, T("覆盖同名文件", "Overwrite existing files"))
            ]);
            ChoiceRow(cards, T("创建与改名 · 名称冲突保护", "Create & rename · name conflicts"), s => s.NamingConflict, (s, v) => s with { NamingConflict = v },
            [
                (NamingConflict.Block, T("有冲突时停止并提示", "Stop and show conflicts")),
                (NamingConflict.Number, T("自动追加序号保留", "Add a number")),
                (NamingConflict.Skip, T("跳过冲突项继续执行", "Skip conflicting items"))
            ]);
            ChoiceRow(cards, T("复制整理 · 默认目录结构", "Copy & organize · folder structure"), s => s.CopyLayout, (s, v) => s with { CopyLayout = v },
            [
                (CopyLayout.Preserve, T("保留原目录相对层级", "Keep relative folder paths")),
                (CopyLayout.Extension, T("按文件扩展名自动分夹", "Group by file extension")),
                (CopyLayout.Flat, T("全部平铺至目标根目录", "Place in the target root"))
            ]);
            ChoiceRow(cards, T("CSV 导出与文本提取编码", "CSV and extracted text encoding"), s => s.TextEncoding, (s, v) => s with { TextEncoding = v },
            [
                (DelimitedEncoding.Utf8, "UTF-8"), (DelimitedEncoding.Gb18030, T("GB18030（兼容旧版 Excel）", "GB18030 (legacy Excel)"))
            ]);
            var quality = T("有损图像转换默认画质（WebP / JPG / AVIF）", "Default lossy image quality (WebP / JPG / AVIF)");
            Row(cards, quality, QualitySlider(quality));
        });
        AddSection("性能并发与安全防护", "Performance & safeguards", "并发 线程 显卡 GPU 硬件 FFmpeg 校验 回收站 注册表 BCU", cards =>
        {
            ChoiceRow(cards, T("最大并行处理任务数", "Maximum parallel jobs"), s => s.MaxParallel, (s, v) => s with { MaxParallel = v },
            [
                (0, T("自动（按 CPU 核心分配）", "Automatic (CPU cores)")), (2, T("低占用（最多 2 并发）", "Low (up to 2)")),
                (4, T("标准（最多 4 并发）", "Standard (up to 4)")), (8, T("全速（最多 8 并发）", "Full speed (up to 8)"))
            ]);
            SwitchRow(cards, T("音视频转码优先启用 GPU 硬件加速", "Prefer hardware encoding for media conversion"),
                s => s.PreferHardwareEncoding, (s, v) => s with { PreferHardwareEncoding = v },
                T("探测 NVENC、QSV 和 AMF 编码器，缺少支持时使用软件编码", "Detect NVENC, QSV, and AMF encoders and use software encoding when needed."));
            ChoiceRow(cards, T("复制整理写入后完整性核对", "Verify files after copying"), s => s.VerifyCopyHash, (s, v) => s with { VerifyCopyHash = v },
            [
                (false, T("快速核对（校验字节长度）", "Quick check (byte length)")), (true, T("严格校验（SHA-256 哈希）", "Strict check (SHA-256)"))
            ]);
            ChoiceRow(cards, T("文件清理与卸载残留默认删除方式", "Default deletion for files and uninstall leftovers"),
                s => s.PermanentDelete, (s, v) => s with { PermanentDelete = v },
            [
                (false, T("移入系统回收站（推荐）", "Move to Recycle Bin")), (true, T("直接永久删除", "Delete permanently"))
            ]);
            SwitchRow(cards, T("清理软件注册表残留前导出 .reg 备份", "Back up registry leftovers to .reg before cleanup"),
                s => s.BackupRegistry, (s, v) => s with { BackupRegistry = v });
        });
    }

    private async Task PickOutputLocation(OutputLocation value)
    {
        if (value != OutputLocation.Fixed) { await Save(s => s with { OutputLocation = value }); return; }
        var files = await FileInputSources.PickFolderAsync();
        if (files.Count == 1) await Save(s => s with { OutputLocation = value, OutputFolder = files[0] });
    }

    private FrameworkElement QualitySlider(string title)
    {
        var slider = new Slider { Minimum = 50, Maximum = 100, StepFrequency = 1, Width = 126, VerticalAlignment = VerticalAlignment.Center };
        NameControl(slider, title);
        var value = Label("", "SettingHeading");
        value.MinWidth = 34;
        int revision = 0;
        bool pending = false;
        Bind(s =>
        {
            if (!pending) { slider.Value = s.ImageQuality; value.Text = s.ImageQuality + "%"; }
        });
        slider.ValueChanged += (_, e) =>
        {
            int quality = (int)Math.Round(e.NewValue);
            value.Text = quality + "%";
            if (!applying) pendingQualitySave = Commit(++revision, quality);
        };
        async Task Commit(int current, int quality)
        {
            pending = true;
            await Task.Delay(170);
            if (current != revision) return;
            await Save(s => s with { ImageQuality = quality });
            if (current == revision) { pending = false; if (opened) Rebind(); }
        }
        return Horizontal(8, slider, value);
    }
}
