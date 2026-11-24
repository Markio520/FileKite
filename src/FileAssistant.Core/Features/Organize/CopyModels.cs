using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Settings;

namespace FileAssistant.Core.Features.Organize;

public enum CopyOperation { Copy = 0, Move = 1 }
public enum CopyLayout { Preserve = 0, Flat = 1, Extension = 2, Classify = 3, Skeleton = 4 }
public enum GatherFilter { All = 0, DocumentsAndSheets = 1, Images = 2 }
public enum ClassifyPrimary { FileType = 0, Date = 1, NameMatch = 2, Extension = 3 }
public enum ClassifyDateGranularity { None = 0, YearMonth = 1, Year = 2 }
public enum NameMatchRule { ExistingFolderFirst = 0, PrefixAlways = 1 }
public enum CopyConflictPolicy { Number, Skip, Confirm }
public enum CopyAction { Copy, Number, Overwrite, Skip, Rejected, Pending }
public enum CopyOutcome { Success, Skipped, Failed, Cancelled, NotStarted, Pending }
public enum CopyDecision { Number, Skip, Overwrite }
public enum CopyRecoveryKind { OverwriteBackup = 0, MoveUndo = 1 }

public sealed record CopyRequest(
    IReadOnlyList<string> Paths,
    string Destination,
    CopyLayout Layout = CopyLayout.Preserve,
    CopyConflictPolicy Conflict = CopyConflictPolicy.Number)
{
    public CopyOperation Operation { get; init; } = CopyOperation.Copy;
    public OutputLocation Location { get; init; } = OutputLocation.Fixed;
    // Existing direct callers use strict verification, including pre-v6 retry plans.
    public bool VerifyHash { get; init; } = true;

    // Option 2 (Flat / Gather from subfolders) sub-options
    public GatherFilter GatherFilter { get; init; } = GatherFilter.All;
    public bool GatherPrefixSourceFolder { get; init; }

    // Option 3 (Auto classify into folders) sub-options
    public ClassifyPrimary ClassifyBy { get; init; } = ClassifyPrimary.FileType;
    public ClassifyDateGranularity SubDateLayer { get; init; } = ClassifyDateGranularity.None;
    public ClassifyDateGranularity DateGranularity { get; init; } = ClassifyDateGranularity.YearMonth;
    public bool PreferMediaDate { get; init; } = true;
    public NameMatchRule NameMatchRule { get; init; } = NameMatchRule.ExistingFolderFirst;

    // Option 4 (Copy empty folder structure only) sub-options
    public bool ReplaceSkeletonText { get; init; }
    public string SkeletonFind { get; init; } = "";
    public string SkeletonReplace { get; init; } = "";

    public string DirectoryFor(CopySource source) => BatchOutputDirectory.Resolve(Location, Destination,
        source.Root.Length == 0 ? source.Path : source.Root);
}

public sealed record FileStamp(long Length, long ModifiedUtcTicks, string Identity, string? Hash = null);

public sealed record CopySource(string Path, string Root, string Relative, bool IsDirectory, FileStamp? Stamp, string? Error = null)
{
    public long? MediaTakenUtcTicks { get; init; }
}

public sealed record CopyTarget(bool IsDirectory, FileStamp? Stamp, string? Error = null);

public sealed record CopyPlanItem(Guid Id, CopySource Source, string Target, CopyAction Action,
    string? Message = null, FileStamp? ApprovedTarget = null, bool BatchConflict = false)
{
    public string? Note { get; init; }
    public bool IsMove { get; init; }
    public bool CanRun => Action is CopyAction.Copy or CopyAction.Number or CopyAction.Overwrite;
    public bool NeedsDecision => Action == CopyAction.Pending;
    public bool CanOverwrite => NeedsDecision && !BatchConflict && ApprovedTarget is not null;
    public string Status => Action switch
    {
        CopyAction.Copy => Source.IsDirectory ? "创建目录" : IsMove ? "待移动" : "待复制",
        CopyAction.Number => "自动编号",
        CopyAction.Overwrite => "覆盖 · 保留备份",
        CopyAction.Skip => "跳过",
        CopyAction.Rejected => "不可处理",
        _ => "待确认"
    };
    public string Description => $"{Status} · {System.IO.Path.GetFileName(Source.Path)}";
    public string Paths => $"{Source.Path}\n→ {Target}{(Message is null ? "" : "\n" + Message)}";
}

