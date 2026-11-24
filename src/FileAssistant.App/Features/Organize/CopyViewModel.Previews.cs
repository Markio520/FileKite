using CommunityToolkit.Mvvm.ComponentModel;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.Organize;

public sealed record CopyTargetFolderChild(
    string DisplayName,
    string SourceDetail,
    string StatusBadge,
    bool IsWarning);

public sealed partial class CopyTargetFolderGroup : ObservableObject
{
    public string Key { get; }
    public string FolderDisplayPath { get; }
    public string BadgeText { get; }
    public bool IsWarningGroup { get; }
    public bool IsSkeletonDirectory { get; }
    public string SkeletonDetail { get; }
    public bool HasSkeletonDetail => SkeletonDetail.Length > 0;
    public IReadOnlyList<CopyTargetFolderChild> Children { get; }
    public bool HasChildren => Children.Count > 0;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ChevronGlyph), nameof(ShowChildren))]
    public partial bool IsExpanded { get; set; }

    public string ChevronGlyph => !HasChildren ? "" : IsExpanded ? "\uE70D" : "\uE76C";
    public bool ShowChildren => IsExpanded && HasChildren;

    public CopyTargetFolderGroup(
        string key,
        string folderDisplayPath,
        string badgeText,
        IReadOnlyList<CopyTargetFolderChild> children,
        bool isExpanded = false,
        bool isWarningGroup = false,
        bool isSkeletonDirectory = false,
        string skeletonDetail = "")
    {
        Key = key;
        FolderDisplayPath = folderDisplayPath;
        BadgeText = badgeText;
        Children = children;
        IsExpanded = isExpanded;
        IsWarningGroup = isWarningGroup;
        IsSkeletonDirectory = isSkeletonDirectory;
        SkeletonDetail = skeletonDetail;
    }

    public void ToggleExpanded()
    {
        if (HasChildren) IsExpanded = !IsExpanded;
    }
}

public partial class CopyViewModel
{
    private const int MaxRenderedChildrenPerGroup = 200;
    private sealed record FilePreview(string Text, string Status, string Details);
    private readonly Dictionary<string, FilePreview> filePreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FilePreview> folderPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> manuallyToggledGroups = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CopyTargetFolderGroup> TargetFolderGroups { get; private set; } = [];
    public bool HasTargetFolderGroups => TargetFolderGroups.Count > 0;
    public bool ShowEmptyTargetFoldersNotice => !HasTargetFolderGroups;
    public string EmptyTargetFoldersMessage => IsScanning
        ? "正在扫描并汇总目标文件夹结构…"
        : string.IsNullOrWhiteSpace(Destination) && EffectiveLocation == OutputLocation.Fixed
            ? "请先在右侧选择或填写目标文件夹，即可按目标目录预览归档结构。"
            : IsLayoutSkeleton
                ? "当前输入没有可复制的文件夹层级（单文件不含目录结构），请添加文件夹。"
                : Feedback;

    public void RecordGroupExpansion(CopyTargetFolderGroup group)
    {
        if (group.IsExpanded) manuallyToggledGroups.Add(group.Key);
        else manuallyToggledGroups.Remove(group.Key);
    }

