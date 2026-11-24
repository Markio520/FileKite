using System.Globalization;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private void BuildGeneral()
    {
        AddSection("语言与启动", "Language & startup", "多语言 简体 繁体 英文 开机 Windows login system tray launch", cards =>
        {
            var culture = CultureInfo.CurrentUICulture;
            var systemName = culture.TwoLetterISOLanguageName != "zh" ? "English" :
                culture.Name is "zh-TW" or "zh-HK" or "zh-MO" || culture.Name.Contains("Hant") ? "繁體中文" : "简体中文";
            ChoiceRow(cards, T("界面语言", "Interface language"), s => s.Language, (s, v) => s with { Language = v },
            [
                (InterfaceLanguage.System, string.Format(T("跟随系统（{0}）", "System default ({0})"), systemName)),
                (InterfaceLanguage.SimplifiedChinese, "简体中文"), (InterfaceLanguage.TraditionalChinese, "繁體中文"),
                (InterfaceLanguage.English, "English")
            ], T("切换主界面导航、操作栏与提示文本的显示语言", "Set the language for navigation, controls, and interface messages."), "\uE774");
            ChoiceRow(cards, T("启动时打开页面", "Page to open at startup"), s => s.StartupModule, (s, v) => s with { StartupModule = v },
            [
                ("last", T("上次关闭时停留的模块", "Last opened module")), ("workspace", T("文件暂存区", "Staging area")),
                ("search", T("搜索与筛选", "Search & filter")), ("convert", T("格式转换", "Format conversion")),
                ("space", T("空间分析", "Space analysis")), ("organize", T("复制与整理", "Copy & organize")),
                ("naming", T("创建与改名", "Create & rename")), ("cleanup", T("卸载与清理", "Uninstall & cleanup")),
                ("outputs", T("文件输出区", "Outputs"))
            ], glyph: "\uE80F");
            SwitchRow(cards, T("开机自动启动", "Start at sign-in"), s => s.StartAtLogin, (s, v) => s with { StartAtLogin = v },
                T("登录 Windows 后在系统托盘中启动", "Start quietly in the system tray when you sign in to Windows."), "\uE945");
        });
        AddSection("窗口与关闭行为", "Window & closing behavior", "最小化 拦截 任务 退出 关闭 驻留 置顶 记忆 常驻 win position",
            cards =>
        {
            ChoiceRow(cards, T("点击主窗口关闭按钮（✕）时", "When closing the main window"), s => s.CloseAction, (s, v) => s with { CloseAction = v },
            [
                (CloseBehavior.Tray, T("最小化到系统托盘", "Minimize to the system tray")),
                (CloseBehavior.Exit, T("直接退出程序", "Exit the app")), (CloseBehavior.Ask, T("每次询问", "Ask every time"))
            ], T("驻留托盘时可继续更新索引，并通过热键唤起窗口", "Keep indexing in the tray and bring back the window with a shortcut."), "\uE8BB");
            SwitchRow(cards, T("后台有批处理任务时确认退出", "Confirm before exiting with active batch tasks"),
                s => s.ConfirmBusyExit, (s, v) => s with { ConfirmBusyExit = v }, glyph: "\uE7BA");
            SwitchRow(cards, T("窗口始终置顶", "Keep the window on top"), s => s.AlwaysOnTop, (s, v) => s with { AlwaysOnTop = v },
                T("便于从资源管理器连续拖入文件", "Keep FileAssistant visible while dragging files from Explorer."), "\uE718");
            SwitchRow(cards, T("跨重启记住窗口尺寸、屏幕位置与分栏比例", "Remember window size, position, and pane widths"),
                s => s.RememberWindow, (s, v) => s with { RememberWindow = v }, glyph: "\uE91B");
        });
        AddSection("列表交互与显示单位", "File lists & units", "双击 容量 分组 移出 移除 文件扩展 Windows Explorer locate group", cards =>
        {
            ChoiceRow(cards, T("双击列表中的文件时", "When double-clicking a file"), s => s.DoubleClick, (s, v) => s with { DoubleClick = v },
            [
                (FileDoubleClick.Open, T("使用系统默认程序打开", "Open with the default app")),
                (FileDoubleClick.Preview, T("打开内置快速预览", "Open Quick Look")),
                (FileDoubleClick.Reveal, T("在资源管理器中定位", "Show in File Explorer"))
            ], glyph: "\uE8A7");
            ChoiceRow(cards, T("容量与文件大小单位制", "File size units"), s => s.Units, (s, v) => s with { Units = v },
            [
                (SizeUnits.Classic, "KB / MB / GB (1024)"), (SizeUnits.Binary, T("KiB / MiB / GiB（二进制）", "KiB / MiB / GiB (binary)")),
                (SizeUnits.Decimal, "KB / MB / GB (1000)")
            ], glyph: "\uE9D9");
            SwitchRow(cards, T("移出非空软件分组前询问确认", "Confirm before removing a nonempty group"),
                s => s.ConfirmNonemptyGroup, (s, v) => s with { ConfirmNonemptyGroup = v },
                T("只移除软件内的引用，磁盘上的文件保留", "Removing a group leaves its files on disk."), "\uE8B7");
        });
    }
}
