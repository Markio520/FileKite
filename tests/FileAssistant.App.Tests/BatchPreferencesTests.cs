using FileAssistant.App.Features.Cleanup;
using FileAssistant.App.Features.Convert;
using FileAssistant.App.Features.CreateOrRename;
using FileAssistant.App.Features.Organize;
using FileAssistant.App.Features.Outputs;
using FileAssistant.App.Features.Tasks;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Tests;

public sealed class BatchPreferencesTests
{
    [Fact]
    public async Task Conversion_uses_defaults_in_real_requests_and_preserves_overrides_and_active_plan()
    {
        var files = new WorkspaceViewModel(null, WorkspaceInputPurpose.Conversion);
        var service = new RecordingConversion();
        var jobs = new ProcessingJobsViewModel();
        var vm = new ConversionViewModel(service, files, new OutputsViewModel(null!), jobs);
        var defaults = new AppSettings { ImageQuality = 75, TextEncoding = DelimitedEncoding.Gb18030, ConversionConflict = ConversionCollision.Overwrite, OutputLocation = OutputLocation.Subfolder };
        vm.ApplyDefaults(null, defaults);
        files.AddSearchFiles([new(@"C:\one\photo.png", false, 1, null)]);
        vm.TargetFormatValue = (int)ConversionTarget.Jpeg;
        await vm.BuildPlanCommand.ExecuteAsync(null);
        var plan = Assert.IsType<ConversionPlan>(vm.Plan);
        Assert.Equal(OutputLocation.Subfolder, plan.Request.Location);
        Assert.Equal(75, plan.Request.Quality);
        Assert.Equal(DelimitedEncoding.Gb18030, plan.Request.Options.Encoding);
        Assert.Equal(ConversionCollision.Overwrite, plan.Request.Collision);
        Assert.True(plan.Request.Options.PreferHardwareEncoding);
        vm.OutputDirectory = @"D:\manual";
        vm.Quality = 61;
        vm.EncodingValue = (int)DelimitedEncoding.Utf8;
        vm.ConflictPolicyValue = (int)ConversionCollision.Skip;
        await vm.BuildPlanCommand.ExecuteAsync(null);
        var manual = Assert.IsType<ConversionPlan>(vm.Plan);
        var running = vm.RunCommand.ExecuteAsync(null);
        var updated = defaults with
        {
            OutputLocation = OutputLocation.Fixed, OutputFolder = @"D:\default", ImageQuality = 98,
            TextEncoding = DelimitedEncoding.Gb18030, ConversionConflict = ConversionCollision.Number,
            PreferHardwareEncoding = false
        };
        vm.ApplyDefaults(defaults, updated);
        Assert.Same(manual, vm.Plan);
        Assert.Same(manual, service.Executing);
        Assert.Equal(@"D:\manual", manual.Request.OutputDirectory);
        Assert.Equal(61, manual.Request.Quality);
        Assert.Equal(DelimitedEncoding.Utf8, manual.Request.Options.Encoding);
        Assert.True(manual.Request.Options.PreferHardwareEncoding);
        Assert.Equal(ConversionCollision.Skip, manual.Request.Collision);
        service.Execution.SetResult(new([], false));
        await running;
        Assert.Equal(@"D:\manual", vm.OutputDirectory);
        files.ClearCommand.Execute(null);
        Assert.Equal(updated.OutputFolder, vm.OutputDirectory);
        Assert.Equal(98, vm.Quality);
        Assert.Equal((int)updated.ConversionConflict, vm.ConflictPolicyValue);
        Assert.Equal((int)updated.TextEncoding, vm.EncodingValue);
    }

