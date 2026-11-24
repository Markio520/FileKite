using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.Search;
using FileAssistant.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileAssistant.App.Features.Search;

public sealed class SearchResultItem(SearchFile file, SearchColumnWidths? columns = null) : ObservableObject
{
    public SearchFile File { get; private set; } = file;
    public SearchColumnWidths Columns => columns ??= new();
    internal int FileCategory { get; private set; } = Category(file);
    internal bool Update(SearchFile updated)
    {
        if (File == updated) return false;
        File = updated;
        FileCategory = Category(updated);
        OnPropertyChanged(string.Empty);
        return true;
    }
    internal void RefreshDisplay() { OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(SizeLabel)); }
    public string Name => FileDisplay.Name(File.Name, File.IsFolder);
    public string Directory => File.Directory;
    public string FullPath => File.FullPath;
    public string SizeLabel => File.DirectoryUsage is { } usage
        ? usage.Bytes is { } bytes ? (usage.Volume ? "已用 " : usage.Partial ? "占用 ≥ " : "占用 ") + FileDisplay.Size(bytes) : "占用未知"
        : File.IsFolder ? "—" : File.Size is { } size ? FileDisplay.Size(size) : "未索引";
    public string ModifiedLabel => File.Modified?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "未索引";
    public string TypeLabel => File.IsFolder ? "文件夹" : File.Extension.Length == 0 ? "文件" : File.Extension.ToUpperInvariant() + " 文件";
    public string LocationBadge => WorkspaceFile.FormatLocationBadge(Directory);
    public int IconTone => File.IsFolder ? 1 : FileCategory switch
    {
        1 => 2,
        2 => 3,
        3 => 5,
        4 or 5 => 4,
        _ => 0
    };
    public string Glyph => File.IsFolder ? "\uE8B7" : FileCategory switch { 1 => "\uEB9F", 3 => "\uF012", 4 => "\uE714", 5 => "\uE8D6", _ => "\uE8A5" };
    private static readonly Dictionary<string, int> categories = BuildCategories();

    private static Dictionary<string, int> BuildCategories()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { SearchKind.Images, SearchKind.Documents, SearchKind.Archives, SearchKind.Video, SearchKind.Audio })
            foreach (var extension in SearchRules.Extensions(kind).Split(';'))
                result.Add(extension, (int)kind);
        return result;
    }

    public static int Category(SearchFile file) => file.IsFolder ? 0 : categories.GetValueOrDefault(file.Extension);
    public override string ToString() => $"{Name}，{TypeLabel}，{SizeLabel}，{Directory}";
}
