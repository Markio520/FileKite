using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private readonly List<Action> engineRows = [];
    private EnginesView? engineFeedback;
    private Button? engineRecheck;
    private bool enginesChecked, checkingEngines;

    private void BuildEngines()
    {
        AddSection("本机离线引擎（点击行展开详情与路径设置）", "Local engines · select a row for details and paths",
            "引擎 状态 版本 图片 图像 PDFium PDFiumCore PdfPig FFmpeg ffprobe BCU BCUninstaller LibreOffice soffice Magick.NET 音视频 安装 下载 路径 修复 取消 license video documents conversion",
            cards =>
        {
            engineFeedback = new EnginesView();
            var rows = engineFeedback.EngineRows;
            rows.Children.Add(EngineCard("magick", T("图片处理", "Image processing"),
                T("Magick.NET · PNG / JPG / WebP / AVIF / GIF / TIFF / ICO", "Magick.NET · PNG / JPG / WebP / AVIF / GIF / TIFF / ICO"),
                T("内置图像组件及其原生依赖随软件提供。", "Image processing libraries and their native dependencies are bundled with the app."), "\uEB9F"));
            rows.Children.Add(EngineCard("ffmpeg", T("音视频处理", "Audio & video processing"),
                T("FFmpeg · 视频转码、音频互转、帧提取与音轨分离", "FFmpeg · video transcoding, audio conversion, frames, and audio tracks"),
                T("可指定外部 ffmpeg.exe。相邻的 ffprobe.exe 用于读取媒体信息。", "You can select an external ffmpeg.exe with ffprobe.exe in the same folder."), "\uE714"));
            rows.Children.Add(EngineCard("pdf", T("PDF 渲染与文本提取", "PDF rendering & text extraction"),
                T("PDFium + PdfPig · 页面转图、多图合并 PDF 与文本提取", "PDFium + PdfPig · render pages, combine images into PDFs, and extract text"),
                T("PDFium 提供原生页面渲染，PdfPig 负责生成与文本提取。", "PDFium renders pages. PdfPig creates documents and extracts text."), "\uEA90"));
            rows.Children.Add(EngineCard("bcu", T("软件卸载与残留扫描", "Software uninstall & leftover scan"),
                T("BCUninstaller 6.3 核心适配 · 独立隔离辅助进程", "BCUninstaller 6.3 core adapter · isolated helper process"),
                T("FileAssistant.CleanupHost.exe 按需启动卸载与清理，受保护的操作单独请求提升权限。",
                  "FileAssistant.CleanupHost.exe runs on demand. Protected operations request elevation separately."), "\uE74D"));
            rows.Children.Add(EngineCard("libreoffice", T("Office 文档转换", "Office document conversion"),
                T("LibreOffice Headless · Word、Excel、PPT 与 PDF 本地转换", "LibreOffice Headless · convert Word, Excel, PowerPoint, and PDF locally"),
                T("可下载文档扩展包，或指定本机安装的 soffice.exe。", "Download the document package or point to an installed LibreOffice executable."), "\uE8A5"));
            cards.Children.Add(engineFeedback);
            UpdateEngineCards();
        }, () => engineRecheck = ActionButton(T("重新检查", "Check again"), CheckEnginesAsync));
    }

    private Expander EngineCard(string id, string title, string description, string detail, string glyph)
    {
        var model = App.EngineSettings;
        bool downloadable = id is "ffmpeg" or "libreoffice";

        // Col 0: Uniform 36x36 rounded icon badge
        var iconGlyph = Icon(glyph, 16);
        iconGlyph.Style = LocalStyle("SettingEngineIconGlyph");
        iconGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        iconGlyph.VerticalAlignment = VerticalAlignment.Center;
        var iconBadge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new(8),
            BorderThickness = new(1),
            Style = LocalStyle("SettingInset"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = iconGlyph
        };

        // Col 1: Left-aligned Title & single-line Subtitle
        var titleLabel = Label(title);
        titleLabel.FontWeight = FontWeights.SemiBold;
        titleLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        titleLabel.MaxLines = 1;
        var descLabel = Label(description, "SettingCaption");
        descLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        descLabel.MaxLines = 1;
        ToolTipService.SetToolTip(descLabel, description);
        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(titleLabel);
        titleStack.Children.Add(descLabel);

        // Col 2: Fixed-width Status Pill (aligned along strict vertical axis across all 5 rows)
        var statusDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new(3),
            VerticalAlignment = VerticalAlignment.Center,
            Style = LocalStyle("SettingStatusDotMuted")
        };
        var statusText = Label(T("待检查", "Not checked"), "SettingCaption");
        statusText.FontSize = 11.5;
        statusText.FontWeight = FontWeights.SemiBold;
        statusText.TextWrapping = TextWrapping.NoWrap;
        var statusInner = Horizontal(5, statusDot, statusText);
        statusInner.HorizontalAlignment = HorizontalAlignment.Center;
        var statusPill = new Border
        {
            Width = 96,
            Height = 24,
            CornerRadius = new(12),
            BorderThickness = new(1),
            Padding = new(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = LocalStyle("SettingInset"),
            Child = statusInner
        };

        // Col 3: Fixed-width Action Slot (90px uniform width across all 5 rows)
        Button? install = null;
        FrameworkElement actionElement;
        if (downloadable)
        {
            install = ActionButton(T("下载", "Download"), async () =>
            {
                var command = id == "ffmpeg" ? model.DownloadVideoCommand : model.DownloadOfficeCommand;
                if (command.CanExecute(null)) await command.ExecuteAsync(null);
            });
            install.Width = 90;
            install.MinHeight = 30;
            install.Padding = new(8, 4, 8, 4);
            install.HorizontalAlignment = HorizontalAlignment.Right;
            install.VerticalAlignment = VerticalAlignment.Center;
            actionElement = install;
        }
        else
        {
            var builtinText = Label(T("内置免配", "Built-in"), "SettingCaption");
            builtinText.FontSize = 11.5;
            builtinText.Opacity = 0.78;
            builtinText.HorizontalAlignment = HorizontalAlignment.Center;
            builtinText.VerticalAlignment = VerticalAlignment.Center;
            actionElement = new Border
            {
                Width = 90,
                Height = 28,
                CornerRadius = new(6),
                BorderThickness = new(1),
                Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = LocalStyle("SettingBuiltinBadge"),
                Child = builtinText
            };
        }

        var headerGrid = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
        var colIcon = new ColumnDefinition { Width = new(36) };
        var colTitle = new ColumnDefinition { Width = new(1, GridUnitType.Star) };
        var colStatus = new ColumnDefinition { Width = GridLength.Auto };
        var colAction = new ColumnDefinition { Width = GridLength.Auto };
        headerGrid.ColumnDefinitions.Add(colIcon);
        headerGrid.ColumnDefinitions.Add(colTitle);
        headerGrid.ColumnDefinitions.Add(colStatus);
        headerGrid.ColumnDefinitions.Add(colAction);
        Place(headerGrid, iconBadge, 0);
        Place(headerGrid, titleStack, 1);
        Place(headerGrid, statusPill, 2);
        Place(headerGrid, actionElement, 3);

        headerGrid.SizeChanged += (_, e) =>
        {
            if (!downloadable)
                actionElement.Visibility = Visible(e.NewSize.Width >= 380);
        };

        // Expanded Drawer aligned with Title column (16px expander pad + 48px = 64px == 16px + 36px icon + 12px gap)
        var body = new StackPanel { Spacing = 9 };
        body.Children.Add(Label(detail, "SettingCaption"));

        var details = Label("", "SettingCaption");
        details.IsTextSelectionEnabled = true;
        var metaCard = new Border
        {
            Style = LocalStyle("SettingCard"),
            Padding = new(11, 8, 11, 8),
            CornerRadius = new(6),
            Child = details
        };
        body.Children.Add(metaCard);
        if (downloadable) body.Children.Add(EnginePathEditor(id));

        var headerHost = new Border { Padding = new(0, 4, 4, 4), HorizontalAlignment = HorizontalAlignment.Stretch, Child = headerGrid };
        var contentHost = new Border { Padding = new(48, 2, 4, 4), HorizontalAlignment = HorizontalAlignment.Stretch, Child = body };
        var expander = new Expander
        {
            Style = LocalStyle("SettingExpander"),
            Header = headerHost,
            Content = contentHost
        };
        AutomationProperties.SetName(expander, title);
        AutomationProperties.SetAutomationId(expander, "Settings.Engine." + id);

        void Update()
        {
            var checkedItems = id == "pdf" ? model.Engines.Where(x => x.Info.Id is "pdfium" or "pdfpig").ToArray()
                : model.Engines.Where(x => x.Info.Id == id).ToArray();
            bool known = enginesChecked || model.Engines.Count > 0;
            bool available = id == "bcu" ? known && model.MissingCleanupComponents.Count == 0
                : checkedItems.Length == (id == "pdf" ? 2 : 1) && checkedItems.All(x => x.IsAvailable);
            var statusName = !known ? T("正在检查…", "Checking…") : available
                ? downloadable ? T("已就绪", "Ready") : T("随软件提供", "Bundled")
                : id == "libreoffice" ? T("未安装", "Not installed") : T("组件缺失", "Missing");
            statusText.Text = statusName;
            statusText.Style = LocalStyle(available ? "SettingSuccess" : "SettingCaption");
            statusText.FontSize = 11.5;
            statusText.FontWeight = FontWeights.SemiBold;
            statusPill.Style = LocalStyle(available ? "SettingStatusPillOk" : "SettingInset");
            statusPill.BorderOpacity(available ? 0.35 : 1.0);
            statusDot.Style = LocalStyle(available ? "SettingStatusDotOk" : "SettingStatusDotMuted");

            if (install is not null)
            {
                install.Content = available ? T("修复 / 校验", "Repair / verify") : T("下载", "Download");
                install.Style = LocalStyle(known && !available ? "SettingPrimaryButton" : "SettingButton");
                install.Width = 90;
                install.MinHeight = 30;
                install.Padding = new(8, 4, 8, 4);
                install.IsEnabled = known && !model.IsBusy && !checkingEngines;
                NameControl(install, install.Content + " · " + title);
            }
            if (id == "bcu")
            {
                details.Text = model.CleanupHostPath;
                if (known && !available) details.Text += "\n" + T("缺少组件", "Missing components") + " · " + string.Join(", ", model.MissingCleanupComponents);
                return;
            }
            var setting = App.Preferences.Current;
            var custom = id == "ffmpeg" ? setting.FfmpegPath : id == "libreoffice" ? setting.OfficePath : "";
            var lines = new List<string>();
            foreach (var item in checkedItems)
            {
                string name = item.Info.Id == "libreoffice" ? "LibreOffice" : item.Info.Name;
                if (custom.Length > 0) lines.Add(name + " · " + T("外部可执行文件", "External executable") + "\n" + custom);
                else if (item.Info.IsAvailable)
                {
                    var location = item.Info.Location;
                    if (id == "ffmpeg" && Directory.Exists(location)) location = Path.Combine(location, "ffmpeg.exe");
                    lines.Add(name + " " + item.Info.Version + "\n" + location);
                }
                else lines.Add(name + " · " + T("当前不可用", "Unavailable"));
            }
            if (downloadable)
            {
                var pack = id == "ffmpeg" ? model.VideoPackage : model.OfficePackage;
                lines.Add(string.Format(T("下载包 {0} · Windows x64 · {1} · {2}", "Download {0} · Windows x64 · {1} · {2}"),
                    pack.Version, FileDisplay.Size(pack.DownloadBytes, shown.Units), pack.License));
            }
            details.Text = string.Join("\n", lines);
        }
        engineRows.Add(Update);
        return expander;
    }

    private FrameworkElement EnginePathEditor(string engine)
    {
        bool media = engine == "ffmpeg";
        string title = T("可执行文件路径", "Executable path");
        var input = new TextBox
        {
            Style = LocalStyle("SettingFieldStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = media ? T("自动检测 ffmpeg.exe", "Automatically detect ffmpeg.exe") : T("自动检测 LibreOffice", "Automatically detect LibreOffice")
        };
        NameControl(input, title + " · " + (media ? "ffmpeg.exe" : "soffice.exe"));
        string? previous = null;
        async Task Set(string value)
        {
            var path = value.Trim().Trim('"');
            if (path.Length > 0)
            {
                string file = Path.GetFileName(path);
                bool validName = media ? file.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                    : file.Equals("soffice.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("soffice.com", StringComparison.OrdinalIgnoreCase);
                if (!validName || !Path.IsPathFullyQualified(path) || !File.Exists(path))
                    throw new ArgumentException(media
                        ? T("请选择现有 ffmpeg.exe 的完整路径。", "Choose the full path to an existing ffmpeg.exe.")
                        : T("请选择现有 soffice.exe 或 soffice.com 的完整路径。", "Choose the full path to an existing soffice.exe or soffice.com."));
                path = Path.GetFullPath(path);
            }
            await Save(s => media ? s with { FfmpegPath = path } : s with { OfficePath = path });
        }
        var apply = ActionButton(T("应用", "Apply"), () => Set(input.Text));
        var browse = ActionButton(T("浏览…", "Browse…"), async () =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            if (!media) picker.FileTypeFilter.Add(".com");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            if (await picker.PickSingleFileAsync() is { } selected) await Set(selected.Path);
        });
        var clear = ActionButton(T("恢复自动检测", "Use automatic detection"), () => Set(""));
        var buttons = Flow(6, browse, apply, clear);
        Bind(s =>
        {
            string path = media ? s.FfmpegPath : s.OfficePath;
            if (path != previous) { previous = path; input.Text = path; }
            clear.Visibility = Visible(path.Length > 0);
        });
        input.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            try { await Set(input.Text); }
            catch (Exception error) { Notify(T("无法应用引擎路径", "Could not apply the engine path"), error.Message, InfoBarSeverity.Error); }
        };
        var rowGrid = new Grid { ColumnSpacing = 8 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(rowGrid, input, 0);
        Place(rowGrid, buttons, 1);

        var editor = new StackPanel { Spacing = 5 };
        editor.Children.Add(Label(title, "SettingCaption"));
        editor.Children.Add(rowGrid);
        return editor;
    }

    private async void LoadEngines()
    {
        if (enginesChecked || checkingEngines || App.EngineSettings.IsBusy) return;
        try { await CheckEnginesAsync(); }
        catch (Exception error) { Notify(T("引擎检查失败", "Engine check failed"), error.Message, InfoBarSeverity.Error); }
    }
    private async Task CheckEnginesAsync()
    {
        if (checkingEngines || App.EngineSettings.IsBusy) return;
        checkingEngines = true;
        UpdateEngineCards();
        try
        {
            var refresh = App.EngineSettings.RefreshCommand;
            if (refresh.CanExecute(null)) await refresh.ExecuteAsync(null);
            else if (refresh.ExecutionTask is { } inFlight) await inFlight;
            enginesChecked = true;
        }
        finally { checkingEngines = false; if (opened) UpdateEngineCards(); }
    }
    private void EngineAvailabilityChanged(object? sender, EventArgs args)
    {
        enginesChecked = true;
        UpdateEngineCards();
    }
    private void EngineProgressChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!opened) return;
        if (args.PropertyName is nameof(EnginesViewModel.IsBusy)) UpdateEngineCards();
        else if (args.PropertyName is nameof(EnginesViewModel.Stage) or nameof(EnginesViewModel.Progress) or nameof(EnginesViewModel.Feedback))
            engineFeedback?.UpdateFeedback(App.EngineSettings);
    }
    private void UpdateEngineCards()
    {
        foreach (var render in engineRows) render();
        if (engineRecheck is not null) engineRecheck.IsEnabled = !App.EngineSettings.IsBusy && !checkingEngines;
        engineFeedback?.UpdateFeedback(App.EngineSettings);
    }
}

internal static class BorderStyleExtensions
{
    public static void BorderOpacity(this Border border, double thickness) =>
        border.BorderThickness = new Thickness(thickness >= 1 ? 1 : 1);
}
