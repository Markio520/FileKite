using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.Settings;

public sealed partial class EnginesView : UserControl
{
    internal StackPanel EngineRows => Rows;
    public EnginesView() => InitializeComponent();

    internal void UpdateFeedback(EnginesViewModel model)
    {
        FeedbackPanel.Visibility = model.Stage == EngineInstallStage.Idle ? Visibility.Collapsed : Visibility.Visible;
        ProgressArea.Visibility = model.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = model.Progress;
        CancelDownload.Content = InterfaceText.Get("取消下载", "Cancel download");
        AutomationProperties.SetName(DownloadProgress, InterfaceText.Get("引擎下载进度", "Engine download progress"));
        string name = model.LastPackageId == "libreoffice" ? "LibreOffice" : "FFmpeg";
        FeedbackText.Text = model.Stage switch
        {
            EngineInstallStage.Downloading => string.Format(InterfaceText.Get("正在下载 {0}… {1:0}%", "Downloading {0}… {1:0}%"), name, model.Progress),
            EngineInstallStage.Verifying => InterfaceText.Get("正在解包和校验引擎。取消时会在解包结束后清理临时文件。",
                "Unpacking and checking the engine. Cancellation cleans up temporary files after unpacking."),
            EngineInstallStage.Ready => string.Format(InterfaceText.Get("{0} 扩展包已安装。", "The {0} package has been installed."), name),
            EngineInstallStage.Cancelled => InterfaceText.Get("已取消下载。", "Download cancelled."),
            EngineInstallStage.Failed => InterfaceText.Get("引擎安装失败", "Engine installation failed") + " · " + model.Feedback,
            _ => ""
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) => App.EngineSettings.CancelCommand.Execute(null);
}
