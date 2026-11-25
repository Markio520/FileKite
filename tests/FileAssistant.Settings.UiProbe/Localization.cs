using FileAssistant.App.Controls;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static async Task CheckLanguages(FrameworkElement root, WorkspacePage workspace)
    {
        await Preferences.UpdateAsync(s => s with { Language = InterfaceLanguage.SimplifiedChinese });
        var opened = workspace.OpenSettingsAsync(initialPage: "appearance");
        var dialog = await Opened(workspace, opened);
        foreach (var (palette, theme) in new[] { ("ocean", ElementTheme.Dark), ("forest", ElementTheme.Dark), ("frost", ElementTheme.Light) })
        {
            var mode = theme == ElementTheme.Light ? AppearanceMode.Light : AppearanceMode.Dark;
            if (Preferences.Current.Mode != mode)
            {
                var option = mode == AppearanceMode.Light ? "外观模式 · 浅色" : "外观模式 · 深色";
                InvokeButton(WithName<ToggleButton>(dialog, option));
                await Until(() => Preferences.Current.Mode == mode, "Color mode did not switch to " + mode);
                await Settle(dialog);
            }
            InvokeButton(WithId<ToggleButton>(dialog, "Settings.Palette." + palette));
            await Until(() => Preferences.Current.Palette == palette && root.ActualTheme == theme, "Palette did not update the main workbench: " + palette);
            await Settle(dialog);
            Require(WithId<ToggleButton>(dialog, "Settings.Palette." + palette).IsChecked == true, "Selected palette lost its indicator.");
            if (palette == "frost")
            {
                await SavePng(Part<Grid>(dialog, "DialogLayout"), "appearance-light");
                await Navigate(dialog, "engines");
                await Task.Delay(350);
                await Settle(dialog);
                await SavePng(Part<Grid>(dialog, "DialogLayout"), "engines-light");
                await Navigate(dialog, "appearance");
            }
        }

        // Verify Sand Light-mode ListViewItem selection color AND QuickLookDialog video open/close stability
        InvokeButton(WithId<ToggleButton>(dialog, "Settings.Palette.sand"));
        await Until(() => Preferences.Current.Palette == "sand" && root.ActualTheme == ElementTheme.Light, "Sand palette did not apply.");
        dialog.Hide();
        await opened;
        await Settle(root);

        var stagingPanel = (FrameworkElement)workspace.FindName("StagingPanel");
        if (stagingPanel.Visibility != Visibility.Visible)
        {
            var navigation = Preferences.Current.TopNavigation ? "TopWorkspaceNav" : "LeftWorkspaceNav";
            InvokeButton((ToggleButton)workspace.FindName(navigation));
            await Until(() => stagingPanel.Visibility == Visibility.Visible, "Staging did not open for the preview checks.");
        }

        var fixtureVideo = Path.Combine(profile, "独立站视频测试.mp4");
        var fixtureText = Path.Combine(profile, "readme.txt");
        File.WriteAllText(fixtureText, "QuickLook preview fixture text.");
        if (!File.Exists(fixtureVideo))
        {
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "Engines", "ffmpeg", "bin", "ffmpeg.exe");
            using var gen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                ffmpeg, $"-y -f lavfi -i color=c=steelblue:s=320x180:d=1 -pix_fmt yuv420p \"{fixtureVideo}\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
            await gen.WaitForExitAsync();
        }
        var searchFiles = new[]
        {
            new FileAssistant.Core.Features.Search.SearchFile(fixtureVideo, false, new FileInfo(fixtureVideo).Length, DateTimeOffset.Now),
            new FileAssistant.Core.Features.Search.SearchFile(fixtureText, false, new FileInfo(fixtureText).Length, DateTimeOffset.Now)
        };
        Workspace.AddSearchFiles(searchFiles);
        await Settle(root);
        var fileList = (ListView)stagingPanel.FindName("FileList");
        fileList.SelectedIndex = 0;
        await Task.Delay(200);
        await Settle(fileList);
        await SavePng(stagingPanel, "selected-row-sand");

        var qlType = typeof(FileAssistant.App.App).Assembly.GetType("FileAssistant.App.Features.Preview.QuickLookDialog", throwOnError: true)!;
        var showMethod = qlType.GetMethod("ShowAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        for (var pass = 0; pass < 2; pass++)
        {
            var qlTask = (Task)showMethod.Invoke(null, [stagingPanel, searchFiles, 0, false, null])!;
            await Until(() => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(p => p.Child is ContentDialog), "QuickLook dialog did not open.");
            await Task.Delay(550);
            var qlDialog = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .Select(p => p.Child).OfType<ContentDialog>().First();
            if (pass == 0) await SavePng((FrameworkElement)qlDialog.Content, "quicklook-video");
            qlDialog.Hide();
            await qlTask;
            await Task.Delay(350);
        }

        opened = workspace.OpenSettingsAsync(initialPage: "appearance");
        dialog = await Opened(workspace, opened);
        InvokeButton(WithId<ToggleButton>(dialog, "Settings.Navigation.Top"));
        await Until(() => Preferences.Current.TopNavigation, "Top navigation preference was not applied.");
        evidence.Add(new { check = "instant palettes", palettes = new[] { "ocean", "forest", "frost" }, finalTheme = root.ActualTheme.ToString(), navigation = "top" });
        var probes = new[]
        {
            (InterfaceLanguage.English, "English", "General", "Language & startup", "Staging area", "Task queue", "All tasks", "Search this list", 1180),
            (InterfaceLanguage.TraditionalChinese, "繁體中文", "一般", "語言與啟動", "檔案暫存區", "任務隊列", "全部任務", "搜尋當前列表", 640),
            (InterfaceLanguage.SimplifiedChinese, "简体中文", "常规", "语言与启动", "文件暂存区", "任务队列", "全部任务", "搜索当前列表", 1180)
        };
        foreach (var (language, option, title, landmark, staging, tasks, allTasks, filter, dipWidth) in probes)
        {
            await ResizeWindow(root, dipWidth, 800);
            await Navigate(dialog, "general");
            var dropdown = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<SelectionDropDown>()
                .Single(d => d.ItemsSource is IEnumerable<SelectionOption> options && options.Any(o => o.Text == "English"));
            await PickOption(dropdown, option);
            await Until(() => Preferences.Current.Language == language && Part<TextBlock>(dialog, "PageTitle").Text == title, "Native title did not switch to " + language);
            await Settle(dialog);
            InspectPage(dialog, "general-" + language, title, landmark, dipWidth < 660);
            await SavePng(Part<Grid>(dialog, "DialogLayout"), "general-" + language);
            dialog.Hide(); await opened;
            await Settle(root);
            var stagingNav = (ToggleButton)workspace.FindName("TopWorkspaceNav");
            var namingNav = (ToggleButton)workspace.FindName("TopNamingNav");
            string stagingName = AutomationProperties.GetName(stagingNav);
            string tasksName = AutomationProperties.GetName((Button)workspace.FindName("TopTasksButton"));
            var naming = Elements((FrameworkElement)namingNav.Content).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault();
            var listFilter = Elements(workspace).OfType<FilterSearchBox>().FirstOrDefault();
            var placeholder = (listFilter?.FindName("QueryInput") as AutoSuggestBox)?.PlaceholderText;
            var stagingView = (FrameworkElement)workspace.FindName("StagingPanel");
            var pane = (ResizablePane)stagingView.FindName("DetailsHost");
            pane.IsPaneOpen = true;
            await Settle(stagingView);
            var inspectorTitle = ((TextBlock)stagingView.FindName("InspectorTitle")).Text;
            // Staging intentionally removes its operation tab. Exercise the shared inspector's supported two-tab state as well.
            var inspector = new FileInspector { ViewModel = Workspace };
            inspector.ShowParameters(true);
            var inspectorHost = new ContentDialog { XamlRoot = workspace.XamlRoot, Content = inspector, CloseButtonText = "OK" };
            var showingInspector = inspectorHost.ShowAsync();
            await Until(() => inspector.IsLoaded, "Shared inspector did not load.");
            var parameters = ((Pivot)inspector.FindName("InspectorTabs")).Items.Cast<PivotItem>().Last().Header as string;
            inspectorHost.Hide(); await showingInspector;
            var flyout = (Flyout)workspace.FindName("TaskQueueFlyout");
            flyout.ShowAt(workspace);
            await Until(() => flyout.IsOpen && ((ScrollViewer)flyout.Content).IsLoaded, "Task flyout did not open.");
            await Settle((FrameworkElement)flyout.Content);
            var panel = (WorkspaceTaskPanel)((ScrollViewer)flyout.Content).Content;
            var all = ((ToggleButton)panel.FindName("AllTasks")).Content?.ToString();
            flyout.Hide();
            var addLabels = Elements(workspace).OfType<AddFilesButton>().FirstOrDefault() is { } add
                ? Elements(add).OfType<ButtonBase>().Select(ControlName).Where(x => x.Length > 0).Distinct().ToArray() : [];
            var sample = new { language = language.ToString(), dipWidth, stagingName, naming, tasksName, placeholder, all, inspectorTitle, parameters, addLabels };
            evidence.Add(sample);
            Log(System.Text.Json.JsonSerializer.Serialize(sample, JsonOptions));
            Require(stagingName == staging && tasksName == tasks && all == allTasks && placeholder == filter,
                "Native workspace localization differed for " + language + ": " + string.Join(" | ", stagingName, tasksName, all, placeholder));
            string expectedTab = language switch { InterfaceLanguage.English => "Operation settings", InterfaceLanguage.TraditionalChinese => "操作參數", _ => "操作参数" };
            Require(parameters == expectedTab, "Shared operation inspector tab was not translated: " + parameters);
            if (language == InterfaceLanguage.English) Require(naming == "Create & rename", "A workbench navigation title was not translated.");
            opened = workspace.OpenSettingsAsync(initialPage: "general"); dialog = await Opened(workspace, opened);
        }
        await Navigate(dialog, "appearance");
        if (Preferences.Current.Mode != AppearanceMode.Dark)
        {
            InvokeButton(WithName<ToggleButton>(dialog, "外观模式 · 深色"));
            await Until(() => Preferences.Current.Mode == AppearanceMode.Dark, "Color mode did not switch back to Dark.");
            await Settle(dialog);
        }
        InvokeButton(WithId<ToggleButton>(dialog, "Settings.Palette.charcoal"));
        await Until(() => Preferences.Current.Palette == "charcoal" && root.ActualTheme == ElementTheme.Dark, "Could not restore the dark fixture.");
        InvokeButton(WithId<ToggleButton>(dialog, "Settings.Navigation.Left"));
        await Until(() => !Preferences.Current.TopNavigation, "Could not restore side navigation.");
        dialog.Hide(); await opened;
        Log("palette and language checks completed");
    }
}