    [Fact]
    public async Task Copy_requests_carry_layout_hash_and_location_and_preserve_single_task_choice()
    {
        var input = new WorkspaceViewModel(null, WorkspaceInputPurpose.Copy);
        var service = new RecordingCopy();
        var vm = new CopyViewModel(service, input, new(null!), new());
        var initial = new AppSettings { OutputLocation = OutputLocation.Source, CopyLayout = CopyLayout.Extension, VerifyCopyHash = true };
        vm.ApplyDefaults(null, initial);
        input.AddSearchFiles([new(@"C:\one\a.txt", false, 2, null), new(@"D:\two\a.txt", false, 2, null)]);
        Assert.True(vm.CanBuild);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(OutputLocation.Source, service.Last!.Location);
        Assert.Equal(CopyLayout.Extension, service.Last.Layout);
        Assert.True(service.Last.VerifyHash);
        vm.Destination = @"E:\manual";
        vm.LayoutValue = (int)CopyLayout.Flat;
        var next = initial with { OutputLocation = OutputLocation.Fixed, OutputFolder = @"E:\default", CopyLayout = CopyLayout.Preserve, VerifyCopyHash = false };
        vm.ApplyDefaults(initial, next);
        await vm.BuildPlanCommand.ExecuteAsync(null);
        Assert.Equal(OutputLocation.Fixed, service.Last.Location);
        Assert.Equal(@"E:\manual", service.Last.Destination);
        Assert.Equal(CopyLayout.Flat, service.Last.Layout);
        Assert.False(service.Last.VerifyHash);
        input.ClearCommand.Execute(null);
        Assert.Equal(next.OutputFolder, vm.Destination);
        Assert.Equal((int)CopyLayout.Preserve, vm.LayoutValue);
    }

    [Fact]
    public async Task Cleanup_sends_backup_and_method_in_the_reviewed_request_and_ignores_diagnostic_jobs()
    {
        var jobs = new ProcessingJobsViewModel();
        var service = new RecordingCleanup();
        var vm = new CleanupViewModel(service, jobs);
        var notices = new List<BatchCompletion>();
        jobs.BatchCompleted += (_, notice) => notices.Add(notice);
        var options = new AppSettings { PermanentDelete = true, BackupRegistry = false, Completion = CompletionBehavior.Tray };
        jobs.ApplyDefaults(null, options);
        vm.ApplyDefaults(null, options);
        Assert.Equal(CleanupMethod.Permanent, vm.Method);
        vm.SelectSoftware(new("fixture", "Fixture", "", "", @"C:\fixture", 0, "", false, false, "fixture"));
        await vm.ScanResidueAsync();
        Assert.Empty(notices);
        vm.SetOptions(CleanupMethod.Recycle, false);
        vm.ApplyDefaults(options, options with { BackupRegistry = true });
        Assert.Equal(CleanupMethod.Recycle, vm.Method);
        await vm.RemoveResidueAsync(vm.Residues);
        var removed = Assert.Single(service.Requests, request => request.Operation == CleanupOperation.RemoveResidue);
        Assert.True(removed.BackupRegistry);
        Assert.Equal(CleanupMethod.Recycle, removed.Method);
        Assert.Equal(CompletionBehavior.Tray, Assert.Single(notices).Behavior);
        await vm.ScanResidueAsync();
        vm.ApplyDefaults(options with { BackupRegistry = true }, options);
        await vm.RemoveResidueAsync(vm.Residues);
        Assert.False(service.Requests[^1].BackupRegistry);
    }

    [Fact]
    public void Naming_defaults_do_not_replace_a_manually_selected_policy()
    {
        var input = new WorkspaceViewModel(null, WorkspaceInputPurpose.Rename);
        var vm = new NamingViewModel(null!, input, new(null!), new());
        var initial = new AppSettings { NamingConflict = NamingConflict.Number };
        vm.ApplyDefaults(null, initial);
        Assert.Equal((int)NamingConflict.Number, vm.ConflictValue);
        input.AddSearchFiles([new(@"C:\one\item.txt", false, 2, null)]);
        vm.ConflictValue = (int)NamingConflict.Skip;
        var next = initial with { NamingConflict = NamingConflict.Block };
        vm.ApplyDefaults(initial, next);
        Assert.Equal((int)NamingConflict.Skip, vm.ConflictValue);
        input.ClearCommand.Execute(null);
        Assert.Equal((int)NamingConflict.Block, vm.ConflictValue);
    }

