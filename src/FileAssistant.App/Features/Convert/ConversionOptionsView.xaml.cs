using FileAssistant.App.Features.FileInput;
using FileAssistant.App.Controls;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.App.Features.Convert;

public sealed partial class ConversionOptionsView : UserControl
{
    private readonly DispatcherQueueTimer previewTimer;
    private ConversionViewModel? observedModel;
    private bool previewActive;
    private bool previewPending;
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel), typeof(ConversionViewModel),
        typeof(ConversionOptionsView), new PropertyMetadata(null, (owner, _) =>
        {
            var view = (ConversionOptionsView)owner;
            view.Bindings.Update();
            view.AttachPreviewModel();
        }));
    public ConversionViewModel ViewModel { get => (ConversionViewModel)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    public event EventHandler<FileInputTransferEventArgs>? FilesForNamingRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForCopyRequested;
    public ConversionOptionsView()
    {
        InitializeComponent();
        previewTimer = DispatcherQueue.CreateTimer();
        previewTimer.Interval = TimeSpan.FromMilliseconds(400);
        previewTimer.IsRepeating = false;
        previewTimer.Tick += PreviewTimer_Tick;
        Loaded += (_, _) => { AttachInteractiveMotion(this); AttachPreviewModel(); };
        Unloaded += (_, _) => { previewTimer.Stop(); DetachPreviewModel(); };
    }

    private static void AttachInteractiveMotion(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase btn) WorkspaceMotion.AttachTactilePress(btn, 0.97f);
            else AttachInteractiveMotion(child);
        }
    }

    public void SetPreviewActive(bool active)
    {
        previewActive = active;
        if (active && ViewModel?.Plan is null) RequestPreview();
        else if (!active) previewTimer.Stop();
    }

    private void AttachPreviewModel()
    {
        if (!IsLoaded || ReferenceEquals(observedModel, ViewModel)) return;
        DetachPreviewModel();
        observedModel = ViewModel;
        if (observedModel is null) return;
        observedModel.PlanInvalidated += PlanInvalidated;
        observedModel.PropertyChanged += PreviewModelChanged;
        if (observedModel.Plan is null) RequestPreview();
    }
    private void DetachPreviewModel()
    {
        if (observedModel is null) return;
        observedModel.PlanInvalidated -= PlanInvalidated;
        observedModel.PropertyChanged -= PreviewModelChanged;
        observedModel = null;
    }
    private void PlanInvalidated(object? sender, EventArgs e) => RequestPreview();
    private void PreviewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConversionViewModel.IsExecuting) or nameof(ConversionViewModel.ShowResultPanel))
            Panel.ScrollToTop();
        if (e.PropertyName != nameof(ConversionViewModel.IsBusy) || ViewModel.IsBusy || !previewPending) return;
        if (ViewModel.Plan is null) RequestPreview();
        else { previewPending = false; previewTimer.Stop(); }
    }

    private void ToggleAdvanced_Click(object sender, RoutedEventArgs e)
    {
        var open = AdvancedBody.Visibility != Visibility.Visible;
        AdvancedBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedChevron.Glyph = open ? "\uE70D" : "\uE76C";
        if (open)
        {
            WorkspaceMotion.AlignSingleLineInputs(AdvancedBody);
            WorkspaceMotion.AnimateFadeIn(AdvancedBody, 160);
        }
    }
    private void RequestPreview()
    {
        previewPending = true;
        if (!previewActive || !IsLoaded) return;
        previewTimer.Stop();
        previewTimer.Start();
    }
    private async void PreviewTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!previewActive || !IsLoaded || ViewModel is null || !previewPending) return;
        if (ViewModel.IsBusy || ViewModel.BuildPlanCommand.IsRunning) return;
        previewPending = false;
        if (ViewModel.CanBuildPlan) await ViewModel.BuildPlanCommand.ExecuteAsync(null);
    }
    private void ScenarioPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: string preset })
            ViewModel?.ApplyScenarioPreset(preset);
    }

    private async void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await FileInputSources.PickFolderAsync();
            if (paths.Count > 0) ViewModel.OutputDirectory = paths[0];
        }
        catch (Exception) { ViewModel.Feedback = "无法打开目录选择器，也可以直接填写输出文件夹。"; }
    }

    private void HandoffToNaming_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel?.OutputFiles;
        if (files is { Count: > 0 }) FilesForNamingRequested?.Invoke(this, new(files));
    }

    private void HandoffToCopy_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel?.OutputFiles;
        if (files is { Count: > 0 }) FilesForCopyRequested?.Invoke(this, new(files));
    }

    private async void HandoffToStaging_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model) return;
        var files = model.OutputFiles;
        if (files.Count == 0) return;
        try
        {
            var bundle = Core.Features.FileCollections.FileCollectionBundle.FromFiles(files);
            var report = await FileCollectionDialogs.ImportAsync(App.Workspace, bundle, this);
            if (report is not null) model.Feedback = $"新文件已加入暂存区：{report.Summary}";
        }
        catch (Exception) { model.Feedback = "无法打开暂存区目录选择器，请稍后重试。"; }
    }
}
