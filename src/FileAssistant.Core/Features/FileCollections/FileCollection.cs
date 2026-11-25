using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.Core.Features.FileCollections;

/// <summary>Software folders and memberships. All operations change references only and perform no file I/O.</summary>
public sealed class FileCollection
{
    private readonly List<CollectionFolder> folders = [];
    private readonly List<CollectionEntry> entries = [];
    public IReadOnlyList<CollectionFolder> Folders => folders;
    public IReadOnlyList<CollectionEntry> Entries => entries;
    public bool HasFolder(Guid id) => id == Guid.Empty || folders.Any(folder => folder.Id == id);
    public FileCollectionSnapshot Snapshot(Guid current = default) => new(1, folders.ToArray(), entries.ToArray(), current);

    public static FileCollectionSnapshot Remap(FileCollectionSnapshot snapshot, IReadOnlyList<PathChange> changes,
        IReadOnlyList<string> removed, string? removalReason = null)
    {
        var deleted = removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapped = snapshot.Entries.Select(entry =>
        {
            var path = NamingRulesEngine.MapPath(entry.File.FullPath, changes);
            var error = IsRemoved(path, deleted) ? removalReason ?? "本次创建已撤销，文件不存在。"
                : path != entry.File.FullPath ? null : entry.Error;
            return (Changed: path != entry.File.FullPath, Entry: entry with { File = entry.File with { FullPath = path }, Error = error });
        });
        return snapshot with { Entries = mapped.GroupBy(x => (x.Entry.FolderId, Path: x.Entry.File.FullPath.ToUpperInvariant()))
            .Select(group => group.OrderByDescending(x => x.Changed).ThenBy(x => x.Entry.Error is not null).First().Entry).ToArray() };
    }

    public static bool IsRemoved(string path, IReadOnlySet<string> removed)
    {
        if (removed.Count == 0) return false;
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if (removed.Contains(part)) return true;
        return false;
    }

