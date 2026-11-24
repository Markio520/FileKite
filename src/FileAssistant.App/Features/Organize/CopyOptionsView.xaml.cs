using System.ComponentModel;
using FileAssistant.App.Features.FileInput;
using FileAssistant.Core.Features.Organize;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FileAssistant.App.Features.Organize;

public sealed partial class CopyOptionsView : UserControl
{
    public CopyViewModel ViewModel => App.Copy;
    public event EventHandler<FileInputTransferEventArgs>? FilesForNamingRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForConversionRequested;
    private readonly DispatcherQueueTimer timer;
    private bool active, pending;

    public CopyOptionsView()
    {
        InitializeComponent();
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(360);
        timer.IsRepeating = false;
        timer.Tick += Preview_Tick;
        ActualThemeChanged += (_, _) => UpdateLayoutCardSurfaces();
        Loaded += (_, _) =>
        {
            AttachInteractiveMotion(this);
            UpdateLayoutCardSurfaces();
            ViewModel.PlanInvalidated += Invalidated;
            ViewModel.PropertyChanged += Changed;
            Request();
        };
        Unloaded += (_, _) =>
        {
            timer.Stop();
            ViewModel.PlanInvalidated -= Invalidated;
            ViewModel.PropertyChanged -= Changed;
        };
    }

