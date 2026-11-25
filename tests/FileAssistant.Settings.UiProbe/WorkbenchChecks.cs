using System.Reflection;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static readonly (string Id, string Title, string Landmark)[] Pages =
    [
        ("general", "常规", "语言与启动"), ("appearance", "外观", "色彩与主题"),
        ("shortcuts", "快捷键", "全局唤起热键"), ("batch", "批处理", "性能并发与安全防护"),
        ("search", "搜索与索引", "NTFS 全盘索引"), ("engines", "引擎管理", "本机离线引擎"),
        ("storage", "存储与数据", "应用本地数据占用"), ("about", "纸鸢 · FileKite", "隐私承诺与开源许可声明")
    ];

    private static async Task Exercise(FrameworkElement root, WorkspacePage workspace, SettingsDialog dialog, Task opening)
    {
        Log("opened settings");
        foreach (var page in Pages)
        {
            await Navigate(dialog, page.Id);
            InspectPage(dialog, page.Id, page.Title, page.Landmark, false);
            await SavePng(Part<Grid>(dialog, "DialogLayout"), page.Id);
            if (page.Id == "shortcuts") await InspectKeys(dialog);
            if (page.Id == "search")
            {
                var exclusions = WithName<Expander>(dialog, "搜索默认排除目录");
                Require(!exclusions.IsExpanded, "Search exclusions opened on entry.");
                var input = WithId<TextBox>(dialog, "Settings.Exclude.Input");
                var rule = "FileAssistant-Probe-" + Guid.NewGuid().ToString("N");
                input.Text = rule;
                var add = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<Button>().Single(b => b.Content as string == "添加");
                InvokeButton(add);
                await Until(() => Preferences.Current.ExcludedDirectories.Contains(rule), "Exclusion was not added.");
                await Settle(dialog);
                Require(!exclusions.IsExpanded, "Adding an exclusion opened the list.");
                await Expand(exclusions);
                InvokeButton(WithName<Button>(dialog, "移除此排除项 · " + rule));
                await Until(() => !Preferences.Current.ExcludedDirectories.Contains(rule), "Exclusion was not removed.");
                Require(exclusions.IsExpanded, "Removing an exclusion closed the manually opened list.");
                evidence.Add(new { interaction = "add and remove a search exclusion", rule });
            }
            if (page.Id == "engines")
            {
                var office = WithId<Expander>(dialog, "Settings.Engine.libreoffice");
                var recheck = Elements(dialog).OfType<Button>().Single(b => Equals(b.Content, "重新检查"));
                await Until(() => recheck.IsEnabled, "Initial engine check did not finish.");
                Require(!office.IsExpanded, "Office details opened on entry.");
                InvokeButton(recheck);
                await Until(() => recheck.IsEnabled, "Engine recheck did not finish.");
                Require(!office.IsExpanded, "Engine recheck opened Office details.");
                await Expand(office);
                _ = WithName<TextBox>(office, "可执行文件路径 · soffice.exe");
                var ffmpeg = WithId<Expander>(dialog, "Settings.Engine.ffmpeg");
                ffmpeg.IsExpanded = true;
                await Settle(dialog);
                var details = Elements(ffmpeg).OfType<TextBlock>().Select(t => t.Text).Where(t => t.Contains("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                Require(details.Length > 0, "FFmpeg engine expansion did not reveal installation details.");
                evidence.Add(new { interaction = "expand FFmpeg", details });
                ffmpeg.IsExpanded = false;
            }
            if (page.Id == "about")
            {
                var licenses = WithName<Expander>(dialog, "第三方开源组件与许可证清单");
                Require(!licenses.IsExpanded, "Licenses opened on entry.");
                await Expand(licenses);
                Require(Elements(licenses).OfType<Button>().Any(b => Equals(b.Content, "MIT ↗")), "MIT notice is missing after expansion.");
            }
        }
        var search = Part<TextBox>(dialog, "SettingsSearch");
        search.Text = "FFmpeg";
        await Settle(dialog);
        var hits = Headings(dialog);
        Require(Part<TextBlock>(dialog, "PageTitle").Text == "搜索结果" && hits.Any(x => x.StartsWith("批处理"))
            && hits.Any(x => x.StartsWith("引擎管理")), "Cross-category FFmpeg search missed batch or engine settings: " + string.Join(" / ", hits));
        evidence.Add(new { search = search.Text, categoriesAndSections = hits });
        search.Text = "no-such-option-45ED9B5C";
        await Settle(dialog);
        Require(Part<TextBlock>(dialog, "NoResults").Visibility == Visibility.Visible && Headings(dialog).Length == 0, "No-results placeholder was missing.");
        search.Text = "";
        await Navigate(dialog, "general");
        var confirm = WithName<ToggleSwitch>(dialog, "移出非空软件分组前询问确认");
        confirm.IsOn = false;
        await Until(() => !Preferences.Current.ConfirmNonemptyGroup, "General toggle was not saved.");
        await Navigate(dialog, "batch");
        var parallel = WithName<SelectionDropDown>(dialog, "最大并行处理任务数");
        parallel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await Settle(dialog);
        await PickOption(parallel, "标准（最多 4 并发）");
        await Until(() => Preferences.Current.MaxParallel == 4, "The parallel option was not saved.");
        var quality = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<Slider>().Single();
        Require(quality.Minimum == 50 && quality.Maximum == 100, "Wrong quality range.");
        quality.Value = 73;
        dialog.Hide();
        await opening;
        Require(Preferences.Current.ImageQuality == 73, "Closing during the slider debounce lost the selected quality.");
        var reopened = workspace.OpenSettingsAsync(initialPage: "general");
        dialog = await Opened(workspace, reopened);
        await Navigate(dialog, "engines");
        Require(!WithId<Expander>(dialog, "Settings.Engine.libreoffice").IsExpanded, "Office reopened expanded.");
        await Navigate(dialog, "search");
        Require(!WithName<Expander>(dialog, "搜索默认排除目录").IsExpanded, "Search exclusions reopened expanded.");
        await Navigate(dialog, "about");
        Require(!WithName<Expander>(dialog, "第三方开源组件与许可证清单").IsExpanded, "Licenses reopened expanded.");
        evidence.Add(new { check = "Office, exclusions and licenses start collapsed, expand on demand, and reset when settings reopen" });
        await CheckPersisted(dialog);
        await ResizeWindow(root, 580, 750);
        await Navigate(dialog, "appearance");
        InspectPage(dialog, "appearance-narrow", "外观", "主导航栏样式", true);
        await SavePng(Part<Grid>(dialog, "DialogLayout"), "appearance-narrow");
        var compact = Part<TextBox>(dialog, "CompactSearch");
        compact.Text = "FFmpeg";
        await Settle(dialog);
        Require(Headings(dialog).Any(x => x.StartsWith("引擎管理")), "Compact cross-category search did not find engine settings.");
        dialog.Hide();
        await reopened;
    }

    private static async Task Expand(Expander expander)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(expander);
        var provider = peer?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
            ?? throw new InvalidOperationException("No expand/collapse pattern for " + AutomationProperties.GetName(expander));
        provider.Expand();
        await Settle(expander);
        Require(expander.IsExpanded, "Manual expansion failed.");
    }

    private static async Task InspectKeys(SettingsDialog dialog)
    {
        var button = WithId<Button>(dialog, "Settings.Shortcut.ToggleWindow");
        var caps = Elements((FrameworkElement)button.Content).OfType<Border>().Where(b => b.Child is TextBlock).ToArray();
        var labels = caps.Select(b => ((TextBlock)b.Child).Text).ToArray();
        Require(labels.SequenceEqual(["Alt", "Space"]) && caps.All(cap => cap.ActualWidth >= 23 && cap.ActualHeight >= 22),
            "Global shortcut keycaps missing or too small: " + string.Join("+", labels));
        var duplicateCount = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<Button>()
            .Count(b => AutomationProperties.GetAutomationId(b).StartsWith("Settings.Shortcut.", StringComparison.Ordinal));
        Require(duplicateCount == 7, $"Expected all seven shortcut editors, found {duplicateCount}.");
        var boxes = caps.Select(cap => Bounds(cap, button)).ToArray();
        Require(boxes[1].X >= boxes[0].X + boxes[0].Width, "Shortcut keycaps overlap.");
        evidence.Add(new { check = "keycaps", labels, bounds = boxes, editors = duplicateCount });
        InvokeButton(button);
        await Until(() => SettingsDialog.IsRecordingShortcut, "Shortcut editor did not start recording.");
        Require(AutomationProperties.GetName(button).Contains("Esc"), "Shortcut editor has no cancel hint.");
        await Navigate(dialog, "batch");
        Require(!SettingsDialog.IsRecordingShortcut, "Shortcut recording remained active after navigating.");
    }

    private static async Task PickOption(SelectionDropDown dropdown, string desired)
    {
        var flyout = (Flyout)dropdown.Flyout;
        flyout.ShowAt(dropdown);
        await Until(() => flyout.IsOpen && flyout.Content is ListView { ActualWidth: > 0 }, "Native dropdown did not open.");
        var list = (ListView)flyout.Content;
        list.UpdateLayout();
        var options = list.Items.Cast<SelectionOption>().ToArray();
        var chosen = options.Single(option => option.Text == desired);
        var row = (ListViewItem)list.ContainerFromItem(chosen);
        Require(row is not null && row.ActualHeight > 0, "The popup option has no realized native row.");
        string activation;
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(row!);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
            await Task.Delay(100);
        }
        if (flyout.IsOpen)
        {
            // The same commit method handles both the native list's ItemClick and keyboard confirmation.
            typeof(SelectionDropDown).GetMethod("Commit", InstancePrivate)!.Invoke(dropdown, [chosen]);
            activation = "SelectionDropDown.Commit";
        }
        else activation = "ListViewItem.Invoke";
        await Until(() => !flyout.IsOpen, "Selected option did not dismiss the flyout.");
        evidence.Add(new { interaction = "native list selection", options = options.Select(o => o.Text), selected = chosen.Text, activation });
    }

    private static async Task CheckPersisted(SettingsDialog dialog)
    {
        await Navigate(dialog, "general");
        bool confirm = WithName<ToggleSwitch>(dialog, "移出非空软件分组前询问确认").IsOn;
        await Navigate(dialog, "batch");
        var parallel = WithName<SelectionDropDown>(dialog, "最大并行处理任务数");
        var quality = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<Slider>().Single().Value;
        Require(!confirm && parallel.SelectedValue == 2 && quality == 73 && Preferences.Current.MaxParallel == 4,
            $"Persisted controls mismatch: confirm={confirm}, parallel={parallel.Content}, quality={quality}.");
        evidence.Add(new { check = "persisted controls", reopenedProcess = reopen, confirm, parallel = parallel.Content?.ToString(), quality });
    }

    private static async Task Navigate(SettingsDialog dialog, string id)
    {
        var nav = WithId<ToggleButton>(Part<StackPanel>(dialog, "NavigationList"), "Settings.Nav." + id);
        InvokeButton(nav);
        await Settle(dialog);
        var title = id == "about" ? InterfaceText.Brand : AutomationProperties.GetName(nav);
        Require(nav.IsChecked == true && Part<TextBlock>(dialog, "PageTitle").Text == title, "Navigation failed for " + id);
    }

    private static void InvokeButton(ButtonBase button)
    {
        var onClick = typeof(ButtonBase).GetMethod("OnClick", InstancePrivate);
        if (onClick is not null) { onClick.Invoke(button, null); return; }
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }
        if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) { toggle.Toggle(); return; }
        throw new InvalidOperationException("The native button does not expose activation: " + AutomationProperties.GetName(button));
    }

    private static void InspectPage(SettingsDialog dialog, string id, string title, string landmark, bool compact)
    {
        var layout = Part<Grid>(dialog, "DialogLayout");
        var rail = Part<Border>(dialog, "Rail");
        var detail = Part<Grid>(dialog, "DetailPane");
        var close = Part<Button>(dialog, "CloseSettings");
        var scroll = Part<ScrollViewer>(dialog, "PageScroll");
        var host = Part<StackPanel>(dialog, "SectionsHost");
        var sections = Headings(dialog);
        var controls = Elements(host).OfType<Control>().Where(c => c is ButtonBase or ToggleSwitch or Slider or Expander)
            .Select(c => new { type = c.GetType().Name, name = ControlName(c), id = AutomationProperties.GetAutomationId(c), rect = Bounds(c, layout) })
            .Where(c => c.name.Length > 0 || c.id.Length > 0).ToArray();
        var railBox = Bounds(rail, layout);
        var panel = Bounds(detail, layout);
        var closeBox = Bounds(close, layout);
        var categories = Elements(Part<StackPanel>(dialog, "NavigationList")).OfType<ToggleButton>()
            .Select(n => new { name = AutomationProperties.GetName(n), rect = Bounds(n, layout) }).ToArray();
        var pageTitle = Part<TextBlock>(dialog, "PageTitle").Text;
        Log($"{id} {layout.ActualWidth}x{layout.ActualHeight} rail={rail.ActualWidth}, controls={controls.Length}");
        evidence.Add(new { page = id, pageTitle, compact, dip = new { layout.ActualWidth, layout.ActualHeight }, scale = layout.XamlRoot.RasterizationScale,
            rail = railBox, detail = panel, close = closeBox, categories, sections, controls,
            viewportHeight = scroll.ViewportHeight, extentHeight = scroll.ExtentHeight });
        Require(pageTitle == title && sections.Any(s => s.StartsWith(landmark, StringComparison.Ordinal)), "Missing page text: " + id + " / " + string.Join(", ", sections));
        Require(categories.Length == 8 && categories.All(n => n.rect.Width > 20 && n.rect.Height >= 28), "Wrong number or size of categories on " + id);
        Require(compact ? railBox.Width is >= 50 and <= 58 : railBox.Width >= 160, "Wrong sidebar width on " + id);
        Require(compact == (Part<TextBox>(dialog, "CompactSearch").Visibility == Visibility.Visible), "Incorrect compact search visibility.");
        Require(panel.X >= railBox.Width - 1 && Math.Abs(panel.X + panel.Width - layout.ActualWidth) < 2
            && closeBox.X >= panel.X && closeBox.X + closeBox.Width <= layout.ActualWidth + 1 && closeBox.Width >= 25
            && scroll.ViewportHeight > 140, "Dialog columns or close button overflow on " + id);
        var outside = controls.Where(c => c.rect.Width > 0 && (c.rect.X < panel.X - 2 || c.rect.X + c.rect.Width > layout.ActualWidth + 2)).ToArray();
        Require(outside.Length == 0, "Named controls exceed the horizontal viewport on " + id + ": " + string.Join(", ", outside.Select(x => x.name)));
    }

    private static string ControlName(Control control) => AutomationProperties.GetName(control) is { Length: > 0 } name
        ? name : (control as ContentControl)?.Content as string ?? "";
    private static string[] Headings(SettingsDialog d) => Elements(Part<StackPanel>(d, "SectionsHost")).OfType<TextBlock>()
        .Where(t => AutomationProperties.GetHeadingLevel(t) == AutomationHeadingLevel.Level3).Select(t => t.Text).ToArray();
    private static T Part<T>(SettingsDialog d, string name) where T : FrameworkElement => (T)d.FindName(name);
    private static T WithId<T>(FrameworkElement root, string id) where T : FrameworkElement
        => Elements(root).OfType<T>().Single(x => AutomationProperties.GetAutomationId(x) == id);
    private static T WithName<T>(FrameworkElement root, string name) where T : FrameworkElement
        => Elements(root).OfType<T>().Single(x => AutomationProperties.GetName(x) == name);
    private static IEnumerable<FrameworkElement> Elements(DependencyObject root)
    {
        if (root is UIElement { Visibility: Visibility.Collapsed }) yield break;
        if (root is FrameworkElement f) yield return f;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Elements(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private sealed record Box(double X, double Y, double Width, double Height);
    private static Box Bounds(FrameworkElement target, FrameworkElement relative)
    {
        var rect = target.TransformToVisual(relative).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        return new(Math.Round(rect.X, 2), Math.Round(rect.Y, 2), Math.Round(rect.Width, 2), Math.Round(rect.Height, 2));
    }
}
