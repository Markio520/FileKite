using System.ComponentModel;
using System.Diagnostics;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Infrastructure.FileSystem;

public sealed class SpaceScanner(string? excludedDirectory = null) : ISpaceScanner, IStreamingSpaceScanner
{
    private const int MaxIssueDetails = 100;

    public IReadOnlyList<SpaceDrive> GetDrives() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.Ram)
        .Select(drive => new SpaceDrive(drive.Name, drive.DriveType switch
        {
            DriveType.Network => drive.Name + "  网络驱动器",
            DriveType.Removable => drive.Name + "  可移动磁盘",
            _ => drive.Name + "  磁盘"
        })).ToArray();

    public Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        => Task.Run(() => Scan(root, progress, token), token);

    public SpaceSnapshot Scan(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
    {
        var rows = new DirectoryScanEntries();
        return Scan(root, progress, token, rows).WithEntries(rows.MemoryRows);
    }

    // Ordinary folder, UNC and non-elevated fallback scans retain only directories and hard-link
    // bookkeeping. The transient file is closed and removed on success, cancellation or failure.
    public T ScanTo<T>(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token,
        Func<SpaceScanMetadata, int, IEnumerable<SpaceEntry>, T> consume)
        => DirectoryScanEntries.Stream(rows => Scan(root, progress, token, rows), consume, token);

    public Task<T> ScanToAsync<T>(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token,
        Func<SpaceScanMetadata, int, IEnumerable<SpaceEntry>, T> consume)
        => Task.Run(() => ScanTo(root, progress, token, consume), token);

    private SpaceScanMetadata Scan(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token,
        DirectoryScanEntries rows)
    {
        root = Normalize(root);
        CheckAncestors(root);
        token.ThrowIfCancellationRequested();
        var metadata = SpaceFileMetadata.Read(root);
        if (!metadata.Stamp.IsDirectory || metadata.Stamp.IsReparsePoint || metadata.Cloud)
            throw new IOException("请选择可直接读取的磁盘或文件夹。");

        rows.Add(new(Path.GetFileName(root) is { Length: > 0 } name ? name : root, -1, metadata.Stamp, 0, 0, 0), -1);
        return new ScanState(root, rows, progress, token, excludedDirectory).Run(metadata.Stamp);
    }

    internal static string Normalize(string path)
    {
        var full = CopyPlanner.Normalize(path.Trim());
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("请输入常规磁盘或网络共享路径。");
        return full;
    }

    internal static void CheckAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        // A bare SMB share may reject GetFileAttributes. Its directory handle still validates it.
        if (!root.StartsWith(@"\\", StringComparison.Ordinal)) Check(root);
        for (int start = root.Length; start < full.Length;)
        {
            int end = full.IndexOf(Path.DirectorySeparatorChar, start);
            if (end < 0) { Check(full); break; }
            if (end != start) Check(full[..end]);
            start = end + 1;
        }

        static void Check(string part)
        {
            var attributes = File.GetAttributes(part);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || SpaceFileMetadata.IsOffline(attributes))
                throw new SpaceTraversalBoundaryException(part);
        }
    }

    internal static SpaceVolume? ReadVolume(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return new(Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName), drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception error) when (IsFileError(error)) { return null; }
    }

    internal static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or NotSupportedException;

    internal static SpaceEntry MakeEntry(string name, int parent, SpaceFileMetadata.Metadata metadata)
    {
        var stamp = metadata.Stamp;
        var flags = metadata.Cloud ? SpaceEntryFlags.Cloud : SpaceEntryFlags.None;
        if (stamp.IsReparsePoint && (!metadata.Cloud || stamp.IsDirectory) || metadata.Cloud && stamp.IsDirectory)
            return new(name, parent, stamp, stamp.IsDirectory ? 0 : metadata.Length, -1, 0,
                flags | SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped);
        if (stamp.IsDirectory) return new(name, parent, stamp, 0, 0, 0, flags);

        if (stamp.Links > 1) flags |= SpaceEntryFlags.Shared;
        var allocated = stamp.Links > 1 && !stamp.Identity.IsKnown ? -1 : metadata.Allocation;
        return new(name, parent, stamp, metadata.Length, allocated, Math.Max(0, allocated), flags);
    }

    private sealed class ScanState(string root, DirectoryScanEntries entries, IProgress<SpaceScanProgress>? progress, CancellationToken token, string? excludedDirectory)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly SpaceDirectoryReader directoryReader = new();
        private const int ProbeBatchSize = 256;
        private readonly SpaceName[] probeNames = new SpaceName[ProbeBatchSize];
        private readonly SpaceFileMetadata.Metadata?[] probed = new SpaceFileMetadata.Metadata?[ProbeBatchSize];
        private readonly ParallelOptions probeOptions = new() { MaxDegreeOfParallelism = 3, CancellationToken = token };
        private readonly bool parallelFileStat = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) &&
            Environment.ProcessorCount >= 4 && !root.StartsWith(@"\\", StringComparison.Ordinal);
        private readonly string? excludedName = Path.GetFileName(excludedDirectory);
        private readonly string? spoolName = Path.GetFileName(entries.SpoolPath);
        private sealed class Branch(string name) { public string Name => name; public long Bytes; }
        // Siblings share their parent's path. Visited names and stamps can be
        // released as soon as the last pending directory no longer needs them.
        private readonly record struct PendingDirectory(int Id, int Slot, string ParentPath, string Name, SpaceStamp Stamp, Branch? Top);
        private readonly Stack<PendingDirectory> pending = new();
        private readonly Dictionary<SpaceIdentity, (long Changed, long Allocated)> sharedFiles = [];
        private readonly List<Branch> topSizes = [];
        private readonly Dictionary<SpaceIssueKind, int> counts = [];
        private readonly List<SpaceIssue> issues = [];
        private int files;
        private int folders = 1;
        private int skipped;
        private long bytes;
        private long lastReport = -300;

        public SpaceScanMetadata Run(SpaceStamp stamp)
        {
            var volume = ReadVolume(root);
            pending.Push(new(0, 0, string.Empty, root, stamp, null));
            var cancelled = false;
            try
            {
                while (pending.TryPop(out var next))
                {
                    ReadDirectory(next);
                    Report();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
                foreach (var directory in pending) entries.MarkPartial(directory.Slot);
                Issue(root, SpaceIssueKind.Cancelled, "扫描已取消，保留已读取的部分。");
            }

            entries.Finish();
            return new(root, files, folders, volume, counts, issues.AsReadOnly(), clock.Elapsed, cancelled);
        }

        private void ReadDirectory(PendingDirectory node)
        {
            var directory = node.Id == 0 ? root : Path.Join(node.ParentPath, node.Name);
            var original = node.Stamp;
            try
            {
                token.ThrowIfCancellationRequested();
                using var handle = SpaceFileMetadata.Open(directory, listDirectory: true);
                var start = SpaceFileMetadata.ReadDirectoryStamp(handle);
                if (!start.IsDirectory || start.IsReparsePoint || SpaceFileMetadata.IsOffline(start.Attributes) ||
                    original.Identity.IsKnown && original.Identity != start.Identity)
                {
                    entries.MarkPartial(node.Slot);
                    Issue(directory, SpaceIssueKind.Changed, "目录已被替换或变为链接。");
                    return;
                }
                if (start.Changed != original.Changed || start.Modified != original.Modified)
                {
                    entries.MarkPartial(node.Slot);
                    Issue(directory, SpaceIssueKind.Changed, "目录在扫描期间有变化。");
                }
                var directoryHandle = handle.DangerousGetHandle();
                int buffered = 0;
                try
                {
                    foreach (var child in directoryReader.Read(handle, directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!parallelFileStat)
                        {
                            ReadChild(node, directory, directoryHandle, child, false, null);
                            if ((entries.Count & 255) == 0) Report();
                            continue;
                        }
                        probeNames[buffered++] = child;
                        if (buffered < ProbeBatchSize) continue;
                        buffered = 0;
                        ReadBatch(node, directory, directoryHandle, ProbeBatchSize);
                    }
                }
                finally
                {
                    if (buffered > 0 && !token.IsCancellationRequested) ReadBatch(node, directory, directoryHandle, buffered);
                }

                // Reopening the path also detects replacement while the original handle stayed valid.
                // For a known identity the returned times describe the very same directory.
                bool changed;
                if (start.Identity.IsKnown)
                {
                    var end = SpaceFileMetadata.ReadForScan(directory, start.Attributes).Stamp;
                    changed = end.Identity != start.Identity || end.Modified != start.Modified || end.Changed != start.Changed;
                }
                else
                {
                    var end = SpaceFileMetadata.ReadDirectoryTimes(handle);
                    changed = end.Modified != start.Modified || end.Changed != start.Changed;
                }
                if (changed)
                {
                    entries.MarkPartial(node.Slot);
                    Issue(directory, SpaceIssueKind.Changed, "目录在读取期间有变化。");
                }
            }
            catch (OperationCanceledException) { entries.MarkPartial(node.Slot); throw; }
            catch (Exception error) when (IsFileError(error))
            {
                var kind = Classify(error);
                entries.MarkPartial(node.Slot, kind == SpaceIssueKind.Access);
                Issue(directory, kind, Describe(error));
            }
        }

        private void ReadBatch(PendingDirectory node, string directory, nint directoryHandle, int count)
        {
            // Only metadata probes run concurrently. Enumeration, hard-link ownership and the
            // snapshot writer keep filesystem order, including across batch boundaries.
            var prefetch = count >= 64;
            if (prefetch)
                Parallel.For(0, count, probeOptions, i =>
                {
                    var name = probeNames[i];
                    probed[i] = !string.Equals(name.Name, excludedName, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name.Name, spoolName, StringComparison.OrdinalIgnoreCase) &&
                        SpaceFileMetadata.TryReadRelative(directoryHandle, name.Name, name.Attributes, out var result) ? result : null;
                });
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                ReadChild(node, directory, directoryHandle, probeNames[i], prefetch, prefetch ? probed[i] : null);
                if ((entries.Count & 255) == 0) Report();
            }
        }

        private void ReadChild(PendingDirectory node, string directory, nint directoryHandle, SpaceName child, bool prefetched, SpaceFileMetadata.Metadata? probe)
        {
            var index = entries.Count;
            var parent = node.Id;
            string? path = null;
            string ChildPath() => path ??= Path.Join(directory, child.Name);
            if (string.Equals(child.Name, excludedName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ChildPath(), excludedDirectory, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(child.Name, spoolName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ChildPath(), entries.SpoolPath, StringComparison.OrdinalIgnoreCase)) return;
            SpaceEntry entry;
            try
            {
                SpaceFileMetadata.Metadata metadata;
                if (probe.HasValue) metadata = probe.Value;
                else if (prefetched || !SpaceFileMetadata.TryReadRelative(directoryHandle, child.Name, child.Attributes, out metadata))
                    metadata = SpaceFileMetadata.ReadForScan(ChildPath(), child.Attributes);
                entry = MakeEntry(child.Name, parent, metadata);
                var identity = metadata.Stamp.Identity;
                if ((metadata.Stamp.Attributes & FileAttributes.Directory) != (child.Attributes & FileAttributes.Directory) ||
                    identity.IsKnown && (child.IdLow != 0 || child.IdHigh != 0) &&
                    (identity.Low != child.IdLow || identity.High != child.IdHigh))
                {
                    entry = entry with { Flags = entry.Flags | SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped, CountedBytes = 0, AllocatedBytes = -1 };
                    Issue(ChildPath(), SpaceIssueKind.Changed, "项目在扫描期间被替换。");
                }
                else if (entry.IsSkipped)
                {
                    Issue(ChildPath(), entry.IsCloud ? SpaceIssueKind.CloudDirectory : SpaceIssueKind.Link,
                        entry.IsCloud ? "云端目录只读取自身元数据。" : "链接或其他重解析点，已跳过目标。");
                }
                else if (!entry.IsDirectory && (entry.AllocatedBytes < 0 || entry.LogicalBytes < 0))
                {
                    entry = entry with { Flags = entry.Flags | SpaceEntryFlags.Partial };
                    Issue(ChildPath(), SpaceIssueKind.Metadata, "无法核实实际占用或共享文件身份。");
                }
                else if (!entry.IsDirectory && entry.IsShared)
                {
                    if (!sharedFiles.TryAdd(entry.Stamp.Identity, (entry.Stamp.Changed, entry.AllocatedBytes)))
                    {
                        var first = sharedFiles[entry.Stamp.Identity];
                        entry = entry with { CountedBytes = 0 };
                        if (entry.Stamp.Changed != first.Changed || entry.AllocatedBytes != first.Allocated)
                        {
                            entry = entry with { Flags = entry.Flags | SpaceEntryFlags.Partial };
                            Issue(ChildPath(), SpaceIssueKind.Changed, "同一份硬链接数据在扫描期间有变化。");
                        }
                    }
                }
            }
            catch (Exception error) when (IsFileError(error))
            {
                var stamp = new SpaceStamp(default, child.Modified, 0, child.Attributes, 0);
                entry = new(child.Name, parent, stamp, stamp.IsDirectory ? 0 : child.Length, -1, 0,
                    SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped |
                    (Classify(error) == SpaceIssueKind.Access ? SpaceEntryFlags.AccessDenied : 0) |
                    (SpaceFileMetadata.IsOffline(child.Attributes) ? SpaceEntryFlags.Cloud : SpaceEntryFlags.None));
                if (Classify(error) == SpaceIssueKind.Access && node.Stamp.Identity.IsKnown)
                {
                    entry = new ListedSpaceFile(child.Name, child.IdLow, child.IdHigh, child.Attributes, child.Length,
                        child.Modified, child.Allocation, child.Changed, node.Stamp.Identity.Volume).Restricted(parent);
                    if (entry.AllocatedBytes >= 0 && !sharedFiles.TryAdd(entry.Stamp.Identity, (child.Changed, child.Allocation)))
                        entry = entry with { CountedBytes = 0 };
                }
                Issue(ChildPath(), Classify(error), Describe(error));
            }

            var slot = entries.Add(entry, node.Slot);
            if (entry.IsDirectory)
            {
                folders++;
                var branch = node.Top;
                if (parent == 0) { branch = new(entry.Name); topSizes.Add(branch); }
                if (!entry.IsSkipped) pending.Push(new(index, slot, directory, entry.Name, entry.Stamp, branch));
            }
            else
            {
                files++;
                bytes += entry.CountedBytes;
                if (node.Top is { } top) top.Bytes += entry.CountedBytes;
            }
        }

        private void Issue(string path, SpaceIssueKind kind, string message)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (kind != SpaceIssueKind.Cancelled) skipped++;
            if (issues.Count < MaxIssueDetails) issues.Add(new(path, kind, message));
        }

        private void Report()
        {
            if (progress is null || clock.ElapsedMilliseconds - lastReport < 300) return;
            lastReport = clock.ElapsedMilliseconds;
            var largest = topSizes.Where(branch => branch.Bytes > 0).OrderByDescending(branch => branch.Bytes).Take(6)
                .Select(branch => new SpaceProgressFolder(branch.Name, branch.Bytes)).ToArray();
            progress.Report(new(files, folders, skipped, bytes, clock.Elapsed, largest));
        }

        private static SpaceIssueKind Classify(Exception error)
        {
            var code = error is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;
            return code switch
            {
                2 or 3 or 303 or 1168 => SpaceIssueKind.Changed,
                5 or 32 or 33 => SpaceIssueKind.Access,
                _ => SpaceIssueKind.Metadata
            };
        }

        private static string Describe(Exception error) => Classify(error) switch
        {
            SpaceIssueKind.Access => "权限不足或项目正在使用。",
            SpaceIssueKind.Changed => "项目已移动、删除或正在变化。",
            _ => "无法读取元数据。" + error.Message
        };
    }
}