    private static void AttachInteractiveMotion(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase btn) Controls.WorkspaceMotion.AttachTactilePress(btn, 0.97f);
            else AttachInteractiveMotion(child);
        }
    }

    private void UpdateLayoutCardSurfaces()
    {
        if (!IsLoaded) return;
        var choiceBg = LookupBrush("OpChoiceBrush");
        var borderBrush = LookupBrush("WorkspaceBorderBrush");
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        ApplyCard(CardPreserve, ViewModel.IsLayoutPreserve);
        ApplyCard(CardGather, ViewModel.IsLayoutFlat);
        ApplyCard(CardClassify, ViewModel.IsLayoutClassify);
        ApplyCard(CardSkeleton, ViewModel.IsLayoutSkeleton);

        void ApplyCard(Border card, bool selected)
        {
            card.Background = selected ? choiceBg : transparent;
            card.BorderBrush = selected ? borderBrush : transparent;
        }
    }

    private Brush LookupBrush(string key)
    {
        var themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themed) && themed is ResourceDictionary td && td.TryGetValue(key, out var b) && b is Brush brush)
                return brush;
            if (dict.TryGetValue(key, out var direct) && direct is Brush directBrush)
                return directBrush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public void SetActive(bool value)
    {
        active = value;
        if (value && ViewModel.Plan is null) Request();
        else if (!value) timer.Stop();
    }

    private void Invalidated(object? sender, EventArgs e) => Request();

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CopyViewModel.LayoutValue) or nameof(CopyViewModel.IsLayoutPreserve) or nameof(CopyViewModel.IsLayoutFlat) or nameof(CopyViewModel.IsLayoutClassify) or nameof(CopyViewModel.IsLayoutSkeleton))
            UpdateLayoutCardSurfaces();
        if (e.PropertyName == nameof(CopyViewModel.PanelTitle))
        {
            Panel.ScrollToTop();
            if (ViewModel.ShowSettings && ViewModel.Plan is null) Request();
        }
        if (e.PropertyName == nameof(CopyViewModel.IsBusy) && !ViewModel.IsBusy && pending) Request();
        if (e.PropertyName == nameof(CopyViewModel.HasPendingItems) && ViewModel.HasPendingItems) Panel.ScrollToTop();
    }

    private void Request()
    {
        pending = true;
        if (active && IsLoaded) { timer.Stop(); timer.Start(); }
    }

    private async void Preview_Tick(DispatcherQueueTimer sender, object e)
    {
        if (!active || !IsLoaded || !pending || ViewModel.IsBusy || ViewModel.BuildPlanCommand.IsRunning) return;
        pending = false;
        if (ViewModel.Plan is not null) return;
        if (ViewModel.CanBuild) await ViewModel.BuildPlanCommand.ExecuteAsync(null);
    }

    private void SelectCopyOperation_Click(object sender, RoutedEventArgs e) => ViewModel.SetOperation(CopyOperation.Copy);
    private void SelectMoveOperation_Click(object sender, RoutedEventArgs e) => ViewModel.SetOperation(CopyOperation.Move);

    private void LayoutChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } model && sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var layout))
        {
            // Preserve legacy Extension default until explicitly changed by user.
            if (layout == 3 && model.LayoutValue == 2) return;
            model.LayoutValue = layout;
        }
    }

    private void GatherFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<GatherFilter>(tag, out var filter))
            ViewModel.SelectGatherFilter(filter);
    }

    private void ClassifyBy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<ClassifyPrimary>(tag, out var primary))
            ViewModel.SelectClassifyBy(primary);
    }

    private void SubDateLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<ClassifyDateGranularity>(tag, out var layer))
            ViewModel.SelectSubDateLayer(layer);
    }

    private void DateGranularity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<ClassifyDateGranularity>(tag, out var granularity))
            ViewModel.SelectDateGranularity(granularity);
    }

    private void NameMatchRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && Enum.TryParse<NameMatchRule>(tag, out var rule))
            ViewModel.SelectNameMatchRule(rule);
    }

    private async void PickDestination_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await FileInputSources.PickFolderAsync();
            if (paths.Count > 0) ViewModel.Destination = paths[0];
        }
        catch (Exception error) { ViewModel.Feedback = "无法选择目录，也可直接填写：" + error.Message; }
    }

    private async void Decide_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPlanItem is { } item && Enum.TryParse<CopyDecision>((string)((Button)sender).Tag, out var decision))
            await ViewModel.DecideAsync(item.Id, decision);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is CopyRecovery record) await ViewModel.RecoverAsync(record.Id, false);
        else ViewModel.Feedback = "请先选择一条恢复或撤回记录。";
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (RecoveryList.SelectedItem is not CopyRecovery record) { ViewModel.Feedback = "请先选择一条恢复或撤回记录。"; return; }
        var isMoveUndo = record.IsMoveUndo;
        var dialog = Controls.DialogDismiss.Attach(new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = isMoveUndo ? "移除这条移动撤回记录？" : "删除这份覆盖备份？",
            Content = isMoveUndo
                ? $"移除后将不再显示此批次的一键撤回入口，已移动到目标目录的文件保持不变。\n目标目录：{record.Target}"
                : $"删除后无法通过此记录恢复旧目标，当前目标文件保留。\n{record.Backup}",
            PrimaryButtonText = isMoveUndo ? "移除记录" : "删除备份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        });
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RecoverAsync(record.Id, true);
    }

    private void HandoffToNaming_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel.OutputFiles;
        if (files.Count > 0) FilesForNamingRequested?.Invoke(this, new(files));
    }

    private void HandoffToConversion_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel.OutputFiles;
        if (files.Count > 0) FilesForConversionRequested?.Invoke(this, new(files));
    }

    private async void HandoffToStaging_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel.OutputFiles;
        if (files.Count == 0) return;
        try
        {
            var bundle = Core.Features.FileCollections.FileCollectionBundle.FromFiles(files);
            var report = await FileCollectionDialogs.ImportAsync(App.Workspace, bundle, this);
            if (report is not null) ViewModel.Feedback = $"新文件已加入暂存区：{report.Summary}";
        }
        catch (Exception) { ViewModel.Feedback = "无法打开暂存区目录选择器，请稍后重试。"; }
    }
}

/// <summary>Wraps progressive sub-option chips onto the next line cleanly when the right pane is narrow.</summary>
public sealed partial class OrganizeWrapPanel : Panel
{
    public double HorizontalGap { get; set; } = 5;
    public double VerticalGap { get; set; } = 5;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new(availableSize.Width, double.PositiveInfinity));
        return LayoutChildren(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutChildren(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size LayoutChildren(double maxWidth, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, maxUsedWidth = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var w = Math.Min(maxWidth, child.DesiredSize.Width);
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > maxWidth)
            {
                x = 0;
                y += rowHeight + VerticalGap;
                rowHeight = 0;
            }
            if (arrange) child.Arrange(new(x, y, w, h));
            maxUsedWidth = Math.Max(maxUsedWidth, x + w);
            rowHeight = Math.Max(rowHeight, h);
            x += w + HorizontalGap;
        }
        return new(maxUsedWidth, y + rowHeight);
    }
}
