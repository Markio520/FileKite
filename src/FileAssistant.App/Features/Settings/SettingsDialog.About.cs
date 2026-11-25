using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private void BuildAbout()
    {
        AddSection("版本与运行环境", "Version & environment", "纸鸢 FileKite Markio 作者 关于 软件 版本 环境 检查更新 GitHub 仓库 反馈 Issues .NET Windows App SDK WinUI 3 SQLite author specifications release feedback",
            cards =>
        {
            var name = Label(InterfaceText.Brand, "SettingHeading");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(name, "Settings.About.Brand");
            name.FontSize = 17;
            var metadata = Label(string.Format(T("版本 {0} · Windows {1} · 作者 Markio", "Version {0} · Windows {1} · Author Markio"),
                SettingsOperations.AppVersion, RuntimeInformation.ProcessArchitecture), "SettingCaption");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(metadata, "Settings.About.Metadata");
            var titles = new StackPanel { Spacing = 3 };
            titles.Children.Add(name); titles.Children.Add(metadata);
            var idleCheckLabel = T("检查更新", "Check for updates");
            var checkingLabel = T("正在检查…", "Checking...");
            Button check = null!;
            check = ActionButton(idleCheckLabel, async () =>
            {
                check.IsEnabled = false;
                check.Content = checkingLabel;
                try
                {
                    var result = await SettingsOperations.QueryUpdateAsync();
                    HyperlinkButton? action = null;
                    if (result.ReleaseUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        action = new HyperlinkButton
                        {
                            Content = result.UpdateAvailable ? T("前往下载 ↗", "Download ↗") : T("打开发布页 ↗", "Open releases ↗"),
                            NavigateUri = uri
                        };
                    }
                    var severity = result.Failed
                        ? InfoBarSeverity.Warning
                        : result.UpdateAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
                    Notify(T("版本更新", "App updates"), result.Message, severity, action);
                }
                finally
                {
                    check.Content = idleCheckLabel;
                    check.IsEnabled = true;
                }
            });
            check.Style = LocalStyle("SettingPrimaryButton");
            var image = new Image { Width = 46, Height = 46 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(image, "Settings.About.Icon");
            Bind(s => AppIconAssets.SetImage(image, s.AppIcon));
            var header = Card(Pair(Leading(image, titles), check, 370), 84);
            header.Padding = new(17, 13, 17, 13);
            cards.Children.Add(header);
            SwitchRow(cards, T("启动时自动检查更新提醒", "Check for updates at startup"), s => s.CheckUpdatesAtStartup, (s, v) => s with { CheckUpdatesAtStartup = v },
                T("开启后仅查询发布版本信息", "When enabled, the app checks published version information."));
            Row(cards, T("运行环境规格", "Runtime specifications"), ActionButton(T("复制规格信息", "Copy specifications"), () =>
            {
                var data = new DataPackage(); data.SetText(SettingsOperations.RuntimeDetails);
                Clipboard.SetContent(data);
                Notify(T("运行环境信息已复制", "Runtime information copied"), "", InfoBarSeverity.Success);
                return Task.CompletedTask;
            }), SettingsOperations.RuntimeSummary);
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var repoBtn = ActionButton(T("GitHub 仓库 ↗", "GitHub ↗"), () => OpenExternalUriAsync(SettingsOperations.RepositoryUrl));
            var issuesBtn = ActionButton(T("反馈问题 ↗", "Report issue ↗"), () => OpenExternalUriAsync(SettingsOperations.IssuesUrl));
            links.Children.Add(repoBtn);
            links.Children.Add(issuesBtn);
            Row(cards, T("项目主页与问题反馈", "Project homepage & feedback"), links,
                T("查看开源代码、发布说明，或附带规格信息提交反馈", "View source code, release notes, or report issues with your runtime specs"));
        }, showHeading: false);
        AddSection("隐私承诺与开源许可声明", "Privacy & open-source licenses", "隐私 离线 零遥测 许可证 来源 声明 第三方 MIT Apache BSD LGPL MPL licenses privacy telemetry",
            cards =>
        {
            Row(cards, T("本机离线处理 · 零遥测", "Local processing · zero telemetry"),
                desc: T("文件搜索、预览、转换、空间扫描和卸载清理均在本机执行。文件内容和路径保留在你的电脑上。",
                    "File search, preview, conversion, space analysis, and cleanup run on this device. File contents and paths stay on your computer."), glyph: "\uE72E");
            var sheet = new StackPanel { Spacing = 3 };
            AddLicense(sheet, "CommunityToolkit.Mvvm / Microsoft.Data.Sqlite / TaskScheduler", "MIT", "CommunityToolkit*");
            AddLicense(sheet, "Magick.NET / PdfPig / PDFiumCore / BCUninstaller", "Apache-2.0", "Apache-2*");
            AddLicense(sheet, "PDFium (Chromium)", "BSD-3-Clause", "PDFium-*_pdfium.txt");
            AddLicense(sheet, "FFmpeg (LGPL shared)", "LGPL-3.0", null);
            AddLicense(sheet, "LibreOffice Headless", "MPL-2.0", "LibreOffice-*-license.txt");
            var folder = ActionButton(T("打开全部随包声明 ↗", "Open all bundled notices ↗"), async () =>
            {
                var directory = await StorageFolder.GetFolderFromPathAsync(SettingsOperations.LicenseDirectory);
                if (!await Launcher.LaunchFolderAsync(directory)) throw new IOException(T("无法打开许可文件夹。", "Could not open the license folder."));
            });
            sheet.Children.Add(folder);
            string title = T("第三方开源组件与许可证清单", "Third-party components and licenses");
            cards.Children.Add(Disclosure(title, Texts(title, "Licenses/", "\uE8A5"), sheet, expanded: false));
        });
    }

    private void AddLicense(StackPanel panel, string component, string license, string? pattern)
    {
        var open = ActionButton(license + " ↗", async () =>
        {
            var file = pattern is null ? Path.Combine(AppContext.BaseDirectory, "Engines", "ffmpeg", "LICENSE.txt") :
                Directory.EnumerateFiles(SettingsOperations.LicenseDirectory, pattern).FirstOrDefault();
            if (file is null || !File.Exists(file)) throw new FileNotFoundException(T("此构建中未找到相应的许可文件。", "The license file is missing from this build."));
            if (!await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(file)))
                throw new IOException(T("无法打开许可文件。", "Could not open the license file."));
        });
        open.MinHeight = 26; open.FontSize = 11.5; open.Padding = new(8, 2, 8, 2);
        NameControl(open, component + " · " + license);
        panel.Children.Add(Pair(Label(component, "SettingCaption"), open, 235));
    }

    private async Task OpenExternalUriAsync(string url)
    {
        if (!await Launcher.LaunchUriAsync(new Uri(url)))
            throw new IOException(T("无法打开外部链接。", "Could not open the external link."));
    }
}
