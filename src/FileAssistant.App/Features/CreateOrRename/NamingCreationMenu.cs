using FileAssistant.App.Features.FileInput;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Features.CreateOrRename;

internal static class NamingCreationMenu
{
    public static T? TargetAt<T>(object? source) where T : class
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: T target }) return target;
            if (node is ListViewItem { Content: T item }) return item;
            if (node is TreeViewItem { Content: FrameworkElement { Tag: T content } }) return content;
        }
        return null;
    }
    public static void Show(FrameworkElement surface, ContextRequestedEventArgs e, CreationContentRow? row, DirectoryDraft? emptyParent = null)
    {
        var vm = App.Naming;
        if (!vm.CanEdit) return;
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["ActionDropDownPresenterStyle"] };
        MenuFlyoutItem Item(string label, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled, Style = (Style)Application.Current.Resources["ActionDropDownItemStyle"] };
            item.Click += (_, _) => action(); return item;
        }
        var folder = row?.IsDirectory == true ? row.Draft : emptyParent ?? vm.CurrentCreationDirectory;
        if (row is not null)
        {
            menu.Items.Add(Item(row.IsDirectory ? "打开文件夹" : "打开文件", () => vm.OpenCreationItem(row), row.IsExisting || row.IsDirectory));
            if (row.IsExisting) menu.Items.Add(Item("打开所在文件夹", () => vm.OpenCreationItem(row, true)));
            menu.Items.Add(Item("复制完整路径", () => vm.CopyCreationText(row), row.Path.Length > 0));
            menu.Items.Add(Item("复制名称", () => vm.CopyCreationText(row, true)));
            if (row.IsExisting)
            {
                var use = new MenuFlyoutSubItem { Text = "用于功能", Style = (Style)Application.Current.Resources["ActionDropDownSubItemStyle"] };
                use.Items.Add(Item("加入暂存区…", async () => await ImportAsync(surface, row, false)));
                use.Items.Add(Item("批量改名…", async () => await ImportAsync(surface, row, true)));
                use.Items.Add(Item("复制与整理…", () => vm.TransferCreationItem(row, false)));
                use.Items.Add(Item("格式转换…", () => vm.TransferCreationItem(row, true)));
                menu.Items.Add(use);
            }
            if (row.Draft is { IsExisting: false, IsDestination: false } draft)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                if (!draft.IsMissing) menu.Items.Add(Item("重命名…", async () => await RenameAsync(surface, draft)));
                menu.Items.Add(Item("从待创建清单移除", () => vm.RemoveCreationDraft(draft)));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (row is null || row.IsDirectory)
        {
            menu.Items.Add(Item(row is null || folder?.IsDestination == true ? "新建文件夹…" : "新建子文件夹…", () => vm.PrepareCreationAt(folder, false), folder is { IsMissing: false }));
            menu.Items.Add(Item("新建文件…", () => vm.PrepareCreationAt(folder, true), folder is { IsMissing: false }));
        }
        menu.Items.Add(Item("刷新", async () => await vm.ReloadCreationContentsCommand.ExecuteAsync(null)));
        if (e.TryGetPosition(surface, out var position)) menu.ShowAt(surface, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(surface);
        e.Handled = true;
    }
    private static async Task ImportAsync(FrameworkElement owner, CreationContentRow row, bool rename)
    {
        try
        {
            var target = rename ? App.Naming.Input : App.Workspace;
            var report = await FileCollectionDialogs.ImportAsync(target, FileCollectionBundle.FromFiles([new SearchFile(row.Path, row.IsDirectory, null, null)]), owner);
            if (report is not null) { if (rename) App.Naming.ModeValue = 0; App.Naming.Feedback = report.Summary; }
        }
        catch (Exception error) { App.Naming.Feedback = "无法加入目标列表：" + error.Message; }
    }
    private static async Task RenameAsync(FrameworkElement owner, DirectoryDraft node)
    {
        var name = new TextBox { Text = node.Name, Header = "新名称", MinWidth = 260 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(name); content.Children.Add(error);
        var dialog = FileCollectionDialogs.Dialog(owner, "重命名待创建项", content, "保存");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { App.Naming.RenameCreationDraft(node, name.Text); }
            catch (ArgumentException failure) { error.Text = failure.Message; args.Cancel = true; }
        };
        try { await dialog.ShowAsync(); }
        catch (Exception failure) { App.Naming.Feedback = failure.Message; }
    }
}
