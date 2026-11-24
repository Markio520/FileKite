using FileAssistant.Core.Features.Search;

namespace FileAssistant.Core.Abstractions;

/// <summary>Lists the immediate contents of a directory without importing or modifying them.</summary>
public interface IDirectoryContentReader
{
    Task<IReadOnlyList<SearchFile>> ReadAsync(string path, CancellationToken token);

    /// <summary>Available roots for read-only location navigation.</summary>
    IReadOnlyList<SearchFile> GetRoots() => [];

    /// <summary>Lists one level of folders; implementations may avoid reading file metadata.</summary>
    async Task<IReadOnlyList<SearchFile>> ReadFoldersAsync(string path, CancellationToken token)
        => (await ReadAsync(path, token)).Where(item => item.IsFolder).ToArray();
}
