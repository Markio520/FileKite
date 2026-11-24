using System.Runtime.InteropServices;
using System.Text.Json;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.FileSystem.Ntfs;
using Xunit.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.Tests;

public sealed class NtfsLiveTests(ITestOutputHelper output)
{
    [LiveMftFact]
    [Trait("Category", "Mft")]
    public async Task CancellingBulkReadStopsWithoutStartingDirectoryFallback()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var progress = new InlineProgress(p => { if (p.Stage == "正在读取磁盘文件索引") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FastSpaceScanner(Environment.GetEnvironmentVariable("FILEASSISTANT_MFT_HELPER")!)
            .ScanAsync("D:\\", progress, cancellation.Token));
    }

    [LiveMftFact]
    [Trait("Category", "Mft")]
    public async Task ElevatedHelperScansRealVolumesAndMatchesFreshFileMetadata()
    {
        var helper = Environment.GetEnvironmentVariable("FILEASSISTANT_MFT_HELPER")!;
        var root = Path.Combine(Path.GetTempPath(), "FileAssistant-mft-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var item in new[] { ("resident.txt", 30), ("ordinary.bin", 8001), ("empty.txt", 0) })
            {
                using var file = new FileStream(Path.Combine(root, item.Item1), FileMode.CreateNew, FileAccess.Write);
                file.Write(new byte[item.Item2]); file.Flush(true);
            }
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Assert.True(CreateHardLinkW(Path.Combine(root, "nested", "alias.bin"), Path.Combine(root, "ordinary.bin"), 0));
            using (var sparse = new FileStream(Path.Combine(root, "sparse.bin"), FileMode.CreateNew, FileAccess.ReadWrite))
            {
                Assert.True(DeviceIoControl(sparse.SafeFileHandle, 0x900c4, [], 0, 0, 0, out _, 0));
                sparse.SetLength(32 * 1024 * 1024); sparse.Write(new byte[8192]); sparse.Flush(true);
            }
            using (var compressed = new FileStream(Path.Combine(root, "compressed.bin"), FileMode.CreateNew, FileAccess.ReadWrite))
            {
                compressed.Write(new byte[128 * 1024]); compressed.Flush(true);
                Assert.True(DeviceIoControl(compressed.SafeFileHandle, 0x9c040, [1, 0], 2, 0, 0, out _, 0));
                compressed.Flush(true);
            }
            var expected = new SpaceScanner().Scan(root, null, CancellationToken.None);
            foreach (var drive in new[] { "D:\\", Path.GetPathRoot(root)! }.Distinct())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var result = await new FastSpaceScanner(helper).ScanAsync(drive, null, timeout.Token);
                output.WriteLine(JsonSerializer.Serialize(new { drive, result.ScanMethod, result.ScanNotice, result.Files, result.Folders,
                    seconds = result.Elapsed.TotalSeconds, bytes = result.Entries[0].CountedBytes, result.IssueCounts, Issues = result.Issues.Take(5) }));
                Assert.Equal("NTFS 快速扫描", result.ScanMethod);
                Assert.True(result.IsWholeVolume);
                Assert.True(result.Files > 1000);
                var compared = 0;
                var differences = new List<string>();
                foreach (var i in Enumerable.Range(1, result.Entries.Count - 1).Where(i => !result.Entries[i].IsDirectory && !result.Entries[i].IsSkipped).Where((_, n) => n % 97 == 0).Take(1000))
                {
                    var entry = result.Entries[i]; var path = result.PathOf(i);
                    try
                    {
                        var metadata = SpaceFileMetadata.Read(path);
                        if (metadata.Stamp.Modified != entry.Stamp.Modified || metadata.Stamp.Changed != entry.Stamp.Changed) continue;
                        compared++;
                        if (metadata.Stamp != entry.Stamp || metadata.Length != entry.LogicalBytes || metadata.Allocation != entry.AllocatedBytes)
                            differences.Add($"{path}: raw={entry.Stamp}, {entry.LogicalBytes}/{entry.AllocatedBytes}; api={metadata.Stamp}, {metadata.Length}/{metadata.Allocation}");
                    }
                    catch (Exception error) when (SpaceScanner.IsFileError(error)) { }
                }
                output.WriteLine($"Compared {compared} stable metadata samples; mismatches {differences.Count}.");
                foreach (var difference in differences.Take(10)) output.WriteLine(difference);
                Assert.Empty(differences);
                if (drive != Path.GetPathRoot(root)) continue;
                Assert.NotNull(result.Find(root));
                foreach (var i in expected.Descendants(0))
                {
                    var id = result.Find(expected.PathOf(i)); Assert.NotNull(id);
                    var actual = result.Entries[id.Value]; var original = expected.Entries[i];
                    Assert.Equal(original.Stamp.Identity, actual.Stamp.Identity);
                    Assert.Equal(original.Stamp.Modified, actual.Stamp.Modified);
                    Assert.Equal(original.Stamp.Changed, actual.Stamp.Changed);
                    Assert.Equal(original.Stamp.Links, actual.Stamp.Links);
                    Assert.Equal(original.LogicalBytes, actual.LogicalBytes);
                    Assert.Equal(original.AllocatedBytes, actual.AllocatedBytes);
                }
                Assert.Equal(expected.Entries[0].CountedBytes, result.Entries[result.Find(root)!.Value].CountedBytes);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class LiveMftFactAttribute : FactAttribute
    {
        public LiveMftFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FILEASSISTANT_MFT_HELPER")))
                Skip = "Set FILEASSISTANT_MFT_HELPER to explicitly run the elevated full-volume acceptance.";
        }
    }
    private sealed class InlineProgress(Action<FileAssistant.Core.Features.SpaceAnalysis.SpaceScanProgress> action)
        : IProgress<FileAssistant.Core.Features.SpaceAnalysis.SpaceScanProgress>
    { public void Report(FileAssistant.Core.Features.SpaceAnalysis.SpaceScanProgress value) => action(value); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
}
