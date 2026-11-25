using System;
using System.Threading.Tasks;
using FileAssistant.App.Controls;
using FileAssistant.App.Resources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.Workspace;

/// <summary>
/// Centered, backdrop-dismissable, user-resizable modal dialog for the Task Queue when clicking the task entry.
/// Hovering the task entry continues to show the lightweight transient popover preview.
/// </summary>
internal static class TaskCenterDialog
{
    public static bool IsOpen { get; private set; }
    private static ContentDialog? activeDialog;

    public static async Task ShowAsync(FrameworkElement owner, WorkspaceViewModel viewModel)
    {
        if (IsOpen || owner.XamlRoot is not { } xamlRoot) return;
        IsOpen = true;
        try
        {
            var theme = WorkspaceMaterials.IsActiveLightTheme(App.Preferences.Current) ? ElementTheme.Light : ElementTheme.Dark;
            var panel = new WorkspaceTaskPanel
            {
                ViewModel = viewModel,
                RequestedTheme = theme
            };
            panel.ConfigureModalMode();

            var layoutRoot = new Grid
            {
                Name = "TaskCenterLayout",
                Width = Math.Clamp(xamlRoot.Size.Width * 0.52, 420, 580),
                Height = Math.Clamp(xamlRoot.Size.Height * 0.62, 340, 480),
                Background = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceDialogBrush"),
                CornerRadius = new CornerRadius(12)
            };
            layoutRoot.Children.Add(panel);

            var dialog = new ContentDialog
            {
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                XamlRoot = xamlRoot,
                RequestedTheme = theme,
                Background = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceDialogBrush"),
                Foreground = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceTextBrush"),
                BorderBrush = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceBorderBrush"),
                CornerRadius = new CornerRadius(12),
                Content = layoutRoot
            };
            dialog.Resources["ContentDialogMinWidth"] = 360d;
            dialog.Resources["ContentDialogMinHeight"] = 0d;
            dialog.Resources["ContentDialogPadding"] = new Thickness(0);
            dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0);
            dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
            dialog.Resources["ContentDialogTopOverlay"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetName(dialog, WorkspaceLocalization.Translate("任务队列"));

            panel.CloseRequested += (_, _) => dialog.Hide();
            DialogDismiss.Attach(dialog);
            ResizableDialogShell.Attach(dialog, layoutRoot, minWidth: 380, minHeight: 300);

            void SyncTheme(FrameworkElement s, object e)
            {
                var nextTheme = WorkspaceMaterials.IsActiveLightTheme(App.Preferences.Current) ? ElementTheme.Light : ElementTheme.Dark;
                dialog.RequestedTheme = panel.RequestedTheme = nextTheme;
                var dialogBg = WorkspaceMaterials.ThemeBrush(nextTheme, "WorkspaceDialogBrush");
                layoutRoot.Background = dialog.Background = dialogBg;
                dialog.Foreground = WorkspaceMaterials.ThemeBrush(nextTheme, "WorkspaceTextBrush");
                dialog.BorderBrush = WorkspaceMaterials.ThemeBrush(nextTheme, "WorkspaceBorderBrush");
            }

            var winRoot = App.Window?.Content as FrameworkElement;
            if (winRoot is not null) winRoot.ActualThemeChanged += SyncTheme;
            activeDialog = dialog;
            try
            {
                await dialog.ShowAsync();
            }
            finally
            {
                activeDialog = null;
                if (winRoot is not null) winRoot.ActualThemeChanged -= SyncTheme;
            }
        }
        finally
        {
            IsOpen = false;
        }
    }

    public static void HideActive() => activeDialog?.Hide();
}
