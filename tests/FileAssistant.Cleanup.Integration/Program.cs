using System.Diagnostics;
using System.Text.Json;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Infrastructure.Cleanup;
using Microsoft.Win32;

namespace FileAssistant.Cleanup.Integration;

internal static class Program
{
    private static string Root(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid fixture ID");
        return Path.Combine(Path.GetTempPath(), "FileAssistant-CleanupIntegration", id);
    }
    private static string Key(string id) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileAssistant-CleanupFixture-" + id;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task<int> Main(string[] args)
    {
        // This executable can only modify its own GUID-scoped fixture registration and directory.
        if (args.Length == 2 && args[0] == "--interactive-fixture")
        {
            File.WriteAllText(Path.Combine(Root(args[1]), "wizard.flag"), "started");
            return 1;
        }
        if (args.Length == 2 && args[0] == "--uninstall-fixture")
        {
            var root = Root(args[1]);
            using (var key = Registry.CurrentUser.OpenSubKey(Key(args[1])))
                if (key?.GetValue("DisplayName") as string != "FileAssistant Cleanup Fixture " + args[1]) return 2;
            Registry.CurrentUser.DeleteSubKeyTree(Key(args[1]), false);
            File.WriteAllText(Path.Combine(root, "uninstalled.flag"), "done"); return 0;
        }
        if (args.Length == 2 && args[0] == "--hold-fixture")
        {
            var root = Root(args[1]);
            using var held = File.Open(Path.Combine(root, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
            File.WriteAllText(Path.Combine(root, "locked.flag"), "ready");
            await Task.Delay(TimeSpan.FromMinutes(2)); return 0;
        }
        if (args.Length != 2) return 2;
        var results = new List<string>(); var fixtureId = Guid.NewGuid().ToString("N"); var fixture = Root(fixtureId);
        Process? holder = null;
        try
        {
            Directory.CreateDirectory(fixture);
            var service = new CleanupService(Path.GetFullPath(args[0]));
            async Task<CleanupResponse> Run(CleanupRequest request)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var response = await service.RunAsync(request, false, null, timeout.Token);
                Require(response.Error.Length == 0, request.Operation + ": " + response.Error); return response;
            }
            var install = Path.Combine(fixture, "installed"); Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "residue.txt"), "fixture");
            using (var key = Registry.CurrentUser.CreateSubKey(Key(fixtureId)))
            {
                key.SetValue("DisplayName", "FileAssistant Cleanup Fixture " + fixtureId);
                key.SetValue("Publisher", "FileAssistant Isolated Tests"); key.SetValue("DisplayVersion", "1.0"); key.SetValue("InstallLocation", install);
                key.SetValue("UninstallString", "\"" + Environment.ProcessPath + "\" --interactive-fixture " + fixtureId);
                key.SetValue("EstimatedSize", 1, RegistryValueKind.DWord);
            }
            var list = await Run(new(CleanupOperation.ListSoftware));
            var software = list.Software.Single(item => item.Name == "FileAssistant Cleanup Fixture " + fixtureId);
            Require(software.CanUninstall, "Fixture must be uninstallable"); results.Add("BCU registry discovery through deployed helper: passed");
            var unsupported = await service.RunAsync(new(CleanupOperation.UninstallSoftware) { Software = software, QuietUninstall = true }, false, null, default);
            Require(!software.CanUninstallQuietly && unsupported.Error.Length > 0 && !File.Exists(Path.Combine(fixture, "wizard.flag")), "Quiet request must never start the interactive fallback");
            using (var key = Registry.CurrentUser.OpenSubKey(Key(fixtureId), true)!)
                key.SetValue("QuietUninstallString", "\"" + Environment.ProcessPath); // Missing closing quote
            list = await Run(new(CleanupOperation.ListSoftware));
            Require(!list.Software.Single(item => item.Id == software.Id).CanUninstallQuietly, "Malformed quiet command must not be offered");
            using (var key = Registry.CurrentUser.OpenSubKey(Key(fixtureId), true)!)
            {
                key.SetValue("UninstallString", "");
                key.SetValue("QuietUninstallString", "\"" + Environment.ProcessPath + "\" --uninstall-fixture " + fixtureId);
            }
            list = await Run(new(CleanupOperation.ListSoftware));
            Require(list.Software.Single(item => item.Id == software.Id).CanUninstallQuietly, "Quiet-only registrations must be supported");
            using (var key = Registry.CurrentUser.OpenSubKey(Key(fixtureId), true)!)
                key.SetValue("UninstallString", "\"" + Environment.ProcessPath + "\" --interactive-fixture " + fixtureId);
            list = await Run(new(CleanupOperation.ListSoftware));
            software = list.Software.Single(item => item.Name == "FileAssistant Cleanup Fixture " + fixtureId);
            Require(software.CanUninstallQuietly, "Explicit quiet command should be available");
            var tampered = await service.RunAsync(new(CleanupOperation.UninstallSoftware) { Software = software with { Fingerprint = "stale" } }, false, null, default);
            Require(tampered.Error.Length > 0 && !File.Exists(Path.Combine(fixture, "uninstalled.flag")), "Stale software selection must not execute");
            results.Add("Stale uninstall fingerprint rejected: passed");
            var uninstall = await Run(new(CleanupOperation.UninstallSoftware) { Software = software, QuietUninstall = true });
            Require(!File.Exists(Path.Combine(fixture, "wizard.flag")), "Quiet uninstall must never launch the wizard");
            Require(uninstall.Results.Single().Status == CleanupStatus.Succeeded && File.Exists(Path.Combine(fixture, "uninstalled.flag")), "Fixture uninstall failed: " + JsonSerializer.Serialize(uninstall));
            results.Add("Isolated software uninstall and registry verification: passed");
            var residue = await Run(new(CleanupOperation.ScanResidue) { Software = software });
            var own = residue.Residues.Where(item => item.Path.Equals(install, StringComparison.OrdinalIgnoreCase)).ToArray();
            Require(own.Length > 0, "BCU must detect fixture install directory; warnings=" + string.Join(";", residue.Warnings));
            var cleaned = await Run(new(CleanupOperation.RemoveResidue) { Residues = own, Method = CleanupMethod.Permanent });
            Require(cleaned.Results.All(item => item.Status == CleanupStatus.Succeeded) && !Directory.Exists(install), "Residue cleanup failed");
            results.Add("BCU residue discovery and selected-folder removal: passed");
            var locked = Path.Combine(fixture, "locked.txt"); File.WriteAllText(locked, "locked fixture");
            holder = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { Arguments = "--hold-fixture " + fixtureId, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            for (var i = 0; i < 100 && !File.Exists(Path.Combine(fixture, "locked.flag")); i++) await Task.Delay(50);
            Require(File.Exists(Path.Combine(fixture, "locked.flag")), "Lock fixture did not start");
            var locks = await Run(new(CleanupOperation.FindLocks) { Paths = [locked] });
            var ownLock = locks.Locks.Single(item => item.ProcessId == holder!.Id); Require(ownLock.CanClose, "Own fixture lock must be closable");
            var stopped = await Run(new(CleanupOperation.CloseLocks) { Paths = [locked], Locks = [ownLock], Terminate = true });
            Require(stopped.Results.Single().Status == CleanupStatus.Succeeded, "Fixture termination failed: " + JsonSerializer.Serialize(stopped));
            results.Add("Restart Manager detects and closes only the selected fixture process: passed");
            var plan = await Run(new(CleanupOperation.PlanFiles) { Paths = [locked] });
            var removed = await Run(new(CleanupOperation.RunFiles) { Plan = plan.Plan, Method = CleanupMethod.Permanent });
            Require(removed.Results.Single().Status == CleanupStatus.Succeeded && !File.Exists(locked), "Planned file deletion failed");
            results.Add("IPC plan, verified file removal and returned paths: passed");
            var recycle = Path.Combine(fixture, "recycle.txt"); File.WriteAllText(recycle, "isolated recycle fixture");
            var recyclingPlan = await Run(new(CleanupOperation.PlanFiles) { Paths = [recycle] });
            var recycling = await Run(new(CleanupOperation.RunFiles) { Plan = recyclingPlan.Plan, Method = CleanupMethod.Recycle });
            Require(recycling.Results.Single().Status == CleanupStatus.Succeeded && !File.Exists(recycle), "Recycle fixture failed: " + JsonSerializer.Serialize(recycling));
            results.Add("STA helper recycles the isolated fixture without permanent fallback: passed");
            File.WriteAllText(args[1], string.Join(Environment.NewLine, results)); return 0;
        }
        catch (Exception error) { File.WriteAllText(args[1], string.Join(Environment.NewLine, results) + Environment.NewLine + error); return 1; }
        finally
        {
            if (holder is not null) { if (!holder.HasExited) holder.Kill(); holder.WaitForExit(5000); holder.Dispose(); }
            Registry.CurrentUser.DeleteSubKeyTree(Key(fixtureId), false);
            // Re-resolve the exact GUID-owned test directory before recursive cleanup.
            if (fixture == Root(fixtureId) && Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }
}
