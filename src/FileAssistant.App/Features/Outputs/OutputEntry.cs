using FileAssistant.App.Features.Search;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.Outputs;

public sealed record OutputEntry(int TaskId, string TaskName, ProducedFile Result) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshDisplay() => PropertyChanged?.Invoke(this, new(string.Empty));
    public SearchFile File { get; } = new(Result.FullPath, Result.IsDirectory, Result.IsDirectory ? null : Result.Size, Result.Modified);
    public string Name => File.Name;
    public string DisplayName => FileAssistant.Core.Settings.FileDisplay.Name(File.Name, File.IsFolder);
    public string FullPath => File.FullPath;
    public string LocationBadge => WorkspaceFile.FormatLocationBadge(File.Directory);
    public string Glyph => File.IsFolder ? "\uE8B7" : SearchResultItem.Category(File) switch { 1 => "\uEB9F", 3 => "\uF012", 4 => "\uE714", 5 => "\uE8D6", _ => "\uE8A5" };
    public int IconTone => File.IsFolder ? 1 : SearchResultItem.Category(File) switch
    {
        1 => 2,
        2 => 3,
        3 => 5,
        4 or 5 => 4,
        _ => 0
    };
    public string SizeLabel => File.IsFolder ? "文件夹" : WorkspaceFile.FormatSize(Result.Size);
    public string SourceLabel => string.Join("、", Result.SourcePaths.Select(Path.GetFileName));
    public override string ToString() => $"{Name}，{TaskName}，{FullPath}";
}
