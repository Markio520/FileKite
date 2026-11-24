using FileAssistant.App.Controls;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.FileInput;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Features.FileInput;

/// <summary>Shared destination and naming dialogs for input, search and output transfers.</summary>
public static class FileCollectionDialogs
{
    public static async Task<FileImportReport?> ImportAsync(WorkspaceViewModel target, FileCollectionBundle bundle, FrameworkElement owner)
    {
        if (!bundle.HasItems || !target.CanReceiveFiles) return null;
        var picker = new CollectionFolderPicker();
        picker.InitializeModal(
            target,
            title: "选择加入位置",
            badge: target.Title,
            noteText: BundleSummary(bundle) + " · 原文件位置不变",
            primaryLabel: JoinLabel(bundle),
            showPreserveOption: bundle.Folders.Count > 0,
            canConfirm: (_, preserve) => bundle.Files.Count > 0 || (preserve && bundle.Folders.Count > 0));

        var card = WrapInModalCard(picker);
        var dialog = FramelessPickerDialog(owner, card, "加入" + target.Title, minWidth: 440);
        picker.CloseRequested += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        if (!picker.IsConfirmed || picker.SelectedFolderId is not { } folder) return null;
        return await ImportCheckedAsync(target, bundle, folder, picker.PreserveFolders);
    }

    public static async Task<FileImportReport?> ImportFromAsync(WorkspaceViewModel source, WorkspaceViewModel target, FrameworkElement owner)
    {
        if (!target.CanReceiveFiles || !source.CanReceiveFiles) return null;
        var picker = new CollectionSourcePicker();
        picker.Initialize(source, target, targetBadge: target.Title, verb: "导入");

        var dialog = FramelessPickerDialog(owner, picker, "从暂存区导入" + target.Title, minWidth: 520);
        picker.CloseRequested += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        if (!picker.IsConfirmed || picker.SelectedDestinationFolderId is not { } folder) return null;
        return await ImportCheckedAsync(target, picker.Selection, folder, picker.PreserveFolders);
    }

    public static async Task<FileCollectionBundle?> PickFromStagingAsync(WorkspaceViewModel source, FrameworkElement owner, string title, string primaryVerb = "导入")
    {
        var picker = new CollectionSourcePicker();
        picker.Initialize(source, target: null, targetBadge: "清理清单", verb: primaryVerb);

        var dialog = FramelessPickerDialog(owner, picker, title, minWidth: 520);
        picker.CloseRequested += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        return picker.IsConfirmed ? picker.Selection : null;
    }

    public static async Task MoveAsync(WorkspaceViewModel target, IReadOnlyList<WorkspaceFile> items, FrameworkElement owner, bool alsoAdd = false)
    {
        var folders = items.Where(item => item.FolderId is not null).Select(item => item.FolderId!.Value).ToArray();
        var bundle = target.ExportItems(items);
        var picker = new CollectionFolderPicker();
        picker.InitializeModal(
            target,
            title: alsoAdd ? "添加到其他文件夹" : "移动到文件夹",
            badge: target.Title,
            noteText: alsoAdd ? "加入后原文件夹项目保留 · 原件不变" : "仅在软件内移动分组 · 磁盘原件不变",
            primaryLabel: alsoAdd ? JoinLabel(bundle) : "移动",
            showPreserveOption: false,
            canConfirm: (_, _) => true,
            allowed: alsoAdd ? null : id => target.CanMoveTo(folders, id));

        var card = WrapInModalCard(picker);
        var dialog = FramelessPickerDialog(owner, card, alsoAdd ? "添加到其他文件夹" : "移动到文件夹", minWidth: 440);
        picker.CloseRequested += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
        if (!picker.IsConfirmed || picker.SelectedFolderId is not { } folder) return;
        if (alsoAdd) await target.ImportBundleAsync(bundle, folder, preserveFolders: true);
        else target.MoveItemsTo(items, folder);
    }

    private static Border WrapInModalCard(FrameworkElement child) => new()
    {
        Style = (Style)Application.Current.Resources["WorkspaceModalCardStyle"],
        Padding = new Thickness(0),
        Child = child
    };

    private static string JoinLabel(FileCollectionBundle bundle) => bundle.Files.Count > 0 ? $"加入 {bundle.Files.Count:N0} 项" : "加入文件夹";
    private static string BundleSummary(FileCollectionBundle bundle) => bundle.Folders.Count == 0 ? $"已选 {bundle.Files.Count:N0} 项"
        : $"已选文件夹及内容（{bundle.Files.Count:N0} 项）";
    private static async Task<FileImportReport> ImportCheckedAsync(WorkspaceViewModel target, FileCollectionBundle bundle, Guid folder, bool preserve)
    {
        try { return await target.ImportBundleAsync(bundle, folder, preserve); }
        catch (ArgumentException error) { target.InputFeedback = error.Message; return new([], error.Message); }
        catch (Exception) { target.InputFeedback = "加入失败，请检查文件和目标文件夹后重试。"; return new([], target.InputFeedback); }
    }

    public static async Task RenameOrCreateAsync(WorkspaceViewModel target, FrameworkElement owner, Guid? id = null, Guid? parent = null)
    {
        var destination = parent ?? target.CurrentFolderId;
        var name = new TextBox { Header = "文件夹名称", Text = id is { } folder ? target.FolderName(folder) : "新建文件夹", MaxLength = 120 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 };
        if (id is null)
            panel.Children.Add(new TextBlock { Text = "新建到：" + target.FolderChoices.First(folder => folder.Id == destination).Label,
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["WorkspaceCaptionStyle"] });
        panel.Children.Add(name); panel.Children.Add(error);
        var dialog = Dialog(owner, id is null ? "新建文件夹" : "重命名文件夹", panel, id is null ? "创建" : "保存");
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { if (id is { } existing) target.RenameFolder(existing, name.Text); else target.CreateFolder(name.Text, destination); }
            catch (ArgumentException issue) { error.Text = issue.Message; args.Cancel = true; }
        };
        await dialog.ShowAsync();
    }

    public static ContentDialog Dialog(FrameworkElement owner, string title, object content, string primary) => DialogDismiss.Attach(new()
    {
        Title = title, Content = content, XamlRoot = owner.XamlRoot, RequestedTheme = owner.ActualTheme,
        PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
    });

    private static ContentDialog FramelessPickerDialog(FrameworkElement owner, FrameworkElement content, string automationName, double minWidth = 520)
    {
        var dialog = new ContentDialog
        {
            Content = content,
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12)
        };
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var styleObj) && styleObj is Style dialogStyle)
            dialog.Style = dialogStyle;
        dialog.Resources["ContentDialogMaxWidth"] = 740d;
        dialog.Resources["ContentDialogMinWidth"] = minWidth;
        dialog.Resources["ContentDialogMaxHeight"] = 620d;
        dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0);
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogBorderThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(dialog, automationName);
        return DialogDismiss.Attach(dialog);
    }
}
