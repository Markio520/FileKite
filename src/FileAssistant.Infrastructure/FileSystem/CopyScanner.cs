using System.Diagnostics;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;

namespace FileAssistant.Infrastructure.FileSystem;

internal static class CopyScanner
{
    public static IReadOnlyList<CopySource> Scan(CopyRequest request, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        if (request.Location == OutputLocation.Fixed) CopyFileSystem.CheckPath(CopyPlanner.Normalize(request.Destination));
        var sources = new List<CopySource>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var directories = new List<string>();
        var clock = Stopwatch.StartNew();
        var last = 0L;
        var skeletonOnly = request.Layout == CopyLayout.Skeleton;
        var readMediaDate = !skeletonOnly &&
            request.Layout == CopyLayout.Classify &&
            request.PreferMediaDate &&
            (request.ClassifyBy == ClassifyPrimary.Date || request.SubDateLayer != ClassifyDateGranularity.None);
        var verb = request.Operation == CopyOperation.Move ? "移动" : "复制";

        foreach (var raw in request.Paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string path;
            try { path = CopyPlanner.Normalize(raw); }
            catch (ArgumentException error) { sources.Add(new(raw, "", "", false, null, error.Message)); continue; }
            if (directories.Any(root => CopyPlanner.Within(path, root))) continue;
            FileAttributes? attributes;
            try { attributes = CopyFileSystem.Attributes(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { sources.Add(new(path, "", "", false, null, CopyFileSystem.Friendly(error))); continue; }
            var isDirectory = attributes is { } a && (a & FileAttributes.Directory) != 0;
            var destination = BatchOutputDirectory.Resolve(request.Location, request.Destination, path);
            if (isDirectory && CopyPlanner.Within(destination, path))
                throw new IOException($"目标目录位于待{verb}源目录内部或与源目录相同，请调整目标位置。");
            if (isDirectory) directories.Add(path);
            Visit(path, isDirectory ? path : "", "");
        }

        if (request.Location != OutputLocation.Fixed && directories.Count > 0)
        {
            var inputDirectories = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in sources.Where(source => source.Error is null).Select(request.DirectoryFor).Distinct(StringComparer.OrdinalIgnoreCase))
                for (string? part = folder; part is not null; part = Path.GetDirectoryName(part))
                    if (inputDirectories.Contains(part)) throw new IOException("输出位置位于本批输入目录内，请为本次任务指定其他目录。");
        }

        progress?.Report(new(sources.Count, sources.Count, $"扫描完成：{sources.Count:N0} 项"));
        return sources;

        void Visit(string path, string root, string relative)
        {
            token.ThrowIfCancellationRequested();
            if (!known.Add(path)) return;
            var folder = false;
            try
            {
                var attributes = CopyFileSystem.Attributes(path) ?? throw new FileNotFoundException("源文件已不存在。", path);
                folder = (attributes & FileAttributes.Directory) != 0;
                CopyFileSystem.CheckPath(path);
                if (folder)
                {
                    sources.Add(new(path, root, relative, true, null));
                    var entries = skeletonOnly
                        ? Directory.EnumerateDirectories(path)
                        : Directory.EnumerateFileSystemEntries(path);
                    // Enumeration errors leave a visible rejected entry, never a silently incomplete scan.
                    foreach (var child in entries.Select(entry => { token.ThrowIfCancellationRequested(); return entry; }).Order(StringComparer.OrdinalIgnoreCase))
                        Visit(child, root, Path.GetRelativePath(root, child));
                }
                else if (!skeletonOnly)
                {
                    var stamp = CopyFileSystem.Stamp(path, false, token);
                    if (identities.Add(stamp.Identity))
                    {
                        var mediaTicks = readMediaDate ? MediaDateReader.TryReadTakenUtcTicks(path) : null;
                        sources.Add(new(path, root, relative, false, stamp) { MediaTakenUtcTicks = mediaTicks });
                    }
                }
                if (clock.ElapsedMilliseconds - last >= 100)
                {
                    last = clock.ElapsedMilliseconds;
                    progress?.Report(new(sources.Count, 0, $"正在扫描：{sources.Count:N0} 项 · {path}"));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                sources.Add(new(path, root, relative, folder, null, CopyFileSystem.Friendly(error)));
            }
        }
    }
}