public sealed record CopyPlan(CopyRequest Request, IReadOnlyList<CopyPlanItem> Items)
{
    public bool IsMove => Request.Operation == CopyOperation.Move && Request.Layout != CopyLayout.Skeleton;
    public bool IsSkeleton => Request.Layout == CopyLayout.Skeleton;
    public int FileCount => Items.Count(x => !x.Source.IsDirectory);
    public long TotalBytes => Items.Where(x => !x.Source.IsDirectory).Sum(x => x.Source.Stamp?.Length ?? 0);
    public long CopyBytes => Items.Where(x => x.CanRun && !x.Source.IsDirectory).Sum(x => x.Source.Stamp?.Length ?? 0);
    public int PendingCount => Items.Count(x => x.NeedsDecision);
    public int ReadyCount => Items.Count(x => x.CanRun);
}

public sealed record CopyItemResult(CopyPlanItem Item, CopyOutcome Outcome, string Message, ProducedFile? Output = null)
{
    public bool Retryable => Outcome is CopyOutcome.Failed or CopyOutcome.Cancelled or CopyOutcome.NotStarted or CopyOutcome.Pending;
    public string Description => $"{Item.Source.Path}\n→ {Item.Target}\n{Message}";
}

public sealed record CopyReport(CopyPlan Plan, IReadOnlyList<CopyItemResult> Items, bool Cancelled, string? StopReason = null)
{
    public IReadOnlyList<PathChange> Changes { get; init; } = [];
    public IReadOnlyList<ProducedFile> Outputs => Items.Where(x => x.Output is not null).Select(x => x.Output!).ToArray();
    public string Summary => $"成功 {Items.Count(x => x.Outcome == CopyOutcome.Success)} · 跳过 {Items.Count(x => x.Outcome == CopyOutcome.Skipped)} · 未完成 {Items.Count(x => x.Retryable)}" +
        (Cancelled ? " · 已取消" : "") + (StopReason is null ? "" : " · " + StopReason);
}

public sealed record MovedFileRecord(string OriginalPath, string MovedPath, long Length, long ModifiedUtcTicks, string Identity, string? Hash = null);

public sealed record CopyRecovery(Guid Id, string Target, string Backup, string Source, string NewHash, string OldHash,
    DateTimeOffset Created, string State, string? Message = null)
{
    public CopyRecoveryKind Kind { get; init; } = CopyRecoveryKind.OverwriteBackup;
    public IReadOnlyList<MovedFileRecord>? MovedItems { get; init; }
    public bool IsMoveUndo => Kind == CopyRecoveryKind.MoveUndo;
    public string ActionLabel => IsMoveUndo ? "撤回所选移动" : "恢复所选旧目标";
    public string Description => IsMoveUndo
        ? $"{Created.LocalDateTime:g} · {State}（共 {MovedItems?.Count ?? 0} 项文件移动）\n目标目录：{Target}{(Message is null ? "" : "\n" + Message)}"
        : $"{Created.LocalDateTime:g} · {State}\n{Target}\n备份：{Backup}{(Message is null ? "" : "\n" + Message)}";
}

public interface ICopyService
{
    Task<CopyPlan> PlanAsync(CopyRequest request, IProgress<ProcessingProgress>? progress, CancellationToken token);
    Task<CopyPlan> DecideAsync(CopyPlan plan, Guid item, CopyDecision decision, CancellationToken token);
    Task<CopyReport> ExecuteAsync(CopyPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token);
    Task<CopyPlan> RetryPlanAsync(CopyReport report, CancellationToken token);
    Task<IReadOnlyList<CopyRecovery>> RecoveriesAsync(CancellationToken token);
    Task RestoreAsync(Guid recovery, CancellationToken token);
    Task<IReadOnlyList<PathChange>> RestoreWithChangesAsync(Guid recovery, CancellationToken token) =>
        RestoreAsync(recovery, token).ContinueWith(_ => (IReadOnlyList<PathChange>)[], token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    Task DiscardBackupAsync(Guid recovery, CancellationToken token);
}
