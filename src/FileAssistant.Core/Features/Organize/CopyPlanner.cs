using FileAssistant.Core.Settings;

namespace FileAssistant.Core.Features.Organize;

public static class CopyPlanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "webp", "bmp", "gif", "tif", "tiff", "svg", "ico", "avif", "heic", "heif", "jxl" };

    private static readonly HashSet<string> SheetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "xls", "xlsx", "ods", "csv", "tsv", "xlsm" };

    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "ppt", "pptx", "odp", "ppsx" };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "pdf", "doc", "docx", "odt", "rtf", "txt", "md", "markdown", "wps", "epub" };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "mov", "webm", "avi", "wmv", "flv", "m4v", "mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "zip", "7z", "rar", "tar", "gz", "bz2", "xz", "tgz", "iso" };

    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "exe", "msi", "msix", "msixbundle", "appx", "bat", "cmd" };

    public static CopyPlan Create(
        CopyRequest request,
        IReadOnlyList<CopySource> sources,
        Func<string, CopyTarget?> target,
        CancellationToken token = default,
        IReadOnlyCollection<string>? existingTargetFolders = null)
    {
        if (!Enum.IsDefined(request.Location) || !Enum.IsDefined(request.Layout) || !Enum.IsDefined(request.Conflict) || !Enum.IsDefined(request.Operation))
            throw new ArgumentException("不支持此输出、目录、操作或冲突策略。");
        if (request.Layout == CopyLayout.Skeleton && request.Operation == CopyOperation.Move)
            throw new ArgumentException("只复制空文件夹结构不支持移动模式，请切换回复制。");

        var fixedDirectory = request.Location == OutputLocation.Fixed ? Normalize(request.Destination) : "";
        if (request.Layout == CopyLayout.Skeleton)
            return CreateSkeletonPlan(request, fixedDirectory, sources, target, token);

        var isMove = request.Operation == CopyOperation.Move;
        var items = new List<CopyPlanItem>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = sources.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceIdentities = sources.Where(x => x.Stamp is not null).Select(x => x.Stamp!.Identity).ToHashSet(StringComparer.Ordinal);
        var folderCandidates = existingTargetFolders?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        // Pre-count duplicate base filenames per destination when Flat + GatherPrefixSourceFolder is enabled.
        Dictionary<string, int>? flatNameCounts = null;
        if (request.Layout == CopyLayout.Flat && request.GatherPrefixSourceFolder)
        {
            flatNameCounts = new(StringComparer.OrdinalIgnoreCase);
            foreach (var src in sources)
            {
                if (src.IsDirectory || src.Error is not null || !MatchesGatherFilter(src.Path, request.GatherFilter)) continue;
                string dest;
                try { dest = request.Location == OutputLocation.Fixed ? fixedDirectory : Normalize(request.DirectoryFor(src)); }
                catch (ArgumentException) { continue; }
                var key = Path.Combine(dest, Path.GetFileName(src.Path));
                flatNameCounts[key] = flatNameCounts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var path = fixedDirectory;
            string? itemNote = null;
            try
            {
                if (source.Error is not null && !Path.IsPathFullyQualified(source.Path))
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, source.Error) { IsMove = isMove });
                    continue;
                }

                var destination = request.Location == OutputLocation.Fixed ? fixedDirectory : Normalize(request.DirectoryFor(source));
                if (request.Layout == CopyLayout.Preserve && source.Root.Length > 0)
                {
                    if (!roots.TryGetValue(source.Root, out var root))
                    {
                        var rootName = Path.GetFileName(source.Root);
                        if (rootName.Length == 0) rootName = source.Root.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd(':') + "_根目录";
                        root = Path.Combine(destination, rootName);
                        if (request.Conflict == CopyConflictPolicy.Number)
                            root = Number(root, true, p => reserved.Contains(p) || Occupied(p, target));
                        roots.Add(source.Root, root);
                        reserved.Add(root);
                    }
                    path = source.Relative.Length == 0 ? root : Path.Combine(root, source.Relative);
                }
                else if (request.Layout == CopyLayout.Preserve)
                {
                    path = Path.Combine(destination, Path.GetFileName(source.Path));
                }
                else
                {
                    if (source.IsDirectory)
                    {
                        if (source.Error is not null)
                            items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, source.Error) { IsMove = isMove });
                        continue;
                    }

                    if (request.Layout == CopyLayout.Flat)
                    {
                        if (!MatchesGatherFilter(source.Path, request.GatherFilter))
                        {
                            var skipMsg = request.GatherFilter == GatherFilter.DocumentsAndSheets
                                ? "非文档/表格格式，本次不提取。"
                                : "非图片格式，本次不提取。";
                            items.Add(new(Guid.NewGuid(), source, Path.Combine(destination, Path.GetFileName(source.Path)), CopyAction.Skip, skipMsg) { IsMove = isMove });
                            continue;
                        }

                        var fileName = Path.GetFileName(source.Path);
                        var rawTarget = Path.Combine(destination, fileName);
                        if (request.GatherPrefixSourceFolder && flatNameCounts is not null && flatNameCounts.GetValueOrDefault(rawTarget) > 1)
                        {
                            var parentFolder = Path.GetFileName(Path.GetDirectoryName(source.Path) ?? "");
                            if (parentFolder.Length > 0)
                            {
                                var prefixed = $"{parentFolder}_{fileName}";
                                if (prefixed.Length <= 255)
                                {
                                    fileName = prefixed;
                                    itemNote = "撞名已加来源目录名";
                                }
                            }
                        }
                        path = Path.Combine(destination, fileName);
                    }
                    else if (request.Layout == CopyLayout.Extension)
                    {
                        path = Path.Combine(destination, ExtensionFolder(source.Path), Path.GetFileName(source.Path));
                    }
                    else // CopyLayout.Classify
                    {
                        var classified = ResolveClassifyFolder(request, source, folderCandidates, out var skipReason, out itemNote);
                        if (skipReason is not null)
                        {
                            items.Add(new(Guid.NewGuid(), source, Path.Combine(destination, Path.GetFileName(source.Path)), CopyAction.Skip, skipReason) { IsMove = isMove });
                            continue;
                        }
                        path = classified.Length == 0
                            ? Path.Combine(destination, Path.GetFileName(source.Path))
                            : Path.Combine(destination, classified, Path.GetFileName(source.Path));
                    }
                }

                if (!Within(path, destination)) throw new ArgumentException("目标路径超出所选目标目录。");
                if (source.Error is not null)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, source.Error) { IsMove = isMove, Note = itemNote });
                    continue;
                }

                var existing = target(path);
                if (existing?.Error is { } error)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, error) { IsMove = isMove, Note = itemNote });
                    continue;
                }

                var sameSource = !source.IsDirectory && (Equal(source.Path, path) ||
                    existing?.Stamp is { } same && same.Identity == source.Stamp?.Identity);
                var numberBesideSource = !isMove && sameSource && request.Location == OutputLocation.Source && request.Conflict == CopyConflictPolicy.Number;
                if (sameSource && !numberBesideSource)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Skip, "源和目标是同一个文件，已在目标位置。") { IsMove = isMove });
                    continue;
                }

                // A source-relative copy may number a sibling beside its own input.
                if (!numberBesideSource && !source.IsDirectory && (sourcePaths.Contains(path) || existing?.Stamp is { } stamp && sourceIdentities.Contains(stamp.Identity)))
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, "目标是本批次的其他源文件，请调整位置。") { IsMove = isMove });
                    continue;
                }

                var blocked = BlockedParent(path, destination, target);
                if (blocked is not null)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, blocked) { IsMove = isMove });
                    continue;
                }

                if (source.IsDirectory)
                {
                    items.Add(new(Guid.NewGuid(), source, path, existing is { IsDirectory: false } ? CopyAction.Rejected : CopyAction.Copy,
                        existing is { IsDirectory: false } ? "目录位置已被文件占用。" : null) { IsMove = isMove });
                    reserved.Add(path);
                    continue;
                }

                var batch = reserved.Contains(path);
                var action = CopyAction.Copy;
                string? message = null;
                if (existing is not null || batch)
                {
                    if (request.Conflict == CopyConflictPolicy.Number)
                    {
                        path = Number(path, false, p => reserved.Contains(p) || Occupied(p, target));
                        action = CopyAction.Number;
                    }
                    else if (request.Conflict == CopyConflictPolicy.Skip)
                    {
                        action = CopyAction.Skip;
                    }
                    else if (existing?.IsDirectory == true)
                    {
                        action = CopyAction.Pending;
                        message = "目标是目录，请编号保留或跳过。";
                    }
                    else
                    {
                        action = CopyAction.Pending;
                        message = batch ? "本批次有相同目标，请编号保留或跳过。" : "确认后替换目标，旧文件将保留备份。";
                    }
                }

                items.Add(new(Guid.NewGuid(), source, path, action, message, existing?.Stamp, batch) { IsMove = isMove, Note = itemNote });
                if (action != CopyAction.Skip) reserved.Add(path);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, error.Message) { IsMove = isMove });
            }
        }
        return new(request with { Destination = fixedDirectory, Paths = request.Paths.ToArray() }, items.ToArray());
    }

    private static CopyPlan CreateSkeletonPlan(
        CopyRequest request,
        string fixedDirectory,
        IReadOnlyList<CopySource> sources,
        Func<string, CopyTarget?> target,
        CancellationToken token)
    {
        var items = new List<CopyPlanItem>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, (string Path, bool Renamed)>(StringComparer.OrdinalIgnoreCase);
        var find = request.ReplaceSkeletonText ? request.SkeletonFind : "";
        var replace = request.ReplaceSkeletonText ? request.SkeletonReplace : "";

        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            if (!source.IsDirectory)
            {
                if (source.Error is not null)
                    items.Add(new(Guid.NewGuid(), source, fixedDirectory, CopyAction.Rejected, source.Error));
                continue;
            }

            var path = fixedDirectory;
            try
            {
                var destination = request.Location == OutputLocation.Fixed ? fixedDirectory : Normalize(request.DirectoryFor(source));
                var renamed = false;

                if (source.Root.Length > 0)
                {
                    if (!roots.TryGetValue(source.Root, out var rootInfo))
                    {
                        var rawRoot = Path.GetFileName(source.Root);
                        if (rawRoot.Length == 0)
                            rawRoot = source.Root.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Last().TrimEnd(':') + "_根目录";
                        var replacedRoot = ReplaceSegment(rawRoot, find, replace, out var rootChanged);
                        var rootPath = Path.Combine(destination, replacedRoot);
                        if (request.Conflict == CopyConflictPolicy.Number && source.Relative.Length == 0)
                            rootPath = Number(rootPath, true, p => reserved.Contains(p));
                        rootInfo = (rootPath, rootChanged);
                        roots.Add(source.Root, rootInfo);
                    }

                    if (source.Relative.Length == 0)
                    {
                        path = rootInfo.Path;
                        renamed = rootInfo.Renamed;
                    }
                    else
                    {
                        var segments = source.Relative
                            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                            .Select(seg => ReplaceSegment(seg, find, replace, out var segChanged) is var next && (renamed |= segChanged) ? next : next)
                            .ToArray();
                        path = Path.Combine([rootInfo.Path, .. segments]);
                        renamed |= rootInfo.Renamed;
                    }
                }
                else
                {
                    var folderName = ReplaceSegment(Path.GetFileName(source.Path), find, replace, out renamed);
                    path = Path.Combine(destination, folderName);
                }

                path = Normalize(path);
                if (!Within(path, destination)) throw new ArgumentException("替换后的目录路径超出目标目录。");
                if (source.Error is not null)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, source.Error));
                    continue;
                }

                if (!reserved.Add(path)) continue;
                var existing = target(path);
                if (existing?.Error is { } error)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, error));
                    continue;
                }
                if (existing is { IsDirectory: false })
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, "目录位置已被同名文件占用。"));
                    continue;
                }
                var blocked = BlockedParent(path, destination, target);
                if (blocked is not null)
                {
                    items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, blocked));
                    continue;
                }

                items.Add(new(Guid.NewGuid(), source, path, CopyAction.Copy)
                {
                    Note = renamed ? "已替换目录关键词" : null
                });
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                items.Add(new(Guid.NewGuid(), source, path, CopyAction.Rejected, error.Message));
            }
        }

        return new(request with { Destination = fixedDirectory, Paths = request.Paths.ToArray() }, items.ToArray());
    }

    private static string ReplaceSegment(string segment, string find, string replace, out bool changed)
    {
        changed = false;
        if (string.IsNullOrEmpty(find)) return segment;
        var next = segment.Replace(find, replace, StringComparison.OrdinalIgnoreCase).Trim();
        if (next.Length == 0) throw new ArgumentException($"替换后文件夹名「{segment}」变为空名称，请调整替换文字。");
        if (next.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':', '*', '?', '"', '<', '>', '|']) >= 0 || next.EndsWith('.'))
            throw new ArgumentException($"替换后的文件夹名「{next}」包含非法字符。");
        if (next.Length > 255) throw new ArgumentException($"替换后的文件夹名超过 255 个字符。");
        changed = !string.Equals(segment, next, StringComparison.Ordinal);
        return next;
    }

    private static string ResolveClassifyFolder(
        CopyRequest request,
        CopySource source,
        IReadOnlyList<string> existingFolders,
        out string? skipReason,
        out string? note)
    {
        skipReason = null;
        note = null;
        var parts = new List<string>(2);

        switch (request.ClassifyBy)
        {
            case ClassifyPrimary.FileType:
                parts.Add(CategoryFolder(source.Path));
                break;

            case ClassifyPrimary.Extension:
                parts.Add(ExtensionFolder(source.Path));
                break;

            case ClassifyPrimary.Date:
            {
                var dateFolder = FormatDateFolder(source, request.DateGranularity == ClassifyDateGranularity.None ? ClassifyDateGranularity.YearMonth : request.DateGranularity, request.PreferMediaDate, out var photoNote);
                parts.Add(dateFolder);
                note = photoNote;
                break;
            }

            case ClassifyPrimary.NameMatch:
            {
                var stem = Path.GetFileNameWithoutExtension(source.Path);
                if (request.NameMatchRule == NameMatchRule.ExistingFolderFirst)
                {
                    var matches = existingFolders.Where(folder => MatchesFolderName(stem, folder)).ToArray();
                    if (matches.Length == 0)
                    {
                        skipReason = "未命中目标已有同名文件夹，留在原处待核对。";
                        return "";
                    }
                    if (matches.Length > 1)
                    {
                        // Prefer exact stem match if one exists; otherwise report ambiguity.
                        var exact = matches.FirstOrDefault(f => string.Equals(f, stem, StringComparison.OrdinalIgnoreCase));
                        if (exact is null)
                        {
                            skipReason = $"匹配到多个候选文件夹（{string.Join("、", matches.Take(3))}），留在原处待核对。";
                            return "";
                        }
                        parts.Add(exact);
                    }
                    else
                    {
                        parts.Add(matches[0]);
                    }
                    note = "匹配已有同名夹";
                }
                else
                {
                    var prefix = ExtractPrefix(stem);
                    if (prefix.Length > 0)
                    {
                        parts.Add(prefix);
                        note = "按前缀自动建夹";
                    }
                    else
                    {
                        parts.Add("未带前缀文件");
                    }
                }
                break;
            }
        }

        if (request.ClassifyBy != ClassifyPrimary.Date && request.SubDateLayer != ClassifyDateGranularity.None)
        {
            var subDate = FormatDateFolder(source, request.SubDateLayer, request.PreferMediaDate, out var photoNote);
            parts.Add(subDate);
            if (photoNote is not null)
                note = note is null ? photoNote : $"{note} · {photoNote}";
        }

        return Path.Combine([.. parts]);
    }

    private static string FormatDateFolder(CopySource source, ClassifyDateGranularity granularity, bool preferMediaDate, out string? photoNote)
    {
        photoNote = null;
        var modTicks = source.Stamp?.ModifiedUtcTicks ?? DateTimeOffset.UtcNow.UtcTicks;
        var chosenTicks = preferMediaDate && source.MediaTakenUtcTicks is { } mediaTicks ? mediaTicks : modTicks;
        var chosenLocal = new DateTime(chosenTicks, DateTimeKind.Utc).ToLocalTime();

        if (preferMediaDate && source.MediaTakenUtcTicks is { } mTicks)
        {
            var modLocal = new DateTime(modTicks, DateTimeKind.Utc).ToLocalTime();
            if (chosenLocal.Year != modLocal.Year || chosenLocal.Month != modLocal.Month)
                photoNote = $"按拍摄期 {chosenLocal:yyyy-MM}";
        }

        return granularity == ClassifyDateGranularity.Year
            ? $"{chosenLocal:yyyy}年"
            : $"{chosenLocal:yyyy-MM}";
    }

    private static bool MatchesFolderName(string stem, string folderName)
    {
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(folderName)) return false;
        if (string.Equals(stem, folderName, StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.StartsWith(folderName, StringComparison.OrdinalIgnoreCase) && stem.Length > folderName.Length && IsPrefixSeparator(stem[folderName.Length]))
            return true;
        var prefix = ExtractPrefix(stem);
        return prefix.Length > 0 && string.Equals(prefix, folderName, StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractPrefix(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return "";
        for (var i = 0; i < stem.Length; i++)
        {
            if (IsPrefixSeparator(stem[i]))
            {
                var candidate = stem[..i].Trim().TrimEnd('.');
                return IsValidSegment(candidate) ? candidate : "";
            }
        }
        return "";
    }

    private static bool IsPrefixSeparator(char c) => c is '-' or '_' or ' ' or '—' or '·' or '（' or '(' or '【' or '[';

    private static bool IsValidSegment(string value) =>
        value.Length is > 0 and <= 255 &&
        !value.EndsWith('.') &&
        !value.EndsWith(' ') &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':', '*', '?', '"', '<', '>', '|']) < 0;

    public static bool MatchesGatherFilter(string path, GatherFilter filter)
    {
        if (filter == GatherFilter.All) return true;
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length == 0) return false;
        return filter switch
        {
            GatherFilter.DocumentsAndSheets => DocumentExtensions.Contains(ext) || SheetExtensions.Contains(ext) || PresentationExtensions.Contains(ext),
            GatherFilter.Images => ImageExtensions.Contains(ext),
            _ => true
        };
    }

    public static string CategoryFolder(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Length == 0) return "其他文件";
        if (ImageExtensions.Contains(ext)) return "图片";
        if (SheetExtensions.Contains(ext)) return "表格";
        if (PresentationExtensions.Contains(ext)) return "演示";
        if (DocumentExtensions.Contains(ext)) return "文档";
        if (MediaExtensions.Contains(ext)) return "音视频";
        if (ArchiveExtensions.Contains(ext)) return "压缩包";
        if (InstallerExtensions.Contains(ext)) return "安装程序";
        return "其他文件";
    }

    public static CopyPlan Decide(CopyPlan plan, Guid id, CopyDecision decision, Func<string, CopyTarget?> target)
    {
        var item = plan.Items.Single(x => x.Id == id);
        if (!item.NeedsDecision) throw new InvalidOperationException("此项不需要确认。");
        var replacement = decision switch
        {
            CopyDecision.Skip => item with { Action = CopyAction.Skip, Message = "用户选择跳过。" },
            CopyDecision.Overwrite when item.CanOverwrite => item with { Action = CopyAction.Overwrite, Message = "已确认覆盖；执行前将再次检查目标。" },
            CopyDecision.Number => item with
            {
                Action = CopyAction.Number,
                ApprovedTarget = null,
                BatchConflict = false,
                Target = Number(item.Target, false, p => Occupied(p, target) || plan.Items.Any(x => x.Id != id && x.Action != CopyAction.Skip && Equal(x.Target, p))),
                Message = "编号保留两个文件。"
            },
            _ => throw new InvalidOperationException("本批重名或目录不能直接覆盖，请编号或跳过。")
        };
        return plan with { Items = plan.Items.Select(x => x.Id == id ? replacement : x).ToArray() };
    }

    public static string ExtensionFolder(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length == 0 ? "无扩展名" : extension.ToUpperInvariant();
    }

    public static string Number(string path, bool directory, Func<string, bool> exists)
    {
        if (!exists(path)) return path;
        var parent = Path.GetDirectoryName(path)!;
        var stem = directory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var extension = directory ? "" : Path.GetExtension(path);
        for (var index = 1; ; index++)
        {
            var name = $"{stem} ({index}){extension}";
            if (name.Length > 255) throw new PathTooLongException("编号后的文件名超过 255 个字符，请缩短原文件名或使用其他规则。");
            var next = Path.Combine(parent, name);
            if (!exists(next)) return next;
        }
    }

    private static bool Occupied(string path, Func<string, CopyTarget?> target)
    {
        var state = target(path);
        if (state?.Error is { } error) throw new IOException(error);
        return state is not null;
    }

    public static bool Equal(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool Within(string path, string parent)
    {
        path = Normalize(path);
        parent = Normalize(parent);
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("请填写完整的磁盘或网络目录路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var segment in full[(Path.GetPathRoot(full)?.Length ?? 0)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment.Length > 255 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':'))
                throw new ArgumentException("路径包含过长的单个名称、不支持的名称或数据流。");
        return full;
    }

    private static string? BlockedParent(string path, string destination, Func<string, CopyTarget?> target)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && Within(parent, destination); parent = Path.GetDirectoryName(parent))
        {
            var state = target(parent);
            if (state?.Error is { } error) return error;
            if (state is { IsDirectory: false }) return "上级目录位置已被文件占用。";
        }
        return null;
    }
}
