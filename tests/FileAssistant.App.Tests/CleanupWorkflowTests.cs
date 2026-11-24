using FileAssistant.App.Features.Cleanup;
using FileAssistant.App.Features.Tasks;
using FileAssistant.Core.Features.Cleanup;

namespace FileAssistant.App.Tests;

public sealed class CleanupWorkflowTests
{
    [Theory]
    [InlineData(@"C:\fixture\file.txt", @"C:\", true)]
    [InlineData(@"C:\fixture-other\file.txt", @"C:\fixture", false)]
    [InlineData(@"C:\fixture\file.txt", @"C:\fixture", true)]
    public void SpaceCleanupRefreshMatchesVolumeRootsWithoutMatchingSimilarNames(string path, string root, bool expected) =>
        Assert.Equal(expected, FileAssistant.App.Features.SpaceAnalysis.SpaceAnalysisViewModel.IsWithin(path, root));

    private sealed class FakeService : ICleanupService
    {
        public Func<CleanupRequest, CancellationToken, Task<CleanupResponse>> Handle { get; set; } = (_, _) => Task.FromResult(new CleanupResponse());
        public List<CleanupRequest> Calls { get; } = [];
        public Task<CleanupResponse> RunAsync(CleanupRequest request, bool elevated, IProgress<CleanupProgress>? progress, CancellationToken token)
        { Calls.Add(request); return Handle(request, token); }
    }
    private static CleanupPlan Plan(string path) => new([new(path, [])], [], DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingModesRefreshesTheActionMessageWithoutLosingInputs(bool isProtected)
    {
        var software = new InstalledSoftware("fixture", "Fixture", "Publisher", "1", @"C:\fixture-app", 5,
            "Fixture", isProtected, !isProtected, "hash");
        var model = new CleanupViewModel(new FakeService(), new());
        model.AddPaths([@"C:\chosen", @"C:\hidden"]);
        model.SetOptions(CleanupMethod.Permanent, false);
        model.SetMode(false);
        model.SelectSoftware(software);
        Assert.Contains("卸载", model.Message);

        model.SetMode(true);
        Assert.DoesNotContain("卸载", model.Message);
        Assert.Equal(new[] { @"C:\chosen", @"C:\hidden" }, model.Paths);
        Assert.Equal(CleanupMethod.Permanent, model.Method);
        Assert.Same(software, model.SelectedSoftware);

        model.SetMode(false);
        Assert.Contains(isProtected ? "受保护" : "卸载向导", model.Message);
        Assert.Equal(2, model.Paths.Length);
    }

    [Fact] public async Task Selected_or_filtered_scope_does_not_delete_other_queued_targets()
    {
        var service = new FakeService { Handle = (request, _) => Task.FromResult(request.Operation == CleanupOperation.PlanFiles
            ? new CleanupResponse { Plan = Plan(request.Paths.Single()) }
            : new CleanupResponse { Results = [new(@"C:\chosen", CleanupStatus.Succeeded, "done", [@"C:\chosen"])] }) };
        var model = new CleanupViewModel(service, new()); model.AddPaths([@"C:\chosen", @"C:\hidden"]);
        await model.PlanAsync([@"C:\chosen", @"C:\not-in-queue"]);
        Assert.Equal(@"C:\chosen", service.Calls[0].Paths.Single());
        await model.RunFilesAsync(); Assert.Equal(@"C:\hidden", model.Paths.Single());
        await model.PlanAsync([]); Assert.Null(model.Plan); Assert.Equal(2, service.Calls.Count);
    }

    [Fact] public async Task FailureAutomaticallyFindsLocksWithoutLosingDeletionResults()
    {
        var held = new CleanupLock(42, 1, "Fixture", @"C:\fixture.exe", true);
        var service = new FakeService { Handle = (request, _) => Task.FromResult(request.Operation switch
        {
            CleanupOperation.PlanFiles => new() { Plan = Plan(@"C:\sample") },
            CleanupOperation.RunFiles => new() { Results = [new(@"C:\sample", CleanupStatus.Failed, "locked", [])] },
            _ => new CleanupResponse { Locks = [held] }
        }) };
        var model = new CleanupViewModel(service, new()); model.AddPaths([@"C:\sample"]);
        await model.PlanAsync(); Assert.Single(service.Calls); await model.RunFilesAsync();
        Assert.Equal(CleanupOperation.FindLocks, service.Calls[^1].Operation);
        Assert.Equal(CleanupStatus.Failed, Assert.Single(model.Results).Status); Assert.Equal(held, Assert.Single(model.Locks)); Assert.Single(model.Paths);
    }

    [Fact] public async Task SuccessfulDeletionNeedsNoLockScanAndLeavesNoTargetsToRetry()
    {
        var service = new FakeService { Handle = (request, _) => Task.FromResult(request.Operation == CleanupOperation.PlanFiles
            ? new CleanupResponse { Plan = Plan(@"C:\sample") } : new CleanupResponse { Results = [new(@"C:\sample", CleanupStatus.Succeeded, "done", [@"C:\sample"])] }) };
        var model = new CleanupViewModel(service, new()); model.AddPaths([@"C:\sample", @"C:\sample\child"]);
        await model.PlanAsync(); await model.RunFilesAsync(); Assert.Equal(2, service.Calls.Count); Assert.Empty(model.Paths);
    }

    [Fact] public async Task FailedPreparationClearsAnyEarlierExecutablePlan()
    {
        var service = new FakeService { Handle = (_, _) => Task.FromResult(new CleanupResponse { Plan = Plan(@"C:\sample") }) };
        var model = new CleanupViewModel(service, new()); model.AddPaths([@"C:\sample"]); await model.PlanAsync();
        service.Handle = (_, _) => Task.FromResult(new CleanupResponse { Error = "fixture error" });
        await model.PlanAsync(); Assert.False(model.CanRunFiles); Assert.Null(model.Plan);
    }

    [Fact] public async Task AddedPathsAndChangedOptionsInvalidateExecutablePlan()
    {
        var service = new FakeService { Handle = (request, _) => Task.FromResult(new CleanupResponse { Plan = Plan(request.Paths[0]) }) };
        var model = new CleanupViewModel(service, new()); model.AddPaths([@"C:\sample"]);
        await model.PlanAsync(); Assert.True(model.CanRunFiles);
        model.AddPaths([@"C:\more"]); Assert.False(model.CanRunFiles);
        await model.PlanAsync(); model.SetOptions(CleanupMethod.Permanent, true); Assert.False(model.CanRunFiles);
        await model.RunFilesAsync(); Assert.Equal(2, service.Calls.Count);
    }

    [Fact] public async Task PartialDeletionInvalidatesOnlyReportedPathsAndRequiresNewPlan()
    {
        var jobs = new ProcessingJobsViewModel(); var service = new FakeService(); var model = new CleanupViewModel(service, jobs);
        service.Handle = (request, _) => Task.FromResult(request.Operation == CleanupOperation.PlanFiles ? new CleanupResponse { Plan = Plan(@"C:\sample") } :
            new CleanupResponse { Results = [new(@"C:\sample", CleanupStatus.Partial, "locked", [@"C:\sample\removed.txt"])] });
        IReadOnlyList<string>? removed = null; model.FilesRemoved += (_, paths) => removed = paths;
        model.AddPaths([@"C:\sample"]); await model.PlanAsync(); await model.RunFilesAsync();
        Assert.Equal(new[] { @"C:\sample\removed.txt" }, removed); Assert.False(model.CanRunFiles); Assert.False(jobs.HasRunningJobs);
        Assert.Single(model.Results); Assert.Equal(CleanupStatus.Partial, model.Results[0].Status);
    }

    [Fact] public async Task BusyWorkCannotChangeSelectionAndCancellationKeepsPartialResults()
    {
        var service = new FakeService(); var model = new CleanupViewModel(service, new());
        model.AddPaths([@"C:\sample"]); service.Handle = (_, _) => Task.FromResult(new CleanupResponse { Plan = Plan(@"C:\sample") }); await model.PlanAsync();
        service.Handle = async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { } return new() { Results = [new(@"C:\sample", CleanupStatus.Partial, "stopped", [@"C:\sample\a.txt"])] }; };
        var task = model.RunFilesAsync(); Assert.True(model.IsBusy);
        model.AddPaths([@"C:\unrelated"]); Assert.Single(model.Paths);
        IReadOnlyList<string>? removed = null; model.FilesRemoved += (_, paths) => removed = paths;
        await model.StopAsync(); await task; Assert.False(model.IsBusy); Assert.Single(removed!);
    }

    [Fact] public async Task ResidueScanRequiresUninstalledRegistrationAndKeepsSoftwareSnapshot()
    {
        var software = new InstalledSoftware("fixture", "Fixture", "Publisher", "1", @"C:\fixture", 5, "Unknown", false, true, "hash");
        var service = new FakeService { Handle = (request, _) => Task.FromResult(request.Operation == CleanupOperation.ListSoftware ? new CleanupResponse { Software = [software] } :
            new CleanupResponse { Results = [new("Fixture", CleanupStatus.Succeeded, "removed", [])] }) };
        var model = new CleanupViewModel(service, new()); await model.LoadSoftwareAsync(); model.SelectSoftware(software);
        Assert.True(model.CanUninstall); Assert.False(model.CanScanResidue);
        await model.ScanResidueAsync(); Assert.Single(service.Calls);
        await model.UninstallAsync(); Assert.True(model.CanScanResidue); Assert.Same(software, model.SelectedSoftware);
        await model.ScanResidueAsync(); Assert.True(model.ShowResidues); Assert.Equal(CleanupOperation.ScanResidue, service.Calls[^1].Operation);
    }

    [Fact] public async Task CleaningOneResidueKeepsUncheckedAndFailedCandidatesEvenOnSameRegistryPath()
    {
        var software = new InstalledSoftware("fixture", "Fixture", "", "", @"C:\fixture", 0, "Unknown", false, true, "hash");
        var residues = new[] { "success", "failed", "unchecked" }.Select(id => new CleanupResidue(id, ResidueKind.RegistryValue, @"HKEY_CURRENT_USER\Software\Fixture\App", id, 5, "fixture", "hash", "fixture")).ToArray();
        var service = new FakeService { Handle = (request, _) => Task.FromResult(request.Operation switch
        {
            CleanupOperation.ScanResidue => new() { Residues = residues },
            CleanupOperation.RemoveResidue => new() { Results = [new(residues[0].Path, CleanupStatus.Succeeded, "removed", [], "success"), new(residues[1].Path, CleanupStatus.Failed, "access", [], "failed")] },
            _ => new CleanupResponse()
        }) };
        var model = new CleanupViewModel(service, new()); model.SelectSoftware(software); await model.ScanResidueAsync();
        await model.RemoveResidueAsync(residues[..2]);
        Assert.Equal(new[] { "failed", "unchecked" }, model.Residues.Select(item => item.Id)); Assert.True(model.ShowResidues);
        model.SetOptions(CleanupMethod.Recycle, true); Assert.Equal(2, model.Residues.Length);
    }
}
