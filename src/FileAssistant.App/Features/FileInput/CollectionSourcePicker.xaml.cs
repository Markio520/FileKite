using CommunityToolkit.Mvvm.ComponentModel;
using FileAssistant.App.Controls;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.FileCollections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FileAssistant.App.Features.FileInput;

public sealed partial class StagingTreeNodeItem : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }
    public int Count { get; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    public StagingTreeNodeItem(Guid id, string name, int count)
    {
        Id = id;
        Name = name;
        Count = count;
    }

    public override string ToString() => $"{Name}，{Count} 项";
}

public sealed partial class CollectionSourcePicker : UserControl
{
    private readonly WorkspaceViewModel browser = new();
    private readonly Dictionary<Guid, TreeViewNode> treeNodes = [];
    private readonly Dictionary<Guid, StagingTreeNodeItem> treeItems = [];
    private CollectionSourceSelection? selection;
    private bool hasDestinationTarget;
    private bool sourceHasSamples;
    private string primaryVerb = "导入";
    private bool syncing;
    private bool syncingTree;

    public bool HasSelection => selection?.Count > 0;
    public int SelectedItemCount => selection?.Count ?? 0;
    public FileCollectionBundle Selection => selection?.Bundle ?? new([], []);
    public Guid? SelectedDestinationFolderId => DestinationPicker.SelectedFolderId;
    public bool PreserveFolders => PreserveCheck.IsChecked == true;
    public bool IsConfirmed { get; private set; }

    public event EventHandler? SelectionChanged;
    public event EventHandler? CloseRequested;

