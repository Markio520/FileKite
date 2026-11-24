using FileAssistant.Core.Features.Search;

namespace FileAssistant.Core.Features.SpaceAnalysis;

[Flags]
public enum SpaceEntryFlags : byte
{
    None = 0,
    Partial = 1,
    Skipped = 2,
    Cloud = 4,
    Shared = 8,
    ContainsUnverifiedItems = 16,
    AccessDenied = 32
}

public readonly record struct SpaceIdentity(ulong Volume, ulong Low, ulong High)
{
    public bool IsKnown => Low != 0 || High != 0;
}

public readonly record struct SpaceStamp(SpaceIdentity Identity, long Modified, long Changed, FileAttributes Attributes, uint Links)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}

public readonly record struct SpaceEntry(string Name, int Parent, SpaceStamp Stamp, long LogicalBytes, long AllocatedBytes,
    long CountedBytes, SpaceEntryFlags Flags = SpaceEntryFlags.None)
{
    public bool IsDirectory => Stamp.IsDirectory;
    public bool IsPartial => (Flags & SpaceEntryFlags.Partial) != 0;
    public bool IsSkipped => (Flags & SpaceEntryFlags.Skipped) != 0;
    public bool IsCloud => (Flags & SpaceEntryFlags.Cloud) != 0;
    public bool IsShared => (Flags & SpaceEntryFlags.Shared) != 0;
    public bool UnknownAllocation => AllocatedBytes < 0 || IsDirectory && IsPartial && AllocatedBytes == 0;
    public long TotalBytes(bool logical) => logical ? Math.Max(0, LogicalBytes) : CountedBytes;
    public bool CanRecycle => Parent >= 0 && Stamp.Identity.IsKnown && !Stamp.IsReparsePoint &&
        (Flags & (SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped | SpaceEntryFlags.Cloud | SpaceEntryFlags.ContainsUnverifiedItems)) == 0;
    public DateTimeOffset? Modified => Stamp.Modified is > 0 and <= 2650467743999999999
        ? new DateTimeOffset(DateTime.FromFileTimeUtc(Stamp.Modified)) : null;
}

public readonly record struct ListedSpaceFile(string Name, ulong Low, ulong High, FileAttributes Attributes, long Length, long Modified,
    long Allocation, long Changed, ulong Volume)
{
    public SpaceEntry Restricted(int parent)
    {
        const FileAttributes unsupported = FileAttributes.Directory | FileAttributes.Compressed | FileAttributes.SparseFile |
            FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x00440000;
        bool measured = (Attributes & unsupported) == 0 && (Low != 0 || High != 0) && Allocation >= 0;
        var identity = measured ? new SpaceIdentity(Volume, Low, High) : default;
        var stamp = new SpaceStamp(identity, Modified, Changed, Attributes, 0);
        return new(Name, parent, stamp, stamp.IsDirectory ? 0 : Length, measured ? Allocation : -1, measured ? Allocation : 0,
            SpaceEntryFlags.Partial | SpaceEntryFlags.Skipped | SpaceEntryFlags.AccessDenied |
            ((Attributes & (FileAttributes.Offline | (FileAttributes)0x00440000)) != 0 ? SpaceEntryFlags.Cloud : 0));
    }
}

public enum SpaceIssueKind { Access, Changed, Link, CloudDirectory, Metadata, Cancelled }

public sealed record SpaceIssue(string Path, SpaceIssueKind Kind, string Message);
public sealed record SpaceVolume(string Root, long TotalBytes, long FreeBytes)
{
    public long IndexCacheBytes { get; init; }
    public long ReservedBytes { get; init; }
    public long NtfsAttributeBytes { get; init; }
    public long UsedBytes => TotalBytes - FreeBytes;
    public long DifferenceFrom(long attributedBytes) => UsedBytes - attributedBytes;

