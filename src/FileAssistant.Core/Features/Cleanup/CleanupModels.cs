using FileAssistant.Core.Features.SpaceAnalysis;

namespace FileAssistant.Core.Features.Cleanup;

public enum CleanupOperation { ListSoftware, UninstallSoftware, ScanResidue, RemoveResidue, PlanFiles, RunFiles, FindLocks, CloseLocks }
public enum CleanupMethod { Recycle, Permanent }
public enum CleanupStatus { Succeeded, Failed, Cancelled, Partial, NeedsAttention }
public enum ResidueKind { File, Directory, RegistryKey, RegistryValue }

public sealed record InstalledSoftware(string Id, string Name, string Publisher, string Version,
    string InstallLocation, long EstimatedBytes, string InstallerType, bool IsProtected, bool CanUninstall,
    string Fingerprint, string UninstallString = "", string RegistryKeyName = "")
{
    public string QuietUninstallString { get; init; } = "";
    public bool CanUninstallQuietly => CanUninstall && QuietUninstallString.Length > 0;
}

public sealed record CleanupResidue(string Id, ResidueKind Kind, string Path, string? ValueName,
    int Confidence, string Reason, string Fingerprint, string SoftwareId);

public sealed record CleanupNode(string RelativePath, SpaceStamp Stamp, long Length);
public sealed record CleanupRoot(string Path, IReadOnlyList<CleanupNode> Nodes)
{
    public long Bytes => Nodes.Where(node => !node.Stamp.IsDirectory).Sum(node => Math.Max(0, node.Length));
}
public sealed record CleanupIssue(string Path, string Message);
public sealed record CleanupPlan(IReadOnlyList<CleanupRoot> Roots, IReadOnlyList<CleanupIssue> Issues, DateTimeOffset CreatedAt);
public sealed record CleanupResult(string Path, CleanupStatus Status, string Message, IReadOnlyList<string> RemovedPaths, string? ItemId = null);
public sealed record CleanupLock(int ProcessId, long StartTime, string Name, string ExecutablePath, bool CanClose);
public sealed record CleanupProgress(string Stage, int Completed, int Total, string Path = "");

/// <summary>Only explicit operation kinds are accepted by the helper; it never accepts shell commands.</summary>
public sealed record CleanupRequest(CleanupOperation Operation)
{
    public string[] Paths { get; init; } = [];
    public InstalledSoftware? Software { get; init; }
    public CleanupPlan? Plan { get; init; }
    public CleanupMethod Method { get; init; }
    public CleanupResidue[] Residues { get; init; } = [];
    public CleanupLock[] Locks { get; init; } = [];
    public bool QuietUninstall { get; init; }
    public bool BackupRegistry { get; init; } = true;
    public bool Terminate { get; init; }
}

public sealed record CleanupResponse
{
    public InstalledSoftware[] Software { get; init; } = [];
    public CleanupResidue[] Residues { get; init; } = [];
    public CleanupPlan? Plan { get; init; }
    public CleanupResult[] Results { get; init; } = [];
    public CleanupLock[] Locks { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public string Error { get; init; } = "";
    public bool Cancelled { get; init; }
}

public interface ICleanupService
{
    Task<CleanupResponse> RunAsync(CleanupRequest request, bool elevated, IProgress<CleanupProgress>? progress, CancellationToken token);
}
