using System.IO.Pipes;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.SpaceScan;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--parent" || args[4] != "--drive" ||
            !args[1].StartsWith("FileAssistant.Space.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1][20..], "N", out _) || !uint.TryParse(args[3], out var parent) ||
            args[5].Length != 1 || !char.IsAsciiLetter(args[5][0])) return 2;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await pipe.ConnectAsync(15000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent) return 3;
            // Parent disconnect (including cancellation/exit) ends the read-only worker.
            var monitor = Task.Run(async () =>
            {
                try { await pipe.ReadExactlyAsync(new byte[1], cancellation.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { cancellation.Cancel(); }
            });
            using var output = new BufferedStream(pipe, 65536);
            using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
            try
            {
                new NtfsSpaceScanner().ScanTo(writer, args[5] + ":\\", new InlineProgress(p => SpaceScanWire.WriteProgress(writer, p)), cancellation.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (!cancellation.IsCancellationRequested) { writer.Write((byte)3); writer.Write(error.Message); writer.Flush(); }
            }
            finally
            {
                await cancellation.CancelAsync();
                await monitor;
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException) { return 1; }
    }

    private sealed class InlineProgress(Action<SpaceScanProgress> action) : IProgress<SpaceScanProgress>
    { public void Report(SpaceScanProgress value) => action(value); }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
