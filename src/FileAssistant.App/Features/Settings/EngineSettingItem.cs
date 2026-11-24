using System.Windows.Input;
using FileAssistant.Core.Engines;

namespace FileAssistant.App.Features.Settings;

/// <summary>Presentation of a checked engine; only catalogue-backed engines offer installation.</summary>
public sealed record EngineSettingItem(ProcessingEngineInfo Info, ICommand? InstallCommand = null, string DownloadDescription = "")
{
    public string Title => Info.Id switch
    {
        "magick" => "图片处理",
        "ffmpeg" => "音视频处理",
        "pdfium" => "PDF 预览",
        "pdfpig" => "PDF 生成与文本提取",
        "libreoffice" => "Office 文档",
        _ => Info.Name
    };
    public string Glyph => Info.Id switch
    {
        "magick" => "\uEB9F",
        "ffmpeg" => "\uE714",
        "pdfium" => "\uEA90",
        _ => "\uE8A5"
    };
    public bool IsAvailable => Info.IsAvailable;
    public bool IsUnavailable => !IsAvailable;
    public bool CanInstall => InstallCommand is not null;
    public string InstallLabel => IsAvailable ? "修复" : "下载";
    public string InstallAutomationName => $"{InstallLabel}{Title}引擎";
    public string VersionLabel => $"版本 {Info.Version}";
    public string StatusLabel => IsAvailable
        ? CanInstall ? "已就绪" : "随软件提供"
        : Info.Id == "libreoffice" ? "未安装" : "组件缺失";
    public bool ShowDownloadDescription => CanInstall && !IsAvailable;
}
