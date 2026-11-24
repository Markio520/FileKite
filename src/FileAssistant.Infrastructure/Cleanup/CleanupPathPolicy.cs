using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Cleanup;

internal static class CleanupPathPolicy
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/') || path[2..].Contains(':'))
            throw new IOException("请选择本地磁盘上的完整文件或文件夹路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Split(Path.DirectorySeparatorChar).Any(name => name.EndsWith(' ') || name.EndsWith('.')))
            throw new IOException("路径带有不明确的尾部空格或句点，无法安全处理。");
        return full;
    }

    public static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Check(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full)!;
        if (full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("不能清理整个磁盘或此类型的位置。");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var app = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Within(full, windows) || Within(full, app) || Within(app, full))
            throw new IOException("此目标属于系统或当前应用，不能清理。");
        var special = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments };
        if (special.Select(Environment.GetFolderPath).Where(value => value.Length > 0)
            .Any(value => Within(value, full)))
            throw new IOException("不能清理整个系统、用户资料或共享数据目录。");
        if (full.Split(Path.DirectorySeparatorChar).Any(name => name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("此目录由 Windows 管理，不能直接清理。");
        CheckAncestors(full);
        return full;
    }

    public static void CheckAncestors(string path)
    {
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var metadata = SpaceFileMetadata.Read(current);
            if (metadata.Stamp.IsReparsePoint || metadata.Cloud || !metadata.Stamp.IsDirectory)
                throw new IOException("路径经过链接或云端目录，未执行清理。");
        }
    }
}
