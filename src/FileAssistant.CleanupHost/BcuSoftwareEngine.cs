using System.Diagnostics;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Infrastructure.Cleanup;
using Klocman.Tools;
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Junk;
using UninstallTools.Junk.Containers;
using UninstallTools.Uninstaller;

namespace FileAssistant.CleanupHost;

/// <summary>BCU supplies discovery and matching. Our adapter owns review, execution and result verification.</summary>
internal sealed class BcuSoftwareEngine
{
    private static List<ApplicationUninstallerEntry> ReadEntries(IProgress<CleanupProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        progress.Report(new("正在读取已安装软件", 0, 0));
        var entries = new RegistryFactory(Array.Empty<Guid>()).GetUninstallerEntries(_ => { }).ToList();
        token.ThrowIfCancellationRequested();
        return entries;
    }

    public CleanupResponse List(IProgress<CleanupProgress> progress, CancellationToken token) => new()
    {
        Software = ReadEntries(progress, token).Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName) && !entry.IsUpdate)
            .Select(ToSoftware).OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
    };

    private static InstalledSoftware ToSoftware(ApplicationUninstallerEntry entry)
    {
        var isProtected = entry.IsProtected || entry.SystemComponent || entry.IsUpdate;
        if (!string.IsNullOrEmpty(entry.InstallLocation))
        {
            try { CleanupPathPolicy.Check(entry.InstallLocation); }
            catch { isProtected = true; }
        }
        var quiet = isProtected ? "" : QuietCommand(entry);
        var software = new InstalledSoftware(entry.RegistryPath ?? "", entry.DisplayName ?? "", entry.Publisher ?? "",
            entry.DisplayVersion ?? "", entry.InstallLocation ?? "", Math.Max(0, entry.EstimatedSize.GetKbSize()) * 1024,
            entry.UninstallerKind.ToString(), isProtected,
            !isProtected && (entry.UninstallPossible || quiet.Length > 0 || entry.UninstallerKind == UninstallerType.Msiexec && entry.BundleProviderKey != Guid.Empty),
            "", entry.UninstallString ?? "", entry.RegistryKeyName ?? "")
            { QuietUninstallString = quiet };
        return software with { Fingerprint = RegistryCleanup.Hash(software) };
    }

    private static string QuietCommand(ApplicationUninstallerEntry entry)
    {
        if (entry.UninstallerKind == UninstallerType.Msiexec && entry.BundleProviderKey != Guid.Empty &&
            (string.IsNullOrWhiteSpace(entry.UninstallString) || ApplicationEntryTools.PathPointsToMsiExec(entry.UninstallString)))
            return $"\"{Path.Combine(Environment.SystemDirectory, "msiexec.exe")}\" /x {entry.BundleProviderKey:B} /qn /norestart";
        if (!string.IsNullOrWhiteSpace(entry.QuietUninstallString))
        {
            // A malformed command or document association could open interactive shell UI.
            if (!ProcessStartCommand.TryParse(entry.QuietUninstallString, out var command) || string.IsNullOrWhiteSpace(command.FileName)) return "";
            var extension = Path.GetExtension(command.FileName).ToLowerInvariant();
            return extension is "" or ".exe" or ".com" or ".cmd" or ".bat" ? entry.QuietUninstallString : "";
        }
        if (entry.UninstallerKind == UninstallerType.InnoSetup && !string.IsNullOrWhiteSpace(entry.UninstallerFullFilename))
            return $"\"{entry.UninstallerFullFilename}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        if (entry.UninstallerKind == UninstallerType.Nsis && !string.IsNullOrWhiteSpace(entry.UninstallerFullFilename) &&
            string.Equals(entry.UninstallString?.Trim().Trim('"'), entry.UninstallerFullFilename, StringComparison.OrdinalIgnoreCase))
            return $"\"{entry.UninstallerFullFilename}\" /S";
        return "";
    }

    public CleanupResponse Uninstall(InstalledSoftware selected, bool quiet, IProgress<CleanupProgress> progress, CancellationToken token)
    {
        var entry = ReadEntries(progress, token).SingleOrDefault(item => string.Equals(item.RegistryPath, selected.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("软件登记已不存在，请刷新列表。");
        var current = ToSoftware(entry);
        if (current.Fingerprint != selected.Fingerprint || !current.CanUninstall)
            throw new IOException("软件登记已变化或不允许卸载，请刷新后重新选择。");
        // Use a fresh, verified registry entry, never a command supplied in the IPC request.
        if (quiet)
        {
            if (!current.CanUninstallQuietly) throw new IOException("此软件未提供后台卸载方式。");
            entry.QuietUninstallString = current.QuietUninstallString;
        }
        else if (entry.UninstallerKind == UninstallerType.Msiexec && entry.BundleProviderKey != Guid.Empty)
            entry.UninstallString = $"\"{Path.Combine(Environment.SystemDirectory, "msiexec.exe")}\" /x {entry.BundleProviderKey:B} /norestart";
        token.ThrowIfCancellationRequested();
        progress.Report(new(quiet ? "正在后台卸载软件" : "请在软件的卸载向导中完成操作", 0, 0, entry.DisplayName));
        using var process = entry.RunUninstaller(quiet, false, safeMode: true);
        if (process is null) return Result(CleanupStatus.NeedsAttention, "已交给系统处理。完成后请刷新软件列表，再检查残留。");
        while (!process.WaitForExit(200))
            if (token.IsCancellationRequested) return Result(CleanupStatus.NeedsAttention, "已停止等待。卸载程序可能仍在运行，请稍后刷新软件列表。");
        if (process.ExitCode == 1602) return Result(CleanupStatus.Cancelled, "卸载程序已取消操作。");
        if (process.ExitCode is not (0 or 3010 or 1641))
            return Result(CleanupStatus.NeedsAttention, $"卸载程序已退出，返回码 {process.ExitCode}。请刷新并核实结果。");
        var exists = ReadEntries(progress, CancellationToken.None).Any(item => string.Equals(item.RegistryPath, selected.Id, StringComparison.OrdinalIgnoreCase));
        return Result(exists ? CleanupStatus.NeedsAttention : CleanupStatus.Succeeded,
            exists ? "卸载程序已退出，软件登记仍存在。它可能仍有后续进程，请完成后刷新。" :
            process.ExitCode is 3010 or 1641 ? "软件登记已移除；卸载程序提示需要重启。可继续检查残留。" : "软件登记已移除。可继续检查残留。");

        CleanupResponse Result(CleanupStatus status, string message)
        {
            string[] removed = [];
            if (!string.IsNullOrWhiteSpace(selected.InstallLocation))
                try { if (FileCleanupEngine.IsGone(CleanupPathPolicy.Normalize(selected.InstallLocation))) removed = [selected.InstallLocation]; }
                catch (Exception) { /* An inaccessible path is not proof that it was removed. */ }
            return new() { Results = [new(selected.Name, status, message, removed)] };
        }
    }

    public CleanupResponse ScanResidue(InstalledSoftware selected, IProgress<CleanupProgress> progress, CancellationToken token)
    {
        if (selected.IsProtected || selected.Fingerprint != RegistryCleanup.Hash(selected with { Fingerprint = "" }))
            throw new IOException("软件信息无效或受保护，请重新选择。");
        var all = ReadEntries(progress, token);
        // A live registration must be uninstalled first. Retain the selected snapshot for matching after it disappears.
        if (all.Any(entry => string.Equals(entry.RegistryPath, selected.Id, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("此软件仍在已安装列表中。请先完成卸载并刷新，再检查残留。");
        var target = new ApplicationUninstallerEntry
        {
            DisplayName = selected.Name, Publisher = selected.Publisher, DisplayVersion = selected.Version,
            InstallLocation = selected.InstallLocation, RegistryPath = selected.Id, RegistryKeyName = selected.RegistryKeyName,
            UninstallString = selected.UninstallString,
            UninstallerKind = Enum.TryParse<UninstallerType>(selected.InstallerType, out var kind) ? kind : UninstallerType.Unknown
        };
        var scanners = typeof(IJunkCreator).Assembly.GetTypes().Where(type => typeof(IJunkCreator).IsAssignableFrom(type) && type.IsClass && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var warnings = new List<string>();
        var candidates = new Dictionary<string, CleanupResidue>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < scanners.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var scanner = (IJunkCreator)Activator.CreateInstance(scanners[index])!;
                progress.Report(new("正在检查软件残留", index, scanners.Length, scanner.CategoryName));
                scanner.Setup(all);
                foreach (var junk in scanner.FindJunk(target))
                {
                    token.ThrowIfCancellationRequested();
                    if (candidates.Count >= 500) { warnings.Add("残留清单已达 500 项，请分批清理后重新扫描。"); break; }
                    ResidueKind type; string path; string? value = null;
                    switch (junk)
                    {
                        case RegistryValueJunk registryValue: type = ResidueKind.RegistryValue; path = registryValue.FullRegKeyPath; value = registryValue.ValueName; break;
                        case RegistryKeyJunk registryKey: type = ResidueKind.RegistryKey; path = registryKey.FullRegKeyPath; break;
                        case FileSystemJunk file: type = file.Path is DirectoryInfo ? ResidueKind.Directory : ResidueKind.File; path = file.Path.FullName; break;
                        default: continue; // Never execute third-party process or startup cleanup actions.
                    }
                    try
                    {
                        if (type is ResidueKind.File or ResidueKind.Directory)
                        {
                            path = CleanupPathPolicy.Check(path);
                            if (all.Any(other => !string.IsNullOrWhiteSpace(other.InstallLocation) &&
                                (CleanupPathPolicy.Within(other.InstallLocation, path) || CleanupPathPolicy.Within(path, other.InstallLocation))))
                                throw new IOException("仍有已安装软件使用此目录，已排除。");
                        }
                        var id = RegistryCleanup.Hash(new { type, path = path.ToUpperInvariant(), value });
                        var confidence = junk.Confidence.GetRawConfidence();
                        if (candidates.TryGetValue(id, out var previous) && previous.Confidence >= confidence) continue;
                        candidates[id] = new(id, type, path, value, confidence, scanner.CategoryName,
                            RegistryCleanup.Fingerprint(type, path, value, token), selected.Id);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { warnings.Add(path + "：" + error.Message); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { warnings.Add(scanners[index].Name + "：" + error.Message); }
        }
        // A parent already represents its descendants. Avoid stale duplicate actions in the same execution.
        var compact = candidates.Values.Where(item => !candidates.Values.Any(parent => parent.Id != item.Id &&
            ((parent.Kind == ResidueKind.Directory && item.Kind is ResidueKind.Directory or ResidueKind.File && CleanupPathPolicy.Within(item.Path, parent.Path)) ||
             (parent.Kind == ResidueKind.RegistryKey && item.Kind is ResidueKind.RegistryKey or ResidueKind.RegistryValue && CleanupPathPolicy.Within(item.Path, parent.Path)))));
        return new() { Residues = compact.OrderByDescending(item => item.Confidence).ThenBy(item => item.Path).ToArray(), Warnings = warnings.Distinct().ToArray() };
    }
}
