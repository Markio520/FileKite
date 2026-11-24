using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.Infrastructure.FileSystem;

public sealed class DirectoryContentReader : IDirectoryContentReader
{
    public IReadOnlyList<SearchFile> GetRoots() => Directory.GetLogicalDrives()
        .Select(path => new SearchFile(path, true, null, null)).ToArray();

    public Task<IReadOnlyList<SearchFile>> ReadFoldersAsync(string path, CancellationToken token) => Task.Run<IReadOnlyList<SearchFile>>(() =>
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请提供完整的文件夹路径。");
        var folders = new List<SearchFile>();
        foreach (var entry in new DirectoryInfo(path).EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();
            var attributes = entry.Attributes;
            folders.Add(new(entry.FullName, true, null, entry.LastWriteTimeUtc)
            {
                IsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0,
                IsHiddenOrSystem = (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
            });
        }
        token.ThrowIfCancellationRequested();
        return folders.OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }, token);

    public Task<IReadOnlyList<SearchFile>> ReadAsync(string path, CancellationToken token) => Task.Run<IReadOnlyList<SearchFile>>(() =>
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请提供完整的文件夹路径。");
        var items = new List<SearchFile>();
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            var folder = (entry.Attributes & FileAttributes.Directory) != 0;
            items.Add(new(entry.FullName, folder, folder ? null : ((FileInfo)entry).Length, entry.LastWriteTimeUtc)
                { IsReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0 });
        }
        token.ThrowIfCancellationRequested();
        return items.OrderByDescending(x => x.IsFolder).ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }, token);
}
