using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Indexing.Windows;

public sealed partial class SharedIndexEngine
{
    private TrackedLocation ScopeFor(string root)
    {
        lock (sync)
        {
            var parent = locations.Keys.Concat(online).Where(p => IsSelected(p) && IndexStore.Within(root, p)).OrderBy(p => p.Length).FirstOrDefault();
            var scope = Location(parent ?? root);
            if (discoverSaved && IsSelected(scope.Root) && !string.Equals(scope.Root, Path.GetPathRoot(scope.Root), StringComparison.OrdinalIgnoreCase))
                manualRoots.Add(scope.Root);
            return scope;
        }
    }

    private async Task<SearchPage> SearchFolderAsync(SearchRequest request, IProgress<SearchProgress>? progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Folder))
            throw new SearchException("先选择一个文件夹，再开始搜索。");
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SearchException("文件夹路径无效，请重新选择。");
        }

        if (!Directory.Exists(root))
            throw new SearchException("文件夹不存在或无法访问，请检查路径与访问权限。");
        request = request with
        {
            Folder = root
        };
        var refresh = Interlocked.Exchange(ref refreshFolder, 0) != 0;
        // Listing one unindexed directory must never start a recursive scan of an unrequested share.
        if (!request.IncludeSubfolders)
        {
            bool covered;
            lock (sync)
                covered = locations.Values.Any(s => IsSelected(s.Root) && (s.IsInitialized || s.HasSearchNames) && IndexStore.Within(root, s.Root));
            if (!covered)
                return await Task.Run(() => DirectChildren(request, progress, token), token).ConfigureAwait(false);
        }

        var scope = ScopeFor(root);
        return await Task.Run(async () =>
        {
            IProgress<SpaceScanProgress>? notice = progress is null ? null : new InlineProgress<SpaceScanProgress>(p => progress.Report(new(p.Files + p.Folders, root, p.Stage)));
            var tracked = IsSelected(scope.Root);
            if (refresh || !tracked || !scope.IsInitialized && !scope.HasSearchNames)
                await scope.UpdateAsync(ordinary, notice, token, force: !tracked).ConfigureAwait(false);
            if (tracked) StartMonitor();
            try
            {
                return IndexedFileSearch.Query([scope.Store], request, token);
            }
            catch (DirectoryNotFoundException) when (!string.Equals(scope.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                // A newly created directory may precede the next journal tick.
                await scope.UpdateAsync(ordinary, notice, token).ConfigureAwait(false);
                return IndexedFileSearch.Query([scope.Store], request, token);
            }
        }, token).ConfigureAwait(false);
    }

    private static SearchPage DirectChildren(SearchRequest request, IProgress<SearchProgress>? progress, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PageSize);
        var matches = SearchRules.Compile(request, contentMatcher: LocalSearchFiles.ContentMatcher(request, token));
        var visible = LocalSearchFiles.Visibility(request);
        var sorted = new SortedSet<SearchFile>(IndexedFileSearch.Order(request));
        var capacity = (long)request.Offset + request.PageSize;
        long hits = 0;
        int examined = 0, skipped = 0;
        try
        {
            if (IndexEntryReader.Read(request.Folder) is not { IsDirectory: true, IsSkipped: false })
                throw new SearchException("请选择可直接读取的文件夹。");
            foreach (var item in new DirectoryInfo(request.Folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                token.ThrowIfCancellationRequested();
                if (++examined % 256 == 0)
                    progress?.Report(new(examined, request.Folder));
                try
                {
                    var folder = (item.Attributes & FileAttributes.Directory) != 0;
                    var row = new SearchFile(item.FullName, folder, folder ? null : ((FileInfo)item).Length, item.LastWriteTimeUtc)
                    {
                        IsReparsePoint = (item.Attributes & FileAttributes.ReparsePoint) != 0
                    };
                    if (!visible(row) || !matches.Matches(row))
                        continue;
                    hits++;
                    if (capacity == 0)
                        continue;
                    if (sorted.Count < capacity)
                        sorted.Add(row);
                    else if (sorted.Comparer.Compare(row, sorted.Max) < 0)
                    {
                        sorted.Remove(sorted.Max!);
                        sorted.Add(row);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SearchException("无法读取此文件夹，请检查访问权限或连接状态。");
        }

        return new(sorted.Skip(request.Offset).Take(request.PageSize).ToArray(), hits, "当前目录", skipped);
    }
}
