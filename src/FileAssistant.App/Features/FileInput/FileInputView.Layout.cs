using FileAssistant.App.Features.Workspace;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FileAssistant.App.Features.FileInput;

public sealed partial class FileInputView
{
    private const double MinimumFoldersWidth = 176;
    private const double MaximumFoldersWidth = 360;
    private const double MinimumFilesWidth = 360;
    private const double FilePaneSpacing = Controls.ResizeHandle.PaneSpacing;
    private double? preferredFoldersWidth;
    private string? foldersWidthKey;
    private bool? wasCompact;
    private bool? lastFoldersOpen;

    private double AvailableFoldersWidth => Math.Clamp(InputSurface.ActualWidth - MinimumFilesWidth - FilePaneSpacing, MinimumFoldersWidth, MaximumFoldersWidth);
    private double DesiredFoldersWidth => Math.Clamp(preferredFoldersWidth ?? 194, MinimumFoldersWidth, MaximumFoldersWidth);
    private double CurrentFoldersWidth => Math.Min(DesiredFoldersWidth, AvailableFoldersWidth);

    private void LoadPaneWidths(WorkspaceInputPurpose purpose)
    {
        foldersWidthKey = $"{purpose}.Folders";
        preferredFoldersWidth = App.PaneWidths.GetWidth(foldersWidthKey);
        DetailsHost.WidthPreferenceKey = $"{purpose}.Details";
        UpdateFoldersWidth();
    }

    private void InputSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFoldersWidth();
    private void UpdateFoldersWidth()
    {
        if (FoldersResizeHandle is null) return;
        if (!HasFolderSidebar)
        {
            Controls.WorkspaceMotion.CancelWidthAnimation(this);
            FolderSidebar.Opacity = 1;
            FoldersColumn.Width = new GridLength(0);
            FoldersGapColumn.Width = new GridLength(0);
            ResetFoldersOverlap();
            FolderSidebar.Visibility = CollapsedFolders.Visibility = FoldersResizeHandle.Visibility = Visibility.Collapsed;
            DetailsHost.MinimumContentWidth = MinimumFilesWidth;
            return;
        }

        var open = EffectiveFoldersOpen;
        foldersOpen = open;
        if (FolderDrawerToggle is not null) FolderDrawerToggle.IsChecked = open;

        var toggled = lastFoldersOpen is not null && lastFoldersOpen != open;
        lastFoldersOpen = open;
        var collapsedRailWidth = ShowAlternateContent ? 48d : 0d;
        var rail = open ? DesiredFoldersWidth : collapsedRailWidth;
        DetailsHost.MinimumContentWidth = MinimumFilesWidth + (rail > 0 ? FilePaneSpacing + rail : 0);

        if (toggled && IsLoaded && InputSurface.ActualWidth > 0)
        {
            Controls.WorkspaceMotion.AnimateSidePaneToggle(
                this,
                FoldersColumn,
                FoldersGapColumn,
                FolderSidebar,
                CollapsedFolders,
                FoldersResizeHandle,
                FilesContentHost,
                opening: open,
                expandedWidth: CurrentFoldersWidth,
                collapsedWidth: collapsedRailWidth,
                applyRestingGeometry: () => ApplyFoldersRestingGeometry(open));
            return;
        }

        Controls.WorkspaceMotion.CancelWidthAnimation(this);
        FolderSidebar.Opacity = 1;
        ApplyFoldersRestingGeometry(open);
    }

    private void ResetFoldersOverlap()
    {
        Grid.SetColumn(FolderSidebar, 0);
        Grid.SetColumnSpan(FolderSidebar, 1);
        FolderSidebar.Margin = new Thickness(0);
        Grid.SetColumn(FilesContentHost, 2);
        Grid.SetColumnSpan(FilesContentHost, 1);
        FilesContentHost.Margin = new Thickness(0);
    }

