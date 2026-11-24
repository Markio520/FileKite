using System.ComponentModel;
using FileAssistant.App.Features.FileInput;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed partial class NamingOptionsView : UserControl
{
    public NamingViewModel ViewModel => App.Naming;
    public event EventHandler<FileInputTransferEventArgs>? FilesForConversionRequested;
    public event EventHandler<FileInputTransferEventArgs>? FilesForCopyRequested;
    public NamingOptionsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AttachInteractiveMotion(this);
            Controls.WorkspaceMotion.AlignSingleLineInputs(this);
            ViewModel.PropertyChanged += SettingsChanged;
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= SettingsChanged;
    }

    private static void AttachInteractiveMotion(DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase btn) Controls.WorkspaceMotion.AttachTactilePress(btn, 0.968f);
            else AttachInteractiveMotion(child);
        }
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || !int.TryParse(button.Tag?.ToString(), out var tab)) return;
        ViewModel.SelectRenameTab(tab);
        button.IsChecked = true;
        DispatcherQueue.TryEnqueue(() => Controls.WorkspaceMotion.AlignSingleLineInputs(this));
    }

    private void CleanSubMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || !int.TryParse(button.Tag?.ToString(), out var action)) return;
        ViewModel.SelectCleanSubMode(action);
        button.IsChecked = true;
        DispatcherQueue.TryEnqueue(() => Controls.WorkspaceMotion.AlignSingleLineInputs(this));
    }

    private void UniformSubMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || !int.TryParse(button.Tag?.ToString(), out var action)) return;
        ViewModel.SelectUniformSubMode(action);
        button.IsChecked = true;
        DispatcherQueue.TryEnqueue(() => Controls.WorkspaceMotion.AlignSingleLineInputs(this));
    }

    private void ToggleNumberingTuning_Click(object sender, RoutedEventArgs e)
    {
        var open = NumberingTuningBody.Visibility != Visibility.Visible;
        NumberingTuningBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NumberingTuningChevron.Glyph = open ? "\uE70D" : "\uE76C";
        if (open)
        {
            Controls.WorkspaceMotion.AlignSingleLineInputs(NumberingTuningBody);
            Controls.WorkspaceMotion.AnimateFadeIn(NumberingTuningBody, 160);
        }
    }

    private void ToggleComboCleaning_Click(object sender, RoutedEventArgs e)
    {
        var open = ComboCleaningBody.Visibility != Visibility.Visible;
        ComboCleaningBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ComboCleaningChevron.Glyph = open ? "\uE70D" : "\uE76C";
        if (open)
        {
            Controls.WorkspaceMotion.AlignSingleLineInputs(ComboCleaningBody);
            Controls.WorkspaceMotion.AnimateFadeIn(ComboCleaningBody, 160);
        }
    }

    private void ToggleFileTemplate_Click(object sender, RoutedEventArgs e)
    {
        var open = FileTemplateBody.Visibility != Visibility.Visible;
        FileTemplateBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FileTemplateChevron.Glyph = open ? "\uE70D" : "\uE76C";
        if (open)
        {
            Controls.WorkspaceMotion.AlignSingleLineInputs(FileTemplateBody);
            Controls.WorkspaceMotion.AnimateFadeIn(FileTemplateBody, 160);
        }
    }

    private async void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        var open = InlineHistoryBody.Visibility != Visibility.Visible;
        InlineHistoryBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        InlineHistoryChevron.Glyph = open ? "\uE70D" : "\uE76C";
        if (open)
        {
            Controls.WorkspaceMotion.AnimateFadeIn(InlineHistoryBody, 160);
            await ViewModel.RefreshRecoveriesCommand.ExecuteAsync(null);
        }
    }

    private void CreationKind_Click(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        ViewModel.KindValue = int.Parse(button.Tag.ToString()!);
        button.IsChecked = true;
    }

    private void InsertNumberedToOutline_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AppendNumberedToOutline(ViewModel.CreateBaseName, ViewModel.Count, ViewModel.IsFileCreation);
    }

    private void QuickRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase { Tag: string recipe }) return;
        if (recipe == "replaceDraft")
        {
            ViewModel.SelectCleanSubMode(1);
            if (string.IsNullOrEmpty(ViewModel.Find)) ViewModel.Find = "初稿";
            if (string.IsNullOrEmpty(ViewModel.Replacement)) ViewModel.Replacement = "定稿";
        }
    }

    private void InsertToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var current = ViewModel.RenamePattern;
        var needsJoin = token.StartsWith('{') && current.Length > 0 && !current.EndsWith('_') && !current.EndsWith('-') && !current.EndsWith(' ');
        ViewModel.RenamePattern = current + (needsJoin ? "_" : "") + token;
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(NamingViewModel.PanelValue) or nameof(NamingViewModel.MoreSection) or nameof(NamingViewModel.IsExecuting) or nameof(NamingViewModel.ModeValue))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded) return;
            OptionsPanel.ScrollToTop();
            Controls.WorkspaceMotion.AlignSingleLineInputs(this);
            if (e.PropertyName == nameof(NamingViewModel.PanelValue) && ViewModel.ShowPanelBack) SettingsBack.Focus(FocusState.Programmatic);
        });
    }

    private async void PickDestination_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await FileInputSources.PickFolderAsync();
            if (paths.Count > 0) ViewModel.Destination = paths[0];
        }
        catch (Exception error) { ViewModel.Feedback = "无法选择文件夹，请重试：" + error.Message; }
    }

    private async void PickTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var paths = await FileInputSources.PickFilesAsync();
            if (paths.Count == 1) { ViewModel.Template = paths[0]; ViewModel.FileContentValue = 1; }
            else if (paths.Count > 1) ViewModel.Feedback = "请选择一个模板文件。";
        }
        catch (Exception error) { ViewModel.Feedback = error.Message; }
    }

    private void HandoffToConversion_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel.OutputFiles;
        if (files.Count > 0) FilesForConversionRequested?.Invoke(this, new(files));
    }

    private void HandoffToCopy_Click(object sender, RoutedEventArgs e)
    {
        var files = ViewModel.OutputFiles;
        if (files.Count > 0) FilesForCopyRequested?.Invoke(this, new(files));
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
