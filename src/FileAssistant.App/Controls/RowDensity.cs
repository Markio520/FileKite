using System;
using System.Runtime.CompilerServices;
using FileAssistant.App.Resources;
using FileAssistant.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FileAssistant.App.Controls;

public sealed class RowDensity : DependencyObject
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled",
        typeof(bool), typeof(RowDensity), new PropertyMetadata(false, Changed));
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    private static readonly ConditionalWeakTable<ListViewItem, Row> rows = new();

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListViewItem item) return;
        var row = rows.GetValue(item, control => new Row(control));
        if ((bool)args.NewValue) { item.Loaded += row.Loaded; item.Unloaded += row.Unloaded; if (item.IsLoaded) row.Loaded(item, null!); }
        else { item.Loaded -= row.Loaded; item.Unloaded -= row.Unloaded; row.Unloaded(item, null!); }
    }

    private sealed class Row(ListViewItem item)
    {
        private ListDensity? applied;
        private double normalHeight;
        private Thickness normalPadding;

        public void Loaded(object sender, RoutedEventArgs e)
        {
            normalHeight = item.MinHeight;
            normalPadding = item.Padding;
            item.ActualThemeChanged -= ThemeChanged;
            item.ActualThemeChanged += ThemeChanged;
            App.Preferences.Changed -= PreferenceChanged;
            App.Preferences.Changed += PreferenceChanged;
            PreferenceChanged(null, EventArgs.Empty);
            ApplySelectionBrushes();
        }

        public void Unloaded(object sender, RoutedEventArgs e)
        {
            item.ActualThemeChanged -= ThemeChanged;
            App.Preferences.Changed -= PreferenceChanged;
            item.ClearValue(FrameworkElement.MinHeightProperty);
            item.ClearValue(Control.PaddingProperty);
            applied = null;
        }

        private void ThemeChanged(FrameworkElement sender, object args) => ApplySelectionBrushes();

        private void PreferenceChanged(object? sender, EventArgs e)
        {
            var density = App.Preferences.Current.Density;
            if (applied != density)
            {
                applied = density;
                var delta = density == ListDensity.Compact ? -8 : density == ListDensity.Spacious ? 10 : 0;
                item.MinHeight = Math.Max(24, normalHeight + delta);
                var p = normalPadding;
                item.Padding = new(p.Left, Math.Max(0, p.Top + delta / 3d), p.Right, Math.Max(0, p.Bottom + delta / 3d));
            }
            ApplySelectionBrushes();
        }

        private void ApplySelectionBrushes()
        {
            var presenter = FindPresenter(item);
            if (presenter is null) return;
            var theme = WorkspaceMaterials.IsActiveLightTheme(App.Preferences.Current) ? ElementTheme.Light : ElementTheme.Dark;
            presenter.PointerOverBackground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemBackgroundPointerOver");
            presenter.PressedBackground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemBackgroundPressed");
            presenter.SelectedBackground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemBackgroundSelected");
            presenter.SelectedPointerOverBackground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemBackgroundSelectedPointerOver");
            presenter.SelectedPressedBackground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemBackgroundSelectedPressed");
            presenter.PointerOverForeground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemForegroundPointerOver");
            presenter.SelectedForeground = WorkspaceMaterials.ThemeBrush(theme, "ListViewItemForegroundSelected");
            presenter.SelectionIndicatorBrush = WorkspaceMaterials.ThemeBrush(theme, "SelectionIndicatorBrush");
            presenter.SelectionIndicatorPointerOverBrush = WorkspaceMaterials.ThemeBrush(theme, "SelectionIndicatorBrush");
            presenter.SelectionIndicatorPressedBrush = WorkspaceMaterials.ThemeBrush(theme, "SelectionIndicatorBrush");
            presenter.CheckBrush = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceOnAccentBrush");
            presenter.CheckPressedBrush = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceOnAccentBrush");
            presenter.CheckBoxSelectedBrush = WorkspaceMaterials.ThemeBrush(theme, "WorkspaceAccentBrush");
        }

        private static ListViewItemPresenter? FindPresenter(DependencyObject root)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ListViewItemPresenter p) return p;
                if (FindPresenter(child) is { } nested) return nested;
            }
            return null;
        }
    }
}
