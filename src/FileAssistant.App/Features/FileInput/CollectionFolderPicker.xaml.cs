using FileAssistant.App.Controls;
using FileAssistant.App.Features.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileAssistant.App.Features.FileInput;

public sealed partial class CollectionFolderPicker : UserControl
{
    private WorkspaceViewModel target = null!;
    private Func<Guid, bool> allowed = _ => true;
    private Func<Guid, bool, bool>? modalCanConfirm;

    public Guid? SelectedFolderId => (Folders.SelectedNode?.Content as FolderBreadcrumb)?.Id;
    public string SelectedPath => TargetPath.Text;
    public string SelectedShortName => SelectedFolderId is { } id && id != Guid.Empty && target.HasFolder(id)
        ? target.FolderName(id)
        : "全部文件";
    public bool PreserveFolders => ModalPreserveCheck.IsChecked == true;
    public bool IsConfirmed { get; private set; }

    public event EventHandler? SelectionChanged;
    public event EventHandler? CloseRequested;

    public CollectionFolderPicker()
    {
        InitializeComponent();
        KeyDown += Picker_KeyDown;
        Loaded += (_, _) => WorkspaceMotion.AlignSingleLineInputs(this);
    }

    public void Initialize(WorkspaceViewModel target, Func<Guid, bool>? allowed = null, bool compact = true, bool hideHeader = false)
    {
        IsConfirmed = false;
        modalCanConfirm = null;
        ModalHeader.Visibility = ModalFooter.Visibility = Visibility.Collapsed;
        RootShell.ClearValue(WidthProperty);
        RootShell.ClearValue(BackgroundProperty);
        RootShell.BorderThickness = new Thickness(0);
        RootShell.CornerRadius = new CornerRadius(0);
        BodySurface.Padding = new Thickness(0);

        this.target = target;
        this.allowed = allowed ?? (_ => true);
        Refresh(target.CurrentFolderId);
        HeaderRegion.Visibility = hideHeader ? Visibility.Collapsed : Visibility.Visible;
        TreeRegion.Visibility = !compact || hideHeader ? Visibility.Visible : Visibility.Collapsed;
        ChangeButton.Content = compact ? "更改" : "收起";
    }

    public void InitializeModal(
        WorkspaceViewModel target,
        string title,
        string badge,
        string noteText,
        string primaryLabel,
        bool showPreserveOption,
        Func<Guid, bool, bool> canConfirm,
        Func<Guid, bool>? allowed = null)
    {
        Initialize(target, allowed, compact: false, hideHeader: true);
        modalCanConfirm = canConfirm;

        RootShell.Width = 460;
        BodySurface.Padding = new Thickness(16, 12, 16, 12);
        ModalHeader.Visibility = ModalFooter.Visibility = Visibility.Visible;
        ModalTitleText.Text = title;
        ModalBadgeText.Text = badge;
        ModalBadgePill.Visibility = string.IsNullOrWhiteSpace(badge) ? Visibility.Collapsed : Visibility.Visible;
        ModalNoteText.Text = noteText;
        ToolTipService.SetToolTip(ModalNoteText, noteText + "；重复项自动跳过。");
        ModalPreserveCheck.Visibility = showPreserveOption ? Visibility.Visible : Visibility.Collapsed;
        ModalPreserveCheck.IsChecked = true;
        ModalConfirmButton.Content = primaryLabel;
        UpdateModalState();
    }

    private void Refresh(Guid selected)
    {
        var root = new TreeViewNode { Content = new FolderBreadcrumb(Guid.Empty, "全部文件"), IsExpanded = true };
        var nodes = new Dictionary<Guid, TreeViewNode> { [Guid.Empty] = root };
        var folders = target.CollectionSnapshot.Folders.Where(folder => allowed(folder.Id)).ToArray();
        foreach (var folder in folders)
            nodes[folder.Id] = new() { Content = new FolderBreadcrumb(folder.Id, folder.Name), IsExpanded = true };
        foreach (var folder in folders)
            if (nodes.TryGetValue(folder.ParentId, out var parent)) parent.Children.Add(nodes[folder.Id]);
        Folders.RootNodes.Clear();
        Folders.RootNodes.Add(root);
        var current = nodes.GetValueOrDefault(selected) ?? root;
        for (var parent = current.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        Folders.SelectedNode = current;
        UpdatePath();
    }

    private void UpdatePath()
    {
        if (target is null) return;
        TargetPath.Text = target.FolderChoices.FirstOrDefault(folder => folder.Id == SelectedFolderId)?.Label ?? "请选择目标文件夹";
        NewFolderLocationHint.Text = "新建在：" + TargetPath.Text;
        UpdateModalState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateModalState()
    {
        if (modalCanConfirm is null) return;
        ModalConfirmButton.IsEnabled = SelectedFolderId is { } id && modalCanConfirm(id, PreserveFolders);
    }

    private void Folders_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs e) => UpdatePath();

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var show = TreeRegion.Visibility != Visibility.Visible;
        TreeRegion.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChangeButton.Content = show ? "收起" : "更改";
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        NameEditor.Visibility = Visibility.Visible;
        SetMessage(string.Empty);
        WorkspaceMotion.AlignSingleLineInputs(NameEditor);
        NewName.Focus(FocusState.Programmatic);
    }

    private void CancelNew_Click(object sender, RoutedEventArgs e)
    {
        NameEditor.Visibility = Visibility.Collapsed;
        NewName.Text = string.Empty;
        SetMessage(string.Empty);
    }

    private void NewName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; Create_Click(sender, e); }
        else if (e.Key == VirtualKey.Escape) { e.Handled = true; CancelNew_Click(sender, e); }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolderId is not { } parent || !target.CanReceiveFiles) return;
        try
        {
            var id = target.CreateFolder(NewName.Text, parent);
            NewName.Text = string.Empty;
            NameEditor.Visibility = Visibility.Collapsed;
            Refresh(id);
            SetMessage("已创建并选中新建文件夹。");
        }
        catch (ArgumentException error) { SetMessage(error.Message); }
    }

    private void ModalPreserve_Changed(object sender, RoutedEventArgs e) => UpdateModalState();

    private void ModalConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ModalConfirmButton.IsEnabled) return;
        IsConfirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ModalClose_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Picker_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ModalHeader.Visibility == Visibility.Visible && NameEditor.Visibility != Visibility.Visible)
        {
            e.Handled = true;
            IsConfirmed = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
