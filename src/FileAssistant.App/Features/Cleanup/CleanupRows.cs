using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Features.Cleanup;

namespace FileAssistant.App.Features.Cleanup;

public sealed record SoftwareRow(InstalledSoftware Item)
{
    public string Name => Item.Name;
    public string Detail => string.Join(" · ", new[] { Item.Publisher, Item.Version, Item.IsProtected ? "受保护" : "" }.Where(text => text.Length > 0));
    public string Size => Item.EstimatedBytes > 0 ? WorkspaceFile.FormatSize(Item.EstimatedBytes) : "未提供";
}
public sealed record CleanupFileRow(string Path, string Detail)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public bool IsDirectory => System.IO.Directory.Exists(Path);
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
}
public sealed record ResidueRow(CleanupResidue Item)
{
    public string Path => Item.Path + (Item.Kind == ResidueKind.RegistryValue ? " → " + (string.IsNullOrEmpty(Item.ValueName) ? "(默认值)" : Item.ValueName) : "");
    public string Detail => (Item.Kind switch { ResidueKind.File => "文件", ResidueKind.Directory => "文件夹", ResidueKind.RegistryKey => "注册表项", _ => "注册值" }) +
        " · " + (Item.Confidence >= 5 ? "关联较强" : Item.Confidence >= 2 ? "可能相关" : "关联较弱，请仔细核对") + " · " + Item.Reason;
}
public sealed record CleanupLockRow(CleanupLock Item)
{
    public string Name => $"{Item.Name} · PID {Item.ProcessId}";
    public string Detail => Item.CanClose ? Item.ExecutablePath : "受保护或无法确认身份，请自行处理。";
}
