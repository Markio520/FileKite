using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Infrastructure.Cleanup;
using Microsoft.Win32;

namespace FileAssistant.Infrastructure.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FileAssistant-CleanupTests", Guid.NewGuid().ToString("N"));
    private readonly FileCleanupEngine engine = new();
    public CleanupTests() => Directory.CreateDirectory(directory);
    private string Write(string name, string value = "fixture")
    {
        var path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); return path;
    }

    [Fact] public void PlanCollapsesChildrenAndRefusesProtectedRoots()
    {
        var file = Write("child/data.txt");
        var plan = engine.Plan([directory, file, Path.GetPathRoot(directory)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)], null, default);
        Assert.Single(plan.Roots); Assert.Equal(2, plan.Issues.Count); Assert.Equal(7, plan.Roots[0].Bytes);
    }

    [Fact] public void ChangedDescendantRejectsWholeRoot()
    {
        var file = Write("child/data.txt"); var plan = engine.Plan([directory], null, default);
        File.WriteAllText(file, "modified after preview");
        var result = Assert.Single(engine.Run(plan, CleanupMethod.Permanent, null, default));
        Assert.Equal(CleanupStatus.Failed, result.Status); Assert.Empty(result.RemovedPaths); Assert.True(File.Exists(file));
    }

    [Fact] public void PermanentDeleteHandlesReadOnlyFilesAndNestedDirectories()
    {
        var file = Write("child/deep/data.txt"); File.SetAttributes(file, FileAttributes.ReadOnly);
        var plan = engine.Plan([directory], null, default);
        var result = Assert.Single(engine.Run(plan, CleanupMethod.Permanent, null, default));
        Assert.True(result.Status == CleanupStatus.Succeeded, result.Message); Assert.False(Directory.Exists(directory));
    }

    [Fact] public void CancellationDoesNotDeleteAnything()
    {
        var file = Write("stay.txt"); var plan = engine.Plan([file], null, default);
        var result = Assert.Single(engine.Run(plan, CleanupMethod.Permanent, null, new CancellationToken(true)));
        Assert.Equal(CleanupStatus.Cancelled, result.Status); Assert.True(File.Exists(file));
    }

    [Fact] public void CancellingBetweenTargetsPreservesCompletedResults()
    {
        var first = Write("first.txt"); var second = Write("second.txt"); var plan = engine.Plan([first, second], null, default);
        using var cancellation = new CancellationTokenSource();
        var results = engine.Run(plan, CleanupMethod.Permanent, new ImmediateProgress(value => { if (value.Completed == 1) cancellation.Cancel(); }), cancellation.Token);
        Assert.Equal(CleanupStatus.Succeeded, results[0].Status); Assert.Equal(CleanupStatus.Cancelled, results[1].Status);
        Assert.False(File.Exists(first)); Assert.True(File.Exists(second)); Assert.Equal(new[] { first }, results[0].RemovedPaths);
    }

    [Fact] public void JunctionInsideTargetRejectsPlanAndRetainsDestination()
    {
        var retained = Write("destination/keep.txt"); var selected = Directory.CreateDirectory(Path.Combine(directory, "selected")).FullName;
        var link = Path.Combine(selected, "link");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { Arguments = $"/d /c mklink /J \"{link}\" \"{Path.GetDirectoryName(retained)}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try { var plan = engine.Plan([selected], null, default); Assert.Empty(plan.Roots); Assert.Single(plan.Issues); Assert.True(File.Exists(retained)); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private sealed class ImmediateProgress(Action<CleanupProgress> action) : IProgress<CleanupProgress> { public void Report(CleanupProgress value) => action(value); }

    [Fact] public void LockedFileReportsFailureWithoutFallback()
    {
        var file = Write("locked.txt"); var plan = engine.Plan([file], null, default);
        using var held = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = Assert.Single(engine.Run(plan, CleanupMethod.Permanent, null, default));
        Assert.Equal(CleanupStatus.Failed, result.Status); Assert.Empty(result.RemovedPaths); Assert.True(File.Exists(file));
    }

    [Fact] public void AddedFileRejectsStalePlan()
    {
        Write("original.txt"); var plan = engine.Plan([directory], null, default); Write("added.txt");
        Assert.Equal(CleanupStatus.Failed, Assert.Single(engine.Run(plan, CleanupMethod.Permanent, null, default)).Status);
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact] public void RegistryChangePreventsDeletionThenReviewedValueCanBeBackedUpAndRemoved()
    {
        var subkey = @"Software\FileAssistantCleanupTests\" + Guid.NewGuid().ToString("N");
        var path = "HKEY_CURRENT_USER\\" + subkey;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subkey);
            key.SetValue("sample", "before");
            var residue = new CleanupResidue("one", ResidueKind.RegistryValue, path, "sample", 5, "fixture",
                RegistryCleanup.Fingerprint(ResidueKind.RegistryValue, path, "sample", default), "fixture");
            key.SetValue("sample", "after");
            Assert.Equal(CleanupStatus.Failed, Assert.Single(RegistryCleanup.Remove([residue], CleanupMethod.Recycle, null, default).Results).Status);
            Assert.Equal("after", key.GetValue("sample"));
            residue = residue with { Fingerprint = RegistryCleanup.Fingerprint(residue.Kind, path, "sample", default) };
            var result = Assert.Single(RegistryCleanup.Remove([residue], CleanupMethod.Recycle, null, default).Results);
            Assert.Equal(CleanupStatus.Succeeded, result.Status); Assert.Null(key.GetValue("sample"));
            var backup = result.Message.Split("备份：")[1]; Assert.Contains("hex(1):", File.ReadAllText(backup));
            File.Delete(backup);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft")]
    public void SharedRegistryRootsCannotBePlanned(string path) => Assert.Throws<IOException>(() => RegistryCleanup.Fingerprint(ResidueKind.RegistryKey, path, null, default));

    [Fact] public void RegistryBackupRoundTripsSupportedValuesThroughWindowsImporter()
    {
        var subkey = @"Software\FileAssistantCleanupTests\" + Guid.NewGuid().ToString("N"); var path = "HKEY_CURRENT_USER\\" + subkey;
        string? backup = null;
        var binary = Enumerable.Range(0, 8192).Select(i => (byte)(i % 256)).ToArray();
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subkey))
            {
                key.SetValue("unicode", "中文\r\n\"quoted\"", RegistryValueKind.String);
                key.SetValue("expand", "%TEMP%\\fixture", RegistryValueKind.ExpandString);
                key.SetValue("multi", new[] { "one", "二" }, RegistryValueKind.MultiString);
                key.SetValue("dword", -1, RegistryValueKind.DWord); key.SetValue("qword", long.MinValue, RegistryValueKind.QWord);
                key.SetValue("binary", binary, RegistryValueKind.Binary); key.SetValue("none", new byte[] { 0, 1, 255 }, RegistryValueKind.None);
                using var child = key.CreateSubKey("child"); child.SetValue("", "default");
            }
            var residue = new CleanupResidue("fixture", ResidueKind.RegistryKey, path, null, 5, "fixture", RegistryCleanup.Fingerprint(ResidueKind.RegistryKey, path, null, default), "fixture");
            var result = Assert.Single(RegistryCleanup.Remove([residue], CleanupMethod.Recycle, null, default).Results);
            Assert.Equal(CleanupStatus.Succeeded, result.Status); backup = result.Message.Split("备份：")[1];
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("import"); start.ArgumentList.Add(backup);
            using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
            Assert.Equal(residue.Fingerprint, RegistryCleanup.Fingerprint(ResidueKind.RegistryKey, path, null, default));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); if (backup is not null) File.Delete(backup); }
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
}
