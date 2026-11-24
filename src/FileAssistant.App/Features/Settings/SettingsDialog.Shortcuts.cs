using FileAssistant.Core.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace FileAssistant.App.Features.Settings;

public sealed partial class SettingsDialog
{
    private ShortcutAction? recording;
    private Button? recorder;
    private Action? showRecordedChord;

    private void BuildShortcuts()
    {
        AddSection("全局唤起热键", "Global shortcuts", "快捷键 键帽 录制 唤醒 呼出 托盘 drop keyboard shortcut hotkey register Alt+Space Ctrl+Shift+F",
            cards =>
        {
            ShortcutRow(cards, ShortcutAction.ToggleWindow, T("呼出 / 隐藏主窗口", "Show or hide the main window"),
                T("驻留系统托盘时，在任意界面唤起或收起工作区", "Bring the workspace forward or hide it from anywhere while it is in the tray."));
            ShortcutRow(cards, ShortcutAction.GlobalSearch, T("全局秒开全盘搜索", "Open global file search"),
                T("唤醒主窗口并定位到搜索关键词输入框", "Show the main window and focus the file search box."));
            ChoiceRow(cards, T("将外部文件拖入托盘图标时默认送入", "When dropping files on the tray icon"), s => s.DropTarget,
                (s, v) => s with { DropTarget = v },
            [
                (DropDestination.Staging, T("文件暂存区（并唤起窗口）", "Staging area and show window")),
                (DropDestination.CurrentModule, T("当前停留的功能模块", "Current workspace module")),
                (DropDestination.SilentStaging, T("静默收入暂存区", "Add to staging silently"))
            ]);
        }, () => ActionButton(T("恢复默认", "Restore defaults"), async () =>
        {
            EndRecording();
            SetShortcutResult(await App.ResetShortcutsAsync());
        }));
        AddSection("工作区内部快捷键", "Workspace shortcuts", "录制 键盘 快捷键 QuickLook Space Alt+C Ctrl+F F5 Ctrl+,",
            cards =>
        {
            ShortcutRow(cards, ShortcutAction.QuickLook, T("快速预览选中文件 (QuickLook)", "Quick Look for the selected file"));
            ShortcutRow(cards, ShortcutAction.Compare, T("图片转换压缩前后画质对比", "Compare image quality before and after conversion"));
            ShortcutRow(cards, ShortcutAction.Filter, T("聚焦当前页面筛选搜索框", "Focus the current page’s filter box"));
            ShortcutRow(cards, ShortcutAction.Refresh, T("刷新当前目录或检索列表", "Refresh the current folder or search results"));
            ShortcutRow(cards, ShortcutAction.Settings, T("打开设置窗口", "Open settings"));
        });
    }

    private void ShortcutRow(StackPanel cards, ShortcutAction action, string title, string? description = null)
    {
        var control = new Button { Style = LocalStyle("SettingBareButton"), HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(control, "Settings.Shortcut." + action);
        var warning = Label("", "SettingWarning");
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(Texts(title, description)); left.Children.Add(warning);
        cards.Children.Add(Card(Pair(left, control, 345), description is null ? 48 : 56));
        string previous = "";
        void Refresh()
        {
            if (recording == action) return;
            string chord = App.Preferences.Current.Shortcuts.Get(action);
            if (control.Content is not SettingsFlowPanel || chord != previous)
            {
                control.Content = Keycaps(chord);
                previous = chord;
                NameControl(control, title + " · " + chord + " · " + T("录制快捷键", "Record shortcut"));
            }
            bool occupied = App.ShortcutWarnings.TryGetValue(action, out var reason);
            warning.Visibility = Visible(occupied);
            warning.Text = occupied ? T("此热键未注册", "This shortcut is unavailable") + " · " + reason : "";
        }
        Bind(_ => Refresh());
        control.Click += (_, _) =>
        {
            EndRecording();
            recording = action; recorder = control; showRecordedChord = Refresh;
            App.SetShortcutRecording(true);
            IsRecordingShortcut = true;
            var prompt = T("请按下新组合键…（Esc 取消）", "Press the new keys… (Esc cancels)");
            control.Content = Label(prompt, "SettingCaption");
            NameControl(control, prompt);
            control.Focus(FocusState.Programmatic);
        };
        control.LostFocus += (_, _) => { if (ReferenceEquals(recorder, control)) EndRecording(); };
    }

    private SettingsFlowPanel Keycaps(string chord)
    {
        var keys = Flow(4);
        foreach (var part in chord.Split('+'))
        {
            if (keys.Children.Count > 0) keys.Children.Add(Label("+", "SettingCaption"));
            var text = Label(part);
            text.FontSize = 11.5; text.FontWeight = FontWeights.SemiBold; text.TextAlignment = TextAlignment.Center;
            keys.Children.Add(new Border
            {
                Child = text, MinWidth = 24, MinHeight = 23, Padding = new(6, 1, 6, 2), CornerRadius = new(4),
                Style = LocalStyle("SettingKeycap"), BorderThickness = new(1, 1, 1, 2)
            });
        }
        return keys;
    }

    private void EndRecording()
    {
        if (recording is null) return;
        recording = null; recorder = null; IsRecordingShortcut = false;
        App.SetShortcutRecording(false);
        var restore = showRecordedChord;
        showRecordedChord = null;
        restore?.Invoke();
    }

    private async void HandleShortcutKey(KeyRoutedEventArgs args)
    {
        if (recording is not { } action) return;
        args.Handled = true;
        if (args.Key == VirtualKey.Escape) { EndRecording(); return; }
        if (args.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Shift or VirtualKey.LeftShift
            or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        if (ShortcutChord.KeyName((uint)args.OriginalKey) is not { } key)
        {
            Notify(T("此按键无法录制", "This key cannot be assigned"),
                T("请按字母、数字、功能键或导航键。按 Esc 取消录制。", "Use a letter, number, function key, or navigation key. Press Esc to cancel."), InfoBarSeverity.Warning);
            return;
        }
        var parts = new List<string>(5);
        if (Down(VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(VirtualKey.Menu)) parts.Add("Alt");
        if (Down(VirtualKey.Shift)) parts.Add("Shift");
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(key);
        EndRecording();
        try { SetShortcutResult(await App.TrySetShortcutAsync(action, string.Join('+', parts))); }
        catch (Exception error) { Notify(T("未能保存快捷键", "Could not save the shortcut"), error.Message, InfoBarSeverity.Error); }
    }
    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    private void SetShortcutResult(string? error)
    {
        if (opened)
        {
            Rebind();
            if (error is not null)
            {
                SaveBadge.Visibility = Visibility.Collapsed;
                Notify(T("快捷键未更改", "Shortcuts were not changed"), error, InfoBarSeverity.Warning);
            }
            else { MessageBar.IsOpen = false; ShowSaved(); }
        }
    }
}
