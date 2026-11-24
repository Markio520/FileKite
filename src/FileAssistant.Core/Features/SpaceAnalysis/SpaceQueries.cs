using FileAssistant.Core.Features.Search;

namespace FileAssistant.Core.Features.SpaceAnalysis;

public enum SpaceKind { All, Folder, Video, Image, Audio, Document, Archive, Program, Other }
public enum SpaceSort { Allocated, Logical, Modified, Name }
public enum SpaceView { Directory, LargeFiles, Types }

public static class SpaceKinds
{
    private static readonly Dictionary<string, SpaceKind> Extensions = BuildExtensions();

    private static Dictionary<string, SpaceKind> BuildExtensions()
    {
        var extensions = new Dictionary<string, SpaceKind>(StringComparer.OrdinalIgnoreCase);
        Add(SpaceKind.Video, SearchRules.Extensions(SearchKind.Video));
        Add(SpaceKind.Image, SearchRules.Extensions(SearchKind.Images));
        Add(SpaceKind.Audio, SearchRules.Extensions(SearchKind.Audio));
        Add(SpaceKind.Document, SearchRules.Extensions(SearchKind.Documents));
        Add(SpaceKind.Archive, SearchRules.Extensions(SearchKind.Archives));
        Add(SpaceKind.Program, SearchRules.Extensions(SearchKind.Executables) + ";dll;sys;appx;msix;lib;ps1");
        return extensions;

        void Add(SpaceKind kind, string list)
        {
            foreach (var extension in list.Split(';')) extensions["." + extension] = kind;
        }
    }

    public static SpaceKind Of(SpaceEntry entry) => entry.IsDirectory ? SpaceKind.Folder :
        Extensions.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(Path.GetExtension(entry.Name.AsSpan()), out var kind)
            ? kind : SpaceKind.Other;

    public static string Label(SpaceKind kind) => kind switch
    {
        SpaceKind.Folder => "文件夹",
        SpaceKind.Video => "视频",
        SpaceKind.Image => "图片",
        SpaceKind.Audio => "音频",
        SpaceKind.Document => "文档",
        SpaceKind.Archive => "压缩包",
        SpaceKind.Program => "程序与库",
        SpaceKind.Other => "其他文件",
        _ => "所有类型"
    };
}

public sealed record SpaceFilter(SpaceKind Kind = SpaceKind.All, long? Minimum = null, long? Maximum = null,
    DateTimeOffset? ModifiedFrom = null, DateTimeOffset? ModifiedBefore = null);
public sealed record SpaceTypeTotal(SpaceKind Kind, int Count, long Bytes, int Unknown);
public sealed record SpaceQueryResult(int[] Indices, long Bytes, int Unknown);
public sealed record SpaceMapData(int Folder, (int Id, long Bytes)[] Largest, int Count, long Bytes);

public static class SpaceQueries
{
    public static SpaceQueryResult List(SpaceSnapshot snapshot, int folder, SpaceView view, SpaceFilter filter,
        SpaceSort sort, bool descending, CancellationToken token, bool logical = false)
    {
        if (snapshot.Entries is IIndexedSpaceView index) return index.List(folder,view,filter,sort,descending,token,logical);
        var matches = new List<int>();
        var shared = new HashSet<SpaceIdentity>();
        long bytes = 0;
        var unknown = 0;
        var candidates = view == SpaceView.Directory ? snapshot.Children(folder) : Enumerable.Range(1, snapshot.Entries.Count - 1);
        foreach (var id in candidates)
        {
            token.ThrowIfCancellationRequested();
            var entry = snapshot.Entries[id];
            var size = logical ? entry.LogicalBytes : entry.AllocatedBytes;
            if (view != SpaceView.Directory)
            {
                if (entry.IsDirectory || (filter.Kind != SpaceKind.All && SpaceKinds.Of(entry) != filter.Kind)) continue;
                if (filter.Minimum is { } minimum && size < minimum ||
                    filter.Maximum is { } maximum && (size < 0 || size > maximum)) continue;
                if (filter.ModifiedFrom is { } from && !(entry.Modified >= from) ||
                    filter.ModifiedBefore is { } before && !(entry.Modified < before)) continue;
            }

            matches.Add(id);
            if (size < 0 || entry.IsPartial && (!logical || entry.IsDirectory)) unknown++;
            if (logical || view == SpaceView.Directory) bytes += entry.TotalBytes(logical);
            else if (entry.AllocatedBytes >= 0 && (!entry.IsShared && (entry.Stamp.Links != 0 || !entry.Stamp.Identity.IsKnown) || shared.Add(entry.Stamp.Identity)))
                bytes += entry.AllocatedBytes;
        }

        matches.Sort((left, right) =>
        {
            var a = snapshot.Entries[left];
            var b = snapshot.Entries[right];
            var result = sort switch
            {
                SpaceSort.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
                SpaceSort.Modified => CompareKnown(a.Stamp.Modified == 0 ? -1 : a.Stamp.Modified,
                    b.Stamp.Modified == 0 ? -1 : b.Stamp.Modified, descending),
                SpaceSort.Logical => CompareKnown(a.LogicalBytes, b.LogicalBytes, descending),
                _ => CompareKnown(a.AllocatedBytes, b.AllocatedBytes, descending)
            };
            if (sort == SpaceSort.Name && descending) result = -result;
            if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return result == 0 ? left.CompareTo(right) : result;
        });
        token.ThrowIfCancellationRequested();
        return new(matches.ToArray(), bytes, unknown);
    }

    private static int CompareKnown(long left, long right, bool descending)
    {
        if ((left < 0) != (right < 0)) return left < 0 ? 1 : -1;
        return descending ? right.CompareTo(left) : left.CompareTo(right);
    }

    public static IReadOnlyList<SpaceTypeTotal> Types(SpaceSnapshot snapshot, CancellationToken token, bool logical = false)
    {
        if (snapshot.Entries is IIndexedSpaceView index) return index.Types(token,logical);
        var groups = new (int Count, long Bytes, int Unknown)[Enum.GetValues<SpaceKind>().Length];
        foreach (var entry in snapshot.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory) continue;
            ref var group = ref groups[(int)SpaceKinds.Of(entry)];
            group.Count++;
            group.Bytes += entry.TotalBytes(logical);
            if ((logical ? entry.LogicalBytes : entry.AllocatedBytes) < 0) group.Unknown++;
        }
        return groups.Select((group, index) => new SpaceTypeTotal((SpaceKind)index, group.Count, group.Bytes, group.Unknown))
            .Where(group => group.Count > 0).OrderByDescending(group => group.Bytes).ToArray();
    }

    public static SpaceMapData Map(SpaceSnapshot snapshot, int folder, CancellationToken token, bool logical = false)
    {
        if (snapshot.Entries is IIndexedSpaceView index) return index.Map(folder,token,logical);
        const int limit = 32;
        var largest = new PriorityQueue<int, (long Bytes, int TieBreak)>();
        var count = 0;
        long bytes = 0;
        foreach (var child in snapshot.Children(folder))
        {
            token.ThrowIfCancellationRequested();
            var size = snapshot.Entries[child].TotalBytes(logical);
            if (size <= 0) continue;
            count++;
            bytes += size;
            if (largest.Count < limit) largest.Enqueue(child, (size, -child));
            else largest.EnqueueDequeue(child, (size, -child));
        }
        return new(folder, largest.UnorderedItems.OrderByDescending(item => item.Priority)
            .Select(item => (item.Element, item.Priority.Bytes)).ToArray(), count, bytes);
    }
}