    private void RebuildFilePreviews()
    {
        filePreviews.Clear();
        folderPreviews.Clear();
        var showResults = ShowResults || ShowRecovery && panelBeforeRecovery == CopyPanel.Results;
        var plan = showResults ? Report?.Plan : Plan;
        var groups = new List<CopyTargetFolderGroup>();

        if (plan is not null)
        {
            var results = showResults ? Report!.Items.ToDictionary(result => result.Item.Id) : null;
            var verbDone = plan.IsMove ? "已移动" : "已复制";
            var verbTodo = plan.IsMove ? "待移动" : "待复制";

            foreach (var group in plan.Items.GroupBy(item => item.Source.Path, StringComparer.OrdinalIgnoreCase))
            {
                var item = group.FirstOrDefault(candidate => candidate.Action == CopyAction.Rejected) ?? group.First();
                var result = results?.GetValueOrDefault(item.Id);
                var target = result?.Output?.FullPath ?? result?.Item.Target ?? item.Target;
                var text = item.Action == CopyAction.Rejected
                    ? (plan.IsMove ? "不可移动" : "不可复制")
                    : item.Action == CopyAction.Skip && !showResults
                        ? "— 本次跳过"
                        : "→ " + Path.GetRelativePath(plan.Request.DirectoryFor(item.Source), target);

                var status = result?.Outcome switch
                {
                    CopyOutcome.Success => item.Source.IsDirectory ? (plan.IsSkeleton ? "空目录已建" : "目录已保留") : verbDone,
                    CopyOutcome.Skipped => "已跳过",
                    CopyOutcome.Pending => "同名待确认",
                    CopyOutcome.Cancelled => "已取消",
                    CopyOutcome.NotStarted => "尚未开始",
                    CopyOutcome.Failed => "未完成",
                    _ => item.Action switch
                    {
                        CopyAction.Copy when !item.Source.IsDirectory => FormatNote(item.Note, plan.IsMove),
                        CopyAction.Number => string.IsNullOrEmpty(item.Note) ? item.Status : $"{item.Status} · {item.Note}",
                        CopyAction.Skip => string.IsNullOrEmpty(item.Message) ? item.Status : $"{item.Status} · {item.Message}",
                        CopyAction.Pending => "同名待确认",
                        _ => item.Status
                    }
                };
                var details = string.Join("\n\n", group.Select(candidate =>
                    results?.GetValueOrDefault(candidate.Id)?.Description ?? candidate.Paths));
                filePreviews[item.Source.Path] = new(text, status, details);
            }

            // Flat, classify, and extension layouts summarize each imported root directory.
            foreach (var group in plan.Items.Where(item => item.Source.Root.Length > 0 && !item.Source.IsDirectory)
                .GroupBy(item => item.Source.Root, StringComparer.OrdinalIgnoreCase))
            {
                var count = group.Count();
                var status = showResults
                    ? $"{verbDone} {group.Count(item => results?.GetValueOrDefault(item.Id)?.Outcome == CopyOutcome.Success):N0} / {count:N0} 个文件"
                    : $"{count:N0} 个文件 · {group.Count(item => item.CanRun):N0} 个{verbTodo}";
                var destination = plan.Request.DirectoryFor(group.First().Source);
                folderPreviews[group.Key] = new("→ " + destination, status, $"{group.Key}\n→ {destination}");
            }

            BuildTargetFolderGroups(plan, results, showResults, groups);
        }

        TargetFolderGroups = groups;
        OnPropertyChanged(nameof(TargetFolderGroups));
        OnPropertyChanged(nameof(HasTargetFolderGroups));
        OnPropertyChanged(nameof(ShowEmptyTargetFoldersNotice));
        OnPropertyChanged(nameof(EmptyTargetFoldersMessage));
        UpdateFilePreviews();
    }

    private static string FormatNote(string? note, bool isMove) =>
        string.IsNullOrEmpty(note) ? "" : isMove ? $"{note} · 移动" : note;