    private void ApplyFoldersRestingGeometry(bool open)
    {
        FolderSidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CollapsedFolders.Visibility = !open && ShowAlternateContent ? Visibility.Visible : Visibility.Collapsed;
        FoldersResizeHandle.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (!open)
        {
            FoldersGapColumn.Width = new GridLength(0);
            ResetFoldersOverlap();
            FoldersColumn.Width = new GridLength(ShowAlternateContent ? 48 : 0);
            return;
        }

        FoldersGapColumn.Width = new GridLength(Controls.ResizeHandle.HitTargetThickness);
        FoldersColumn.Width = new GridLength(Math.Max(0, CurrentFoldersWidth - Controls.ResizeHandle.HitTargetOverlap));
        Grid.SetColumn(FolderSidebar, 0);
        Grid.SetColumnSpan(FolderSidebar, 2);
        FolderSidebar.Margin = new Thickness(0, 0, Controls.ResizeHandle.HitTargetOverlap, 0);
        Grid.SetColumn(FilesContentHost, 1);
        Grid.SetColumnSpan(FilesContentHost, 2);
        FilesContentHost.Margin = new Thickness(Controls.ResizeHandle.HitTargetOverlap, 0, 0, 0);
    }

    private void Folders_ResizeRequested(object? sender, double delta)
    {
        if (FolderSidebar.Visibility != Visibility.Visible || !double.IsFinite(delta)) return;
        var current = CurrentFoldersWidth;
        var width = Math.Clamp(current + delta, MinimumFoldersWidth, AvailableFoldersWidth);
        if (width != current) SetPreferredFoldersWidth(width);
    }

    private void Folders_ResetRequested(object? sender, EventArgs e)
    {
        SetPreferredFoldersWidth(null);
    }

    private void SetPreferredFoldersWidth(double? width)
    {
        preferredFoldersWidth = width;
        UpdateFoldersWidth();
        App.PaneWidths.SetWidth(foldersWidthKey, width);
    }

    private double? preferredListHeight;
    private double AvailableListHeight => Math.Max(0, WorkspaceLayout.ActualHeight - WorkspaceHeader.ActualHeight - WorkspaceLayout.RowSpacing - 12);
    private double ConstrainListHeight(double height) => Math.Clamp(height, Math.Min(320, AvailableListHeight), AvailableListHeight);

    private void ListHeightHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateListHeight();
    private void WorkspaceLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateListHeight();
        UpdateToolbarLayout();
        if (e.NewSize.Width <= 0) return;
        var compact = e.NewSize.Width < 1050;
        if (wasCompact != compact)
        {
            wasCompact = compact;
            SetFoldersOpen(!compact);
        }
    }

    private void ToolbarContent_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateToolbarLayout();

    private void UpdateToolbarLayout()
    {
        if (FilterSearchHost is null || WorkspaceLayout.ActualWidth <= 0) return;
        // Reserve a readable query field before allowing it to move below the actions.
        // The first row retains mode, import and add, including at larger text sizes.
        var wrap = !ShowAlternateContent && WorkspaceLayout.ActualWidth <
            ToolbarLeadingHost.ActualWidth + HeaderActionsHost.ActualWidth + FilterSearch.MinWidth + 48;
        Grid.SetRow(FilterSearchHost, wrap ? 1 : 0);
        Grid.SetColumn(FilterSearchHost, wrap ? 0 : 1);
        Grid.SetColumnSpan(FilterSearchHost, wrap ? 3 : 1);
        FilterSearchHost.Margin = wrap ? new Thickness(0, 8, 0, 0) : new Thickness(0, 0, 16, 0);
        // Once filters wrap, Tab follows the first row's actions before the second row.
        HeaderActionsHost.TabIndex = wrap ? 1 : 2;
        FilterSearchHost.TabIndex = wrap ? 2 : 1;
    }
    private void UpdateListHeight()
    {
        // Keep the default row flexible so a smaller window can remeasure the entire surface.
        var height = preferredListHeight is { } preferred ? ConstrainListHeight(preferred) : AvailableListHeight;
        ListHeightRow.Height = new GridLength(Math.Max(1, height), GridUnitType.Star);
        ListRemainderRow.Height = new GridLength(Math.Max(0, AvailableListHeight - height), GridUnitType.Star);
    }

    private void ListHeight_ResizeRequested(object? sender, double delta)
    {
        if (!double.IsFinite(delta)) return;
        preferredListHeight = ConstrainListHeight(ConstrainListHeight(preferredListHeight ?? AvailableListHeight) + delta);
        UpdateListHeight();
    }

    private void ListHeight_Reset(object sender, DoubleTappedRoutedEventArgs e)
    {
        preferredListHeight = null;
        UpdateListHeight();
        e.Handled = true;
    }
}
