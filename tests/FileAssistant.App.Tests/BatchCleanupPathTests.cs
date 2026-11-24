using FileAssistant.App.Features.Cleanup;
using FileAssistant.Core.Features.Cleanup;

namespace FileAssistant.App.Tests;

public sealed class BatchCleanupPathTests
{
    [Fact]
    public async Task Parent_rename_and_removal_remap_after_an_inflight_check_and_reject_its_old_result()
    {
        var response = new TaskCompletionSource<CleanupResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Fixture(_ => response.Task);
        var vm = new CleanupViewModel(service, new());
        vm.AddPaths([@"C:\old\child.txt", @"C:\old\drop\deeper.txt", @"C:\old-extra\keep.txt"]);
        var running = vm.PlanAsync();
        vm.ApplyPathChanges([new(@"C:\old", @"C:\renamed", true)]);
        vm.ApplyPathChanges([], [@"C:\renamed\drop"]);
        Assert.Equal(@"C:\old\child.txt", vm.Paths[0]);
        response.SetResult(new() { Plan = Plan(@"C:\old\child.txt") });
        await running;
        Assert.Equal([@"C:\renamed\child.txt", @"C:\old-extra\keep.txt"], vm.Paths);
        Assert.Null(vm.Plan); Assert.False(vm.CanRunFiles); Assert.Empty(vm.Locks);
        Assert.Contains("重新核对", vm.Message);
    }

    [Fact]
    public async Task Queued_execution_retains_approved_snapshot_and_does_not_publish_stale_locks()
    {
        var diagnosis = new TaskCompletionSource<CleanupResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Fixture(request => request.Operation switch
        {
            CleanupOperation.PlanFiles => Task.FromResult(new CleanupResponse { Plan = Plan(@"C:\old\one") }),
            CleanupOperation.RunFiles => Task.FromResult(new CleanupResponse { Results = [new(@"C:\old\one", CleanupStatus.Failed, "locked", [])] }),
            CleanupOperation.FindLocks => diagnosis.Task,
            _ => throw new InvalidOperationException()
        });
        var vm = new CleanupViewModel(service, new());
        vm.AddPaths([@"C:\old\one"]); await vm.PlanAsync();
        var approved = vm.Plan;
        var running = vm.RunFilesAsync();
        Assert.Same(approved, Assert.Single(service.Calls, call => call.Operation == CleanupOperation.RunFiles).Plan);
        vm.ApplyPathChanges([new(@"C:\old", @"C:\new-parent", true)]);
        diagnosis.SetResult(new() { Locks = [new(187, 3, "old owner", "", true)] });
        await running;
        Assert.Equal([@"C:\new-parent\one"], vm.Paths);
        Assert.Equal("locked", Assert.Single(vm.Results).Message);
        Assert.Empty(vm.Locks); Assert.Null(vm.Plan);
    }

    [Fact]
    public async Task Removed_descendant_invalidates_a_recursive_plan_but_a_sibling_change_preserves_it()
    {
        var vm = new CleanupViewModel(new Fixture(_ => Task.FromResult(new CleanupResponse { Plan = Plan(@"C:\folder") })), new());
        vm.AddPaths([@"C:\folder"]);
        await vm.PlanAsync();
        var before = vm.Plan;
        vm.ApplyPathChanges([], [@"C:\folder2\child"]);
        Assert.Same(before, vm.Plan);
        vm.ApplyPathChanges([], [@"C:\folder\child"]);
        Assert.Null(vm.Plan);
        Assert.Equal([@"C:\folder"], vm.Paths);
        vm.ApplyPathChanges([], [@"C:\folder"]);
        Assert.Empty(vm.Paths);
    }

    private static CleanupPlan Plan(string path) => new([new(path, [])], [], DateTimeOffset.UtcNow);
    private sealed class Fixture(Func<CleanupRequest, Task<CleanupResponse>> handle) : ICleanupService
    {
        public List<CleanupRequest> Calls { get; } = [];
        public Task<CleanupResponse> RunAsync(CleanupRequest request, bool elevated, IProgress<CleanupProgress>? progress, CancellationToken token)
        {
            Calls.Add(request);
            return handle(request);
        }
    }
}