    private void BuildTargetFolderGroups(
        CopyPlan plan,
        Dictionary<Guid, CopyItemResult>? results,
        bool showResults,
        List<CopyTargetFolderGroup> groups)
    {
        if (plan.IsSkeleton)
        {
            foreach (var item in plan.Items.OrderBy(x => x.Target, StringComparer.OrdinalIgnoreCase))
            {
                var result = results?.GetValueOrDefault(item.Id);
                var rejected = item.Action == CopyAction.Rejected || result?.Outcome == CopyOutcome.Failed;
                var badge = showResults
                    ? (result?.Outcome == CopyOutcome.Success ? "已建空目录" : result?.Message ?? "未完成")
                    : rejected ? "不可创建" : string.IsNullOrEmpty(item.Note) ? "新建空目录" : item.Note;
                var detail = rejected
                    ? (result?.Message ?? item.Message ?? item.Source.Path)
                    : $"来源：{item.Source.Path}";
                groups.Add(new CopyTargetFolderGroup(
                    item.Target,
                    item.Target,
                    badge,
                    [],
                    isExpanded: false,
                    isWarningGroup: rejected,
                    isSkeletonDirectory: true,
                    skeletonDetail: detail));
            }
            return;
        }

        var fileItems = plan.Items.Where(x => !x.Source.IsDirectory).ToArray();
        var autoExpandSmall = fileItems.Length <= 24;
        var runnableGroups = fileItems
            .Where(x => x.CanRun)
            .GroupBy(x => Path.GetDirectoryName(results?.GetValueOrDefault(x.Id)?.Output?.FullPath ?? x.Target) ?? plan.Request.DirectoryFor(x.Source), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < runnableGroups.Length; i++)
        {
            var g = runnableGroups[i];
            var baseDest = plan.Request.DirectoryFor(g.First().Source);
            var isRoot = CopyPlanner.Equal(g.Key, baseDest);
            var headerText = isRoot ? $"{g.Key}（目标根目录）" : g.Key;
            var total = g.Count();
            var badge = showResults
                ? $"成功 {g.Count(x => results?.GetValueOrDefault(x.Id)?.Outcome == CopyOutcome.Success):N0} / {total:N0} 项"
                : $"{(plan.IsMove ? "移入" : "写入")} {total:N0} 项";

            var children = BuildChildRows(g, results, showResults);
            var expand = manuallyToggledGroups.Contains(g.Key) || (autoExpandSmall && i < 3);
            groups.Add(new CopyTargetFolderGroup(g.Key, headerText, badge, children, isExpanded: expand));
        }

        // Surface pending/skipped/rejected items in a clear review group so nothing is silently hidden!
        var nonRunnable = fileItems.Where(x => !x.CanRun).ToArray();
        if (nonRunnable.Length > 0)
        {
            const string reviewKey = "__review__";
            var hasAttention = nonRunnable.Any(x => x.NeedsDecision || x.Action == CopyAction.Rejected);
            var badge = hasAttention
                ? $"待处理 / 异常 {nonRunnable.Length:N0} 项"
                : $"跳过 {nonRunnable.Length:N0} 项";
            var children = BuildChildRows(nonRunnable, results, showResults);
            var expand = manuallyToggledGroups.Contains(reviewKey) || hasAttention || autoExpandSmall;
            groups.Add(new CopyTargetFolderGroup(
                reviewKey,
                "暂不写入 / 待核对项目",
                badge,
                children,
                isExpanded: expand,
                isWarningGroup: hasAttention));
        }
    }

    private static IReadOnlyList<CopyTargetFolderChild> BuildChildRows(
        IEnumerable<CopyPlanItem> items,
        Dictionary<Guid, CopyItemResult>? results,
        bool showResults)
    {
        var list = new List<CopyTargetFolderChild>();
        var count = 0;
        var total = 0;
        foreach (var item in items)
        {
            total++;
            if (count >= MaxRenderedChildrenPerGroup) continue;
            count++;
            var res = results?.GetValueOrDefault(item.Id);
            var targetPath = res?.Output?.FullPath ?? item.Target;
            var name = item.CanRun ? Path.GetFileName(targetPath) : Path.GetFileName(item.Source.Path);
            var sourceParent = Path.GetFileName(Path.GetDirectoryName(item.Source.Path) ?? "");
            var sourceDetail = sourceParent.Length > 0 ? $"来自 {sourceParent}" : item.Source.Path;
            var status = showResults
                ? (res?.Message ?? item.Status)
                : item.CanRun
                    ? (item.Note ?? (item.Action == CopyAction.Number ? "自动编号" : WorkspaceFile.FormatSize(item.Source.Stamp?.Length ?? 0)))
                    : (item.Message ?? item.Status);
            var warn = item.NeedsDecision || item.Action == CopyAction.Rejected || res?.Outcome == CopyOutcome.Failed;
            list.Add(new CopyTargetFolderChild(name, sourceDetail, status, warn));
        }
        if (total > MaxRenderedChildrenPerGroup)
        {
            list.Add(new CopyTargetFolderChild(
                $"… 其余 {total - MaxRenderedChildrenPerGroup:N0} 个文件已折叠",
                "可切回「文件列表」搜索或筛选定位具体文件",
                $"共 {total:N0} 项",
                IsWarning: false));
        }
        return list;
    }

    private void UpdateFilePreviews()
    {
        foreach (var file in Input.VisibleFiles)
        {
            var preview = file.IsCollectionFolder ? null : filePreviews.GetValueOrDefault(file.FullPath);
            if (preview is null && file.Source?.IsFolder == true)
                preview = folderPreviews.GetValueOrDefault(file.FullPath);
            file.NamePreview.ApplyDiffPreview(file.Name, preview?.Text ?? "", preview?.Status ?? "", preview?.Details ?? "");
        }
    }
}
