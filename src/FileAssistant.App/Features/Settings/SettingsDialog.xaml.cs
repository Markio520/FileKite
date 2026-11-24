using System.Text;
using FileAssistant.App.Controls;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog : ContentDialog
{
    private sealed record Page(string Id, string Zh, string En, string Description, string EnglishDescription, string Icon);
    private sealed record Section(Page Page, string Title, string Keywords, StackPanel View, FrameworkElement Header, TextBlock Heading, bool ShowHeading);

    private static readonly Page[] Pages =
    [
        new("general", "常规", "General", "配置界面语言、启动和关闭行为，以及文件列表的操作习惯。",
            "Choose your language, startup and closing behavior, and file list actions.", "\uE7F4"),
        new("appearance", "外观", "Appearance", "自定义窗口色彩模式、精选主题方案、导航结构与视觉密度。",
            "Customize color mode, theme palettes, navigation layout, and visual density.", "\uE706"),
        new("shortcuts", "快捷键", "Shortcuts", "管理全局唤起热键、托盘拖入行为与工作区内部快捷键。",
            "Manage system-wide shortcuts, tray drop behavior, and keyboard shortcuts in the workspace.", "\uE765"),
        new("batch", "批处理", "Batch Rules", "设置格式转换、创建改名、复制整理与清理任务的默认参数与并发策略。",
            "Set defaults for conversion, naming, copying, and cleanup, including concurrency and safeguards.", "\uE9E9"),
        new("search", "搜索与索引", "Search & Index", "管理本机 NTFS 索引、监听盘符、排除目录与快速预览行为。",
            "Manage the local NTFS index, included volumes, excluded directories, and Quick Look.", "\uE721"),
        new("engines", "引擎管理", "Engines", "查看与维护本机离线图像、音视频、PDF、Office 及软件卸载引擎。",
            "Check and manage the local image, media, PDF, Office, and uninstall engines.", "\uE81E"),
        new("storage", "存储与数据", "Storage & Data", "查看缓存与索引的空间占用，清理临时数据，迁移配置或重置偏好。",
            "Review storage usage, clear temporary data, transfer settings, or restore defaults.", "\uEDA2"),
        new("about", "关于", "About", "应用版本、运行环境、更新检查、隐私与第三方组件许可。",
            "View the app version, runtime information, updates, privacy, and third-party licenses.", "\uE946")
    ];

    public static bool IsOpen { get; private set; }
    public static bool IsRecordingShortcut { get; private set; }

    private readonly FrameworkElement themeRoot;
    private readonly List<Section> sections = [];
    private readonly HashSet<string> builtPages = [];
    private readonly List<Action<AppSettings>> settingBindings = [];
    private readonly List<(Page Page, ToggleButton Button, TextBlock Label)> nav = [];
    private readonly DispatcherTimer badgeTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly DispatcherTimer indexTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private AppSettings shown = App.Preferences.Current;
    private Page activePage;
    private Page buildingPage = Pages[0];
    private StringBuilder? sectionKeywords;
    private bool compact;
    private bool applying;
    private bool changingSearch;
    private bool opened;
    private string query = "";
    private int saveSequence;
    private TaskCompletionSource<bool>? confirmation;
    private Control? confirmationReturnFocus;

    public SettingsDialog(FrameworkElement owner, bool selectEngines = false, string? initialPage = null)
    {
        InitializeComponent();
        XamlRoot = owner.XamlRoot;
        themeRoot = (FrameworkElement)App.Window.Content;
        RequestedTheme = themeRoot.ActualTheme;
        activePage = Pages.FirstOrDefault(p => p.Id == initialPage) ?? Pages[selectEngines ? 5 : 1];
        badgeTimer.Tick += (_, _) => { badgeTimer.Stop(); SaveBadge.Visibility = Visibility.Collapsed; };
        indexTimer.Tick += (_, _) => RefreshIndexStatus();
        DialogDismiss.Attach(this);
        Opened += Dialog_Opened;
        Closed += Dialog_Closed;
        Closing += Dialog_Closing;
        LocalizeShell();
        Resize();
        ShowPage();
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        opened = true;
        IsOpen = true;
        themeRoot.ActualThemeChanged += ThemeChanged;
        XamlRoot.Changed += RootChanged;
        App.Preferences.Changed += PreferencesChanged;
        App.EngineSettings.PropertyChanged += EngineProgressChanged;
        App.EngineSettings.AvailabilityChanged += EngineAvailabilityChanged;
        ApplyPreferences();
        UpdateVisibleServices();
        nav.First(item => item.Page == activePage).Button.Focus(FocusState.Programmatic);
        if (App.Preferences.LoadError is { Length: > 0 } failure)
            Notify(T("无法读取已保存的设置", "Could not read saved settings"), failure, InfoBarSeverity.Warning);
    }

    private async void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (confirmation is not null) { args.Cancel = true; FinishConfirmation(false); }
        else if (IsRecordingShortcut) { args.Cancel = true; EndRecording(); }
        else if (!pendingQualitySave.IsCompleted)
        {
            var deferral = args.GetDeferral();
            try { await pendingQualitySave; }
            finally { deferral.Complete(); }
        }
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        opened = false;
        IsOpen = false;
        EndRecording();
        FinishConfirmation(false);
        indexTimer.Stop();
        badgeTimer.Stop();
        App.Preferences.Changed -= PreferencesChanged;
        App.EngineSettings.PropertyChanged -= EngineProgressChanged;
        App.EngineSettings.AvailabilityChanged -= EngineAvailabilityChanged;
        themeRoot.ActualThemeChanged -= ThemeChanged;
        XamlRoot.Changed -= RootChanged;
    }

    private void ThemeChanged(FrameworkElement sender, object args)
    {
        if (RequestedTheme == themeRoot.ActualTheme) return;
        RequestedTheme = themeRoot.ActualTheme;
        if (!opened) return;
        if (confirmation is not null || IsRecordingShortcut) { Rebind(); return; }
        ClearPageReferences();
        sections.Clear();
        builtPages.Clear();
        settingBindings.Clear();
        SectionsHost.Children.Clear();
        LocalizeShell();
        Resize();
        ShowPage();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Resize()
    {
        var root = XamlRoot.Size;
        var width = Math.Max(0, Math.Min(920, root.Width - (root.Width < 660 ? 16 : 48)));
        DialogLayout.Width = width;
        DialogLayout.Height = Math.Max(0, Math.Min(604, root.Height - (root.Height < 480 ? 16 : 48)));
        compact = width < 660;
        SidebarColumn.Width = new(compact ? 54 : width < 820 ? 162 : 208);
        Rail.Padding = compact ? new(4, 14, 4, 12) : new(12, 21, 12, 14);
        var inset = compact ? 14 : width < 820 ? 20 : 36;
        HeadingArea.Margin = new(inset, compact ? 12 : 24, compact ? 12 : 16, compact ? 17 : 12);
        HeaderActions.Translation = new(0, compact ? 0 : -10, 0);
        BodyInset.Padding = new(inset, 1, width >= 820 ? inset + 4 : inset, 32);
        RailTitle.Visibility = SearchHost.Visibility = BrandText.Visibility = Visible(!compact);
        CompactFind.Visibility = Visible(compact);
        CompactSearch.Visibility = Visible(compact);
        foreach (var (_, button, label) in nav)
        {
            label.Visibility = Visible(!compact);
            button.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }
    }

    private void LocalizeShell()
    {
        RailTitle.Text = T("设置", "Settings");
        BrandText.Text = InterfaceText.Brand;
        AppIconAssets.SetImage(RailBrandIcon, App.Preferences.Current.AppIcon);
        var find = T("查找设置", "Find a setting");
        SettingsSearch.PlaceholderText = CompactSearch.PlaceholderText = find;
        AutomationProperties.SetName(this, RailTitle.Text);
        AutomationProperties.SetName(SettingsSearch, find);
        AutomationProperties.SetName(CompactSearch, find);
        NameControl(CompactFind, find);
        NameControl(CloseSettings, T("关闭设置（Esc）", "Close settings (Esc)"));
        AutomationProperties.SetName(NavigationList, T("设置分类", "Settings categories"));
        NoResults.Text = T("未找到匹配的设置项。可以试试「语言」「托盘」「主题」「FFmpeg」「缓存」或「MIT」。",
            "No matching settings. Try language, tray, theme, FFmpeg, cache, or MIT.");
        nav.Clear();
        NavigationList.Children.Clear();
        foreach (var page in Pages)
        {
            if (page.Id is "batch" or "storage")
                NavigationList.Children.Add(new Border { Height = 1, Margin = new(8, 6, 8, 6), Style = LocalStyle("SettingRule") });
            var title = T(page.Zh, page.En);
            var label = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Visibility = Visible(!compact) };
            var button = new ToggleButton
            {
                Style = LocalStyle("SettingNavigationItem"),
                Content = Horizontal(10, Icon(page.Icon, 15), label)
            };
            NameControl(button, title, "Settings.Nav." + page.Id);
            button.Click += (_, _) => Navigate(page.Id);
            NavigationList.Children.Add(button);
            nav.Add((page, button, label));
        }
    }

    internal void SelectCategory(string id)
    {
        if (!Pages.Any(page => page.Id == id)) return;
        FinishConfirmation(false);
        Navigate(id);
    }

    private void Navigate(string id)
    {
        EndRecording();
        activePage = Pages.First(p => p.Id == id);
        MessageBar.IsOpen = false;
        SetSearchText("");
        ShowPage();
        nav.First(item => item.Page == activePage).Button.Focus(FocusState.Programmatic);
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void SettingsSearch_Changed(object sender, TextChangedEventArgs args)
    {
        if (changingSearch || SectionsHost is null) return;
        EndRecording();
        SetSearchText(((TextBox)sender).Text);
        ShowPage();
        PageScroll.ChangeView(null, 0, null, true);
    }
    private void SetSearchText(string value)
    {
        changingSearch = true;
        try
        {
            query = value.Trim();
            if (SettingsSearch.Text != value) SettingsSearch.Text = value;
            if (CompactSearch.Text != value) CompactSearch.Text = value;
        }
        finally { changingSearch = false; }
    }
    private void CompactFind_Click(object sender, RoutedEventArgs e) => CompactSearch.Focus(FocusState.Keyboard);

    private void ShowPage()
    {
        var searching = query.Length > 0;
        foreach (var page in searching ? Pages : [activePage]) EnsurePage(page);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int count = 0;
        foreach (var section in sections)
        {
            var show = searching ? terms.All(term => section.Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)) : section.Page == activePage;
            section.View.Visibility = Visible(show);
            if (!show) continue;
            count++;
            section.Header.Visibility = Visible(section.ShowHeading || searching);
            section.Heading.Text = searching ? T(section.Page.Zh, section.Page.En) + "  ›  " + section.Title : section.Title;
        }
        foreach (var (page, button, _) in nav) button.IsChecked = !searching && page == activePage;
        var pageTitle = activePage.Id == "about" ? InterfaceText.Brand : T(activePage.Zh, activePage.En);
        PageTitle.Text = searching ? T("搜索结果", "Search results") : pageTitle;
        PageDescription.Text = searching
            ? string.Format(T("“{0}”匹配 {1} 个设置分组", "“{0}” matches {1} settings groups"), query, count)
            : T(activePage.Description, activePage.EnglishDescription);
        NoResults.Visibility = Visible(count == 0);
        UpdateVisibleServices();
    }

    private void EnsurePage(Page page)
    {
        if (!builtPages.Add(page.Id)) return;
        buildingPage = page;
        switch (page.Id)
        {
            case "general": BuildGeneral(); break;
            case "appearance": BuildAppearance(); break;
            case "shortcuts": BuildShortcuts(); break;
            case "batch": BuildBatch(); break;
            case "search": BuildSearch(); break;
            case "engines": BuildEngines(); break;
            case "storage": BuildStorage(); break;
            case "about": BuildAbout(); break;
        }
    }

    private void AddSection(string zh, string en, string aliases, Action<StackPanel> build, Func<FrameworkElement>? action = null, bool showHeading = true)
    {
        sectionKeywords = new();
        try
        {
            T(buildingPage.Zh, buildingPage.En);
            var title = T(zh, en);
            sectionKeywords.Append(' ').Append(aliases).Append(' ').Append(InterfaceText.Get(aliases, aliases, InterfaceLanguage.TraditionalChinese));
            var heading = Label(title, "SettingHeading");
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
            var command = action?.Invoke();
            if (command is Button headerButton) headerButton.Style = LocalStyle("SettingHeaderAction");
            var header = Pair(heading, command, 400);
            header.Margin = new(2, 0, 2, 0);
            var cards = new StackPanel { Spacing = 4 };
            build(cards);
            var root = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
            root.Children.Add(header);
            root.Children.Add(cards);
            var item = new Section(buildingPage, title, sectionKeywords.ToString(), root, header, heading, showHeading);
            int pageIndex = Array.IndexOf(Pages, buildingPage);
            int index = sections.FindIndex(s => Array.IndexOf(Pages, s.Page) > pageIndex);
            if (index < 0) index = sections.Count;
            sections.Insert(index, item);
            SectionsHost.Children.Insert(index, root);
        }
        finally { sectionKeywords = null; }
    }

    private string T(string zh, string en)
    {
        var result = InterfaceText.Get(zh, en);
        if (sectionKeywords is not null)
            sectionKeywords.Append(' ').Append(zh).Append(' ').Append(en).Append(' ')
                .Append(InterfaceText.Get(zh, en, InterfaceLanguage.TraditionalChinese));
        return result;
    }

    private void Bind(Action<AppSettings> apply)
    {
        settingBindings.Add(apply);
        var previous = applying;
        applying = true;
        try { apply(App.Preferences.Current); }
        finally { applying = previous; }
    }

    private void Rebind()
    {
        applying = true;
        try { foreach (var bind in settingBindings) bind(App.Preferences.Current); }
        finally { applying = false; }
    }

    private void PreferencesChanged(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess) ApplyPreferences();
        else DispatcherQueue.TryEnqueue(() => { if (opened) ApplyPreferences(); });
    }

    private void ApplyPreferences()
    {
        var previous = shown;
        shown = App.Preferences.Current;
        if (previous.Language != shown.Language)
        {
            EndRecording();
            ClearPageReferences();
            sections.Clear();
            builtPages.Clear();
            settingBindings.Clear();
            SectionsHost.Children.Clear();
            MessageBar.IsOpen = false;
            LocalizeShell();
            Resize();
            ShowPage();
        }
        else Rebind();
        if (previous.AppIcon != shown.AppIcon) AppIconAssets.SetImage(RailBrandIcon, shown.AppIcon);
        if (previous.FfmpegPath != shown.FfmpegPath || previous.OfficePath != shown.OfficePath)
        {
            enginesChecked = false;
            UpdateVisibleServices();
        }
        if (previous.Units != shown.Units) { UpdateStorageFigures(); UpdateEngineCards(); RefreshIndexStatus(); }
    }

    private bool SectionVisible(string page, string? zh = null) =>
        sections.Any(s => s.Page.Id == page && (zh is null || s.Title == zh) && s.View.Visibility == Visibility.Visible);

    private void UpdateVisibleServices()
    {
        if (!opened) return;
        if (indexTitle is not null && SectionVisible("search", T("NTFS 全盘索引", "NTFS volume index")))
        {
            if (!indexTimer.IsEnabled) { RefreshIndexStatus(); indexTimer.Start(); }
            LoadIndexDrives();
        }
        else indexTimer.Stop();
        if (SectionVisible("engines")) LoadEngines();
        if (SectionVisible("storage", T("应用本地数据占用", "Local app storage"))) LoadStorage();
    }

    private void ClearPageReferences()
    {
        indexTitle = indexDescription = null;
        volumeHost = null;
        engineRows.Clear();
        engineFeedback = null;
        storageUpdate = null;
    }

    private async Task Save(Func<AppSettings, AppSettings> update)
    {
        int sequence = ++saveSequence;
        badgeTimer.Stop();
        SaveBadge.Text = T("正在保存…", "Saving…");
        SaveBadge.Visibility = Visibility.Visible;
        try
        {
            await App.Preferences.UpdateAsync(update);
            if (!opened || sequence != saveSequence) return;
            MessageBar.IsOpen = false;
            ShowSaved();
        }
        catch (Exception error)
        {
            if (!opened) return;
            ApplyPreferences();
            if (sequence == saveSequence) SaveBadge.Visibility = Visibility.Collapsed;
            Notify(T("未能保存设置", "Could not save this setting"), error.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowSaved()
    {
        SaveBadge.Text = T("✓ 已保存", "✓ Saved");
        SaveBadge.Visibility = Visibility.Visible;
        badgeTimer.Stop();
        badgeTimer.Start();
    }
    private void Notify(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (!opened) return;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }

    private async Task<bool> ConfirmAsync(string title, string details, string accept)
    {
        if (!opened || confirmation is not null) return false;
        EndRecording();
        confirmationReturnFocus = FocusManager.GetFocusedElement(XamlRoot) as Control;
        var pending = confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmTitle.Text = title;
        ConfirmDetails.Text = details;
        ConfirmAccept.Content = accept;
        ConfirmCancel.Content = T("取消", "Cancel");
        Rail.IsHitTestVisible = DetailPane.IsHitTestVisible = false;
        ConfirmationLayer.Visibility = Visibility.Visible;
        ConfirmCancel.Focus(FocusState.Keyboard);
        return await pending.Task;
    }
    private void FinishConfirmation(bool accept)
    {
        if (confirmation is not { } pending) return;
        confirmation = null;
        ConfirmationLayer.Visibility = Visibility.Collapsed;
        Rail.IsHitTestVisible = DetailPane.IsHitTestVisible = true;
        if (opened) confirmationReturnFocus?.Focus(FocusState.Programmatic);
        confirmationReturnFocus = null;
        pending.TrySetResult(accept);
    }
    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => FinishConfirmation(false);
    private void ConfirmAccept_Click(object sender, RoutedEventArgs e) => FinishConfirmation(true);

    private void Dialog_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (confirmation is not null)
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; FinishConfirmation(false); }
            else if (args.Key == VirtualKey.Tab)
            {
                args.Handled = true;
                (ConfirmCancel.FocusState != FocusState.Unfocused ? ConfirmAccept : ConfirmCancel).Focus(FocusState.Keyboard);
            }
            return;
        }
        if (IsRecordingShortcut) { HandleShortcutKey(args); return; }
        if (args.Key == VirtualKey.Escape && query.Length > 0 && (SettingsSearch.FocusState != FocusState.Unfocused || CompactSearch.FocusState != FocusState.Unfocused))
        { args.Handled = true; SetSearchText(""); ShowPage(); }
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
