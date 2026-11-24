using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Infrastructure.Cleanup;
using FileAssistant.Infrastructure.Engines;
using FileAssistant.Infrastructure.FileSystem;
using ImageMagick;
using Microsoft.Win32;

namespace FileAssistant.Infrastructure.Tests;

public sealed class BatchRecoveryPruneTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("FileAssistant-prune-policy-").FullName;
    private string FileAt(string name, string data)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, data);
        return path;
    }

    [Fact]
    public async Task Copy_prune_discards_verified_expired_backups_but_preserves_targets_orphans_and_bad_records()
    {
        var source = FileAt("in/data.txt", "current");
        var target = FileAt("out/data.txt", "old");
        var logs = Path.Combine(root, "copy-logs");
        var service = new CopyService(logs);
        var request = new CopyRequest([source], Path.GetDirectoryName(target)!, Conflict: CopyConflictPolicy.Confirm);
        var preview = await service.PlanAsync(request, null, default);
        var approved = await service.DecideAsync(preview, Assert.Single(preview.Items).Id, CopyDecision.Overwrite, default);
        Assert.Single((await service.ExecuteAsync(approved, null, default)).Outputs);
        var record = Assert.Single(await service.RecoveriesAsync(default));
        var unfinished = Guid.NewGuid();
        var orphan = record with { Id = unfinished, Backup = CopyRecoveryStore.BackupPath(target, unfinished) };
        var orphanLog = FileAt("copy-logs/" + unfinished.ToString("N") + ".json", JsonSerializer.Serialize(orphan));
        var broken = FileAt("copy-logs/" + Guid.NewGuid().ToString("N") + ".json", "{");
        var standalone = FileAt("out/.fileassistant-backup-" + Guid.NewGuid().ToString("N") + ".bak", "independent");
        await service.PruneRecoveryAsync(record.Created.AddDays(-1), default);
        Assert.True(File.Exists(record.Backup));
        var parked = target + ".parked";
        File.Move(target, parked);
        await service.PruneRecoveryAsync(DateTimeOffset.MaxValue, default);
        Assert.True(File.Exists(record.Backup));
        File.WriteAllText(target, "user edited");
        await service.PruneRecoveryAsync(DateTimeOffset.MaxValue, default);
        Assert.True(File.Exists(record.Backup));
        File.Move(parked, target, overwrite: true);
        await service.PruneRecoveryAsync(DateTimeOffset.MaxValue, default);
        Assert.Equal("current", File.ReadAllText(target));
        Assert.Equal("current", File.ReadAllText(source));
        Assert.False(File.Exists(record.Backup));
        Assert.False(File.Exists(Path.Combine(logs, record.Id.ToString("N") + ".json")));
        Assert.True(File.Exists(orphanLog)); Assert.True(File.Exists(broken)); Assert.True(File.Exists(standalone));
    }

    [Fact]
    public async Task Naming_prune_removes_only_expired_stable_journals_and_preserves_file_contents()
    {
        var logs = Path.Combine(root, "rename-logs");
        var original = FileAt("name.txt", "original");
        var service = new NamingService(logs);
        var plan = await service.PlanAsync(new(NamingMode.Rename, [original], new(Prefix: "new-")), default);
        var result = await service.ExecuteAsync(plan, null, default);
        var id = result.RecoveryId!;
        var stable = Path.Combine(logs, id + ".json");
        var store = new NamingJournalStore(logs);
        var pending = store.Read(id);
        pending.Id = Guid.NewGuid().ToString("N");
        pending.Restoring = true;
        store.Save(pending);
        var damaged = FileAt("rename-logs/" + Guid.NewGuid().ToString("N") + ".json", "{");
        var unrelated = FileAt("rename-logs/other.txt", "leave me");
        await service.PruneRecoveryAsync(pending.Created.AddDays(-1), default);
        Assert.True(File.Exists(stable));
        var output = result.Items[0].ActualPath!;
        File.Move(output, output + ".parked");
        await service.PruneRecoveryAsync(DateTimeOffset.MaxValue, default);
        Assert.True(File.Exists(stable));
        File.Move(output + ".parked", output);
        await service.PruneRecoveryAsync(DateTimeOffset.MaxValue, default);
        Assert.False(File.Exists(stable));
        Assert.True(File.Exists(Path.Combine(logs, pending.Id + ".json")));
        Assert.True(File.Exists(damaged));
        Assert.True(File.Exists(unrelated));
        Assert.Equal("original", File.ReadAllText(result.Items[0].ActualPath!));
    }

    [Fact]
    public async Task Completed_nested_rename_journals_can_be_pruned_after_parents_move()
    {
        var document = FileAt("parent/child/sample.txt", "contents");
        var logs = Path.Combine(root, "nested-logs");
        var service = new NamingService(logs);
        var plan = await service.PlanAsync(new(NamingMode.Rename, [Path.Combine(root, "parent"), document],
            new(Prefix: "x-"), FolderContents: true, IncludeSubfolders: true), default);
        var result = await service.ExecuteAsync(plan, null, default);
        var journal = Path.Combine(logs, result.RecoveryId + ".json");
        Assert.True(File.Exists(journal));
        await service.PruneRecoveryAsync(DateTimeOffset.MaxValue, default);
        Assert.False(File.Exists(journal));
        Assert.Equal("contents", File.ReadAllText(Path.Combine(root, "x-parent", "x-child", "x-sample.txt")));
    }

    [Fact]
    public async Task Preview_cache_clears_top_level_png_only_and_rejects_a_linked_cache()
    {
        var image = Path.Combine(root, "original.jpg");
        using (var picture = new MagickImage(MagickColors.Green, 48, 36)) picture.Write(image);
        var cache = Path.Combine(root, "cache");
        var service = new LocalFileProcessingService(new("unused", cache));
        await service.PreviewAsync(image, 128, default);
        Assert.Single(Directory.GetFiles(cache, "*.png"));
        var retained = FileAt("cache/metadata.txt", "keep");
        var nested = FileAt("cache/nested/item.png", "nested");
        await service.ClearPreviewCacheAsync(default);
        Assert.Empty(Directory.GetFiles(cache, "*.png"));
        Assert.True(File.Exists(retained)); Assert.True(File.Exists(nested)); Assert.True(File.Exists(image));
        var linked = Path.Combine(root, "linked-cache");
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", linked, cache }) info.ArgumentList.Add(arg);
        using var mklink = Process.Start(info)!; await mklink.WaitForExitAsync(); Assert.Equal(0, mklink.ExitCode);
        try
        {
            var saved = FileAt("cache/keep.png", "independent");
            var linkedService = new LocalFileProcessingService(new("unused", linked));
            await Assert.ThrowsAsync<IOException>(() => linkedService.ClearPreviewCacheAsync(default));
            Assert.Equal("independent", File.ReadAllText(saved));
        }
        finally { Directory.Delete(linked, false); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registry_backup_option_controls_reg_file_before_removing_only_the_created_fixture(bool backup)
    {
        var owner = "FileAssistant-Disposable-" + Guid.NewGuid().ToString("N");
        var relative = @"Software\" + owner;
        const string value = "临时样本";
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        Assert.Null(hive.OpenSubKey(relative));
        using (var key = hive.CreateSubKey(relative + @"\OnlyTest")) key.SetValue("fixture", value);
        try
        {
            var path = @"HKEY_CURRENT_USER\" + relative + @"\OnlyTest";
            var fingerprint = RegistryCleanup.Fingerprint(ResidueKind.RegistryValue, path, "fixture", default);
            var residue = new CleanupResidue(Guid.NewGuid().ToString("N"), ResidueKind.RegistryValue, path, "fixture", 5, "temporary", fingerprint, owner);
            var backups = Path.Combine(root, "reg-backups");
            if (backup)
            {
                var occupied = FileAt("unwritable-backup-location", "fixture");
                var failed = RegistryCleanup.Remove([residue], CleanupMethod.Recycle, null, default, backup, occupied);
                Assert.Equal(CleanupStatus.Failed, Assert.Single(failed.Results).Status);
                Assert.Equal(fingerprint, RegistryCleanup.Fingerprint(ResidueKind.RegistryValue, path, "fixture", default));
            }
            var response = RegistryCleanup.Remove([residue], CleanupMethod.Recycle, null, default, backup, backups);
            var item = Assert.Single(response.Results);
            Assert.Equal(CleanupStatus.Succeeded, item.Status);
            using var remaining = hive.OpenSubKey(relative + @"\OnlyTest");
            Assert.NotNull(remaining);
            Assert.DoesNotContain("fixture", remaining.GetValueNames());
            if (backup)
            {
                var reg = Assert.Single(Directory.GetFiles(backups, "*.reg", SearchOption.AllDirectories));
                var text = File.ReadAllText(reg, Encoding.Unicode);
                Assert.Contains("Windows Registry Editor Version 5.00", text);
                Assert.Contains("fixture", text);
                Assert.Contains(reg, item.Message);
            }
            else Assert.False(Directory.Exists(backups));
        }
        finally { hive.DeleteSubKeyTree(relative, throwOnMissingSubKey: false); }
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(root).StartsWith("FileAssistant-prune-policy-", StringComparison.Ordinal)) Directory.Delete(root, true);
    }
}
