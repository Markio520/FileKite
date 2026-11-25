using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using FileAssistant.App.Features.Tasks;
using FileAssistant.Core.Features.Search;
using FileAssistant.Infrastructure.Windows;

namespace FileAssistant.App.Features.Workspace;

public sealed partial class WorkspaceTaskPanel : UserControl
{
    private static readonly WindowsFileInteraction ShellInteraction = new();
    public Tasks.ProcessingJobsViewModel Jobs => App.Jobs;
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WorkspaceViewModel), typeof(WorkspaceTaskPanel), new PropertyMetadata(null));

    public WorkspaceViewModel ViewModel
    {
        get => (WorkspaceViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public event EventHandler? CloseRequested;
    private bool isModal;
    private string filter = "all";
    private ProcessingJob[] shown = [];

    internal Grid RootGrid => PanelRootGrid;

    public void ConfigureModalMode()
    {
        isModal = true;
        PanelRootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        TaskStreamRow.Height = new GridLength(1, GridUnitType.Star);
        HeaderRow.Padding = new Thickness(18, 14, 14, 12);
        TitleText.FontSize = 16;
        FilterBarBorder.Margin = new Thickness(16, 12, 16, 0);
        StreamContainer.Padding = new Thickness(16, 10, 16, 16);
        QueueList.MaxHeight = double.PositiveInfinity;
        QueueList.VerticalAlignment = VerticalAlignment.Stretch;
    }
    public WorkspaceTaskPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Jobs.PropertyChanged += JobsChanged;
            Jobs.Items.CollectionChanged += ItemsCollectionChanged;
            UpdateItems();
        };
        Unloaded += (_, _) =>
        {
            Jobs.PropertyChanged -= JobsChanged;
            Jobs.Items.CollectionChanged -= ItemsCollectionChanged;
        };
    }
    private void JobsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateItems();
    private void ItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateItems();
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        filter = (string)((ToggleButton)sender).Tag;
        AllTasks.IsChecked = filter == "all";
        RunningTasks.IsChecked = filter == "running";
        FinishedTasks.IsChecked = filter == "finished";
        UpdateItems();
    }
    private void UpdateItems()
    {
        var items = Jobs.Items.Where(job => filter == "all" || job.IsRunning == (filter == "running")).ToArray();
        if (!shown.SequenceEqual(items)) { shown = items; QueueList.ItemsSource = items; }
        EmptyFilter.Visibility = Jobs.HasItems && items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string dir } && !string.IsNullOrWhiteSpace(dir))
        {
            try { ShellInteraction.Open(new SearchFile(dir, true, null, null)); }
            catch (Exception error) { App.ReportFailure(error); }
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        if (!isModal && ViewModel is not null) ViewModel.IsQueueExpanded = false;
    }
}
