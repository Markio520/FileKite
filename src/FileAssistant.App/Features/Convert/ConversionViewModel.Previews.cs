using CommunityToolkit.Mvvm.ComponentModel;
using FileAssistant.Core.Engines;

namespace FileAssistant.App.Features.Convert;

public partial class ConversionViewModel
{
    private sealed record FilePreview(string Text, string Status, string Details);
    private sealed record PreviewOutput(ConversionPlanItem Item, string? Path, string? Issue, string Detail, bool Skipped);
    private readonly Dictionary<string, FilePreview> filePreviews = new(StringComparer.OrdinalIgnoreCase);
    private bool showReportPreviews;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreviewDetails), nameof(PlanCount))]
    public partial IReadOnlyList<string> PreviewDetails { get; set; } = [];
    public bool HasPreviewDetails => PreviewDetails.Count > 0;

    partial void OnPlanChanged(ConversionPlan? value)
    {
        if (value is not null) showReportPreviews = false;
        RebuildFilePreviews();
        NotifyScope();
    }
    partial void OnReportChanged(ConversionReport? value)
    {
        showReportPreviews = value is not null;
        RebuildFilePreviews();
        NotifyPanel();
        OnPropertyChanged(nameof(PlanSummary));
    }
    private void RebuildFilePreviews()
    {
        filePreviews.Clear();
        var completed = showReportPreviews && Report is not null;
        var items = completed ? Report!.Items.Select(result => new PreviewOutput(result.Item, result.Output?.FullPath,
                result.Output is null ? result.Message : null, result.Message, result.Skipped)).ToArray()
            : Plan?.Items.Select(item => new PreviewOutput(item, item.CanRun ? item.OutputPath : null,
                item.CanRun ? null : item.Error ?? "没有可用的输出路径", item.Description, item.Skipped)).ToArray() ?? [];
        PreviewDetails = items.Select(item => string.Join("\n", new[]
        {
            item.Detail, "输入：" + string.Join("\n", item.Item.Sources.Select(source => source.Path)),
            item.Path is not null ? "输出：" + item.Path : item.Skipped ? "已按同名策略跳过" : completed ? "未生成文件" : "不可处理"
        })).ToArray();

        // Match full source paths, never display names: files in different folders
        // can share a name, and one source may produce several pages/sheets.
        var groups = new Dictionary<string, List<PreviewOutput>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            foreach (var path in item.Item.Sources.Select(source => source.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!groups.TryGetValue(path, out var group)) groups[path] = group = [];
                group.Add(item);
            }
        foreach (var (path, group) in groups)
        {
            var outputs = group.Where(item => item.Path is not null).Select(item => item.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var skipped = group.Count(item => item.Skipped);
            var failures = group.Count(item => item.Path is null && !item.Skipped);
            var mergedInputs = group.Max(item => item.Item.Sources.Count);
            var text = outputs.Length == 0 ? skipped == group.Count ? "已跳过" : completed ? "未完成" : "无法转换"
                : "→ " + Path.GetFileName(outputs[0]) + (outputs.Length > 1 ? $"（共 {outputs.Length:N0} 个文件）" : "");
            var status = outputs.Length == 0 ? group.FirstOrDefault(item => item.Issue is not null && !item.Skipped)?.Issue ?? group[0].Issue ?? "请查看明细"
                : failures > 0 ? $"{failures:N0} 项{(completed ? "未完成" : "不可处理")}，查看明细"
                : skipped > 0 ? $"{skipped:N0} 项同名跳过"
                : mergedInputs > 1 ? $"{mergedInputs:N0} 项合并为 1 个文件" + (completed ? " · 已完成" : "")
                : completed ? "已完成" : "";
            var details = "输入：" + path + "\n\n" + string.Join("\n\n", group.Select(item => item.Detail +
                (item.Path is not null ? "\n输出：" + item.Path : "\n未生成文件")));
            filePreviews[path] = new(text, status, details);
        }
        UpdateFilePreviews();
    }
    private void UpdateFilePreviews()
    {
        foreach (var file in Input.VisibleFiles)
        {
            var preview = !file.IsCollectionFolder ? filePreviews.GetValueOrDefault(file.FullPath) : null;
            file.NamePreview.ApplyDiffPreview(file.Name, preview?.Text ?? "", preview?.Status ?? "", preview?.Details ?? "");
        }
    }
}
