using System.ComponentModel;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.Windows;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.Tests;

public sealed class SpaceAnalysisTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FileAssistant-space-" + Guid.NewGuid().ToString("N"));

    public SpaceAnalysisTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void RelativeStatUsesTheEnumeratedParentAndPreservesLinkIdentityAndLength()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return;
        const string name = "多字节-🪁.bin";
        var left = Directory.CreateDirectory(Path.Join(root, "left")).FullName;
        var right = Directory.CreateDirectory(Path.Join(root, "right")).FullName;
        File.WriteAllBytes(Path.Join(left, name), new byte[97]);
        var target = Path.Join(right, name);
        File.WriteAllBytes(target, new byte[8193]);
        if (!CreateHardLinkW(Path.Join(right, "alias.bin"), target, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var handle = SpaceFileMetadata.Open(right, listDirectory: true);
        Assert.True(SpaceFileMetadata.TryReadRelative(handle.DangerousGetHandle(), name, FileAttributes.Normal, out var result));
        Assert.Equal(SpaceFileMetadata.Read(target), result);
        Assert.Equal(2u, result.Stamp.Links);
        Assert.False(SpaceFileMetadata.TryReadRelative(handle.DangerousGetHandle(), "missing", FileAttributes.Normal, out _));
        Assert.False(SpaceFileMetadata.TryReadRelative(handle.DangerousGetHandle(), name, FileAttributes.ReparsePoint, out _));
    }

    [Fact]
    public void WideDirectoryKeepsHardLinksAndTotalsAcrossMetadataBatches()
    {
        var payload = Path.Join(root, "0000.bin");
        File.WriteAllBytes(payload, new byte[12345]);
        for (var i = 1; i <= 300; i++) File.WriteAllBytes(Path.Join(root, $"{i:D4}.bin"), []);
        if (!CreateHardLinkW(Path.Join(root, "9999.bin"), payload, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var snapshot = new SpaceScanner().Scan(root, null, default);
        Assert.False(snapshot.IsPartial);
        Assert.Equal(302, snapshot.Files);
        var first = snapshot.Entries[snapshot.Find(payload)!.Value];
        var last = snapshot.Entries[snapshot.Find(Path.Join(root, "9999.bin"))!.Value];
        Assert.Equal(first.Stamp.Identity, last.Stamp.Identity);
        Assert.Equal(24690, snapshot.Entries[0].LogicalBytes);
        Assert.Equal(first.AllocatedBytes, first.CountedBytes + last.CountedBytes);
        Assert.Equal(first.AllocatedBytes, snapshot.Entries[0].CountedBytes);
    }

    [Fact]
    public void ByNameDirectoryScanReturnsTheSameIdentityAndTimestampsAsTheOpenHandle()
    {
        var path = Directory.CreateDirectory(Path.Join(root, "目录")).FullName;
        Directory.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.WriteAllText(Path.Join(path, "changed.txt"), "updated");
        var attributes = File.GetAttributes(path);
        var expected = SpaceFileMetadata.Read(path);
        var actual = SpaceFileMetadata.ReadForScan(path, attributes);
        Assert.True(actual.Stamp.IsDirectory);
        Assert.True(actual.Stamp.Identity.IsKnown);
        Assert.Equal(expected.Stamp, actual.Stamp);
        Assert.Equal(expected.Cloud, actual.Cloud);
    }

    [Fact]
    public void CountsHardLinksOnceAcrossBranchesAndFiltersByActualAllocation()
    {
        var originals = Directory.CreateDirectory(Path.Combine(root, "originals")).FullName;
        var aliases = Directory.CreateDirectory(Path.Combine(root, "aliases")).FullName;
        var file = Path.Combine(originals, "file.bin");
        File.WriteAllBytes(file, new byte[8001]);
        var alias = Path.Combine(aliases, "same.zip");
        if (!CreateHardLinkW(alias, file, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());

        var scan = new SpaceScanner().Scan(root, null, CancellationToken.None);
        Assert.False(scan.IsPartial);
        Assert.Equal(2, scan.Files);
        var a = scan.Entries[scan.Find(file)!.Value];
        var b = scan.Entries[scan.Find(alias)!.Value];
        Assert.True(a.IsShared);
        Assert.True(b.IsShared);
        Assert.Equal(a.Stamp.Identity, b.Stamp.Identity);
        Assert.Equal(8001, a.LogicalBytes);
        Assert.True(a.AllocatedBytes >= a.LogicalBytes);
        Assert.Equal(a.AllocatedBytes, b.AllocatedBytes);
        Assert.Equal(a.AllocatedBytes, scan.Entries[0].CountedBytes);
        Assert.Equal(a.AllocatedBytes, SpaceQueries.Types(scan, CancellationToken.None).Sum(group => group.Bytes));

        var matches = SpaceQueries.List(scan, 0, SpaceView.LargeFiles, new SpaceFilter(SpaceKind.Archive, Minimum: 8001),
            SpaceSort.Allocated, true, CancellationToken.None);
        Assert.Equal(alias, scan.PathOf(Assert.Single(matches.Indices)));
        Assert.Equal(a.AllocatedBytes, matches.Bytes);
        Assert.Equal(new[] { scan.Find(originals)!.Value }, scan.CollapseSelection([scan.Find(originals)!.Value, scan.Find(file)!.Value, 0]));
    }

    [Fact]
    public void SparseFileCountsAllocatedRangesRatherThanLogicalLength()
    {
        var path = Path.Combine(root, "sparse.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            if (!DeviceIoControl(stream.SafeFileHandle, 0x900C4, 0, 0, 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            stream.SetLength(32 * 1024 * 1024);
            var block = new byte[8192];
            Array.Fill(block, (byte)42);
            stream.Write(block);
            stream.Flush(flushToDisk: true);
        }
        var scan = new SpaceScanner().Scan(root, null, CancellationToken.None);
        Assert.False(scan.IsPartial);
        var sparse = scan.Entries[scan.Find(path)!.Value];
        Assert.Equal(32 * 1024 * 1024, sparse.LogicalBytes);
        Assert.InRange(sparse.AllocatedBytes, 1, 64 * 1024);
        Assert.Equal(DiskBytes(path), sparse.AllocatedBytes);
        Assert.Equal(sparse.AllocatedBytes, scan.Entries[0].CountedBytes);
    }

    [Fact]
    public void CompressedFileCountsPhysicalBytes()
    {
        var path = Path.Combine(root, "compressed.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            var block = new byte[128 * 1024];
            Array.Fill(block, (byte)42);
            stream.Write(block);
            stream.Flush(flushToDisk: true);
            var buffer = Marshal.AllocHGlobal(sizeof(short));
            try
            {
                Marshal.WriteInt16(buffer, 1);
                if (!DeviceIoControl(stream.SafeFileHandle, 0x9C040, buffer, sizeof(short), 0, 0, out _, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                stream.Flush(flushToDisk: true);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        var scan = new SpaceScanner().Scan(root, null, CancellationToken.None);
        Assert.False(scan.IsPartial);
        var file = scan.Entries[scan.Find(path)!.Value];
        Assert.Equal(128 * 1024, file.LogicalBytes);
        Assert.InRange(file.AllocatedBytes, 1, file.LogicalBytes / 2);
        Assert.Equal(DiskBytes(path), file.AllocatedBytes);
    }

    [Fact]
    public void CancellationKeepsAReadablePartialSnapshotAndDisablesRecycling()
    {
        var child = Directory.CreateDirectory(Path.Combine(root, "not-yet-scanned")).FullName;
        File.WriteAllBytes(Path.Combine(root, "seen.bin"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(child, "nested.bin"), new byte[8192]);
        using var source = new CancellationTokenSource();
        var scan = new SpaceScanner().Scan(root, new OnProgress(source.Cancel), source.Token);
        Assert.True(scan.Cancelled);
        Assert.Equal(1, scan.Files);
        Assert.True(scan.Entries[scan.Find(child)!.Value].IsPartial);
        Assert.Empty(scan.CollapseSelection(scan.Children(0)));
    }

    [Fact]
    public void GroupsOlderBuildsAndFiltersLargeFilesAcrossDirectoriesByDate()
    {
        var old = Directory.CreateDirectory(Path.Combine(root, "old-builds")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(root, "current")).FullName;
        var file = Path.Combine(current, "release.zip");
        File.WriteAllBytes(file, new byte[128 * 1024]);
        for (var i = 1; i <= 11; i++)
        {
            var build = Directory.CreateDirectory(Path.Combine(old, $"build-{i:00}")).FullName;
            var payload = Path.Combine(build, "release.zip");
            File.WriteAllBytes(payload, new byte[128 * 1024]);
            File.SetLastWriteTimeUtc(payload, DateTime.UtcNow.AddDays(-7));
        }

        var scan = new SpaceScanner().Scan(root, null, CancellationToken.None);
        var oldId = scan.Find(old)!.Value;
        var currentId = scan.Find(current)!.Value;
        Assert.Equal(11, scan.Children(oldId).Count());
        Assert.Equal(scan.Entries[currentId].CountedBytes * 11, scan.Entries[oldId].CountedBytes);
        var result = SpaceQueries.List(scan, 0, SpaceView.LargeFiles,
            new(SpaceKind.Archive, 128 * 1024, ModifiedFrom: DateTimeOffset.UtcNow.AddDays(-1)),
            SpaceSort.Allocated, true, CancellationToken.None);
        Assert.Equal(file, scan.PathOf(Assert.Single(result.Indices)));
        Assert.Equal(scan.Entries[currentId].CountedBytes, result.Bytes);
    }

    [Fact]
    public void JunctionIsReportedAndDoesNotDuplicateTree()
    {
        var target = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        File.WriteAllBytes(Path.Combine(target, "only.bin"), new byte[5000]);
        var link = Path.Combine(root, "junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var command = System.Diagnostics.Process.Start(start)!;
        command.WaitForExit();
        Assert.Equal(0, command.ExitCode);
        try
        {
            // The leaf may have become a junction after its parent was enumerated.
            var raced = SpaceFileMetadata.ReadForScan(link, FileAttributes.Directory);
            Assert.True(raced.Stamp.IsReparsePoint);
            Assert.Equal(SpaceFileMetadata.Read(link).Stamp.Identity, raced.Stamp.Identity);
            var blocked = Assert.Throws<SpaceTraversalBoundaryException>(() =>
                SpaceScanner.CheckAncestors(Path.Join(link, "not-on-this-side", "missing")));
            Assert.Equal(link, blocked.BoundaryPath);
            using var parent = SpaceFileMetadata.Open(root, listDirectory: true);
            Assert.False(SpaceFileMetadata.TryReadRelative(parent.DangerousGetHandle(), "junction", FileAttributes.Directory, out _));
            var scan = new SpaceScanner().Scan(root, null, CancellationToken.None);
            Assert.Equal(1, scan.Files);
            Assert.Equal(1, scan.IssueCounts[SpaceIssueKind.Link]);
            var id = scan.Find(link)!.Value;
            Assert.True(scan.Entries[id].IsSkipped);
            Assert.False(scan.Entries[id].CanRecycle);
            Assert.Empty(scan.Children(id));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ModifiedDescendantStopsDirectoryRecycle()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "selected", "child")).FullName;
        var child = Path.Combine(directory, "file.bin");
        File.WriteAllBytes(child, new byte[8192]);
        var scanner = new SpaceScanner();
        var scan = await scanner.ScanAsync(root, null, CancellationToken.None);
        var selected = scan.Find(Path.GetDirectoryName(directory)!)!.Value;
        Assert.True(scan.Entries[selected].CanRecycle);
        File.AppendAllText(child, "changed");
        var report = await new WindowsSpaceRecycler().RecycleAsync(scan, [selected], null, CancellationToken.None);
        var item = Assert.Single(report.Items);
        Assert.Equal(SpaceRecycleStatus.Failed, item.Status);
        Assert.Contains("变化", item.Message);
        Assert.True(File.Exists(child));
    }

    [Fact]
    public async Task ReplacedFileWithSameSizeAndWriteTimeIsRejected()
    {
        var path = Path.Combine(root, "selected.bin");
        File.WriteAllBytes(path, new byte[8192]);
        var scan = new SpaceScanner().Scan(root, null, CancellationToken.None);
        var modified = File.GetLastWriteTimeUtc(path);
        File.Move(path, Path.Combine(root, "original.bin"));
        File.WriteAllBytes(path, new byte[8192]);
        File.SetLastWriteTimeUtc(path, modified);
        var report = await new WindowsSpaceRecycler().RecycleAsync(scan, [scan.Find(path)!.Value], null, CancellationToken.None);
        Assert.Equal(SpaceRecycleStatus.Failed, Assert.Single(report.Items).Status);
        Assert.True(File.Exists(path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, nint input, int inputLength,
        nint output, int outputLength, out int returned, nint overlapped);

    private static long DiskBytes(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() is var error && error != 0) throw new Win32Exception(error);
        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    private sealed class OnProgress(Action action) : IProgress<SpaceScanProgress>
    {
        public void Report(SpaceScanProgress value) => action();
    }
}
