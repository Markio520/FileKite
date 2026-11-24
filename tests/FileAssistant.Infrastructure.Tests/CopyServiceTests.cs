using System.Security.Cryptography;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Tests;

public sealed class CopyServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FileAssistant-copy-test-" + Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(root, "out");
    private string Records => Path.Combine(root, "records");
    private CopyService Service => new(Records);
    private string FileAt(string relative, string text = "original bytes")
    { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Success(CopyReport report, int files)
    { Assert.True(report.Outputs.Count == files, report.Summary + "\n" + string.Join("\n", report.Items.Select(x => x.Description))); }
    private Task<CopyPlan> Plan(string[] paths, CopyLayout layout = CopyLayout.Flat, CopyConflictPolicy policy = CopyConflictPolicy.Number)
        => Service.PlanAsync(new(paths, Destination, layout, policy), null, default);

    [Fact]
    public async Task Corrupt_recovery_does_not_hide_or_prevent_restoring_other_backups()
    {
        var source = FileAt("source/a.txt", "new"); var target = FileAt("out/a.txt", "old");
        var plan = await Plan([source], policy: CopyConflictPolicy.Confirm);
        plan = await Service.DecideAsync(plan, plan.Items[0].Id, CopyDecision.Overwrite, default);
        Success(await Service.ExecuteAsync(plan, null, default), 1);
        var valid = Assert.Single(await Service.RecoveriesAsync(default));
        var damagedId = Guid.NewGuid();
        var damaged = FileAt("records/" + damagedId.ToString("N") + ".json", "{broken");
        FileAt("records/unrelated.json", "{}");

        var records = await Service.RecoveriesAsync(default);

        Assert.Contains(records, record => record.Id == valid.Id);
        Assert.Contains(records, record => record.Id == damagedId && record.Message != null);
        await Service.RestoreAsync(valid.Id, default);
        Assert.Equal("old", File.ReadAllText(target));
        Assert.Equal("{broken", File.ReadAllText(damaged));
        Assert.Single(await Service.RecoveriesAsync(default));
    }

    [Fact]
    public async Task Preserve_uses_outermost_selected_root_deduplicates_and_keeps_empty_directories()
    {
        var file = FileAt("source/nested/photo.jpg");
        Directory.CreateDirectory(Path.Combine(root, "source", "empty"));
        var plan = await Plan([file, Path.Combine(root, "source", "nested"), Path.Combine(root, "source"), file], CopyLayout.Preserve);
        Assert.Equal(1, plan.FileCount);
        var report = await Service.ExecuteAsync(plan, null, default);
        Success(report, 1);
        Assert.Equal(Path.Combine(Destination, "source", "nested", "photo.jpg"), report.Outputs[0].FullPath);
        Assert.True(Directory.Exists(Path.Combine(Destination, "source", "empty")));
        Assert.Equal(Hash(file), Hash(report.Outputs[0].FullPath));
    }
    [Theory]
    [InlineData(CopyLayout.Flat, "photo.JPG")]
    [InlineData(CopyLayout.Extension, "JPG/photo.JPG")]
    public async Task Layouts_map_files_and_keep_source_content(CopyLayout layout, string relative)
    {
        var file = FileAt("source/photo.JPG"); var original = Hash(file);
        var plan = await Plan([file], layout);
        var report = await Service.ExecuteAsync(plan, null, default);
        Success(report, 1);
        Assert.Equal(Path.Combine(Destination, relative.Replace('/', Path.DirectorySeparatorChar)), report.Outputs[0].FullPath);
        Assert.Equal(original, Hash(file)); Assert.Equal(original, Hash(report.Outputs[0].FullPath));
    }
    [Fact]
    public async Task Extension_mode_keeps_zero_byte_and_extensionless_files()
    {
        var file = FileAt("source/README", "");
        var report = await Service.ExecuteAsync(await Plan([file], CopyLayout.Extension), null, default);
        Success(report, 1); Assert.Equal(0, new FileInfo(report.Outputs[0].FullPath).Length);
        Assert.Contains("无扩展名", report.Outputs[0].FullPath);
    }
    [Fact]
    public async Task Numbering_handles_existing_and_batch_names_and_same_name_roots()
    {
        var a = FileAt("a/photo.jpg", "A"); var b = FileAt("b/photo.jpg", "B"); FileAt("out/photo.jpg", "old");
        var plan = await Plan([b, a]);
        Assert.Equal(2, plan.Items.Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(plan.Items, x => Assert.Equal(CopyAction.Number, x.Action));
        var report = await Service.ExecuteAsync(plan, null, default); Success(report, 2);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Destination, "photo.jpg")));
        FileAt("a/photos/1.txt"); FileAt("b/photos/2.txt");
        var roots = await Plan([Path.Combine(root, "a", "photos"), Path.Combine(root, "b", "photos")], CopyLayout.Preserve);
        Assert.Contains(roots.Items, x => x.Target == Path.Combine(Destination, "photos (1)", "2.txt"));
    }
    [Fact]
    public async Task Skip_and_same_source_target_are_not_failures()
    {
        var file = FileAt("source/a.txt"); FileAt("out/a.txt", "old");
        var report = await Service.ExecuteAsync(await Plan([file], policy: CopyConflictPolicy.Skip), null, default);
        Assert.Equal(CopyOutcome.Skipped, Assert.Single(report.Items).Outcome);
        Assert.Empty(report.Outputs); Assert.False(report.Items[0].Retryable);
        var self = await Service.PlanAsync(new([file], Path.GetDirectoryName(file)!, CopyLayout.Flat), null, default);
        Assert.Equal(CopyAction.Skip, Assert.Single(self.Items).Action);
    }
    [Fact]
    public async Task Destination_inside_source_is_rejected_before_writes()
    {
        FileAt("source/a.txt");
        await Assert.ThrowsAsync<IOException>(() => Service.PlanAsync(new([Path.Combine(root, "source")], Path.Combine(root, "source", "inside")), null, default));
        Assert.False(Directory.Exists(Path.Combine(root, "source", "inside")));
    }
    [Fact]
    public async Task Overwrite_backs_up_and_restores_across_service_restart()
    {
        var source = FileAt("source/a.txt", "new"); var target = FileAt("out/a.txt", "old");
        var service = Service;
        var plan = await Plan([source], policy: CopyConflictPolicy.Confirm);
        Assert.Equal(1, plan.PendingCount);
        plan = await service.DecideAsync(plan, plan.Items[0].Id, CopyDecision.Overwrite, default);
        var report = await service.ExecuteAsync(plan, null, default); Success(report, 1);
        Assert.Equal("new", File.ReadAllText(target)); Assert.Equal("new", File.ReadAllText(source));
        var restarted = Service;
        var record = Assert.Single(await restarted.RecoveriesAsync(default));
        Assert.True(File.Exists(record.Backup), record.Description + "\n" + string.Join("\n", Directory.GetFileSystemEntries(Destination)));
        Assert.Equal("old", File.ReadAllText(record.Backup));
        await restarted.RestoreAsync(record.Id, default);
        Assert.Equal("old", File.ReadAllText(target)); Assert.Empty(await restarted.RecoveriesAsync(default));
    }
    [Fact]
    public async Task Changed_targets_require_new_confirmation_and_restore_never_clobbers_edits()
    {
        var source = FileAt("source/a.txt", "new"); var target = FileAt("out/a.txt", "old");
        var service = Service;
        var plan = await Plan([source], policy: CopyConflictPolicy.Confirm);
        plan = await service.DecideAsync(plan, plan.Items[0].Id, CopyDecision.Overwrite, default);
        File.WriteAllText(target, "external edit");
        var report = await service.ExecuteAsync(plan, null, default);
        Assert.Equal(CopyOutcome.Pending, Assert.Single(report.Items).Outcome); Assert.Equal("external edit", File.ReadAllText(target));
        var retry = await service.RetryPlanAsync(report, default);
        retry = await service.DecideAsync(retry, retry.Items[0].Id, CopyDecision.Overwrite, default);
        Success(await service.ExecuteAsync(retry, null, default), 1);
        var record = Assert.Single(await service.RecoveriesAsync(default));
        File.WriteAllText(target, "edit after copy");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(record.Id, default));
        Assert.Equal("edit after copy", File.ReadAllText(target)); Assert.Equal("external edit", File.ReadAllText(record.Backup));
        await service.DiscardBackupAsync(record.Id, default);
        Assert.False(File.Exists(record.Backup)); Assert.Equal("edit after copy", File.ReadAllText(target));
    }
    [Fact]
    public async Task A_new_conflict_between_copy_and_commit_is_rechecked()
    {
        var source = FileAt("source/a.txt", "new"); var target = Path.Combine(Destination, "a.txt");
        var plan = await Plan([source], policy: CopyConflictPolicy.Confirm);
        var progress = new InlineProgress(p => { if (p.Message.StartsWith("正在校验")) File.WriteAllText(target, "created during copy"); });
        var report = await Service.ExecuteAsync(plan, progress, default);
        Assert.Equal(CopyOutcome.Pending, Assert.Single(report.Items).Outcome);
        Assert.Equal("created during copy", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(Destination, "*.tmp"));
    }
    [Fact]
    public async Task Source_change_since_preview_fails_and_retry_only_copies_unfinished_items()
    {
        var a = FileAt("source/a.txt", "A"); var b = FileAt("source/b.txt", "B");
        var plan = await Plan([a, b]); File.WriteAllText(b, "changed after preview");
        var service = Service; var report = await service.ExecuteAsync(plan, null, default); Success(report, 1);
        Assert.Single(report.Items, x => x.Outcome == CopyOutcome.Failed);
        var retry = await service.RetryPlanAsync(report, default); Assert.Single(retry.Items);
        Success(await service.ExecuteAsync(retry, null, default), 1);
        Assert.Equal(2, Directory.GetFiles(Destination).Length); Assert.Equal("A", File.ReadAllText(Path.Combine(Destination, "a.txt")));
    }
    [Fact]
    public async Task Native_large_file_reports_bytes_and_cancellation_cleans_partial_files()
    {
        var source = FileAt("source/large.bin");
        using (var file = new FileStream(source, FileMode.Create, FileAccess.Write)) { var block = RandomNumberGenerator.GetBytes(1024 * 1024); for (var i = 0; i < 32; i++) file.Write(block); }
        var plan = await Plan([source]);
        using var cancel = new CancellationTokenSource();
        long bytes = 0;
        var progress = new InlineProgress(p => { bytes = Math.Max(bytes, p.BytesCompleted ?? 0); if (p.BytesCompleted > 0 && p.Message.StartsWith("正在复制")) cancel.Cancel(); });
        var report = await Service.ExecuteAsync(plan, progress, cancel.Token);
        Assert.True(bytes > 0); Assert.True(report.Cancelled); Assert.Empty(report.Outputs);
        Assert.Empty(Directory.GetFiles(Destination));
        var retry = await Service.RetryPlanAsync(report, default);
        var completed = await Service.ExecuteAsync(retry, null, default); Success(completed, 1);
        Assert.Equal(Hash(source), Hash(completed.Outputs[0].FullPath));
    }
    [Fact]
    public async Task Disk_full_stops_remaining_items_and_cleans_temporary_data()
    {
        var a = FileAt("source/a.txt"); var b = FileAt("source/b.txt");
        var service = new CopyService(Records, new DiskFull());
        var report = await service.ExecuteAsync(await Plan([a, b]), null, default);
        Assert.NotNull(report.StopReason); Assert.Equal(CopyOutcome.Failed, report.Items[0].Outcome);
        Assert.Equal(CopyOutcome.NotStarted, report.Items[1].Outcome); Assert.Empty(report.Outputs);
        Assert.Empty(Directory.GetFiles(Destination));
    }
    [Fact]
    public async Task Busy_source_is_reported_individually_and_can_be_retried()
    {
        var a = FileAt("source/a.txt"); var b = FileAt("source/b.txt"); var plan = await Plan([a, b]);
        CopyReport report;
        using (var locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            report = await Service.ExecuteAsync(plan, null, default);
        Success(report, 1); Assert.Equal(CopyOutcome.Failed, report.Items[0].Outcome);
        Success(await Service.ExecuteAsync(await Service.RetryPlanAsync(report, default), null, default), 1);
    }
    [Fact]
    public async Task File_directory_conflicts_are_visible_and_do_not_modify_sources()
    {
        var source = FileAt("source/a.txt"); Directory.CreateDirectory(Path.Combine(Destination, "a.txt"));
        var plan = await Plan([source], policy: CopyConflictPolicy.Confirm);
        var item = Assert.Single(plan.Items); Assert.True(item.NeedsDecision); Assert.False(item.CanOverwrite);
        plan = await Service.DecideAsync(plan, item.Id, CopyDecision.Number, default);
        Success(await Service.ExecuteAsync(plan, null, default), 1);
        Assert.True(Directory.Exists(Path.Combine(Destination, "a.txt")));
    }
    [Fact]
    public async Task Two_thousand_small_files_copy_once_with_content_verified()
    {
        for (var i = 0; i < 2000; i++) FileAt($"source/{i:D4}.txt", $"content-{i}");
        var report = await Service.ExecuteAsync(await Plan([Path.Combine(root, "source")]), null, default);
        Success(report, 2000);
        Assert.All(report.Outputs, x => Assert.Equal(Hash(x.SourcePaths[0]), Hash(x.FullPath)));
    }
    [Fact]
    public async Task Long_unicode_paths_support_copy_overwrite_and_restore()
    {
        var source = FileAt("source/照片.txt", "new content");
        var destination = Path.Combine(Destination, new string('甲', 70), new string('乙', 70), new string('丙', 70));
        Directory.CreateDirectory(destination); var target = Path.Combine(destination, "照片.txt"); File.WriteAllText(target, "old content");
        var service = Service;
        var plan = await service.PlanAsync(new([source], destination, CopyLayout.Flat, CopyConflictPolicy.Confirm), null, default);
        plan = await service.DecideAsync(plan, plan.Items[0].Id, CopyDecision.Overwrite, default);
        Success(await service.ExecuteAsync(plan, null, default), 1);
        var recovery = Assert.Single(await service.RecoveriesAsync(default));
        await service.RestoreAsync(recovery.Id, default);
        Assert.Equal("old content", File.ReadAllText(target));
    }
    [Fact]
    public async Task Cancellation_after_commit_retains_the_completed_output()
    {
        var source = FileAt("source/a.txt"); using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Message.StartsWith("已处理")) cancel.Cancel(); });
        var report = await Service.ExecuteAsync(await Plan([source]), progress, cancel.Token);
        Success(report, 1); Assert.Equal(CopyOutcome.Success, report.Items[0].Outcome);
        Assert.Empty((await Service.RetryPlanAsync(report, default)).Items);
    }
    [Fact]
    public async Task A_cancelled_scan_never_returns_a_partial_plan_or_creates_output()
    {
        var source = FileAt("source/a.txt"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.PlanAsync(new([source], Destination), null, cancel.Token));
        Assert.False(Directory.Exists(Destination));
    }
    [Fact]
    public async Task Hard_link_aliases_are_deduplicated_and_a_target_alias_is_skipped()
    {
        var source = FileAt("source/a.txt"); var alias = Path.Combine(root, "source", "b.txt");
        Assert.True(CreateHardLinkW(alias, source, 0));
        Assert.Equal(1, (await Plan([source, alias])).FileCount);
        Directory.CreateDirectory(Destination);
        Assert.True(CreateHardLinkW(Path.Combine(Destination, "a.txt"), source, 0));
        Assert.Equal(CopyAction.Skip, Assert.Single((await Plan([source])).Items).Action);
    }
    [Fact]
    public async Task Junctions_are_reported_without_following_a_cycle_or_redirected_target()
    {
        var source = FileAt("source/a.txt"); var folder = Path.GetDirectoryName(source)!; var link = Path.Combine(folder, "loop");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { Arguments = $"/c mklink /J \"{link}\" \"{folder}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        try
        {
            var plan = await Plan([folder]);
            Assert.Contains(plan.Items, x => x.Action == CopyAction.Rejected && x.Message!.Contains("链接"));
            Assert.Single(plan.Items, x => x.CanRun && !x.Source.IsDirectory);
            await Assert.ThrowsAsync<IOException>(() => Service.PlanAsync(new([source], Path.Combine(link, "out")), null, default));
        }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task Interrupted_replacement_with_missing_target_can_restore_the_original()
    {
        var source = FileAt("source/a.txt", "new"); var target = FileAt("out/a.txt", "old");
        var plan = await Plan([source], policy: CopyConflictPolicy.Confirm);
        plan = await Service.DecideAsync(plan, plan.Items[0].Id, CopyDecision.Overwrite, default);
        Success(await Service.ExecuteAsync(plan, null, default), 1);
        var record = Assert.Single(await Service.RecoveriesAsync(default));
        File.Delete(target); // Simulate interruption after backup rename and before final commit.
        File.WriteAllText(Path.Combine(Records, record.Id.ToString("N") + ".json"), System.Text.Json.JsonSerializer.Serialize(record with { State = "旧目标已备份" }));
        await Service.RestoreAsync(record.Id, default);
        Assert.Equal("old", File.ReadAllText(target));
    }
    [Fact]
    public async Task A_disconnected_target_stops_the_batch_without_registering_partial_outputs()
    {
        var a = FileAt("source/a.txt"); var b = FileAt("source/b.txt");
        var service = new CopyService(Records, new Disconnected());
        var report = await service.ExecuteAsync(await Plan([a, b]), null, default);
        Assert.Equal(CopyOutcome.NotStarted, report.Items[1].Outcome); Assert.Empty(report.Outputs);
        Assert.Contains("不可用", report.StopReason!); Assert.Empty(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task Same_volume_move_removes_sources_cleans_empty_folders_and_supports_tamper_checked_undo()
    {
        var a = FileAt("source/nested/doc.txt", "move-alpha");
        var b = FileAt("source/report.pdf", "move-beta");
        var service = Service;

        var request = new CopyRequest([Path.Combine(root, "source")], Destination, CopyLayout.Preserve)
        {
            Operation = CopyOperation.Move
        };
        var plan = await service.PlanAsync(request, null, default);
        var report = await service.ExecuteAsync(plan, null, default);
        Success(report, 2);

        var movedA = Path.Combine(Destination, "source", "nested", "doc.txt");
        var movedB = Path.Combine(Destination, "source", "report.pdf");
        Assert.True(File.Exists(movedA));
        Assert.True(File.Exists(movedB));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(Directory.Exists(Path.Combine(root, "source")));
        Assert.Contains(report.Changes, c => c.Before == Path.GetFullPath(a) && c.After == movedA);

        var recovery = Assert.Single(await service.RecoveriesAsync(default));
        Assert.True(recovery.IsMoveUndo);
        Assert.Equal("撤回所选移动", recovery.ActionLabel);

        // Tamper check: if original path is occupied, undo must refuse without overwriting.
        FileAt("source/report.pdf", "occupier");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreWithChangesAsync(recovery.Id, default));
        Assert.Equal("occupier", File.ReadAllText(b));
        File.Delete(b);

        // Clean undo moves both files back to their original paths and returns inverse PathChanges.
        var undoChanges = await service.RestoreWithChangesAsync(recovery.Id, default);
        Assert.Equal(2, undoChanges.Count);
        Assert.Equal("move-alpha", File.ReadAllText(a));
        Assert.Equal("move-beta", File.ReadAllText(b));
        Assert.False(File.Exists(movedA));
        Assert.False(File.Exists(movedB));
        Assert.Empty(await service.RecoveriesAsync(default));
    }

    [Fact]
    public async Task Cross_volume_move_verifies_hash_and_reconciles_interrupted_source_delete_without_duplicating()
    {
        var source = FileAt("source/contract.pdf", "cross-volume-payload");
        var service = new CopyService(Records, new RealBufferTransfer());
        var request = new CopyRequest([source], Destination, CopyLayout.Flat)
        {
            Operation = CopyOperation.Move,
            VerifyHash = false // Cross-volume Move must still enforce SHA-256 verification!
        };
        var plan = await service.PlanAsync(request, null, default);

        // Hold a read handle WITHOUT FileShare.Delete so target commits & verifies, while source delete fails cleanly.
        CopyReport interrupted;
        using (var readLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            interrupted = await service.ExecuteAsync(plan, null, default);
        }

        var target = Path.Combine(Destination, "contract.pdf");
        Assert.Equal(CopyOutcome.Failed, Assert.Single(interrupted.Items).Outcome);
        Assert.Contains("两处均有文件", interrupted.Items[0].Message);
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(source));

        // Retry reconciles both ends by SHA-256 and deletes original source instead of creating contract (1).pdf.
        var retryPlan = await service.RetryPlanAsync(interrupted, default);
        Assert.Equal(target, Assert.Single(retryPlan.Items).Target);
        var reconciled = await service.ExecuteAsync(retryPlan, null, default);
        Success(reconciled, 1);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(source));
        Assert.Single(Directory.GetFiles(Destination));
    }

    [Fact]
    public async Task Skeleton_creates_renamed_empty_directory_tree_without_copying_files()
    {
        FileAt("2026-10项目/财务报表/华东区/汇总.xlsx", "sheet");
        Directory.CreateDirectory(Path.Combine(root, "2026-10项目", "空归档夹"));

        var request = new CopyRequest([Path.Combine(root, "2026-10项目")], Destination, CopyLayout.Skeleton)
        {
            ReplaceSkeletonText = true,
            SkeletonFind = "2026-10",
            SkeletonReplace = "2026-11"
        };
        var plan = await Service.PlanAsync(request, null, default);
        var report = await Service.ExecuteAsync(plan, null, default);

        Assert.True(Directory.Exists(Path.Combine(Destination, "2026-11项目", "财务报表", "华东区")));
        Assert.True(Directory.Exists(Path.Combine(Destination, "2026-11项目", "空归档夹")));
        Assert.Empty(Directory.EnumerateFiles(Destination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Classify_reads_jpeg_exif_capture_date_and_matches_existing_target_folders()
    {
        var photo = Path.Combine(root, "wechat", "mmexport.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(photo)!);
        File.WriteAllBytes(photo, CreateMinimalExifJpeg("2025:07:15 14:30:00\0"));
        File.SetLastWriteTime(photo, new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Local));

        var exifPlan = await Service.PlanAsync(new([photo], Destination, CopyLayout.Classify)
        {
            ClassifyBy = ClassifyPrimary.FileType,
            SubDateLayer = ClassifyDateGranularity.YearMonth,
            PreferMediaDate = true
        }, null, default);

        Assert.Equal(Path.Combine(Destination, "图片", "2025-07", "mmexport.jpg"), Assert.Single(exifPlan.Items).Target);

        // Verify disk discovery of existing target subfolders for NameMatch
        Directory.CreateDirectory(Path.Combine(Destination, "张三"));
        var hrFile = FileAt("hr/张三-劳动合同.pdf", "pdf");
        var unmatched = FileAt("hr/赵六-劳动合同.pdf", "pdf");
        var matchPlan = await Service.PlanAsync(new([hrFile, unmatched], Destination, CopyLayout.Classify)
        {
            ClassifyBy = ClassifyPrimary.NameMatch,
            NameMatchRule = NameMatchRule.ExistingFolderFirst
        }, null, default);

        Assert.Equal(Path.Combine(Destination, "张三", "张三-劳动合同.pdf"), matchPlan.Items[0].Target);
        Assert.Equal(CopyAction.Skip, matchPlan.Items[1].Action);
    }

    private static byte[] CreateMinimalExifJpeg(string asciiDate20Bytes)
    {
        // Minimal valid JPEG SOI + APP1 Exif (little-endian TIFF IFD0 -> ExifIFD tag 0x9003 DateTimeOriginal) + EOI
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8, 0xFF, 0xE1]);
        var dateBytes = System.Text.Encoding.ASCII.GetBytes(asciiDate20Bytes);
        using var tiff = new MemoryStream();
        // Little-endian 'II', magic 42, IFD0 offset = 8
        tiff.Write([(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0]);
        // IFD0: 1 entry (0x8769 ExifIFD pointer -> offset 26), next IFD = 0
        tiff.Write([1, 0, 0x69, 0x87, 4, 0, 1, 0, 0, 0, 26, 0, 0, 0, 0, 0, 0, 0]);
        // ExifIFD at offset 26: 1 entry (0x9003 DateTimeOriginal, ASCII=2, count=20 -> offset 44), next IFD = 0
        tiff.Write([1, 0, 0x03, 0x90, 2, 0, 20, 0, 0, 0, 44, 0, 0, 0, 0, 0, 0, 0]);
        // Offset 44: 20-byte ASCII date
        tiff.Write(dateBytes);
        var payload = tiff.ToArray();
        var segLen = 2 + 6 + payload.Length;
        ms.Write([(byte)(segLen >> 8), (byte)(segLen & 0xFF)]);
        ms.Write([(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0]);
        ms.Write(payload);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }

    private sealed class RealBufferTransfer : ICopyFileTransfer
    {
        public void Copy(string source, string temporary, Action<long> progress, CancellationToken token)
        {
            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(temporary, bytes);
            progress(bytes.Length);
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newName, string existing, nint security);
    private sealed class InlineProgress(Action<ProcessingProgress> action) : IProgress<ProcessingProgress> { public void Report(ProcessingProgress value) => action(value); }
    private sealed class DiskFull : ICopyFileTransfer
    { public void Copy(string source, string temporary, Action<long> progress, CancellationToken token) { File.WriteAllText(temporary, "partial"); throw new IOException("disk full", unchecked((int)0x80070070)); } }
    private sealed class Disconnected : ICopyFileTransfer
    { public void Copy(string source, string temporary, Action<long> progress, CancellationToken token) { File.WriteAllText(temporary, "partial"); throw new IOException("disconnected", unchecked((int)0x8007048f)); } }
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("FileAssistant-copy-test-")) throw new InvalidOperationException("Unexpected test cleanup path.");
        Directory.Delete(full, true);
    }
}
