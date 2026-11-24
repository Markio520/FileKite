using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;

namespace FileAssistant.Core.Engines;

/// <summary>Plans actual outputs, including many-to-one PDF creation and one-to-many page export.</summary>
public static class ConversionPlanner
{
    public static ConversionPlan Create(ConversionRequest request, IReadOnlyList<ProcessingSource> sources,
        Func<string, bool> pathExists, Func<string, FileStamp?>? existingFile = null)
    {
        if (!Enum.IsDefined(request.Location) || !Enum.IsDefined(request.Collision)) throw new FileProcessingException("不支持此输出或冲突策略。");
        if (request.Location == OutputLocation.Fixed && (!Path.IsPathFullyQualified(request.OutputDirectory) || request.OutputDirectory.Contains("://", StringComparison.Ordinal)))
            throw new FileProcessingException("请选择完整的本地输出文件夹路径。");
        if (!Enum.IsDefined(request.Target)) throw new FileProcessingException("不支持此输出格式。");
        if (request.Quality is < 1 or > 100) throw new FileProcessingException("图片质量应在 1–100 之间。");
        if (request.PdfDpi is < 72 or > 600) throw new FileProcessingException("PDF 导出分辨率应在 72–600 DPI 之间。");
        if (sources.Count == 0) throw new FileProcessingException("请先添加要转换的文件。");
        request.Options.Validate();
        var items = new List<ConversionPlanItem>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Even an input that does not exist at execution time must never become an output target.
        foreach (var source in sources.Where(source => Path.IsPathFullyQualified(source.Path)))
        {
            try { reserved.Add(Path.GetFullPath(source.Path)); }
            catch (ArgumentException) when (source.Error is not null) { }
        }
        var sourceIdentities = sources.Where(source => source.Identity is not null).Select(source => source.Identity!).ToHashSet(StringComparer.Ordinal);
        ConversionPlanItem AssignOutput(ConversionPlanItem item)
        {
            if (item.OutputPath is not { } name) return item;
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var directory = request.DirectoryFor(item.Sources[0].Path);
            var candidate = Path.Combine(directory, name);
            for (var suffix = 1; Path.GetFileName(candidate).Length <= 255 && (reserved.Contains(candidate) || pathExists(candidate)); suffix++)
            {
                if (request.Collision == ConversionCollision.Skip)
                    return item with { OutputPath = null, Skipped = true, Error = $"已跳过同名输出：{name}" };
                if (suffix == 1 && request.Collision == ConversionCollision.Overwrite && !reserved.Contains(candidate))
                {
                    if (existingFile?.Invoke(candidate) is not { } stamp)
                        return item with { OutputPath = null, Error = "同名位置不是可覆盖的普通文件，请调整保存位置。" };
                    if (!sourceIdentities.Contains(stamp.Identity))
                    {
                        reserved.Add(candidate);
                        return item with { OutputPath = candidate, OverwriteTarget = stamp, Detail = item.Detail is null ? "将覆盖同名文件" : item.Detail + " · 将覆盖同名文件" };
                    }
                }
                candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            }
            if (Path.GetFileName(candidate).Length > 255)
                return item with { OutputPath = null, Error = "输出文件名超过 255 个字符，请缩短原文件名后重新预览。" };
            reserved.Add(candidate);
            return item with { OutputPath = candidate };
        }
        var eligible = sources.Where(source => source.Error is null && (request.Options.Mode == ConversionMode.Process
            ? ConversionCapabilities.GetProcessingTargets(source.Path) : ConversionCapabilities.GetTargets(source.Path, includeSameFormat: true)).Contains(request.Target)).ToArray();
        foreach (var source in sources.Where(source => source.Error is not null))
            items.Add(new([source], null, Error: source.Error));
        foreach (var source in sources.Where(source => source.Error is null && !eligible.Contains(source)))
            items.Add(new([source], null, Error: "此文件不能转换为所选格式，请按类型分批转换。"));
        if (request.Target == ConversionTarget.Pdf)
        {
            foreach (var document in eligible.Where(source => ConversionCapabilities.IsOffice(source.Path)))
                items.Add(new([document], Path.GetFileNameWithoutExtension(document.Path) + ".pdf"));
            var images = eligible.Where(source => ConversionCapabilities.IsImage(source.Path)).ToArray();
            if (images.Length > 0) items.Add(new(images, "合并图片.pdf", Detail: string.Join("；", images.Select(source =>
                Path.GetFileName(source.Path) + (ConversionCapabilities.IsTiff(source.Path) && request.Options.AllTiffPages ? $" 全部 {source.Frames} 页" : $" 第 {request.Options.Frame} 帧/页")))));
        }
        else
        {
            var extension = ConversionCapabilities.Extension(request.Target);
            foreach (var source in eligible)
            {
                var stem = Path.GetFileNameWithoutExtension(source.Path);
                if (request.Target is ConversionTarget.Csv or ConversionTarget.Tsv)
                {
                    var sheets = source.Sheets ?? [];
                    if (sheets.Count == 0 || request.Options.Sheet > sheets.Count)
                    { items.Add(new([source], null, Error: $"工作表不存在（共 {sheets.Count} 表），请重新选择。")); continue; }
                    for (var sheet = 0; sheet < sheets.Count; sheet++)
                    {
                        if (request.Options.Sheet != 0 && request.Options.Sheet != sheet + 1) continue;
                        var safeName = string.Concat(sheets[sheet].Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
                        if (safeName.Length > 60) safeName = safeName[..60];
                        items.Add(new([source], $"{stem}_表{sheet + 1:D3}_{safeName}{extension}", Sheet: sheet + 1,
                            Detail: $"工作表 {sheet + 1}：{sheets[sheet]} · {request.Options.Encoding} · {(request.Target == ConversionTarget.Tsv ? "Tab" : request.Options.Separator)} · {(request.Options.ExportFormulas ? "公式" : "计算结果")}"));
                    }
                }
                else if (source.PdfPages > 0 && request.Target != ConversionTarget.Txt)
                    for (var page = 0; page < source.PdfPages; page++)
                        items.Add(new([source], $"{stem}_第{page + 1:D3}页{extension}", page));
                else if (ConversionCapabilities.IsTiff(source.Path) && request.Options.AllTiffPages)
                    for (var page = 0; page < source.Frames; page++)
                        items.Add(new([source], $"{stem}_第{page + 1:D3}页{extension}", ImageFrame: page, Detail: $"第 {page + 1}/{source.Frames} 页"));
                else if (ConversionCapabilities.IsImage(source.Path) && !ConversionCapabilities.IsAnimation(source.Path, request.Target))
                {
                    if (request.Options.Frame > source.Frames) items.Add(new([source], null, Error: $"帧/页不存在（共 {source.Frames} 帧/页）。"));
                    else items.Add(new([source], stem + extension, ImageFrame: request.Options.Frame - 1,
                        Detail: request.Target == ConversionTarget.Ico ? "16/24/32/48/64/128/256 像素" : $"第 {request.Options.Frame}/{source.Frames} 帧/页"));
                }
                else items.Add(new([source], stem + extension, Detail: ConversionCapabilities.IsAnimation(source.Path, request.Target) ? $"全部 {source.Frames} 帧（保留时序、循环）"
                    : source.AudioTracks is { Count: > 0 } tracks && request.Options.AudioTrack <= tracks.Count ? tracks[request.Options.AudioTrack - 1] : null));
            }
        }
        return new(request, items.Select(AssignOutput).ToArray());
    }
}
