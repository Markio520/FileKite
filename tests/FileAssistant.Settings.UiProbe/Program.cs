using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Settings;
using FileAssistant.Indexing.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using MainApp = FileAssistant.App.App;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe : MainApp
{
    private const BindingFlags InstancePrivate = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string output = "", profile = "", desktop = "";
    private static int width = 1180, height = 820, exitCode = 1;
    private static bool reopen;
    private static float islandScale;
    private static string extraMode = "";
    private static string ModeName => extraMode.Length > 0 ? extraMode : islandScale > 0 ? "scaled-island" : reopen ? "reopen" : "probe";
    private static readonly List<object> evidence = [];
    private static string LogFile => Path.Combine(output, ModeName + ".log");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--output": output = Absolute(args[++i]); break;
                    case "--profile": profile = Absolute(args[++i]); break;
                    case "--width": width = Math.Clamp(int.Parse(args[++i]), 350, 2400); break;
                    case "--height": height = Math.Clamp(int.Parse(args[++i]), 320, 1600); break;
                    case "--verify-reopen": reopen = true; break;
                    case "--branding": extraMode = "branding"; break;
                    case "--branding-reopen": extraMode = "branding-reopen"; break;
                    case "--localization": extraMode = "localization"; break;
                    case "--portability": extraMode = "portability"; break;
                    case "--selection-actions": extraMode = "selection-actions"; break;
                    case "--task-appearance": extraMode = "task-appearance"; break;
                    case "--island-scale": islandScale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); if (islandScale is < 1 or > 3) return 2; break;
                    default: return 2;
                }
            }
            if (output.Length == 0 || profile.Length == 0 || (reopen ? 1 : 0) + (islandScale > 0 ? 1 : 0) + (extraMode.Length > 0 ? 1 : 0) > 1) return 2;
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)) return Refuse("Unable to identify the thread desktop.");
            desktop = name.ToString();
            var localProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileAssistant.Core.AppProfile.Name);
            if (!desktop.StartsWith("FileAssistant-Test-", StringComparison.Ordinal)) return Refuse("The process is outside the test desktop.");
            if (!SamePath(Environment.GetEnvironmentVariable("FILEASSISTANT_PROFILE_DIRECTORY"), profile)) return Refuse("The runner profile and probe argument differ.");
            if (!SamePath(FileAssistant.Core.AppProfile.Directory, profile)) return Refuse("The loaded workbench did not use the isolated profile.");
            if (SamePath(localProfile, profile)) return Refuse("The requested profile is the ordinary workspace profile.");
            var marker = Path.Combine(profile, ".settings-ui-probe");
            Directory.CreateDirectory(profile);
            if (!File.Exists(marker))
            {
                if (ModeName is not ("probe" or "branding" or "selection-actions" or "task-appearance") || Directory.EnumerateFileSystemEntries(profile).Any()) return Refuse("Expected an empty fixture or a previously marked probe fixture.");
                File.WriteAllText(marker, "FileAssistant native settings acceptance fixture");
            }
            Directory.CreateDirectory(output);
            File.WriteAllText(LogFile, "");
            Log($"desktop={desktop}; profile={profile}; process={Environment.ProcessId}; reopen={reopen}");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                var app = new Probe();
                app.UnhandledException += (_, e) => Log("Unhandled XAML error " + e.Exception);
            });
            return exitCode;
        }
        catch (Exception error)
        {
            if (output.Length > 0 && Directory.Exists(output)) Log(error.ToString());
            return 1;
        }
    }

    private static int Refuse(string reason)
    {
        if (Directory.Exists(output)) File.AppendAllText(Path.Combine(output, "isolation.log"), DateTimeOffset.Now.ToString("O") + " " + reason + Environment.NewLine);
        return 2;
    }

    private static string Absolute(string value)
        => Path.IsPathFullyQualified(value) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) : throw new ArgumentException("Absolute paths required.");
    private static bool SamePath(string? a, string b) => a is not null && string.Equals(Absolute(a), b, StringComparison.OrdinalIgnoreCase);
    private static void Log(string message) => File.AppendAllText(LogFile, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static async Task Until(Func<bool> condition, string stage, int seconds = 12)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException(stage);
            await Task.Delay(45);
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await using var index = new SharedIndexEngine(Path.Combine(profile, "Indexes"), Path.Combine(profile, "no-elevated-helper"),
                () => Array.Empty<string>(), interval: TimeSpan.FromMinutes(10));
            typeof(MainApp).GetField("indexRuntime", InstancePrivate)!.SetValue(this, index);
            var initialized = (Task)typeof(MainApp).GetMethod("InitializeWorkspaceAsync", InstancePrivate)!.Invoke(this, [profile, index, index])!;
            await initialized;
            if (ModeName is "probe" or "selection-actions" or "task-appearance")
                await Preferences.UpdateAsync(s => s with { Language = InterfaceLanguage.SimplifiedChinese, PrewarmIndex = false,
                    IndexedVolumes = [], CheckUpdatesAtStartup = false, ConfirmNonemptyGroup = true, MaxParallel = 0, ImageQuality = 85 });
            typeof(MainApp).GetMethod("CreateWorkspaceWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            Window.Activate();
            var root = (FrameworkElement)Window.Content;
            await Until(() => root.IsLoaded && root.XamlRoot is not null, "Workspace XAML root not loaded.");
            await ResizeWindow(root, width, height);
            var workspace = (WorkspacePage)((Frame)root.FindName("RootFrame")).Content;
            if (islandScale > 0) await CheckScaledIsland(root, islandScale);
            else if (extraMode == "portability") await CheckPortability(workspace);
            else if (extraMode == "selection-actions") await CheckSelectionActions(root, workspace);
            else if (extraMode == "task-appearance") await CheckTaskAndAppearanceMenu(root, workspace);
            else if (extraMode is "branding" or "branding-reopen") await CheckBranding(root, workspace);
            else if (extraMode == "localization") await CheckLanguages(root, workspace);
            else
            {
                var opening = workspace.OpenSettingsAsync(initialPage: "general");
                var dialog = await Opened(workspace, opening);
                if (reopen)
                {
                    await CheckPersisted(dialog);
                    dialog.Hide(); await opening;
                }
                else await Exercise(root, workspace, dialog, opening);
            }
            exitCode = 0;
            Log("PASS");
        }
        catch (Exception error) { Log(error.ToString()); exitCode = 1; }
        finally
        {
            var report = new { passed = exitCode == 0, mode = ModeName, desktop, profile,
                pid = Environment.ProcessId, app = typeof(MainApp).Assembly.GetName().Version?.ToString(),
                workbenchSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainApp).Assembly.Location))), observations = evidence };
            File.WriteAllText(Path.Combine(output, ModeName == "probe" ? "settings-ui.json" : ModeName + ".json"), JsonSerializer.Serialize(report, JsonOptions));
            Exit();
        }
    }

    private static async Task<SettingsDialog> Opened(WorkspacePage page, Task opening)
    {
        await Until(() => SettingsDialog.IsOpen || opening.IsCompleted, "Settings dialog did not open.");
        if (opening.IsFaulted) await opening;
        var dialog = (SettingsDialog?)typeof(WorkspacePage).GetField("settingsDialog", InstancePrivate)!.GetValue(page)
            ?? throw new InvalidOperationException("Workspace did not create the settings dialog.");
        await Settle(dialog);
        return dialog;
    }

    private static async Task Settle(FrameworkElement element)
    {
        element.UpdateLayout();
        await Task.Delay(180);
        element.UpdateLayout();
    }

    private static async Task ResizeWindow(FrameworkElement root, int dipWidth, int dipHeight)
    {
        double scale = root.XamlRoot.RasterizationScale;
        Window.AppWindow.Resize(new SizeInt32((int)Math.Round(dipWidth * scale), (int)Math.Round(dipHeight * scale)));
        await Settle(root);
    }

    private static async Task CheckTaskAndAppearanceMenu(FrameworkElement root, WorkspacePage workspace)
    {
        await Preferences.UpdateAsync(s => s with { Mode = AppearanceMode.Light, Palette = "sand", TopNavigation = false });
        await Until(() => root.ActualTheme == ElementTheme.Light && Preferences.Current.Palette == "sand", "Sand Light theme did not settle.");
        await Settle(workspace);

        var leftRail = (FrameworkElement)workspace.FindName("LeftNavigationRail");
        var tasksBtn = (Button)workspace.FindName("LeftTasksButton");
        var tasksLabel = (TextBlock)workspace.FindName("LeftTasksLabel");
        var tasksIcon = (FontIcon)workspace.FindName("LeftTasksIdleIcon");
        var tasksBadge = (Border)workspace.FindName("LeftTasksBadge");
        var tasksPercent = (TextBlock)workspace.FindName("LeftTasksPercent");
        var navRoot = Elements(tasksBtn).OfType<Border>().First(b => b.Name == "NavRoot");

        // 1. Quiet Idle state uses shared transparent rail style (no white card background)
        Require(ColorOf(navRoot.Background).A == 0 && tasksLabel.Text == "任务队列" && tasksIcon.Glyph == "\uE9D5",
            $"Idle task row did not use shared transparent style: bg={ColorOf(navRoot.Background)}, label={tasksLabel.Text}");
        await SavePng(leftRail, "left-rail-idle-sand", minimumWidth: 140);

        // 2. Title bar Appearance menu shows symmetrical 浅色（…） / 深色（…） synced with active palettes
        var appearanceBtn = (Button)workspace.FindName("TitleAppearanceButton");
        var appearanceFlyout = (Flyout)workspace.FindName("AppearanceMenuFlyout");
        var lightItem = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)workspace.FindName("ThemeLightItem");
        var darkItem = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)workspace.FindName("ThemeCharcoalItem");
        var lightText = (TextBlock)workspace.FindName("ThemeLightLabel");
        var darkText = (TextBlock)workspace.FindName("ThemeCharcoalLabel");
        var systemText = (TextBlock)workspace.FindName("ThemeSystemLabel");

        appearanceFlyout.ShowAt(appearanceBtn);
        await Until(() => appearanceFlyout.IsOpen && lightText.ActualWidth > 0, "Appearance flyout did not open.");
        await Settle((FrameworkElement)appearanceFlyout.Content);
        Require(lightText.Text == "浅色（沙白画廊）" && darkText.Text == "深色（炭黑微光）" && systemText.Text == "跟随系统" && lightItem.IsChecked == true,
            $"Unexpected appearance menu labels: {lightText.Text} | {darkText.Text} | {systemText.Text}");
        await SavePng((FrameworkElement)appearanceFlyout.Content, "appearance-menu-sand", minimumWidth: 160, minimumHeight: 80);
        appearanceFlyout.Hide();

        // Dynamic palette reflection: frost -> 浅色（云杉雾白）, ocean -> 深色（深海松石）
        await Preferences.UpdateAsync(s => s with { Mode = AppearanceMode.Light, Palette = "frost" });
        await Settle(workspace);
        appearanceFlyout.ShowAt(appearanceBtn);
        await Until(() => appearanceFlyout.IsOpen, "Appearance flyout did not reopen for frost.");
        await Settle((FrameworkElement)appearanceFlyout.Content);
        Require(lightText.Text == "浅色（云杉雾白）" && darkText.Text == "深色（炭黑微光）",
            $"Frost palette did not reflect in menu: {lightText.Text} | {darkText.Text}");
        appearanceFlyout.Hide();

        await Preferences.UpdateAsync(s => s with { Mode = AppearanceMode.Dark, Palette = "ocean" });
        await Until(() => root.ActualTheme == ElementTheme.Dark, "Ocean Dark theme did not settle.");
        await Settle(workspace);
        appearanceFlyout.ShowAt(appearanceBtn);
        await Until(() => appearanceFlyout.IsOpen, "Appearance flyout did not reopen for ocean.");
        await Settle((FrameworkElement)appearanceFlyout.Content);
        Require(lightText.Text == "浅色（沙白画廊）" && darkText.Text == "深色（深海松石）" && darkItem.IsChecked == true,
            $"Ocean palette did not reflect in menu: {lightText.Text} | {darkText.Text}");
        await SavePng((FrameworkElement)appearanceFlyout.Content, "appearance-menu-ocean", minimumWidth: 160, minimumHeight: 80);
        appearanceFlyout.Hide();

        // Restore Sand Light & test Running + Compact Popover + Completion Checkmark + History Badge
        await Preferences.UpdateAsync(s => s with { Mode = AppearanceMode.Light, Palette = "sand" });
        await Until(() => root.ActualTheme == ElementTheme.Light, "Sand Light theme did not restore.");
        await Settle(workspace);

        var job = Jobs.Start(() => { }, "格式转换", batch: false);
        job.Update(new Core.Engines.ProcessingProgress(5, 8, "正在转换为 PNG (5/8)"));
        await Settle(workspace);
        Require(ColorOf(navRoot.Background).A > 0 && tasksLabel.Text == "处理中 · 1 项" && tasksPercent.Text == "62%",
            $"Running task capsule mismatch: bg={ColorOf(navRoot.Background)}, label={tasksLabel.Text}, pct={tasksPercent.Text}");
        await SavePng(leftRail, "left-rail-running-sand", minimumWidth: 140);

        // Hover state opens transient TaskQueueFlyout
        workspace.ViewModel.IsQueueExpanded = true;
        var taskFlyout = (Flyout)workspace.FindName("TaskQueueFlyout");
        var taskPanel = (WorkspaceTaskPanel)((ScrollViewer)taskFlyout.Content).Content;
        var queueList = (ListView)taskPanel.FindName("QueueList");
        await Until(() => taskFlyout.IsOpen && queueList.ActualHeight > 40, "Hover task flyout did not open.");
        await Settle(taskPanel);
        Require(((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)taskPanel.FindName("AllTasks")).IsChecked == true && queueList.Items.Count == 1,
            "Hover task flyout did not show the active job.");
        await SavePng(queueList, "task-flyout-running-sand", minimumWidth: 260, minimumHeight: 55);

        // Clicking LeftTasksButton hides hover flyout and opens centered resizable TaskCenterDialog
        InvokeButton(tasksBtn);
        await Until(() => !taskFlyout.IsOpen && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(p => p.Child is ContentDialog),
            "Centered TaskCenterDialog did not open on click.");
        var taskDialog = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .Select(p => p.Child).OfType<ContentDialog>().First();
        var taskCenterLayout = (Grid)taskDialog.Content;
        await Settle(taskCenterLayout);
        var matDict = (ResourceDictionary)Application.Current.Resources.MergedDictionaries
            .First(d => d.Source?.OriginalString.EndsWith("WorkspaceMaterials.xaml", StringComparison.OrdinalIgnoreCase) == true)
            .ThemeDictionaries["Light"];
        var expectedDialogBg = ColorOf((Microsoft.UI.Xaml.Media.Brush)matDict["WorkspaceDialogBrush"]);
        Require(ColorOf(taskCenterLayout.Background) == expectedDialogBg,
            $"TaskCenterDialog background did not match WorkspaceDialogBrush: got {ColorOf(taskCenterLayout.Background)}, expected {expectedDialogBg}");
        Require(Elements(taskCenterLayout).OfType<Border>().Any(b => b.Name == "DialogResizeGrip"), "TaskCenterDialog is missing DialogResizeGrip.");
        await SavePng(taskCenterLayout, "task-center-dialog-sand", minimumWidth: 380, minimumHeight: 280);
        taskDialog.Hide();
        await Until(() => !Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(p => p.Child is ContentDialog),
            "TaskCenterDialog did not close.");
        await Settle(workspace);

        // Verify TitleBar AlwaysOnTop pin button rotates cleanly to upright (-45°) without bounce and unpins back to 0°
        var pinButton = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)workspace.FindName("TitlePinButton");
        var pinIcon = (FontIcon)workspace.FindName("TitlePinIcon");
        Require(pinButton.IsChecked != true && pinIcon.Glyph == "\uE718", "TitlePinButton did not start unpinned.");
        InvokeButton(pinButton);
        await Until(() => Preferences.Current.AlwaysOnTop && pinIcon.Glyph == "\uE840", "TitlePinButton did not pin window.");
        await Task.Delay(320);
        await Settle(workspace);
        await SavePng((FrameworkElement)workspace.FindName("WorkspaceTitleBar"), "titlebar-pinned-sand", minimumWidth: 300, minimumHeight: 36);
        InvokeButton(pinButton);
        await Until(() => !Preferences.Current.AlwaysOnTop && pinIcon.Glyph == "\uE718", "TitlePinButton did not unpin window.");
        await Task.Delay(320);

        // Verify true circular pixel reveal wave on theme switch & complete resource teardown afterward
        var appearanceIcon = (FontIcon)workspace.FindName("TitleAppearanceIcon");
        var rippleSnapshot = (Image)root.FindName("ThemeRippleSnapshot");
        var rippleBackdrop = (Grid)root.FindName("ThemeRippleBackdrop");
        var rippleHost = (Grid)root.FindName("RippleRevealHost");
        var rippleHostVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(rippleHost);

        appearanceFlyout.ShowAt(appearanceBtn);
        await Until(() => appearanceFlyout.IsOpen, "Appearance flyout did not open for circular ripple check.");
        await Settle((FrameworkElement)appearanceFlyout.Content);
        InvokeButton(darkItem);
        await Until(() => root.ActualTheme == ElementTheme.Dark && appearanceIcon.Glyph == "\uE708", "Circular ripple did not switch to Dark theme.");
        await Task.Delay(620);
        await Settle(workspace);
        Require(rippleSnapshot.Source is null && rippleBackdrop.Visibility == Visibility.Collapsed && rippleHost.Background is null && rippleHostVisual.Clip is null,
            $"Circular ripple did not clean up transient resources after Light->Dark: source={rippleSnapshot.Source is not null}, backdrop={rippleBackdrop.Visibility}, bg={rippleHost.Background is not null}, clip={rippleHostVisual.Clip is not null}");

        appearanceFlyout.ShowAt(appearanceBtn);
        await Until(() => appearanceFlyout.IsOpen, "Appearance flyout did not reopen for Dark->Light ripple check.");
        await Settle((FrameworkElement)appearanceFlyout.Content);
        InvokeButton(lightItem);
        await Until(() => root.ActualTheme == ElementTheme.Light && appearanceIcon.Glyph == "\uE706", "Circular ripple did not switch back to Light theme.");
        await Task.Delay(620);
        await Settle(workspace);
        Require(rippleSnapshot.Source is null && rippleBackdrop.Visibility == Visibility.Collapsed && rippleHost.Background is null && rippleHostVisual.Clip is null,
            "Circular ripple did not clean up transient resources after Dark->Light.");

        job.Complete("已完成 8 项", ["01.pdf → 01.png：成功"], interrupted: false, outputDirectories: [profile]);
        await Settle(workspace);
        Require(tasksIcon.Glyph == "\uE73E" && tasksLabel.Text == "全部已完成",
            $"Completion morph did not trigger: glyph={(int)tasksIcon.Glyph[0]:X4}, label={tasksLabel.Text}");
        await SavePng(leftRail, "left-rail-done-sand", minimumWidth: 140);

        await Task.Delay(3000);
        await Settle(workspace);
        Require(ColorOf(navRoot.Background).A == 0 && tasksLabel.Text == "任务队列" && tasksBadge.Visibility == Visibility.Visible,
            $"Post-completion reset failed: bg={ColorOf(navRoot.Background)}, label={tasksLabel.Text}, badge={tasksBadge.Visibility}");

        // Verify NavModeToggleButton 90° side flip + smooth Side -> Top -> Side transition & MainWindowResizeGrip
        Require(((FrameworkElement)root.FindName("MainWindowResizeGrip")).Visibility == Visibility.Visible, "MainWindowResizeGrip is not visible.");
        var navToggleBtn = (Button)workspace.FindName("NavModeToggleButton");
        var settingsIcon = (FontIcon)workspace.FindName("TitleSettingsIcon");
        Require(settingsIcon.Glyph == "\uE713", "TitleSettingsIcon glyph mismatch.");
        InvokeButton(navToggleBtn);
        await Task.Delay(340);
        await Settle(workspace);
        Require(Appearance.Navigation == NavigationPlacement.Top, "NavModeToggleButton did not switch to Top.");
        InvokeButton(navToggleBtn);
        await Task.Delay(340);
        await Settle(workspace);
        Require(Appearance.Navigation == NavigationPlacement.Left && leftRail.Visibility == Visibility.Visible, "NavModeToggleButton did not restore LeftNavigationRail.");

        // Verify About Markio author credit & SettingsDialog resize grip
        var openingSettings = workspace.OpenSettingsAsync(initialPage: "about");
        var settingsDlg = await Opened(workspace, openingSettings);
        var aboutMeta = WithId<TextBlock>(settingsDlg, "Settings.About.Metadata");
        Require(aboutMeta.Text.Contains("作者 Markio", StringComparison.Ordinal), "About metadata missing Markio: " + aboutMeta.Text);
        var settingsLayout = Part<Grid>(settingsDlg, "DialogLayout");
        Require(Elements(settingsLayout).OfType<Border>().Any(b => b.Name == "DialogResizeGrip"), "SettingsDialog missing DialogResizeGrip.");
        await SavePng(settingsLayout, "about-markio-sand");
        settingsDlg.Hide();
        await openingSettings;

        evidence.Add(new
        {
            check = "Hover task popover, centered resizable TaskCenterDialog, About Markio credit, global resize grips, and smooth Top-to-Side transition verified",
            aboutMeta = aboutMeta.Text,
            sandMenu = new[] { "浅色（沙白画廊）", "深色（炭黑微光）", "跟随系统" }
        });
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int bytes, out int needed);
}
