using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Core.Features.CreateOrRename;

public enum NamingMode { Rename, Create }
public enum NamingConflict { Block, Skip, Number }
public enum NamingAction { Ready, Numbered, Skipped, Invalid, Existing }
public enum NamingOutcome { Success, Skipped, Failed, Cancelled }
public enum CreationKind { Directory, Text, Markdown, Template }
public enum NamingOrder { Input, Name, Modified }
public enum NameCase { Unchanged, Lower, Upper }
public sealed record NamingRules(string Pattern = "{name}", string Prefix = "", string Suffix = "",
    string Find = "", string Replacement = "", string Delete = "", int RemoveAt = 0, int RemoveCount = 0,
    NameCase Case = NameCase.Unchanged, int Start = 1, int Step = 1, int Digits = 3,
    bool NumberPerDirectory = false, NamingOrder Order = NamingOrder.Input);
public sealed record NamingRequest(NamingMode Mode, IReadOnlyList<string> Paths, NamingRules Rules,
    NamingConflict Conflict = NamingConflict.Block, bool RenameFolders = true, bool FolderContents = false,
    bool IncludeSubfolders = false, string Destination = "", string Names = "", int Count = 1,
    CreationKind Kind = CreationKind.Directory, string Subdirectories = "", string Template = "",
    IReadOnlyList<string>? DirectoryPaths = null, bool ReuseExistingDirectories = false,
    IReadOnlyList<CreationEntry>? Entries = null,
    IReadOnlyDictionary<string, string>? CustomNames = null);
public sealed record CreationEntry(string RelativePath, bool IsDirectory = true, string Template = "");
public sealed record NamingSource(string Path, bool IsDirectory, FileStamp? Stamp, string? Error = null);
public sealed record NamingPlanItem(Guid Id, NamingSource? Source, string Target, bool IsDirectory,
    NamingAction Action = NamingAction.Ready, string? Message = null, string? FinalTarget = null, string? RelativeTarget = null,
    string? RequestedRelativePath = null, FileStamp? ExistingStamp = null,
    string? TemplatePath = null, NamingSource? CreationTemplate = null)
{
    public bool CanRun => Action is NamingAction.Ready or NamingAction.Numbered;
    public string OriginalName => Source is null ? "新增" : Path.GetFileName(Source.Path);
    public string NewName => Source is null && RelativeTarget is not null ? RelativeTarget : Path.GetFileName(Target);
    public string DisplayPath => FinalTarget ?? Target;
    public string Status => Action switch { NamingAction.Ready => Source is null ? "待创建" : "待改名", NamingAction.Numbered => "自动编号", NamingAction.Skipped => "跳过", NamingAction.Existing => "已有", _ => "需调整" };
    public string Details => (Source is null ? "" : Source.Path + "\n→ ") + DisplayPath + (Message is null ? "" : "\n" + Message);
}
public sealed record NamingPlan(NamingRequest Request, IReadOnlyList<NamingPlanItem> Items, NamingSource? Template = null)
{
    public int ReadyCount => Items.Count(x => x.CanRun);
    public int InvalidCount => Items.Count(x => x.Action == NamingAction.Invalid);
}
public sealed record PathChange(string Before, string After, bool IsDirectory);
public sealed record NamingItemResult(NamingPlanItem Item, NamingOutcome Outcome, string Message, string? ActualPath = null)
{
    public bool Retryable => Outcome is NamingOutcome.Failed or NamingOutcome.Cancelled;
    public string Description => $"{Item.OriginalName} → {ActualPath ?? Item.DisplayPath}\n{Message}";
}
public sealed record NamingReport(NamingPlan Plan, IReadOnlyList<NamingItemResult> Items, IReadOnlyList<PathChange> Changes,
    IReadOnlyList<ProducedFile> Outputs, bool Cancelled, string? RecoveryId = null)
{
    public string Summary => $"成功 {Items.Count(x => x.Outcome == NamingOutcome.Success)} · 跳过 {Items.Count(x => x.Outcome == NamingOutcome.Skipped && x.Item.Action != NamingAction.Existing)} · 未完成 {Items.Count(x => x.Retryable)}" + (Cancelled ? " · 已取消" : "");
}
public sealed record NamingRecovery(string Id, DateTimeOffset Created, string Title, int Remaining, string? Error = null)
{
    public string Description => $"{Created.LocalDateTime:g} · {Title} · {Remaining} 步可恢复" + (Error is null ? "" : "\n" + Error);
}
public sealed record NamingRestoreResult(IReadOnlyList<PathChange> Changes, IReadOnlyList<string> Removed, string Message);
public interface INamingService
{
    Task<NamingPlan> PlanAsync(NamingRequest request, CancellationToken token);
    Task<NamingReport> ExecuteAsync(NamingPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token);
    Task<NamingPlan> RetryAsync(NamingReport report, CancellationToken token);
    Task<IReadOnlyList<NamingRecovery>> RecoveriesAsync(CancellationToken token);
    Task<NamingRestoreResult> RestoreAsync(string id, CancellationToken token);
}
