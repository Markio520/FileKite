using System.ComponentModel;
using FileAssistant.App.Controls;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FileAssistant.App.Features.CreateOrRename;

internal sealed record OutlineExternalDragPayload(
    IReadOnlyList<OutlineDraftLine> DraggedLines,
    IReadOnlyList<OutlineDraftLine> RemainingCurrentLines);

public sealed partial class NamingCreationPreviewView : UserControl
{
    private const int MaxRenderedOutlineRows = 160;

    internal static OutlineExternalDragPayload? ActiveExternalDrag;

    private sealed class OutlineRowItem
    {
        public int Indent;
        public string Text = "";
        public bool? ManualIsFile;
        public bool IsCollapsed;
        public Border Root = null!;
        public Border GuideBar = null!;
        public Border DragHandle = null!;
        public Button CollapseButton = null!;
        public FontIcon CollapseIcon = null!;
        public FontIcon TypeIcon = null!;
        public TextBox Input = null!;
        public Border Badge = null!;
        public TextBlock BadgeText = null!;
        public Button DeleteButton = null!;
    }

    public NamingViewModel ViewModel => App.Naming;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer reloadTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer syncTimer;
    private readonly List<OutlineRowItem> rows = [];
    private readonly HashSet<OutlineRowItem> selectedRows = [];
    private List<OutlineDraftLine> overflowLines = [];
    private OutlineRowItem? activeRow;
    private OutlineRowItem? selectionAnchor;
    private int[]? draggingIndices;
    private bool suppressSync;

    // Marquee rubber-band selection state
    private bool marqueeArmed;
    private bool marqueeActive;
    private uint marqueePointerId;
    private Point marqueeStart;

    public NamingCreationPreviewView()
    {
        InitializeComponent();
        reloadTimer = DispatcherQueue.CreateTimer();
        reloadTimer.Interval = TimeSpan.FromMilliseconds(250);
        reloadTimer.IsRepeating = false;
        reloadTimer.Tick += async (_, _) =>
        {
            if (IsLoaded && ViewModel.IsCreate) await ViewModel.ReloadCreationContentsCommand.ExecuteAsync(null);
        };

        syncTimer = DispatcherQueue.CreateTimer();
        syncTimer.Interval = TimeSpan.FromMilliseconds(180);
        syncTimer.IsRepeating = false;
        syncTimer.Tick += (_, _) => FlushOutlineToViewModel();

        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += Changed;
            ViewModel.OutlineReloadRequested += OnOutlineReloadRequested;
            ViewModel.CreationEditorRequested += OnCreationEditorRequested;
            ActualThemeChanged += OnActualThemeChanged;
            ReloadOutlineFromModel();
            RequestContentsReload();
        };
        Unloaded += (_, _) =>
        {
            reloadTimer.Stop();
            syncTimer.Stop();
            ActiveExternalDrag = null;
            ViewModel.PropertyChanged -= Changed;
            ViewModel.OutlineReloadRequested -= OnOutlineReloadRequested;
            ViewModel.CreationEditorRequested -= OnCreationEditorRequested;
            ActualThemeChanged -= OnActualThemeChanged;
        };
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => RefreshAllRowAppearances();