    public static SpaceVolume? ReadLocalRoot(string path)
    {
        if (path.Length != 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        try
        {
            var drive = new DriveInfo(path);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) return null;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                try
                {
                    if (GetDiskSpaceInformationW(path, out var info) == 0 && info.Total >= info.Available &&
                        info.Total - info.Available >= info.Used && info.Total - info.Available - info.Used == info.Reserved &&
                        info.Sectors != 0 && info.SectorBytes != 0)
                    {
                        ulong unit = checked((ulong)info.Sectors * info.SectorBytes);
                        return new(path, checked((long)(info.Total * unit)), checked((long)(info.Available * unit)))
                            { ReservedBytes = checked((long)(info.Reserved * unit)) };
                    }
                }
                catch (EntryPointNotFoundException) { }
            }
            return new(path, drive.TotalSize, drive.TotalFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DiskSpaceInformation
    {
        public ulong Total, Available, PoolUnavailable, CallerTotal, CallerAvailable, CallerPoolUnavailable;
        public ulong Used, Reserved, VolumeReserve, Committed, PoolAvailable;
        public uint Sectors, SectorBytes;
    }

    [System.Runtime.InteropServices.DllImport("kernelbase.dll", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetDiskSpaceInformationW(string path, out DiskSpaceInformation info);
}
public sealed record SpaceDrive(string Root, string Label);
public sealed record SpaceProgressFolder(string Name, long Bytes);
public sealed record SpaceScanProgress(int Files, int Folders, int Skipped, long Bytes, TimeSpan Elapsed,
    IReadOnlyList<SpaceProgressFolder> LargestFolders)
{
    public string Stage { get; init; } = "正在扫描";
}

public sealed record SpaceScanMetadata(string Root, int Files, int Folders, SpaceVolume? Volume,
    IReadOnlyDictionary<SpaceIssueKind, int> IssueCounts, IReadOnlyList<SpaceIssue> Issues, TimeSpan Elapsed,
    bool Cancelled, string ScanMethod = "目录扫描", string ScanNotice = "")
{
    public int Skipped => IssueCounts.Where(item => item.Key != SpaceIssueKind.Cancelled).Sum(item => item.Value);
    public SpaceSnapshot WithEntries(IReadOnlyList<SpaceEntry> entries) =>
        new(Root, entries, Files, Folders, Volume, IssueCounts, Issues, Elapsed, Cancelled)
        { ScanMethod = ScanMethod, ScanNotice = ScanNotice };
}

/// <summary>One scan. Paths and UI rows are built on demand; entries refer to their parent by index.</summary>
public interface IIndexedSpaceView : IReadOnlyList<SpaceEntry>, IDisposable
{
    string? Generation => null;
    bool HasFolders(int id);
    int[] Children(int id);
    int? Find(string path);
    string PathOf(int id);
    SpaceQueryResult List(int folder, SpaceView view, SpaceFilter filter, SpaceSort sort, bool descending, CancellationToken token, bool logical = false);
    IReadOnlyList<SpaceTypeTotal> Types(CancellationToken token, bool logical = false);
    SpaceMapData Map(int id, CancellationToken token, bool logical = false);
}

public sealed class SpaceSnapshot : IDisposable
{
    private readonly Lazy<(int[] First, int[] Next, bool[] HasFolders)> tree;

    public SpaceSnapshot(string root, IReadOnlyList<SpaceEntry> entries, int files, int folders, SpaceVolume? volume,
        IReadOnlyDictionary<SpaceIssueKind, int> issueCounts, IReadOnlyList<SpaceIssue> issues, TimeSpan elapsed, bool cancelled)
    {
        Root = root;
        Entries = entries;
        Files = files;
        Folders = folders;
        Volume = volume;
        IssueCounts = issueCounts;
        Issues = issues;
        Elapsed = elapsed;
        Cancelled = cancelled;

        // The scan helper only transmits entries. Build navigation indexes once in the process that browses them.
        tree = new(() => IndexChildren(entries));
    }

    private static (int[] First, int[] Next, bool[] HasFolders) IndexChildren(IReadOnlyList<SpaceEntry> entries)
    {
        var firstChild = new int[entries.Count];
        var nextSibling = new int[entries.Count];
        var hasFolders = new bool[entries.Count];
        Array.Fill(firstChild, -1);
        Array.Fill(nextSibling, -1);
        for (var i = entries.Count - 1; i > 0; i--)
        {
            var entry = entries[i];
            var parent = entry.Parent;
            if (entry.IsDirectory) hasFolders[parent] = true;
            nextSibling[i] = firstChild[parent];
            firstChild[parent] = i;
        }
        return (firstChild, nextSibling, hasFolders);
    }

    public string Root { get; }
    public string ScanMethod { get; init; } = "目录扫描";
    public string ScanNotice { get; set; } = string.Empty;
    public IReadOnlyList<SpaceEntry> Entries { get; }
    public int Files { get; }
    public int Folders { get; }
    public SpaceVolume? Volume { get; }
    public IReadOnlyDictionary<SpaceIssueKind, int> IssueCounts { get; }
    public IReadOnlyList<SpaceIssue> Issues { get; }
    public TimeSpan Elapsed { get; }
    public bool Cancelled { get; }
    public SpaceScanMetadata Metadata => new(Root, Files, Folders, Volume, IssueCounts, Issues, Elapsed, Cancelled, ScanMethod, ScanNotice);
    public int Skipped => IssueCounts.Where(item => item.Key != SpaceIssueKind.Cancelled).Sum(item => item.Value);
    public bool IsWholeVolume => Volume is { } disk && string.Equals(Root, disk.Root, StringComparison.OrdinalIgnoreCase);
    public bool IsPartial => Cancelled || Skipped != 0 || Entries.Count > 0 && Entries[0].IsPartial;
    public bool HasFolders(int id) => Entries is IIndexedSpaceView index ? index.HasFolders(id) : tree.Value.HasFolders[id];
    public void Dispose() => (Entries as IIndexedSpaceView)?.Dispose();

    public bool Contains(int folder, int item)
    {
        for (var i = item; i >= 0; i = Entries[i].Parent)
            if (i == folder) return true;
        return false;
    }

    public IEnumerable<int> Children(int folder)
    {
        if (Entries is IIndexedSpaceView index) { foreach(var child in index.Children(folder)) yield return child; yield break; }
        var (firstChild, nextSibling, _) = tree.Value;
        for (var i = firstChild[folder]; i >= 0; i = nextSibling[i]) yield return i;
    }

    public IEnumerable<int> Descendants(int folder)
    {
        if (Entries is IIndexedSpaceView index)
        {
            var stack = new Stack<int>(index.Children(folder).Reverse());
            while(stack.TryPop(out var child)) { yield return child; if(index[child].IsDirectory) foreach(var n in index.Children(child).Reverse()) stack.Push(n); }
            yield break;
        }
        var (firstChild, nextSibling, _) = tree.Value;
        var i = firstChild[folder];
        while (i >= 0)
        {
            yield return i;
            if (firstChild[i] >= 0)
            {
                i = firstChild[i];
                continue;
            }
            while (i != folder && nextSibling[i] < 0) i = Entries[i].Parent;
            if (i == folder) yield break;
            i = nextSibling[i];
        }
    }

    public string PathOf(int index)
    {
        if (Entries is IIndexedSpaceView view) return view.PathOf(index);
        if (index == 0) return Root;
        var names = new Stack<string>();
        for (var i = index; i > 0; i = Entries[i].Parent) names.Push(Entries[i].Name);
        return Path.Join(Root, string.Join(Path.DirectorySeparatorChar, names));
    }

    public int? Find(string path)
    {
        if (Entries is IIndexedSpaceView view) return view.Find(path);
        if (string.Equals(Root, path, StringComparison.OrdinalIgnoreCase)) return 0;
        var relative = Path.GetRelativePath(Root, path);
        var parent = 0;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            var next = -1;
            foreach (var child in Children(parent))
            {
                var name = Entries[child].Name;
                if (name.Equals(segment, StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
                if (name.Equals(segment, StringComparison.OrdinalIgnoreCase)) next = child;
            }
            if (next < 0) return null;
            parent = next;
        }
        return parent;
    }

    public SearchFile FileOf(int index)
    {
        var entry = Entries[index];
        return new(PathOf(index), entry.IsDirectory, entry.IsDirectory || entry.LogicalBytes < 0 ? null : entry.LogicalBytes, entry.Modified)
        { IsReparsePoint = entry.Stamp.IsReparsePoint };
    }

    public int[] CollapseSelection(IEnumerable<int> indices)
    {
        if (Cancelled) return [];
        var selected = indices.Where(index => index > 0 && index < Entries.Count && Entries[index].CanRecycle).ToHashSet();
        return selected.Where(index =>
        {
            for (var parent = Entries[index].Parent; parent > 0; parent = Entries[parent].Parent)
                if (selected.Contains(parent)) return false;
            return true;
        }).Order().ToArray();
    }
}

public interface ILiveSpaceScanner : ISpaceScanner
{
    Task<SpaceSnapshot> RescanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token) =>
        ScanAsync(root,progress,token);
    Task<SpaceSnapshot> ReadCurrentAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token);
    Task<SpaceSnapshot> RefreshPathsAsync(string root, IReadOnlyList<string> paths, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        => ScanAsync(root, progress, token);
}

public interface ISpaceScanner
{
    IReadOnlyList<SpaceDrive> GetDrives();
    Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token);
}

/// <summary>Consumes a completed scan on the scanner's worker. Rows are single-pass, in parent-before-child order.
/// The metadata contains totals and issues. The receiver owns any persistent copy of the rows.</summary>
public interface IStreamingSpaceScanner
{
    Task<T> ScanToAsync<T>(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token,
        Func<SpaceScanMetadata, int, IEnumerable<SpaceEntry>, T> receive);
}

public enum SpaceRecycleStatus { Recycled, Failed, Cancelled }
public sealed record SpaceRecycleItem(string Path, SpaceRecycleStatus Status, string Message, long AccountedBytes);
public sealed record SpaceRecycleReport(IReadOnlyList<SpaceRecycleItem> Items, long? FreeBefore, long? FreeAfter);
public sealed record SpaceRecycleProgress(int Finished, int Total, string Path);
public interface ISpaceRecycler
{
    Task<SpaceRecycleReport> RecycleAsync(SpaceSnapshot snapshot, IReadOnlyList<int> selection,
        IProgress<SpaceRecycleProgress>? progress, CancellationToken token);
    void OpenRecycleBin();
}
