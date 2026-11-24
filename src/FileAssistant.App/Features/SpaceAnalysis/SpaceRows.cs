using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;

namespace FileAssistant.App.Features.SpaceAnalysis;

public sealed class SpaceRow(SpaceSnapshot snapshot, int id, string programLabel = "", bool showFolder = false, bool logicalSize = false)
{
    // Recycled XAML containers can outlive their ItemsSource. A visible row owns only its display data.
    private SpaceEntry Entry { get; } = snapshot.Entries[id];
    public int Id => id;
    public string Name => FileDisplay.Name(Entry.Name, Entry.IsDirectory);
    public string Path { get; } = snapshot.PathOf(id);
    internal SpaceIdentity Identity => Entry.Stamp.Identity;
    internal bool SameDisplay(SpaceRow other) =>
        Id == other.Id && Path == other.Path && Entry == other.Entry && SharePercent == other.SharePercent && AccessLimited == other.AccessLimited;
    public bool IsDirectory => Entry.IsDirectory;
    public string Glyph => Entry.IsDirectory ? "\uE8B7" : "\uE8A5";
    public int IconTone => Entry.IsDirectory ? 1 : SpaceKinds.Of(Entry) switch
    {
        SpaceKind.Image => 2,
        SpaceKind.Document => 3,
        SpaceKind.Video or SpaceKind.Audio => 4,
        SpaceKind.Archive => 5,
        _ => 0
    };
    public double SharePercent { get; } = Share(snapshot, id, logicalSize);
    private static double Share(SpaceSnapshot snapshot, int id, bool logical)
    {
        var entry = snapshot.Entries[id];
        var parentBytes = snapshot.Entries[Math.Max(0, entry.Parent)].TotalBytes(logical);
        return parentBytes > 0 ? Math.Clamp(100.0 * entry.TotalBytes(logical) / parentBytes, 0, 100) : 0;
    }
    public Microsoft.UI.Xaml.Visibility ShowShareBar => SharePercent >= 1.0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.GridLength ShareFillStar => new(Math.Clamp(SharePercent, 0, 100), Microsoft.UI.Xaml.GridUnitType.Star);
    public Microsoft.UI.Xaml.GridLength ShareRestStar => new(Math.Clamp(100.0 - SharePercent, 0.01, 100), Microsoft.UI.Xaml.GridUnitType.Star);
    public string Allocated => SpaceLabels.Allocated(Entry);
    public string Logical => SpaceLabels.Logical(Entry);
    public string PrimarySize => logicalSize ? Logical : Allocated;
    public string SecondarySize => logicalSize ? Allocated : Logical;
    public string Date => Entry.Modified?.ToLocalTime().ToString("yyyy/MM/dd") ?? "—";
    public string DateAndTime => Entry.Modified?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "未知";
    public string Kind => SpaceKinds.Label(SpaceKinds.Of(Entry));
    private bool AccessLimited { get; } = IsAccessLimited(snapshot, id);
    private static bool IsAccessLimited(SpaceSnapshot snapshot, int id)
    {
        var entry = snapshot.Entries[id];
        if ((entry.Flags & SpaceEntryFlags.AccessDenied) != 0) return true;
        if (!entry.UnknownAllocation) return false;
        var path = snapshot.PathOf(id);
        return snapshot.Issues.Any(i => i.Kind == SpaceIssueKind.Access && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
    }
    private string EntryNote => AccessLimited ? "访问受限，已有统计仅覆盖可读取的内容"
        : Entry.IsDirectory && Entry.UnknownAllocation && !Entry.IsCloud && !Entry.Stamp.IsReparsePoint ? "目录占用尚未核实"
        : Entry.IsSkipped ? Entry.IsCloud ? "云端目录 · 已跳过" : "已跳过或无法核实"
        : Entry.IsPartial ? "占用统计不完整"
        : Entry.IsCloud ? "云端文件 · 仅显示元数据"
        : !Entry.IsShared ? string.Empty
        : Entry.IsDirectory ? "包含硬链接"
        : Entry.CountedBytes == 0 && Entry.AllocatedBytes > 0 ? "硬链接 · 占用已归入另一位置"
        : $"硬链接 · {Entry.Stamp.Links:N0} 个名称共享数据";
    public string Note => programLabel.Length == 0 ? EntryNote : EntryNote.Length == 0 ? programLabel : programLabel + " · " + EntryNote;
    public string AutomationLabel => $"{Name}，{Kind}，占用 {Allocated}，文件大小 {Logical}";
    public bool HasCaption => showFolder;
    public string RowCaption { get; } = !showFolder ? "" : snapshot.Entries[id].Parent <= 0 ? "扫描根目录"
        : System.IO.Path.GetRelativePath(snapshot.Root, snapshot.PathOf(snapshot.Entries[id].Parent));
}

public sealed record SpaceCrumb(int Id, string Name);
public sealed record SpaceFolderRow(int Id, string Name, string Size, double Percent, string AutomationLabel, bool More = false)
{
    public Microsoft.UI.Xaml.Visibility ShowUsageBar => !More && Percent >= 1.5 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.GridLength FillStar => new(Math.Clamp(Percent, 0, 100), Microsoft.UI.Xaml.GridUnitType.Star);
    public Microsoft.UI.Xaml.GridLength RestStar => new(Math.Clamp(100.0 - Percent, 0.01, 100), Microsoft.UI.Xaml.GridUnitType.Star);
}
public sealed record SpaceTypeRow(SpaceKind Kind, string Name, string Count, string Size, double Percent, string Unknown, Microsoft.UI.Xaml.Media.Brush Color);
public sealed record SpaceReviewRow(int Id, string Path, string Kind, string Size);

internal static class SpaceLabels
{
    public static string Size(long bytes) => bytes < 0 ? "未知" : FileDisplay.Size(bytes);
    public static string Logical(SpaceEntry entry) => entry.IsDirectory && entry.IsPartial
        ? entry.LogicalBytes > 0 ? "≥ " + Size(entry.LogicalBytes) : "未知" : Size(entry.LogicalBytes);

    public static string Allocated(SpaceEntry entry)
        => entry.UnknownAllocation
            ? "未知" : (entry.IsPartial ? "≥ " : "") + Size(entry.AllocatedBytes);

    public static string Elapsed(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒" : $"{duration.TotalSeconds:0.0} 秒";

    public static string IssueKind(SpaceIssueKind kind) => kind switch
    {
        SpaceIssueKind.Access => "权限不足或项目被占用",
        SpaceIssueKind.Changed => "扫描期间有变化",
        SpaceIssueKind.Link => "链接或重解析点",
        SpaceIssueKind.CloudDirectory => "云端目录",
        SpaceIssueKind.Metadata => "元数据缺失",
        _ => "扫描已取消"
    };
}
