using FileAssistant.App.Features.Organize;
using FileAssistant.App.Features.Outputs;
using FileAssistant.App.Features.Tasks;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Tests;

public sealed class CopyWorkflowTests
{
    private readonly WorkspaceViewModel input = new(null, WorkspaceInputPurpose.Copy);
    private readonly OutputsViewModel outputs = new(new Interaction());
    private readonly ProcessingJobsViewModel jobs = new();
    private readonly FakeCopy service = new();
    private CopyViewModel Model() => new(service, input, outputs, jobs) { Destination = @"D:\out" };
    private void Add() => input.AddSearchFiles([new(@"C:\a.txt", false, 10, null), new(@"C:\b.txt", false, 20, null)]);

    [Fact]
    public async Task Inline_targets_distinguish_same_names_and_survive_filtering()
    {
        var vm = Model();
        input.AddSearchFiles([new(@"C:\one\same.txt", false, 10, null), new(@"C:\two\same.txt", false, 20, null)]);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = vm.Plan!;
        vm.Plan = plan with { Items = [plan.Items[0], plan.Items[1] with { Target = @"D:\out\same (2).txt" }] };
        Assert.Equal("→ same.txt", input.VisibleFiles.Single(file => file.FullPath == plan.Items[0].Source.Path).NamePreview.Text);
        Assert.Equal("→ same (2).txt", input.VisibleFiles.Single(file => file.FullPath == plan.Items[1].Source.Path).NamePreview.Text);
        input.SearchText = "no-match"; Assert.Empty(input.VisibleFiles); input.SearchText = "";
        Assert.Contains(@"D:\out\same (2).txt", input.VisibleFiles.Single(file => file.FullPath == plan.Items[1].Source.Path).NamePreview.Details);
        vm.Destination = @"D:\different";
        Assert.All(input.VisibleFiles, file => Assert.False(file.NamePreview.IsVisible));
    }