    private void OnCreationEditorRequested(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var target = rows.LastOrDefault(x => string.IsNullOrWhiteSpace(x.Text)) ?? rows.LastOrDefault();
            target?.Input.Focus(FocusState.Programmatic);
        });
    }

    private void OnOutlineReloadRequested(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        DispatcherQueue.TryEnqueue(ReloadOutlineFromModel);
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NamingViewModel.CurrentCreationPath) or nameof(NamingViewModel.Report) or nameof(NamingViewModel.ModeValue))
        {
            RequestContentsReload();
            if (!ViewModel.IsOutlineSyncing) ReloadOutlineFromModel();
        }
        else if (e.PropertyName == nameof(NamingViewModel.CurrentCreationDirectory) && !ViewModel.IsOutlineSyncing)
        {
            ReloadOutlineFromModel();
        }
    }

    private void RequestContentsReload()
    {
        reloadTimer.Stop();
        if (ViewModel.IsCreate) reloadTimer.Start();
    }

    private void ReloadOutlineFromModel()
    {
        syncTimer.Stop();
        suppressSync = true;
        try
        {
            // Remember collapsed folder names so collapsing persists across light edits
            var collapsedKeys = rows.Where(r => r.IsCollapsed && !string.IsNullOrWhiteSpace(r.Text))
                .Select(r => $"{r.Indent}:{r.Text.Trim()}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            rows.Clear();
            selectedRows.Clear();
            selectionAnchor = null;
            overflowLines.Clear();
            OutlineRowsHost.Children.Clear();
            OutlineErrorBanner.Visibility = Visibility.Collapsed;

            var exported = ViewModel.ExportCurrentOutline();
            if (exported.Count == 0)
            {
                AddRowVisual(0, "", null, insertAt: -1, focus: false);
            }
            else
            {
                var visibleCount = Math.Min(exported.Count, MaxRenderedOutlineRows);
                for (var i = 0; i < visibleCount; i++)
                {
                    var added = AddRowVisual(exported[i].Indent, exported[i].Name, exported[i].ForceIsFile, insertAt: -1, focus: false);
                    if (collapsedKeys.Contains($"{added.Indent}:{added.Text.Trim()}")) added.IsCollapsed = true;
                }
                if (exported.Count > visibleCount)
                    overflowLines = exported.Skip(visibleCount).ToList();
            }

            RefreshAllRowAppearances();
            UpdateSelectionVisuals();
            UpdateOutlineSummary();
        }
        finally
        {
            suppressSync = false;
        }
    }

    private OutlineRowItem AddRowVisual(int indent, string text, bool? manualIsFile, int insertAt, bool focus)
    {
        var item = new OutlineRowItem { Indent = Math.Max(0, indent), Text = text, ManualIsFile = manualIsFile };

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2, 1, 4, 1),
            AllowDrop = true
        };

        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: indent guide
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: collapse chevron (only when has children)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 2: draggable folder/file icon
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 3: input
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4: badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5: delete

        var guide = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Background = ThemeBrush("WorkspaceBorderBrush"),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 4, 2, 4)
        };
        Grid.SetColumn(guide, 0);

        var collapseIcon = new FontIcon
        {
            Glyph = "\uE70D",
            FontSize = 9,
            Foreground = ThemeBrush("WorkspaceSecondaryBrush")
        };
        var collapseBtn = new Button
        {
            Content = collapseIcon,
            Width = 20,
            Height = 24,
            MinWidth = 20,
            MinHeight = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        ToolTipService.SetToolTip(collapseBtn, "折叠 / 展开子项");
        collapseBtn.Click += (_, _) =>
        {
            item.IsCollapsed = !item.IsCollapsed;
            RefreshAllRowAppearances();
            UpdateOutlineSummary();
        };
        Grid.SetColumn(collapseBtn, 1);

        var typeIcon = new FontIcon
        {
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var dragHandle = new Border
        {
            Child = typeIcon,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            CanDrag = true
        };
        ToolTipService.SetToolTip(dragHandle, "拖动可调整层级或放入其他文件夹");
        dragHandle.DragStarting += (_, args) => Row_DragStarting(item, args);
        dragHandle.DropCompleted += (_, _) => ClearDragState();
        dragHandle.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed)
                HandleRowModifierClick(item);
        };
        dragHandle.ContextRequested += (_, args) => ShowOutlineRowMenu(item, dragHandle, args);
        Grid.SetColumn(dragHandle, 2);

        var input = new TextBox
        {
            Text = text,
            PlaceholderText = rows.Count == 0 && insertAt <= 0 ? "输入名称（如 客户甲，Enter 换行，Tab 缩进成子目录，说明.txt 自动建文件）" : "输入名称…",
            FontSize = 13,
            MinHeight = 32,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeBrush("OpFieldEdgeBrush"),
            Background = ThemeBrush("WorkspaceSubtleBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        input.GotFocus += (_, _) => activeRow = item;
        input.TextChanged += (_, _) =>
        {
            if (suppressSync) return;
            var current = input.Text;
            if (current.Contains('\n') || current.Contains('\r'))
            {
                HandleMultiLinePasteIntoRow(item, current);
                return;
            }
            item.Text = current;
            RefreshAllRowAppearances();
            UpdateOutlineSummary();
            ScheduleSync();
        };
        input.Paste += async (_, args) =>
        {
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text)) return;
                var pasted = await content.GetTextAsync();
                if (string.IsNullOrEmpty(pasted) || (!pasted.Contains('\n') && !pasted.Contains('\r'))) return;
                args.Handled = true;
                HandleMultiLinePasteIntoRow(item, pasted);
            }
            catch { }
        };
        input.PreviewKeyDown += (_, args) => HandleRowKeyDown(item, args);
        Grid.SetColumn(input, 3);

        var badgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrush("WorkspaceDiffAddedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            Child = badgeText,
            Background = ThemeBrush("WorkspaceDiffAddedBackgroundBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            CanDrag = true
        };
        badge.DragStarting += (_, args) => Row_DragStarting(item, args);
        badge.DropCompleted += (_, _) => ClearDragState();
        badge.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(badge).Properties.IsLeftButtonPressed)
                HandleRowModifierClick(item);
        };
        badge.ContextRequested += (_, args) => ShowOutlineRowMenu(item, badge, args);
        Grid.SetColumn(badge, 4);

        var deleteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 },
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Foreground = ThemeBrush("WorkspaceSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(deleteBtn, "删除此行");
        deleteBtn.Click += (_, _) => RemoveRow(item);
        Grid.SetColumn(deleteBtn, 5);

        grid.Children.Add(guide);
        grid.Children.Add(collapseBtn);
        grid.Children.Add(dragHandle);
        grid.Children.Add(input);
        grid.Children.Add(badge);
        grid.Children.Add(deleteBtn);
        outerBorder.Child = grid;

        // Drop target handlers on full row width!
        outerBorder.DragOver += (_, args) => Row_DragOver(item, args);
        outerBorder.DragLeave += (_, _) => SetDropTargetHighlight(item, false);
        outerBorder.Drop += (_, args) => Row_Drop(item, args);

        item.Root = outerBorder;
        item.GuideBar = guide;
        item.DragHandle = dragHandle;
        item.CollapseButton = collapseBtn;
        item.CollapseIcon = collapseIcon;
        item.TypeIcon = typeIcon;
        item.Input = input;
        item.Badge = badge;
        item.BadgeText = badgeText;
        item.DeleteButton = deleteBtn;

        if (insertAt < 0 || insertAt >= rows.Count)
        {
            rows.Add(item);
            OutlineRowsHost.Children.Add(outerBorder);
        }
        else
        {
            rows.Insert(insertAt, item);
            OutlineRowsHost.Children.Insert(insertAt, outerBorder);
        }

        UpdateRowAppearance(item);
        input.Loaded += (_, _) => WorkspaceMotion.AlignSingleLineInputs(input);

        if (focus)
        {
            activeRow = item;
            DispatcherQueue.TryEnqueue(() =>
            {
                item.Input.Focus(FocusState.Programmatic);
                item.Input.SelectionStart = item.Input.Text.Length;
            });
        }
        return item;
    }

    // ==================== SELECTION (CTRL / SHIFT / MARQUEE RUBBER-BAND) ====================
    private void HandleRowModifierClick(OutlineRowItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

        if (shift && selectionAnchor is not null && rows.Contains(selectionAnchor))
        {
            var a = rows.IndexOf(selectionAnchor);
            var b = rows.IndexOf(item);
            if (!ctrl) selectedRows.Clear();
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            for (var i = lo; i <= hi; i++)
            {
                if (rows[i].Root.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(rows[i].Text))
                    selectedRows.Add(rows[i]);
            }
            UpdateSelectionVisuals();
        }
        else if (ctrl)
        {
            if (!selectedRows.Add(item)) selectedRows.Remove(item);
            selectionAnchor = item;
            UpdateSelectionVisuals();
        }
    }

    private void ShowOutlineRowMenu(OutlineRowItem item, FrameworkElement anchor, ContextRequestedEventArgs e)
    {
        if (!ViewModel.CanEdit || string.IsNullOrWhiteSpace(item.Text)) return;
        e.Handled = true;

        var resources = Application.Current.Resources;
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = (Style)resources["ActionDropDownPresenterStyle"] };
        MenuFlyoutItem MakeItem(string label, Action action, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = label, IsEnabled = enabled, Style = (Style)resources["ActionDropDownItemStyle"] };
            mi.Click += (_, _) => action();
            return mi;
        }

        var idx = rows.IndexOf(item);
        if (!RowHasChild(idx))
        {
            var currentlyFile = ResolveIsFile(item);
            menu.Items.Add(MakeItem(currentlyFile ? "设为文件夹" : "设为文件", () =>
            {
                item.ManualIsFile = !currentlyFile;
                RefreshAllRowAppearances();
                UpdateOutlineSummary();
                ScheduleSync();
            }));
        }
        if (item.Indent > 0)
        {
            menu.Items.Add(MakeItem("移至顶层", () =>
            {
                var lines = SnapshotCurrentLines();
                int[] targetIndices = selectedRows.Contains(item)
                    ? selectedRows.Select(r => rows.IndexOf(r)).Where(i => i >= 0).ToArray()
                    : [idx];
                var moved = NamingViewModel.MoveOutlineSelectionToRoot(lines, targetIndices, appendToEnd: true);
                if (moved is not null) ApplyReorderedLines(moved);
            }));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeItem("移出列表", () =>
        {
            if (selectedRows.Contains(item)) DeleteSelectedRows();
            else RemoveRow(item);
        }));

        if (e.TryGetPosition(anchor, out var pos)) menu.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pos });
        else menu.ShowAt(anchor);
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(OutlineCanvasGrid).Properties.IsLeftButtonPressed) return;
        // Do not hijack clicks inside TextBox, Button, or draggable handles
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;

        marqueeStart = e.GetCurrentPoint(OutlineCanvasGrid).Position;
        marqueeArmed = true;
        marqueeActive = false;
        marqueePointerId = e.Pointer.PointerId;
        OutlineCanvasGrid.CapturePointer(e.Pointer);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!marqueeArmed || e.Pointer.PointerId != marqueePointerId) return;
        var current = e.GetCurrentPoint(OutlineCanvasGrid).Position;
        var dx = Math.Abs(current.X - marqueeStart.X);
        var dy = Math.Abs(current.Y - marqueeStart.Y);

        if (!marqueeActive)
        {
            if (dx < 5 && dy < 5) return;
            marqueeActive = true;
            MarqueeRect.Visibility = Visibility.Visible;
        }

        var left = Math.Min(marqueeStart.X, current.X);
        var top = Math.Min(marqueeStart.Y, current.Y);
        var width = Math.Max(1, Math.Abs(current.X - marqueeStart.X));
        var height = Math.Max(1, Math.Abs(current.Y - marqueeStart.Y));

        Canvas.SetLeft(MarqueeRect, left);
        Canvas.SetTop(MarqueeRect, top);
        MarqueeRect.Width = width;
        MarqueeRect.Height = height;

        var selectionRect = new Rect(left, top, width, height);
        UpdateMarqueeSelection(selectionRect);
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!marqueeArmed || e.Pointer.PointerId != marqueePointerId) return;
        var wasDragging = marqueeActive;
        EndMarquee(e.Pointer);
        if (!wasDragging && selectedRows.Count > 0)
        {
            selectedRows.Clear();
            UpdateSelectionVisuals();
        }
    }

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (marqueeArmed) EndMarquee(null);
    }

    private void EndMarquee(Pointer? pointer)
    {
        marqueeArmed = false;
        marqueeActive = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        if (pointer is not null) OutlineCanvasGrid.ReleasePointerCapture(pointer);
    }

    private void UpdateMarqueeSelection(Rect rect)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (!ctrl) selectedRows.Clear();

        foreach (var row in rows)
        {
            if (row.Root.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(row.Text)) continue;
            try
            {
                var transform = row.Root.TransformToVisual(OutlineCanvasGrid);
                var topLeft = transform.TransformPoint(new Point(0, 0));
                var bounds = new Rect(topLeft.X, topLeft.Y, row.Root.ActualWidth, row.Root.ActualHeight);
                var intersects = rect.Left < bounds.Right && rect.Right > bounds.Left
                    && rect.Top < bounds.Bottom && rect.Bottom > bounds.Top;
                if (intersects) selectedRows.Add(row);
            }
            catch { }
        }
        selectionAnchor = selectedRows.FirstOrDefault();
        UpdateSelectionVisuals();
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is TextBox or Button or CheckBox) return true;
            if (current is UIElement { CanDrag: true }) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var row in rows)
        {
            var isSelected = selectedRows.Contains(row);
            row.Root.Background = isSelected ? ThemeBrush("WorkspaceAccentSubtleBrush") : new SolidColorBrush(Colors.Transparent);
            row.Root.BorderBrush = isSelected ? ThemeBrush("WorkspaceBorderBrush") : new SolidColorBrush(Colors.Transparent);
        }
        UpdateOutlineSummary();
    }

    private void Canvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        if (e.Key == VirtualKey.Delete && selectedRows.Count > 0)
        {
            e.Handled = true;
            DeleteSelectedRows();
        }
        else if (e.Key == VirtualKey.Escape && selectedRows.Count > 0)
        {
            e.Handled = true;
            selectedRows.Clear();
            UpdateSelectionVisuals();
        }
    }

    private void DeleteSelectedRows_Click(object sender, RoutedEventArgs e) => DeleteSelectedRows();

    private void DeleteSelectedRows()
    {
        if (selectedRows.Count == 0) return;
        var lines = SnapshotCurrentLines();
        var indices = selectedRows.Select(r => rows.IndexOf(r)).Where(i => i >= 0).OrderBy(i => i).ToArray();
        var toRemove = new HashSet<int>();
        foreach (var idx in indices)
        {
            var end = NamingViewModel.FindSubtreeEndExclusive(lines, idx);
            for (var k = idx; k < end; k++) toRemove.Add(k);
        }
        var remaining = lines.Where((_, i) => !toRemove.Contains(i)).ToList();
        ApplyReorderedLines(remaining);
    }

    private void PromoteSelectedToRoot_Click(object sender, RoutedEventArgs e)
    {
        var indices = selectedRows.Select(r => rows.IndexOf(r)).Where(i => i >= 0).ToArray();
        if (indices.Length == 0) return;
        var lines = SnapshotCurrentLines();
        var moved = NamingViewModel.MoveOutlineSelectionToRoot(lines, indices, appendToEnd: true);
        if (moved is not null) ApplyReorderedLines(moved);
    }

    // ==================== DRAG & DROP (CENTER OUTLINE <-> FOLDER / REORDER / LEFT TREE) ====================
    private DirectoryDraft? ResolveOutlineRowDirectory(OutlineRowItem targetRow)
    {
        if (string.IsNullOrWhiteSpace(targetRow.Text) || ResolveIsFile(targetRow)) return null;
        var targetIdx = rows.IndexOf(targetRow);
        if (targetIdx < 0) return null;

        var stack = new List<DirectoryDraft> { ViewModel.CurrentCreationDirectory };
        for (var i = 0; i <= targetIdx; i++)
        {
            var r = rows[i];
            if (string.IsNullOrWhiteSpace(r.Text)) continue;
            var indent = Math.Clamp(r.Indent, 0, stack.Count - 1);
            while (stack.Count > indent + 1) stack.RemoveAt(stack.Count - 1);
            if (ResolveIsFile(r))
            {
                if (i == targetIdx) return null;
                continue;
            }
            var parent = stack[^1];
            var found = parent.FolderChildren.FirstOrDefault(x => x.Name.Equals(r.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found is null) return null;
            if (i == targetIdx) return found;
            stack.Add(found);
        }
        return null;
    }

    private void Row_DragStarting(OutlineRowItem item, DragStartingEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            args.Cancel = true;
            return;
        }

        var rowIdx = rows.IndexOf(item);
        if (rowIdx < 0)
        {
            args.Cancel = true;
            return;
        }

        draggingIndices = selectedRows.Contains(item)
            ? selectedRows.Select(r => rows.IndexOf(r)).Where(i => i >= 0).OrderBy(i => i).ToArray()
            : [rowIdx];
        if (draggingIndices.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        // Prepare external payload so user can ALSO drop onto Left Directory Tree folders!
        var lines = SnapshotCurrentLines();
        var covered = new HashSet<int>();
        var dragged = new List<OutlineDraftLine>();
        foreach (var idx in draggingIndices)
        {
            if (covered.Contains(idx)) continue;
            var end = NamingViewModel.FindSubtreeEndExclusive(lines, idx);
            var baseIndent = lines[idx].Indent;
            for (var k = idx; k < end; k++)
            {
                covered.Add(k);
                dragged.Add(lines[k] with { Indent = Math.Max(0, lines[k].Indent - baseIndent) });
            }
        }
        var remaining = lines.Where((_, i) => !covered.Contains(i)).ToList();
        ActiveExternalDrag = new(dragged, remaining);

        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(draggingIndices.Length == 1 ? item.Text : $"{draggingIndices.Length} 个大纲项");
    }

    private void Row_DragOver(OutlineRowItem targetRow, DragEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(targetRow.Text))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Support dragging a folder from the Left Directory Tree onto a Center Outline folder row
        if ((draggingIndices is null || draggingIndices.Length == 0) && NamingDirectoryTreeView.ActiveTreeDrag is { } treeDraft)
        {
            var targetDraft = ResolveOutlineRowDirectory(targetRow);
            if (targetDraft is not null && ViewModel.CanMoveCreationDraft(treeDraft, targetDraft))
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                e.DragUIOverride.Caption = $"放入「{targetDraft.Name}」";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = false;
                SetDropTargetHighlight(targetRow, true);
                return;
            }
            e.AcceptedOperation = DataPackageOperation.None;
            SetDropTargetHighlight(targetRow, false);
            return;
        }

        if (draggingIndices is null || draggingIndices.Length == 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var targetIdx = rows.IndexOf(targetRow);
        var lines = SnapshotCurrentLines();
        var dropIntoFolder = !ResolveIsFile(targetRow);
        var preview = NamingViewModel.MoveOutlineSelection(lines, draggingIndices, targetIdx, dropIntoFolder);
        if (preview is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDropTargetHighlight(targetRow, false);
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = dropIntoFolder
            ? $"放入「{targetRow.Text.Trim()}」（{draggingIndices.Length} 项）"
            : $"移动到「{targetRow.Text.Trim()}」下方";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        SetDropTargetHighlight(targetRow, true);
    }

    private void Row_Drop(OutlineRowItem targetRow, DragEventArgs e)
    {
        SetDropTargetHighlight(targetRow, false);

        if ((draggingIndices is null || draggingIndices.Length == 0) && NamingDirectoryTreeView.ActiveTreeDrag is { } treeDraft)
        {
            NamingDirectoryTreeView.ActiveTreeDrag = null;
            var targetDraft = ResolveOutlineRowDirectory(targetRow);
            if (targetDraft is not null)
            {
                targetRow.IsCollapsed = false;
                ViewModel.MoveCreationDraft(treeDraft, targetDraft);
            }
            return;
        }

        var indices = draggingIndices;
        ClearDragState();
        if (indices is null || indices.Length == 0) return;

        var targetIdx = rows.IndexOf(targetRow);
        var lines = SnapshotCurrentLines();
        var dropIntoFolder = !ResolveIsFile(targetRow);
        var updated = NamingViewModel.MoveOutlineSelection(lines, indices, targetIdx, dropIntoFolder);
        if (updated is null) return;

        // Expand target folder if user dropped items into it so they see the result immediately
        if (dropIntoFolder) targetRow.IsCollapsed = false;
        ApplyReorderedLines(updated);
    }

    private void RootDropZone_DragOver(object sender, DragEventArgs e)
    {
        if ((draggingIndices is null || draggingIndices.Length == 0)
            && NamingDirectoryTreeView.ActiveTreeDrag is { } treeDraft
            && ViewModel.CanMoveCreationDraft(treeDraft, ViewModel.CurrentCreationDirectory))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = $"移动到当前目录";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            SetRootDropZoneHighlight(sender, true);
            return;
        }

        if (draggingIndices is null || draggingIndices.Length == 0)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = $"移出到顶层（{draggingIndices.Length} 项）";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        SetRootDropZoneHighlight(sender, true);
    }

    private void RootDropZone_DragLeave(object sender, DragEventArgs e) => SetRootDropZoneHighlight(sender, false);

    private void RootDropZone_Drop(object sender, DragEventArgs e)
    {
        SetRootDropZoneHighlight(sender, false);

        if ((draggingIndices is null || draggingIndices.Length == 0) && NamingDirectoryTreeView.ActiveTreeDrag is { } treeDraft)
        {
            NamingDirectoryTreeView.ActiveTreeDrag = null;
            ViewModel.MoveCreationDraft(treeDraft, ViewModel.CurrentCreationDirectory);
            return;
        }

        var indices = draggingIndices;
        ClearDragState();
        if (indices is null || indices.Length == 0) return;

        var lines = SnapshotCurrentLines();
        var appendToEnd = ReferenceEquals(sender, AppendLineGhostButton);
        var updated = NamingViewModel.MoveOutlineSelectionToRoot(lines, indices, appendToEnd);
        if (updated is not null) ApplyReorderedLines(updated);
    }

    private void SetRootDropZoneHighlight(object sender, bool active)
    {
        if (sender is Control control)
        {
            control.Background = active ? ThemeBrush("WorkspaceAccentSubtleBrush") : new SolidColorBrush(Colors.Transparent);
            control.BorderBrush = active ? ThemeBrush("WorkspaceSuccessBrush") : ThemeBrush("WorkspaceBorderBrush");
        }
    }

    private void ClearDragState()
    {
        draggingIndices = null;
        ActiveExternalDrag = null;
    }

    private void SetDropTargetHighlight(OutlineRowItem row, bool active)
    {
        if (active)
        {
            row.Root.Background = ThemeBrush("WorkspaceAccentSubtleBrush");
            row.Root.BorderBrush = ThemeBrush("WorkspaceSuccessBrush");
        }
        else
        {
            var isSelected = selectedRows.Contains(row);
            row.Root.Background = isSelected ? ThemeBrush("WorkspaceAccentSubtleBrush") : new SolidColorBrush(Colors.Transparent);
            row.Root.BorderBrush = isSelected ? ThemeBrush("WorkspaceBorderBrush") : new SolidColorBrush(Colors.Transparent);
        }
    }

    private List<OutlineDraftLine> SnapshotCurrentLines()
    {
        var list = new List<OutlineDraftLine>(rows.Count + overflowLines.Count);
        foreach (var row in rows)
            list.Add(new(row.Indent, row.Text.Trim(), ResolveIsFile(row)));
        list.AddRange(overflowLines);
        return list;
    }

    private void ApplyReorderedLines(IReadOnlyList<OutlineDraftLine> updated)
    {
        var nonEmpty = updated.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        var error = ViewModel.SyncOutlineDrafts(nonEmpty);
        OutlineErrorBanner.Text = error;
        OutlineErrorBanner.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        ReloadOutlineFromModel();
    }

    // ==================== KEYBOARD EDITING & PASTE ====================
    private void HandleRowKeyDown(OutlineRowItem item, KeyRoutedEventArgs e)
    {
        var idx = rows.IndexOf(item);
        if (idx < 0) return;

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var nextIndent = item.Indent;
            var insertAt = item.IsCollapsed ? FindVisibleSubtreeEnd(idx) : idx + 1;
            AddRowVisual(nextIndent, "", null, insertAt: insertAt, focus: true);
            RefreshAllRowAppearances();
            UpdateOutlineSummary();
            ScheduleSync();
        }
        else if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            if (shift) OutdentRow(item);
            else IndentRow(item);
        }
        else if (e.Key == VirtualKey.Back && item.Input.Text.Length == 0)
        {
            if (item.Indent > 0)
            {
                e.Handled = true;
                OutdentRow(item);
            }
            else if (rows.Count > 1)
            {
                e.Handled = true;
                var prev = idx > 0 ? rows[idx - 1] : rows[idx + 1];
                RemoveRow(item);
                DispatcherQueue.TryEnqueue(() =>
                {
                    prev.Input.Focus(FocusState.Programmatic);
                    prev.Input.SelectionStart = prev.Input.Text.Length;
                });
            }
        }
        else if (e.Key == VirtualKey.Up && idx > 0)
        {
            e.Handled = true;
            for (var p = idx - 1; p >= 0; p--)
            {
                if (rows[p].Root.Visibility == Visibility.Visible)
                {
                    rows[p].Input.Focus(FocusState.Programmatic);
                    rows[p].Input.SelectionStart = rows[p].Input.Text.Length;
                    break;
                }
            }
        }
        else if (e.Key == VirtualKey.Down && idx + 1 < rows.Count)
        {
            e.Handled = true;
            for (var n = idx + 1; n < rows.Count; n++)
            {
                if (rows[n].Root.Visibility == Visibility.Visible)
                {
                    rows[n].Input.Focus(FocusState.Programmatic);
                    rows[n].Input.SelectionStart = rows[n].Input.Text.Length;
                    break;
                }
            }
        }
    }

    private void HandleMultiLinePasteIntoRow(OutlineRowItem targetRow, string rawText)
    {
        var parsed = NamingViewModel.ParseOutlineText(rawText, targetRow.Indent);
        if (parsed.Count == 0) return;

        var idx = rows.IndexOf(targetRow);
        if (idx < 0) return;

        suppressSync = true;
        try
        {
            var first = parsed[0];
            targetRow.Indent = first.Indent;
            targetRow.Text = first.Name;
            targetRow.ManualIsFile = first.ForceIsFile;
            targetRow.Input.Text = first.Name;

            var insertIdx = idx + 1;
            OutlineRowItem lastAdded = targetRow;
            for (var i = 1; i < parsed.Count; i++)
            {
                if (rows.Count >= MaxRenderedOutlineRows)
                {
                    overflowLines.Add(parsed[i]);
                    continue;
                }
                lastAdded = AddRowVisual(parsed[i].Indent, parsed[i].Name, parsed[i].ForceIsFile, insertAt: insertIdx++, focus: false);
            }

            RefreshAllRowAppearances();
            UpdateOutlineSummary();
            DispatcherQueue.TryEnqueue(() =>
            {
                lastAdded.Input.Focus(FocusState.Programmatic);
                lastAdded.Input.SelectionStart = lastAdded.Input.Text.Length;
            });
        }
        finally
        {
            suppressSync = false;
        }
        FlushOutlineToViewModel();
    }

    private void IndentRow(OutlineRowItem item)
    {
        var idx = rows.IndexOf(item);
        if (idx <= 0) return;
        var prev = rows[idx - 1];
        if (string.IsNullOrWhiteSpace(prev.Text)) return;
        var maxIndent = prev.Indent + 1;
        if (item.Indent >= maxIndent) return;
        item.Indent++;
        if (prev.ManualIsFile == true) prev.ManualIsFile = false;
        prev.IsCollapsed = false;
        RefreshAllRowAppearances();
        ScheduleSync();
    }

    private void OutdentRow(OutlineRowItem item)
    {
        if (item.Indent <= 0) return;
        item.Indent--;
        RefreshAllRowAppearances();
        ScheduleSync();
    }

    private void RemoveRow(OutlineRowItem item)
    {
        var idx = rows.IndexOf(item);
        if (idx < 0) return;
        selectedRows.Remove(item);
        if (rows.Count == 1)
        {
            suppressSync = true;
            item.Indent = 0;
            item.Text = "";
            item.ManualIsFile = null;
            item.IsCollapsed = false;
            item.Input.Text = "";
            suppressSync = false;
        }
        else
        {
            rows.RemoveAt(idx);
            OutlineRowsHost.Children.RemoveAt(idx);
            if (ReferenceEquals(activeRow, item)) activeRow = rows[Math.Max(0, idx - 1)];
        }
        RefreshAllRowAppearances();
        UpdateSelectionVisuals();
        UpdateOutlineSummary();
        ScheduleSync();
    }

    // ==================== COLLAPSE / EXPAND & VISUAL SYNC (图四) ====================
    private int FindVisibleSubtreeEnd(int startIndex)
    {
        var baseIndent = rows[startIndex].Indent;
        var end = startIndex + 1;
        while (end < rows.Count && rows[end].Indent > baseIndent) end++;
        return end;
    }

    private int CountSubtreeChildren(int index)
    {
        var baseIndent = rows[index].Indent;
        var count = 0;
        for (var i = index + 1; i < rows.Count && rows[i].Indent > baseIndent; i++)
        {
            if (!string.IsNullOrWhiteSpace(rows[i].Text)) count++;
        }
        return count;
    }

    private bool RowHasChild(int index) =>
        index >= 0 && index + 1 < rows.Count && rows[index + 1].Indent > rows[index].Indent && !string.IsNullOrWhiteSpace(rows[index + 1].Text);

    private bool ResolveIsFile(OutlineRowItem item)
    {
        var idx = rows.IndexOf(item);
        if (RowHasChild(idx)) return false;
        if (item.ManualIsFile is { } explicitFile) return explicitFile;
        return NamingViewModel.LooksLikeFileName(item.Text);
    }

    private void RefreshAllRowAppearances()
    {
        // Compute visibility based on any collapsed ancestor folder
        var collapsedDepth = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (collapsedDepth >= 0 && row.Indent <= collapsedDepth)
                collapsedDepth = -1;

            var hiddenByAncestor = collapsedDepth >= 0;
            row.Root.Visibility = hiddenByAncestor ? Visibility.Collapsed : Visibility.Visible;

            var hasChildren = RowHasChild(i);
            if (!hasChildren) row.IsCollapsed = false;

            UpdateRowAppearance(row, i, hasChildren);

            if (!hiddenByAncestor && hasChildren && row.IsCollapsed)
                collapsedDepth = row.Indent;
        }
    }

    private void UpdateRowAppearance(OutlineRowItem item, int index = -1, bool? hasChildrenOverride = null)
    {
        var idx = index >= 0 ? index : rows.IndexOf(item);
        var hasChildren = hasChildrenOverride ?? RowHasChild(idx);

        var indentPx = item.Indent * 24;
        item.Root.Margin = new Thickness(indentPx, 0, 0, 0);
        item.GuideBar.Visibility = item.Indent > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Show disclosure chevron only when row has nested children (no empty placeholder gap)
        item.CollapseButton.Visibility = hasChildren ? Visibility.Visible : Visibility.Collapsed;
        item.CollapseIcon.Glyph = item.IsCollapsed ? "\uE76C" : "\uE70D";

        var isFile = ResolveIsFile(item);
        item.TypeIcon.Glyph = isFile ? "\uE8A5" : "\uE8B7";
        item.TypeIcon.Foreground = ThemeBrush(isFile ? "WorkspaceCategoryDocumentBrush" : "WorkspaceCategoryFolderBrush");

        var hasText = !string.IsNullOrWhiteSpace(item.Text);
        item.Badge.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (hasText)
        {
            if (isFile) item.BadgeText.Text = "新建文件";
            else if (hasChildren && item.IsCollapsed) item.BadgeText.Text = $"文件夹 · {CountSubtreeChildren(idx)} 项";
            else item.BadgeText.Text = item.Indent > 0 ? "子文件夹" : "文件夹";
        }

        item.GuideBar.Background = ThemeBrush("WorkspaceBorderBrush");
        item.Input.Background = ThemeBrush("WorkspaceSubtleBrush");
        item.Input.BorderBrush = ThemeBrush("OpFieldEdgeBrush");
        item.Badge.Background = ThemeBrush("WorkspaceDiffAddedBackgroundBrush");
        item.BadgeText.Foreground = ThemeBrush("WorkspaceDiffAddedTextBrush");
        item.DeleteButton.Foreground = ThemeBrush("WorkspaceSecondaryBrush");
    }

    private void UpdateOutlineSummary()
    {
        var nonEmptyRows = rows.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var visibleCount = nonEmptyRows.Count(x => x.Root.Visibility == Visibility.Visible);
        var totalCount = nonEmptyRows.Count + overflowLines.Count;

        OutlineListSummary.Text = $"{visibleCount} / {totalCount} 项";

        var selectedCount = selectedRows.Count;
        var summary = selectedCount == 0
            ? $"未勾选 · 当前文件夹及子文件夹共 {totalCount:N0} 项"
            : $"已选 {selectedCount:N0} 项";
        OutlineSelectionSummary.Text = summary;
        ToolTipService.SetToolTip(OutlineSelectionSummary, summary);

        RemoveSelectedOutlineButton.IsEnabled = selectedCount > 0;
        PromoteSelectedOutlineButton.Visibility = selectedRows.Any(r => r.Indent > 0) ? Visibility.Visible : Visibility.Collapsed;

        OutlineOverflowHint.Visibility = overflowLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (overflowLines.Count > 0)
            OutlineOverflowHint.Text = $"另有 {overflowLines.Count:N0} 项已同步到左侧目录树，点击「创建全部」将一并创建。";
    }

    private void ScheduleSync()
    {
        if (suppressSync) return;
        syncTimer.Stop();
        syncTimer.Start();
    }

    private void FlushOutlineToViewModel()
    {
        if (suppressSync || !ViewModel.IsCreate) return;
        var all = new List<OutlineDraftLine>(rows.Count + overflowLines.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Text)) continue;
            all.Add(new(row.Indent, row.Text.Trim(), ResolveIsFile(row)));
        }
        all.AddRange(overflowLines);
        var error = ViewModel.SyncOutlineDrafts(all);
        OutlineErrorBanner.Text = error;
        OutlineErrorBanner.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        UpdateOutlineSummary();
    }

    private Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Gray);

    // ==================== TOOLBAR & TEMPLATE FLYOUT ====================
    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        var insertAt = activeRow is not null ? rows.IndexOf(activeRow) + 1 : rows.Count;
        var indent = activeRow?.Indent ?? 0;
        AddRowVisual(indent, "", null, insertAt: insertAt, focus: true);
        RefreshAllRowAppearances();
        UpdateOutlineSummary();
    }

    private void TemplateFlyout_Opening(object? sender, object e)
    {
        TemplateFlyoutMessage.Visibility = Visibility.Collapsed;
        RebuildTemplateFlyoutList();
        DispatcherQueue.TryEnqueue(() => WorkspaceMotion.AlignSingleLineInputs(NewTemplateNameBox));
    }

    private void RebuildTemplateFlyoutList()
    {
        TemplateListHost.Children.Clear();
        var templates = ViewModel.OutlineTemplates;
        if (templates.Count == 0)
        {
            TemplateListHost.Children.Add(new TextBlock
            {
                Text = "暂无模板，可在下方将当前大纲存为模板。",
                FontSize = 12,
                Foreground = ThemeBrush("WorkspaceSecondaryBrush"),
                Margin = new Thickness(8, 10, 8, 10)
            });
            return;
        }

        foreach (var template in templates)
        {
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelStack = new StackPanel { Spacing = 1 };
            labelStack.Children.Add(new TextBlock
            {
                Text = template.Name,
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrush("WorkspaceTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = template.PreviewSummary,
                FontSize = 11,
                Foreground = ThemeBrush("WorkspaceSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var applyBtn = new Button
            {
                Content = labelStack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5)
            };
            var captured = template;
            applyBtn.Click += (_, _) =>
            {
                ViewModel.AppendOutlinePreset(captured.Lines);
                TemplateFlyout.Hide();
            };
            Grid.SetColumn(applyBtn, 0);

            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 11 },
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Foreground = ThemeBrush("WorkspaceSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(deleteBtn, "删除此模板");
            deleteBtn.Click += (_, _) =>
            {
                if (ViewModel.DeleteOutlineTemplate(captured.Id)) TemplateFlyoutMessage.Visibility = Visibility.Collapsed;
                else { TemplateFlyoutMessage.Text = ViewModel.Feedback; TemplateFlyoutMessage.Visibility = Visibility.Visible; }
                RebuildTemplateFlyoutList();
            };
            Grid.SetColumn(deleteBtn, 1);

            row.Children.Add(applyBtn);
            row.Children.Add(deleteBtn);
            TemplateListHost.Children.Add(row);
        }
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e) => SaveCurrentTemplate();

    private void NewTemplateNameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SaveCurrentTemplate();
    }

    private void SaveCurrentTemplate()
    {
        FlushOutlineToViewModel();
        var error = ViewModel.SaveCurrentOutlineAsTemplate(NewTemplateNameBox.Text);
        if (error.Length > 0)
        {
            TemplateFlyoutMessage.Text = error;
            TemplateFlyoutMessage.Visibility = Visibility.Visible;
            return;
        }
        TemplateFlyoutMessage.Visibility = Visibility.Collapsed;
        NewTemplateNameBox.Text = "";
        RebuildTemplateFlyoutList();
    }

    private void ResetTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ResetDefaultOutlineTemplates()) TemplateFlyoutMessage.Visibility = Visibility.Collapsed;
        else { TemplateFlyoutMessage.Text = ViewModel.Feedback; TemplateFlyoutMessage.Visibility = Visibility.Visible; }
        RebuildTemplateFlyoutList();
    }

    private void ClearOutline_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearCurrentOutlineDrafts();
    }

    private void ToggleExisting_Click(object sender, RoutedEventArgs e)
    {
        var open = ExistingListView.Visibility != Visibility.Visible;
        ExistingListView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ExistingChevron.Glyph = open ? "\uE70D" : "\uE76C";
    }

    private void CreationBreadcrumb_Click(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is CreationBreadcrumb crumb && !ReferenceEquals(crumb.Directory, ViewModel.CurrentCreationDirectory) &&
            ViewModel.CreationBreadcrumbs.Contains(crumb))
            ViewModel.BrowseCreationDirectory(crumb.Directory);
    }

    private void Contents_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CreationContentRow { Draft: { } directory } })
            ViewModel.BrowseCreationDirectory(directory);
    }

    private void Contents_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var pointer = e.TryGetPosition(ContentsSurface, out _);
        var source = pointer ? e.OriginalSource : FocusManager.GetFocusedElement(XamlRoot);
        var row = NamingCreationMenu.TargetAt<CreationContentRow>(source) ?? (pointer ? null : ViewModel.SelectedCreationContent);
        ViewModel.SelectedCreationContent = row;
        NamingCreationMenu.Show(ContentsSurface, e, row);
    }
}