    public CollectionSourcePicker()
    {
        InitializeComponent();
        Files.ItemsSource = browser.VisibleFiles;
        // ListViewItem handles gestures for selection; folder activation must still receive the double tap.
        Files.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(Files_DoubleTapped), handledEventsToo: true);
        DestinationPicker.SelectionChanged += (_, _) => UpdateFooterState();
        KeyDown += Picker_KeyDown;
        Loaded += Picker_Loaded;
        Unloaded += Picker_Unloaded;
    }

    private void Picker_Loaded(object sender, RoutedEventArgs e)
    {
        WorkspaceMotion.AlignSingleLineInputs(this);
        UpdateResponsiveSize();
        if (XamlRoot is not null) XamlRoot.Changed += XamlRoot_Changed;
    }

    private void Picker_Unloaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not null) XamlRoot.Changed -= XamlRoot_Changed;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateResponsiveSize();

    private void UpdateResponsiveSize()
    {
        if (XamlRoot is null || XamlRoot.Size.Width <= 0 || XamlRoot.Size.Height <= 0) return;
        PickerLayout.Width = Math.Clamp(XamlRoot.Size.Width - 48, 520, 700);
        PickerLayout.Height = Math.Clamp(XamlRoot.Size.Height - 56, 400, 504);
    }

    public void Initialize(WorkspaceViewModel source, WorkspaceViewModel? target = null, string targetBadge = "", string verb = "导入")
    {
        IsConfirmed = false;
        primaryVerb = verb;
        hasDestinationTarget = target is not null;

        var badge = !string.IsNullOrWhiteSpace(targetBadge) ? targetBadge : target?.Title ?? string.Empty;
        TargetBadgePill.Visibility = string.IsNullOrWhiteSpace(badge) ? Visibility.Collapsed : Visibility.Visible;
        TargetBadgeText.Text = badge;

        if (target is not null)
        {
            DestinationRegion.Visibility = Visibility.Visible;
            FlatModeHint.Visibility = Visibility.Collapsed;
            DestinationPicker.Initialize(target, compact: false, hideHeader: true);
        }
        else
        {
            DestinationRegion.Visibility = Visibility.Collapsed;
            FlatModeHint.Visibility = Visibility.Visible;
        }

        sourceHasSamples = source.HasSamples;
        var snapshot = source.HasSamples
            ? new FileCollectionSnapshot(1, [], [])
            : source.CollectionSnapshot with { CurrentFolderId = Guid.Empty };

        selection = new(snapshot);
        syncing = true;
        try
        {
            browser.InitializeCollection(snapshot);
            TypeFilter.SelectedValue = 0;
            SearchBox.Text = string.Empty;
        }
        finally { syncing = false; }

        BuildFolderTree();
        UpdateNavigation();
    }

    public void ClearSelection()
    {
        selection?.Clear();
        Navigate(() => { });
    }

    private void BuildFolderTree()
    {
        treeNodes.Clear();
        treeItems.Clear();
        FolderTree.RootNodes.Clear();

        var navItems = browser.FolderNavigation;
        var hasCustomFolders = navItems.Count > 1;
        TreePane.Visibility = hasCustomFolders ? Visibility.Visible : Visibility.Collapsed;
        TreeColumn.Width = hasCustomFolders ? new GridLength(184) : new GridLength(0);

        if (!hasCustomFolders) return;

        foreach (var item in navItems)
        {
            var nodeItem = new StagingTreeNodeItem(item.Id, item.Name, item.Count);
            var node = new TreeViewNode { Content = nodeItem, IsExpanded = true };
            treeItems[item.Id] = nodeItem;
            treeNodes[item.Id] = node;
        }

        foreach (var item in navItems)
        {
            if (item.Id == Guid.Empty)
            {
                FolderTree.RootNodes.Add(treeNodes[item.Id]);
            }
            else if (treeNodes.TryGetValue(item.ParentId, out var parent))
            {
                parent.Children.Add(treeNodes[item.Id]);
            }
            else if (treeNodes.TryGetValue(Guid.Empty, out var root))
            {
                root.Children.Add(treeNodes[item.Id]);
            }
        }
    }

    private void UpdateNavigation()
    {
        UpButton.IsEnabled = browser.CanGoUp;
        PathBreadcrumbs.ItemsSource = browser.Breadcrumbs;
        SyncTreeSelection();
        UpdateEmptyState();
        SyncSelectAllCheck();
        UpdateTreeSelectionBadges();
        UpdateFooterState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFooterState()
    {
        var bundle = Selection;
        if (hasDestinationTarget)
        {
            DestinationNameText.Text = DestinationPicker.SelectedShortName;
            PreserveCheck.Visibility = bundle.Folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        ClearButton.Visibility = HasSelection ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = HasSelection
            ? $"已选 {SelectedItemCount:N0} 项 · {bundle.Files.Count:N0} 个文件"
            : "已选 0 项";

        var canConfirm = hasDestinationTarget
            ? HasSelection && DestinationPicker.SelectedFolderId is not null && (bundle.Files.Count > 0 || (PreserveFolders && bundle.Folders.Count > 0))
            : bundle.Files.Count > 0;

        ConfirmButton.IsEnabled = canConfirm;
        ConfirmButton.Content = !HasSelection
            ? $"{primaryVerb} 0 项"
            : bundle.Files.Count > 0
                ? $"{primaryVerb} {bundle.Files.Count:N0} 项"
                : $"{primaryVerb}文件夹";
    }

    private void SyncTreeSelection()
    {
        if (TreePane.Visibility != Visibility.Visible) return;
        if (!treeNodes.TryGetValue(browser.CurrentFolderId, out var target)) return;
        syncingTree = true;
        try
        {
            for (var parent = target.Parent; parent is not null; parent = parent.Parent)
                parent.IsExpanded = true;
            if (!ReferenceEquals(FolderTree.SelectedNode, target))
                FolderTree.SelectedNode = target;
        }
        finally { syncingTree = false; }
    }

    private void UpdateTreeSelectionBadges()
    {
        if (selection is null) return;
        foreach (var (id, item) in treeItems)
            item.HasSelection = id != Guid.Empty && selection.HasSelectionUnder(id);
    }

    private void UpdateEmptyState()
    {
        var empty = browser.VisibleFiles.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            EmptyStateText.Text = sourceHasSamples
                ? "暂存区当前为示例文件，请先前往「文件暂存区」添加真实文件"
                : string.IsNullOrWhiteSpace(SearchBox.Text) && TypeFilter.SelectedValue == 0
                    ? "当前目录暂无可导入的文件"
                    : "没有匹配的暂存区文件";
        }
    }

    private void SyncSelectAllCheck()
    {
        var total = browser.VisibleFiles.Count;
        var selected = Files.SelectedItems.Count;
        SelectAllCheck.IsEnabled = total > 0;
        SelectAllCheck.IsChecked = total == 0 || selected == 0
            ? false
            : selected >= total ? true : null;
    }

    private void Navigate(Action action)
    {
        syncing = true;
        try
        {
            action();
            RestoreVisibleSelection();
        }
        finally { syncing = false; }
        UpdateNavigation();
    }

    private void RestoreVisibleSelection()
    {
        Files.SelectedItems.Clear();
        if (selection is null) return;
        foreach (var item in browser.VisibleFiles.Where(selection.Contains))
            Files.SelectedItems.Add(item);
    }

    private void Enter(Guid folder)
    {
        selection?.BrowseInto(folder);
        Navigate(() =>
        {
            browser.EnterFolder(folder);
            SearchBox.Text = string.Empty;
            TypeFilter.SelectedValue = 0;
        });
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Navigate(() =>
    {
        browser.GoUp();
        SearchBox.Text = string.Empty;
        TypeFilter.SelectedValue = 0;
    });

    private void Breadcrumb_Click(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is FolderBreadcrumb crumb && crumb.Id != browser.CurrentFolderId)
            Navigate(() =>
            {
                browser.EnterFolder(crumb.Id);
                SearchBox.Text = string.Empty;
                TypeFilter.SelectedValue = 0;
            });
    }

    private void FolderTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (syncingTree) return;
        if (sender.SelectedNode?.Content is StagingTreeNodeItem item && item.Id != browser.CurrentFolderId)
            Enter(item.Id);
    }

    private void TypeFilter_SelectionCommitted(object? sender, EventArgs e)
    {
        if (syncing) return;
        Navigate(() => browser.FilterIndex = TypeFilter.SelectedValue);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (syncing) return;
        Navigate(() => browser.SearchText = SearchBox.Text);
    }

    private void SelectAllCheck_Click(object sender, RoutedEventArgs e)
    {
        var total = browser.VisibleFiles.Count;
        if (total == 0) { SyncSelectAllCheck(); return; }
        if (Files.SelectedItems.Count >= total)
            Files.SelectedItems.Clear();
        else
            Files.SelectAll();
        SyncSelectAllCheck();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void PreserveCheck_Changed(object sender, RoutedEventArgs e) => UpdateFooterState();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled) return;
        IsConfirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Picker_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            IsConfirmed = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        selection?.UpdateVisible(browser.VisibleFiles, Files.SelectedItems.OfType<WorkspaceFile>());
        SyncSelectAllCheck();
        UpdateTreeSelectionBadges();
        UpdateFooterState();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Files_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && Files.SelectedItems.Count == 1 && Files.SelectedItem is WorkspaceFile { FolderId: { } folder })
        {
            Enter(folder);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Back)
        {
            Navigate(() =>
            {
                browser.GoUp();
                SearchBox.Text = string.Empty;
                TypeFilter.SelectedValue = 0;
            });
            e.Handled = true;
        }
    }

    private void Files_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ListViewItem { Content: WorkspaceFile { FolderId: { } folder } })
            {
                e.Handled = true;
                // Finish the row's selection gesture before replacing its item containers.
                DispatcherQueue.TryEnqueue(() => Enter(folder));
                return;
            }
    }
}
