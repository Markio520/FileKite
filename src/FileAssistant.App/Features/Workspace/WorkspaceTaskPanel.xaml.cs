using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using FileAssistant.App.Features.Tasks;

namespace FileAssistant.App.Features.Workspace;

public sealed partial class WorkspaceTaskPanel : UserControl
{
    public Tasks.ProcessingJobsViewModel Jobs => App.Jobs;
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(WorkspaceViewModel), typeof(WorkspaceTaskPanel), new PropertyMetadata(null));

    public WorkspaceViewModel ViewModel
    {
        get => (WorkspaceViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private string filter = "all";
    private ProcessingJob[] shown = [];
    public WorkspaceTaskPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { Jobs.PropertyChanged += JobsChanged; UpdateItems(); };
        Unloaded += (_, _) => Jobs.PropertyChanged -= JobsChanged;
    }
    private void JobsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateItems();
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
    private void Close_Click(object sender, RoutedEventArgs e) => ViewModel.IsQueueExpanded = false;
}
