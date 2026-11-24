using FileAssistant.Core.Settings;

namespace FileAssistant.Core.Engines;

public static class BatchOutputDirectory
{
    public const string SubfolderName = "FileAssistant输出";

    // A selected disk folder is one input. Its output belongs beside it, outside the tree being scanned.
    public static string Resolve(OutputLocation location, string fixedDirectory, string input)
    {
        if (location == OutputLocation.Fixed) return fixedDirectory;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)))
            ?? throw new ArgumentException("磁盘根目录没有上级文件夹，请为本次任务指定输出位置。");
        return location switch
        {
            OutputLocation.Source => parent,
            OutputLocation.Subfolder => Path.Combine(parent, SubfolderName),
            _ => throw new ArgumentOutOfRangeException(nameof(location))
        };
    }
}
