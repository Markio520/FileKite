using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Tasks;

public sealed record BatchCompletion(CompletionBehavior Behavior, string Summary, IReadOnlyList<string> OutputDirectories);

public partial class ProcessingJobsViewModel : ObservableObject
{
    private int sequence;
    private int unfinishedBatches;
    private readonly List<string> summaries = [];
    private readonly HashSet<string> outputFolders = new(StringComparer.OrdinalIgnoreCase);
    private CompletionBehavior completion;
    public BatchExecutionSlots Slots { get; } = new();
    public event EventHandler<BatchCompletion>? BatchCompleted;

    public void ApplyDefaults(AppSettings? previous, AppSettings current)
    {
        Slots.SetMaximum(current.MaxParallel);
        completion = current.Completion;
    }
    public ObservableCollection<ProcessingJob> Items { get; } = [];
    public int TotalCount => Items.Count;
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !HasItems;
    public int RunningCount => Items.Count(job => job.IsRunning);
    public int FinishedCount => Items.Count - RunningCount;
    public bool HasRunningJobs => RunningCount > 0;
    public bool HasFinishedJobs => FinishedCount > 0;
    public bool IsIdle => RunningCount == 0;
    public bool HasIdleHistory => IsIdle && Items.Count > 0;
    public double OverallProgress => RunningCount == 0 ? 0 : Items.Where(job => job.IsRunning).Average(job => job.Progress);
    public double RingProgress => RunningCount == 0 ? 0 : Math.Clamp(OverallProgress, 8, 100);
    public string Summary => RunningCount > 0
        ? $"{RunningCount} 项正在处理 · 共 {Items.Count} 项任务"
        : Items.Count > 0 ? $"暂无运行中任务 · 共 {Items.Count} 条记录" : "任务空闲";
    public string CompactLabel => RunningCount > 0 ? $"处理中 · {RunningCount} 项" : "任务队列";
    public string CompactPercent => RunningCount > 0 ? $"{Math.Round(OverallProgress):0}%" : string.Empty;

    [RelayCommand(CanExecute = nameof(HasFinishedJobs))]
    public void ClearFinished()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!Items[i].IsRunning) Items.RemoveAt(i);
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyProgress();
    }

    public ProcessingJob Start(Action cancel, string name = "格式转换", bool batch = true)
    {
        var job = new ProcessingJob(++sequence, $"{name} {sequence}", cancel);
        job.PropertyChanged += (_, _) => NotifyProgress();
        if (batch)
        {
            unfinishedBatches++;
            job.Finished += FinishBatch;
        }
        Items.Insert(0, job);
        OnPropertyChanged(nameof(HasItems)); OnPropertyChanged(nameof(IsEmpty)); NotifyProgress();
        return job;
    }
    private void FinishBatch(ProcessingJob job, IEnumerable<string> folders)
    {
        job.Finished -= FinishBatch;
        summaries.Add(job.Name + " · " + job.Status);
        foreach (var folder in folders.Where(folder => !string.IsNullOrWhiteSpace(folder))) outputFolders.Add(folder);
        if (--unfinishedBatches != 0) return;
        var result = new BatchCompletion(completion, string.Join(Environment.NewLine, summaries), outputFolders.ToArray());
        summaries.Clear();
        outputFolders.Clear();
        BatchCompleted?.Invoke(this, result);
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RunningCount)); OnPropertyChanged(nameof(FinishedCount));
        OnPropertyChanged(nameof(HasRunningJobs)); OnPropertyChanged(nameof(HasFinishedJobs));
        OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(HasIdleHistory));
        OnPropertyChanged(nameof(OverallProgress)); OnPropertyChanged(nameof(RingProgress));
        OnPropertyChanged(nameof(CompactLabel)); OnPropertyChanged(nameof(CompactPercent));
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }
}
