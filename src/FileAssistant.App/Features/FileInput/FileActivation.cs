using FileAssistant.App.Features.Preview;
using FileAssistant.App.Features.Settings;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.FileInput;

internal static class FileActivation
{
    public static async Task OpenAsync(FrameworkElement owner, SearchFile file)
    {
        try
        {
            var shell = new WindowsFileInteraction();
            switch (App.Preferences.Current.DoubleClick)
            {
                case FileDoubleClick.Preview: await QuickLookDialog.ShowAsync(owner, [file]); break;
                case FileDoubleClick.Reveal: shell.Reveal(file); break;
                default: shell.Open(file); break;
            }
        }
        catch (Exception error)
        {
            App.ReportFailure(error);
            if (owner.XamlRoot is not null)
                await new ContentDialog { XamlRoot = owner.XamlRoot, RequestedTheme = owner.ActualTheme, Content = error.Message,
                    CloseButtonText = InterfaceText.Get("关闭", "Close") }.ShowAsync();
        }
    }
}