    [Fact]
    public async Task Retry_keeps_successful_results_and_outputs_in_the_original_batch()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = vm.Plan!;
        var actual = new ProducedFile(@"D:\out\a (2).txt", 10, DateTimeOffset.UtcNow, [plan.Items[0].Source.Path]);
        service.PendingExecution = Task.FromResult(new CopyReport(plan,
            [new(plan.Items[0], CopyOutcome.Success, "done", actual), new(plan.Items[1], CopyOutcome.Failed, "locked")], false));
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.ShowResults); Assert.False(vm.ShowSettings);
        Assert.Equal("→ a (2).txt", input.VisibleFiles.Single(file => file.FullPath == plan.Items[0].Source.Path).NamePreview.Text);
        var batch = 0;
        vm.OutputsRequested += (_, id) => batch = id;
        vm.ShowOutputsCommand.Execute(null);
        var originalBatch = batch;
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.True(vm.ShowSettings); Assert.False(vm.ShowResults);
        Assert.Contains("仅重试未完成项", vm.ScopeSummary);
        Assert.Single(vm.Plan!.Items);
        service.PendingExecution = null;
        await vm.RunCommand.ExecuteAsync(null);
        vm.ShowOutputsCommand.Execute(null);
        Assert.Equal(originalBatch, batch);
        outputs.TaskId = batch;
        Assert.Equal(2, outputs.VisibleFiles.Count);
        Assert.False(vm.CanRetry); Assert.True(vm.ShowResults);
        Assert.Equal("→ a (2).txt", input.VisibleFiles.Single(file => file.FullPath == plan.Items[0].Source.Path).NamePreview.Text);
        Assert.All(input.VisibleFiles, file => Assert.Equal("已复制", file.NamePreview.Status));
        vm.ContinueEditingCommand.Execute(null);
        Assert.True(vm.ShowSettings); Assert.All(input.VisibleFiles, file => Assert.False(file.NamePreview.IsVisible));
    }

    [Fact]
    public async Task Invalidating_or_completing_a_plan_clears_its_displayed_items_and_paths()
    {
        var vm = Model(); Add();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            changed.Add(e.PropertyName);
            // Replacing ItemsSource clears the native ListView's two-way selection.
            if (e.PropertyName == nameof(CopyViewModel.PlanItems)) vm.SelectedPlanItem = null;
        };
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.PlanItems.Count); Assert.NotEmpty(vm.SelectedPlanPaths);
        changed.Clear(); vm.Destination = @"D:\different";
        Assert.Empty(vm.PlanItems); Assert.Empty(vm.SelectedPlanPaths);
        Assert.Contains(nameof(CopyViewModel.PlanItems), changed);
        Assert.Contains(nameof(CopyViewModel.SelectedPlanPaths), changed);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Empty(vm.PlanItems); Assert.Empty(vm.SelectedPlanPaths);
        Assert.NotNull(vm.Report); Assert.Equal(2, outputs.VisibleFiles.Count);
    }
    [Fact]
    public async Task Filtering_preserves_hidden_selection_and_the_fixed_plan()
    {
        var vm = Model(); Add(); input.SetSelection([input.VisibleFiles[0]]);
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan;
        // Native selection removal events during a collection refresh must be ignored.
        input.VisibleFiles.CollectionChanged += (_, _) => input.SetVisibleSelection([]);
        input.SearchText = "b.txt";
        Assert.Equal(0, input.VisibleSelectedCount); Assert.Single(input.EffectiveEntries);
        Assert.Equal(@"C:\a.txt", input.EffectiveEntries[0].FullPath); Assert.Same(plan, vm.Plan);
        Assert.Contains("当前可见 0", vm.ScopeSummary);
        input.SetVisibleSelection([input.VisibleFiles[0]]);
        Assert.Equal(2, input.SelectionCount);
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, input.SelectionCount); Assert.Equal(2, input.EffectiveEntries.Count);
    }
    [Fact]
    public async Task Cancelled_or_outdated_scan_cannot_become_an_executable_plan()
    {
        var vm = Model(); Add();
        var pending = new TaskCompletionSource<CopyPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PendingPlan = pending.Task;
        var building = vm.BuildPlanCommand.ExecuteAsync(null);
        vm.Destination = @"D:\different";
        pending.SetResult(FakeCopy.Plan(service.Request!));
        await building; Assert.Null(vm.Plan); Assert.False(vm.CanRun); Assert.Empty(jobs.Items);
    }
    [Fact]
    public async Task Running_copy_uses_its_snapshot_and_only_records_successful_outputs()
    {
        var vm = Model(); Add(); input.SetSelection([input.VisibleFiles[0]]);
        await vm.BuildPlanCommand.ExecuteAsync(null); var plan = vm.Plan!;
        var pending = new TaskCompletionSource<CopyReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PendingExecution = pending.Task;
        var running = vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.ShowProgress); Assert.False(vm.ShowSettings); Assert.False(vm.ShowResults);
        input.SetSelection([]); input.AddSearchFiles([new(@"C:\later.txt", false, 10, null)]);
        Assert.Same(plan, service.Executed); Assert.Same(plan, vm.Plan);
        Assert.Contains("固定计划", vm.ScopeSummary);
        pending.SetResult(FakeCopy.Report(plan)); await running;
        Assert.Single(outputs.VisibleFiles); Assert.Equal(3, input.SearchEntries.Count);
        var job = Assert.Single(jobs.Items); Assert.StartsWith("批量复制", job.Name); Assert.False(job.IsRunning);
        var status = job.Status; job.Update(new(0, 1, "late", 1, 10)); Assert.Equal(status, job.Status);
        Assert.Null(vm.Plan); Assert.False(vm.CanRun);
        Assert.False(vm.ShowProgress); Assert.True(vm.ShowResults);
    }
    [Fact]
    public async Task Retry_builds_a_reviewable_plan_without_repeating_successes_or_explicit_skips()
    {
        var vm = Model(); Add(); await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = vm.Plan!;
        service.PendingExecution = Task.FromResult(new CopyReport(plan,
            [new(plan.Items[0], CopyOutcome.Skipped, "user skip"), new(plan.Items[1], CopyOutcome.Failed, "locked")], false));
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.CanRetry);
        vm.Destination = @"D:\later-choice";
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.Equal(@"D:\out", vm.Destination);
        Assert.Single(vm.Plan!.Items); Assert.True(vm.CanRun); Assert.Single(jobs.Items);
        service.PendingExecution = null;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Single(outputs.VisibleFiles); Assert.False(vm.CanRetry); Assert.Equal(2, vm.Report!.Items.Count);
        Assert.Equal(CopyOutcome.Skipped, vm.Report.Items[0].Outcome);
    }

    [Fact]
    public async Task Destination_never_flips_copy_to_move_and_skeleton_respects_operation_mode()
    {
        var vm = Model();
        Add();
        Assert.Equal(CopyOperation.Copy, vm.Operation);
        Assert.True(vm.IsCopyOperation);
        Assert.Equal("开始复制", vm.PrimaryActionText);

        // Changing destination to same drive or another folder NEVER silently switches Copy to Move.
        vm.Destination = @"C:\same-drive-target";
        Assert.Equal(CopyOperation.Copy, vm.Operation);
        Assert.True(vm.IsCopyOperation);

        // Selecting Skeleton auto-switches center view to target folders and disables Move.
        vm.LayoutValue = (int)CopyLayout.Skeleton;
        Assert.True(vm.ShowTargetFoldersView);
        Assert.False(vm.CanSelectMove);
        Assert.Equal("复制空目录结构", vm.PrimaryActionText);
        vm.SetOperation(CopyOperation.Move);
        Assert.Equal(CopyOperation.Copy, vm.Operation);

        // Switching away from Skeleton restores File List view and allows explicit Move.
        vm.LayoutValue = (int)CopyLayout.Classify;
        Assert.False(vm.ShowTargetFoldersView);
        Assert.True(vm.CanSelectMove);
        vm.SetOperation(CopyOperation.Move);
        Assert.True(vm.IsMoveOperation);
        Assert.False(vm.CanSelectSkeleton);
        Assert.Equal("开始移动", vm.PrimaryActionText);
    }

    [Fact]
    public async Task Move_emits_path_changes_and_hidden_checked_items_surface_warning_and_folder_groups()
    {
        var vm = Model();
        Add();
        input.SetSelection([input.VisibleFiles[0]]);
        input.SearchText = "b.txt";
        Assert.True(vm.HasHiddenSelectionWarning);
        Assert.Contains("含 1 项被筛选隐藏", vm.HiddenSelectionWarningText);

        input.SearchText = "";
        input.SetSelection([]);
        vm.SetOperation(CopyOperation.Move);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Single(vm.TargetFolderGroups);
        Assert.Contains("移入 2 项", vm.TargetFolderGroups[0].BadgeText);

        var syncedChanges = new List<FileAssistant.Core.Features.CreateOrRename.PathChange>();
        vm.PathsChanged += (_, changes) => syncedChanges.AddRange(changes);
        var plan = vm.Plan!;
        service.PendingExecution = Task.FromResult(new CopyReport(
            plan,
            plan.Items.Select(x => new CopyItemResult(x, CopyOutcome.Success, "moved", new ProducedFile(x.Target, 10, DateTimeOffset.UtcNow, [x.Source.Path]))).ToArray(),
            false)
        {
            Changes = plan.Items.Select(x => new FileAssistant.Core.Features.CreateOrRename.PathChange(x.Source.Path, x.Target, false)).ToArray()
        });

        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(2, syncedChanges.Count);
        Assert.All(input.VisibleFiles, file => Assert.Equal("已移动", file.NamePreview.Status));

        syncedChanges.Clear();
        service.UndoChanges = [new(@"D:\out\a.txt", @"C:\a.txt", false)];
        await vm.RecoverAsync(Guid.NewGuid(), discard: false);
        Assert.Single(syncedChanges);
        Assert.Equal(@"C:\a.txt", syncedChanges[0].After);
    }

    private sealed class Interaction : IFileInteraction
    { public void Open(SearchFile file) { } public void Reveal(SearchFile file) { } public void CopyText(string text) { } }
    private sealed class FakeCopy : ICopyService
    {
        public CopyRequest? Request;
        public CopyPlan? Executed;
        public Task<CopyPlan>? PendingPlan;
        public Task<CopyReport>? PendingExecution;
        public IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.PathChange> UndoChanges = [];
        public static CopyPlan Plan(CopyRequest request) => new(request, request.Paths.Select(p => new CopyPlanItem(Guid.NewGuid(),
            new(p, "", "", false, new(10, 1, p)), Path.Combine(request.Destination, Path.GetFileName(p)), CopyAction.Copy)
        { IsMove = request.Operation == CopyOperation.Move }).ToArray());
        public static CopyReport Report(CopyPlan plan) => new(plan, plan.Items.Select(x => new CopyItemResult(x, CopyOutcome.Success, "done",
            new ProducedFile(x.Target, 10, DateTimeOffset.UtcNow, [x.Source.Path]))).ToArray(), false);
        public Task<CopyPlan> PlanAsync(CopyRequest request, IProgress<ProcessingProgress>? progress, CancellationToken token)
        { Request = request; return PendingPlan ?? Task.FromResult(Plan(request)); }
        public Task<CopyReport> ExecuteAsync(CopyPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
        { Executed = plan; return PendingExecution ?? Task.FromResult(Report(plan)); }
        public Task<CopyPlan> RetryPlanAsync(CopyReport report, CancellationToken token) => Task.FromResult(report.Plan with { Items = report.Items.Where(x => x.Retryable).Select(x => x.Item).ToArray() });
        public Task<CopyPlan> DecideAsync(CopyPlan plan, Guid item, CopyDecision decision, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<CopyRecovery>> RecoveriesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<CopyRecovery>>([]);
        public Task RestoreAsync(Guid recovery, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<FileAssistant.Core.Features.CreateOrRename.PathChange>> RestoreWithChangesAsync(Guid recovery, CancellationToken token) => Task.FromResult(UndoChanges);
        public Task DiscardBackupAsync(Guid recovery, CancellationToken token) => Task.CompletedTask;
    }
}
