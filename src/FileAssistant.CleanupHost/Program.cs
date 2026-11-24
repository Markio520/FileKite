using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Infrastructure.Cleanup;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.CleanupHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--parent" ||
            !args[1].StartsWith("FileAssistant.Cleanup.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1]["FileAssistant.Cleanup.".Length..], "N", out _) ||
            !int.TryParse(args[3], out var parentId)) return 2;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(20_000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId || parent.HasExited) return 3;
            if (pipe.ReadByte() != 0) return 4;
            var request = CleanupWire.Read<CleanupRequest>(pipe);
            using var cancellation = new CancellationTokenSource();
            var monitor = WatchCancellationAsync(pipe, cancellation);
            try
            {
                var progress = new InlineProgress(update => CleanupWire.Write(pipe, 1, update));
                CleanupResponse response;
                try { response = Execute(request, progress, cancellation.Token); }
                catch (OperationCanceledException) { response = new() { Cancelled = true }; }
                catch (Exception error) { response = new() { Error = FileCleanupEngine.Describe(error) }; }
                CleanupWire.Write(pipe, 2, response);
                return 0;
            }
            finally
            {
                cancellation.Cancel();
                monitor.GetAwaiter().GetResult();
            }
        }
        catch (Exception error)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FileAssistant.Core.AppProfile.Name, "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "cleanup-errors.log"), DateTimeOffset.Now.ToString("O") + "\n" + error + "\n");
            }
            catch { }
            return 1;
        }
    }

    private static async Task WatchCancellationAsync(Stream pipe, CancellationTokenSource source)
    {
        try { await pipe.ReadExactlyAsync(new byte[1], source.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { return; }
        catch (IOException) { }
        source.Cancel();
    }

    private static CleanupResponse Execute(CleanupRequest request, IProgress<CleanupProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return request.Operation switch
        {
            CleanupOperation.ListSoftware => new BcuSoftwareEngine().List(progress, token),
            CleanupOperation.UninstallSoftware => new BcuSoftwareEngine().Uninstall(request.Software ?? throw new IOException("未选择软件。"), request.QuietUninstall, progress, token),
            CleanupOperation.ScanResidue => new BcuSoftwareEngine().ScanResidue(request.Software ?? throw new IOException("未选择软件。"), progress, token),
            CleanupOperation.RemoveResidue => RegistryCleanup.Remove(request.Residues, request.Method, progress, token, request.BackupRegistry),
            CleanupOperation.PlanFiles => new() { Plan = new FileCleanupEngine().Plan(request.Paths, progress, token) },
            CleanupOperation.RunFiles => new() { Results = new FileCleanupEngine().Run(request.Plan ?? throw new IOException("请先检查清单。"), request.Method, progress, token) },
            CleanupOperation.FindLocks => WindowsFileLocks.Find(request.Paths, token),
            CleanupOperation.CloseLocks => WindowsFileLocks.Close(request.Paths, request.Locks, request.Terminate, token),
            _ => throw new IOException("不支持的清理操作。")
        };
    }

    private sealed class InlineProgress(Action<CleanupProgress> report) : IProgress<CleanupProgress> { public void Report(CleanupProgress value) => report(value); }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