    [Fact]
    public void Completion_is_emitted_once_after_the_last_batch_with_only_actual_output_directories()
    {
        var jobs = new ProcessingJobsViewModel();
        jobs.ApplyDefaults(null, new() { Completion = CompletionBehavior.OpenFolder, MaxParallel = 4 });
        Assert.Equal(4, jobs.Slots.Limit);
        var notices = new List<BatchCompletion>();
        jobs.BatchCompleted += (_, notice) => notices.Add(notice);
        var reading = jobs.Start(() => { }, "正在准备", batch: false);
        var a = jobs.Start(() => { });
        var b = jobs.Start(() => { }, "复制");
        a.Complete("部分完成", [], false, [@"D:\out", @"d:\OUT", @"C:\successful"]);
        Assert.Empty(notices);
        b.Fail("无可用输出");
        var notice = Assert.Single(notices);
        Assert.Equal(CompletionBehavior.OpenFolder, notice.Behavior);
        Assert.Equal([@"D:\out", @"C:\successful"], notice.OutputDirectories);
        Assert.Contains("部分完成", notice.Summary);
        Assert.Contains("无可用输出", notice.Summary);
        reading.Complete("已读取", [], false);
        b.Fail("重复");
        Assert.Single(notices);
    }

    [Fact]
    public void Template_save_errors_preserve_the_last_good_list_and_existing_disk_contents()
    {
        var directory = Directory.CreateTempSubdirectory("FileAssistant-outline-policy-").FullName;
        try
        {
            var path = Path.Combine(directory, "tmpls.json");
            var vm = new NamingViewModel(null!, new(null, WorkspaceInputPurpose.Rename), new(null!), new());
            Assert.True(vm.ResetDefaultOutlineTemplates(path));
            var initial = vm.OutlineTemplates.ToArray();
            File.WriteAllText(path, "{broken");
            Assert.False(vm.DeleteOutlineTemplate(initial[0].Id, path));
            Assert.Equal(initial, vm.OutlineTemplates);
            Assert.Equal("{broken", File.ReadAllText(path));
            var blocked = Path.Combine(directory, "target-directory");
            Directory.CreateDirectory(blocked);
            Assert.False(vm.ResetDefaultOutlineTemplates(blocked));
            Assert.Contains("未能", vm.Feedback);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            var reloaded = 0;
            vm.OutlineTemplatesChanged += (_, _) => reloaded++;
            vm.ReloadOutlineTemplates();
            Assert.Equal(1, reloaded);
        }
        finally
        {
            if (Path.GetFileName(directory).StartsWith("FileAssistant-outline-policy-", StringComparison.Ordinal) &&
                Path.GetDirectoryName(directory) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())) Directory.Delete(directory, true);
        }
    }

    private sealed class RecordingConversion : IFileProcessingService
    {
        public IReadOnlyList<ProcessingEngineInfo> Engines => [];
        public ConversionPlan? Executing { get; private set; }
        public TaskCompletionSource<ConversionReport> Execution { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PreviewResult> PreviewAsync(string path, int edge, CancellationToken token) => throw new NotSupportedException();
        public Task<ConversionPlan> CreatePlanAsync(ConversionRequest request, CancellationToken token) =>
            Task.FromResult(ConversionPlanner.Create(request, request.Paths.Select(path => new ProcessingSource(path, 1, 1)).ToArray(), _ => false));
        public Task<ConversionReport> ConvertAsync(ConversionPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
        { Executing = plan; return Execution.Task; }
    }

    private sealed class RecordingCopy : ICopyService
    {
        public CopyRequest? Last { get; private set; }
        public Task<CopyPlan> PlanAsync(CopyRequest request, IProgress<ProcessingProgress>? progress, CancellationToken token)
        { Last = request; return Task.FromResult(new CopyPlan(request, [])); }
        public Task<CopyPlan> DecideAsync(CopyPlan plan, Guid item, CopyDecision decision, CancellationToken token) => throw new NotSupportedException();
        public Task<CopyReport> ExecuteAsync(CopyPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<CopyPlan> RetryPlanAsync(CopyReport report, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<CopyRecovery>> RecoveriesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<CopyRecovery>>([]);
        public Task RestoreAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public Task DiscardBackupAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class RecordingCleanup : ICleanupService
    {
        public List<CleanupRequest> Requests { get; } = [];
        public Task<CleanupResponse> RunAsync(CleanupRequest request, bool elevated, IProgress<CleanupProgress>? progress, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(request.Operation == CleanupOperation.ScanResidue
                ? new CleanupResponse { Residues = [new("demo", ResidueKind.RegistryKey, @"HKEY_CURRENT_USER\Software\FixtureOwned\Test", null, 5, "", "fingerprint", "fixture")] }
                : new CleanupResponse());
        }
    }
}
