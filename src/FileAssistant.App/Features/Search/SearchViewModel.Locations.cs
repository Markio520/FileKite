using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.Search;

public partial class SearchViewModel
{
    private readonly IDirectoryContentReader? directoryReader;

    public static string LocationName(string path)
        => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    public IReadOnlyList<SearchFile> GetLocationRoots() => directoryReader?.GetRoots() ?? [];
    public async Task<IReadOnlyList<SearchFile>> ReadLocationFoldersAsync(string path, CancellationToken token)
    {
        if (directoryReader is null) return [];
        var folders = await directoryReader.ReadFoldersAsync(path, token).ConfigureAwait(false);
        if (preferences?.Invoke() is not { } current) return folders;
        var excluded = new SearchDirectoryFilter(current.ExcludedDirectories);
        return folders.Where(folder => (current.ShowHidden || !folder.IsHiddenOrSystem) && !excluded.Excludes(folder.FullPath)).ToArray();
    }
    public int LocationVisibilityRevision { get; private set; }
    private void InvalidateLocations()
    {
        LocationVisibilityRevision++;
        OnPropertyChanged(nameof(LocationVisibilityRevision));
    }

    public void UseFolder(string path)
    {
        // Commit the location as one search change, retaining all query/filter options.
        updatingConditions = true;
        try { Folder = path.Trim().Trim('"'); ScopeIndex = (int)SearchScope.Folder; }
        finally { updatingConditions = false; }
        Schedule();
    }
}