    public static FileCollection Restore(FileCollectionSnapshot snapshot)
    {
        if (snapshot.Version != 1) throw new ArgumentException("文件夹数据版本不受支持。");
        var result = new FileCollection();
        if (snapshot.Folders is null || snapshot.Entries is null) throw new ArgumentException("文件夹数据不完整。");
        result.folders.AddRange(snapshot.Folders);
        var parents = new Dictionary<Guid, Guid>(result.folders.Count);
        foreach (var folder in result.folders)
            if (folder is null || folder.Id == Guid.Empty || !ValidName(folder.Name) || !parents.TryAdd(folder.Id, folder.ParentId))
                throw new ArgumentException("文件夹数据包含无效条目。");
        foreach (var folder in result.folders)
        {
            int depth = 1;
            var parent = folder.ParentId;
            while (parent != Guid.Empty)
            {
                if (!parents.TryGetValue(parent, out parent)) throw new ArgumentException("文件夹数据包含无效条目。");
                if (++depth > 128) throw new ArgumentException("文件夹层级无效或过深。");
            }
        }
        if (result.folders.GroupBy(folder => (folder.ParentId, Name: folder.Name.ToUpperInvariant())).Any(group => group.Count() > 1))
            throw new ArgumentException("文件夹数据包含重复条目。");
        var ids = new HashSet<Guid>();
        var pathsByFolder = new Dictionary<Guid, HashSet<string>>();
        result.entries.EnsureCapacity(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) ||
                (entry.FolderId != Guid.Empty && !parents.ContainsKey(entry.FolderId)) || entry.File is null)
                throw new ArgumentException("文件夹数据包含无效条目。");
            var path = Normalize(entry.File.FullPath);
            if (!pathsByFolder.TryGetValue(entry.FolderId, out var paths))
                pathsByFolder.Add(entry.FolderId, paths = new(StringComparer.Ordinal));
            if (!paths.Add(path.ToUpperInvariant())) throw new ArgumentException("文件夹数据包含重复条目。");
            result.entries.Add(path == entry.File.FullPath ? entry : entry with { File = entry.File with { FullPath = path } });
        }
        return result;
    }
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name.Length <= 120 &&
        name is not ("." or "..") && !name.Any(character => char.IsControl(character) || character is '/' or '\\');
    private void RequireFolder(Guid id) { if (!HasFolder(id)) throw new ArgumentException("目标软件文件夹已不存在。"); }
    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("文件引用需要完整的磁盘路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    public string UniqueName(Guid parent, string name, Guid except = default)
    {
        var candidate = name;
        for (var suffix = 2; folders.Any(folder => folder.ParentId == parent && folder.Id != except && folder.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); suffix++)
            candidate = name[..Math.Min(name.Length, 110)] + $" ({suffix})";
        return candidate;
    }
    public Guid CreateFolder(Guid parent, string name)
    {
        RequireFolder(parent); name = name.Trim();
        if (Ancestors(parent).Count >= 128) throw new ArgumentException("软件文件夹层级过深。");
        if (!ValidName(name)) throw new ArgumentException("名称应为 1–120 个字符，不能包含斜杠、换行或仅为点。");
        var id = Guid.NewGuid(); folders.Add(new(id, parent, UniqueName(parent, name))); return id;
    }
    public void RenameFolder(Guid id, string name)
    {
        var folder = folders.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("软件文件夹已不存在。");
        name = name.Trim();
        if (!ValidName(name)) throw new ArgumentException("软件文件夹名称无效。");
        if (folders.Any(item => item.Id != id && item.ParentId == folder.ParentId && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("同级已有这个名称，请换一个名称。");
        folders[folders.IndexOf(folder)] = folder with { Name = name };
    }
    public int AddFiles(Guid folder, IEnumerable<SearchFile> files)
    {
        RequireFolder(folder);
        var known = entries.Where(entry => entry.FolderId == folder).Select(entry => Normalize(entry.File.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originalCount = entries.Count;
        try
        {
            foreach (var file in files)
            {
                var path = Normalize(file.FullPath);
                if (known.Add(path)) entries.Add(new(Guid.NewGuid(), folder, path == file.FullPath ? file : file with { FullPath = path }));
            }
        }
        catch
        {
            entries.RemoveRange(originalCount, entries.Count - originalCount);
            throw;
        }
        return entries.Count - originalCount;
    }
    public IReadOnlyList<Guid> Ancestors(Guid id)
    {
        RequireFolder(id); var ancestors = new List<Guid>();
        while (id != Guid.Empty) { ancestors.Add(id); id = folders.First(folder => folder.Id == id).ParentId; }
        ancestors.Add(Guid.Empty); ancestors.Reverse(); return ancestors;
    }
    public string FolderPath(Guid id) => string.Join(" / ", Ancestors(id).Where(item => item != Guid.Empty).Select(item => folders.First(folder => folder.Id == item).Name));
    public Guid Parent(Guid id) => id == Guid.Empty ? Guid.Empty : folders.First(folder => folder.Id == id).ParentId;
    public IReadOnlySet<Guid> Descendants(Guid id)
    {
        RequireFolder(id); var result = new HashSet<Guid> { id }; var pending = new Queue<Guid>(); pending.Enqueue(id);
        var children = folders.ToLookup(folder => folder.ParentId);
        while (pending.TryDequeue(out var parent))
            foreach (var folder in children[parent]) if (result.Add(folder.Id)) pending.Enqueue(folder.Id);
        return result;
    }
    public IReadOnlyList<CollectionEntry> Resolve(Guid folder)
    {
        RequireFolder(folder);
        return OrderedEntries(folder).DistinctBy(entry => entry.File.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public IReadOnlyList<CollectionEntry> ResolveSelection(IEnumerable<Guid> folderIds, IEnumerable<Guid> entryIds)
    {
        var groups = folderIds.ToHashSet(); var selectedEntries = entryIds.ToHashSet();
        if (groups.Count == 0 && selectedEntries.Count == 0) return [];
        var selectedFolders = new HashSet<Guid>();
        foreach (var id in groups) selectedFolders.UnionWith(Descendants(id));
        return OrderedEntries(Guid.Empty).Where(entry => selectedFolders.Contains(entry.FolderId) || selectedEntries.Contains(entry.Id))
            .DistinctBy(entry => entry.File.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private IEnumerable<CollectionEntry> OrderedEntries(Guid folder)
    {
        if (folders.Count == 0) return entries;
        if (!folders.Any(item => item.ParentId == folder)) return entries.Where(entry => entry.FolderId == folder);
        var children = folders.ToLookup(item => item.ParentId);
        var members = entries.ToLookup(entry => entry.FolderId);
        var result = new List<CollectionEntry>(folder == Guid.Empty ? entries.Count : 0);
        void Visit(Guid id)
        {
            foreach (var child in children[id]) Visit(child.Id);
            result.AddRange(members[id]);
        }
        Visit(folder);
        return result;
    }

    public IReadOnlyDictionary<Guid, (int Count, long Bytes)> FolderTotals()
    {
        var totals = new Dictionary<Guid, (int, long)>(folders.Count);
        if (folders.Count == 0) return totals;
        var children = folders.ToLookup(folder => folder.ParentId);
        var members = entries.ToLookup(entry => entry.FolderId);
        var last = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int position = 0;
        foreach (var folder in children[Guid.Empty]) Visit(folder, null);
        return totals;

        void Visit(CollectionFolder folder, FolderScan? parent)
        {
            var scope = new FolderScan(position, parent);
            foreach (var child in children[folder.Id]) Visit(child, scope);
            foreach (var entry in members[folder.Id])
            {
                var path = entry.File.FullPath;
                int previous = last.GetValueOrDefault(path, -1);
                last[path] = position++;
                // A subtree occupies a contiguous interval in Resolve's child-first order.
                // Count the first occurrence in each enclosing subtree and use that reference's size.
                for (var node = scope; node != null && previous < node.Start; node = node.Parent)
                {
                    node.Count++;
                    node.Bytes = checked(node.Bytes + (entry.File.Size ?? 0));
                }
            }
            totals.Add(folder.Id, (scope.Count, scope.Bytes));
        }
    }

    private sealed class FolderScan(int start, FolderScan? parent)
    {
        public int Start { get; } = start;
        public FolderScan? Parent { get; } = parent;
        public int Count;
        public long Bytes;
    }
    public FileCollectionBundle Export(Guid current, IEnumerable<Guid>? folderIds = null, IEnumerable<Guid>? entryIds = null)
    {
        RequireFolder(current);
        var selected = (folderIds ?? folders.Where(folder => folder.ParentId == current).Select(folder => folder.Id)).ToHashSet();
        selected.RemoveWhere(id => !HasFolder(id) || id == Guid.Empty);
        var roots = selected.Where(id => !Ancestors(id).Any(ancestor => ancestor != id && selected.Contains(ancestor))).ToHashSet();
        var included = new HashSet<Guid>(); foreach (var id in roots) included.UnionWith(Descendants(id));
        var direct = (entryIds ?? entries.Where(entry => entry.FolderId == current).Select(entry => entry.Id)).ToHashSet();
        return new(folders.Where(folder => included.Contains(folder.Id)).Select(folder => roots.Contains(folder.Id) ? folder with { ParentId = Guid.Empty } : folder).ToArray(),
            OrderedEntries(Guid.Empty).Where(entry => included.Contains(entry.FolderId) || direct.Contains(entry.Id))
                .Select(entry => included.Contains(entry.FolderId) ? entry : entry with { FolderId = Guid.Empty })
                .DistinctBy(entry => (entry.FolderId, Path: Normalize(entry.File.FullPath).ToUpperInvariant())).ToArray());
    }
    public int Import(Guid destination, FileCollectionBundle bundle, bool preserveFolders)
    {
        RequireFolder(destination);
        // Validate the portable tree before applying any changes.
        var source = Restore(new(1, bundle.Folders, bundle.Entries));
        if (!preserveFolders) return AddFiles(destination, bundle.Files);
        if (source.Folders.Any(folder => Ancestors(destination).Count + source.Ancestors(folder.Id).Count - 1 > 128))
            throw new ArgumentException("导入后的软件文件夹层级过深。");
        var map = new Dictionary<Guid, Guid> { [Guid.Empty] = destination };
        var pending = bundle.Folders.ToList();
        while (pending.Count > 0)
            foreach (var folder in pending.ToArray())
                if (map.TryGetValue(folder.ParentId, out var parent)) { map.Add(folder.Id, CreateFolder(parent, folder.Name)); pending.Remove(folder); }
        var added = 0;
        foreach (var group in bundle.Entries.GroupBy(entry => map[entry.FolderId])) added += AddFiles(group.Key, group.Select(entry => entry.File));
        return added;
    }
    public bool CanMove(IEnumerable<Guid> folderIds, Guid destination) => HasFolder(destination) && folderIds.All(id => id != Guid.Empty && HasFolder(id) && !Descendants(id).Contains(destination));
    public void Move(IEnumerable<Guid> folderIds, IEnumerable<Guid> entryIds, Guid destination)
    {
        var ids = folderIds.ToHashSet(); var files = entryIds.ToHashSet();
        if (!CanMove(ids, destination)) throw new ArgumentException("不能把软件文件夹移到自身或其子文件夹中。");
        var roots = ids.Where(id => !Ancestors(id).Any(ancestor => ancestor != id && ids.Contains(ancestor))).ToArray();
        foreach (var root in roots)
            if (Descendants(root).Any(id => Ancestors(destination).Count + Ancestors(id).Count - Ancestors(root).Count >= 128))
                throw new ArgumentException("移动后的软件文件夹层级过深。");
        var movedTree = new HashSet<Guid>(); foreach (var id in roots) movedTree.UnionWith(Descendants(id));
        foreach (var id in roots)
        {
            var index = folders.FindIndex(folder => folder.Id == id); var folder = folders[index];
            folders[index] = folder with { ParentId = destination, Name = UniqueName(destination, folder.Name, id) };
        }
        var memberships = entries.Where(entry => entry.FolderId == destination).Select(entry => Normalize(entry.File.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(entry => files.Contains(entry.Id) && !movedTree.Contains(entry.FolderId) && entry.FolderId != destination).ToArray())
        {
            if (memberships.Add(Normalize(entry.File.FullPath))) entries[entries.IndexOf(entry)] = entry with { FolderId = destination };
            else entries.Remove(entry);
        }
    }
    public void Remove(IEnumerable<Guid> folderIds, IEnumerable<Guid> entryIds)
    {
        var removed = new HashSet<Guid>(); foreach (var id in folderIds.Where(id => id != Guid.Empty)) removed.UnionWith(Descendants(id));
        var files = entryIds.ToHashSet();
        entries.RemoveAll(entry => removed.Contains(entry.FolderId) || files.Contains(entry.Id)); folders.RemoveAll(folder => removed.Contains(folder.Id));
    }
    public void Dissolve(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("根目录不能解散。");
        var parent = Parent(id);
        Move(folders.Where(folder => folder.ParentId == id).Select(folder => folder.Id).ToArray(), entries.Where(entry => entry.FolderId == id).Select(entry => entry.Id).ToArray(), parent);
        folders.RemoveAll(folder => folder.Id == id);
    }
    public void ClearFiles(Guid id) { var descendants = Descendants(id); entries.RemoveAll(entry => descendants.Contains(entry.FolderId)); }
    public void UpdateReferences(IReadOnlyDictionary<string, (SearchFile File, string? Error)> updates)
    {
        for (var index = 0; index < entries.Count; index++)
            if (updates.TryGetValue(entries[index].File.FullPath, out var update)) entries[index] = entries[index] with { File = update.File, Error = update.Error };
    }
}
