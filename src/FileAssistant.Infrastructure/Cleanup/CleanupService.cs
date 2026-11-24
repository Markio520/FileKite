using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.Cleanup;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.Cleanup;

public sealed class CleanupService(string helperPath) : ICleanupService
{
    public Task<CleanupResponse> RunAsync(CleanupRequest request, bool elevated, IProgress<CleanupProgress>? progress, CancellationToken token) =>
        Task.Run(() => Run(request, elevated, progress, token), token);

    private CleanupResponse Run(CleanupRequest request, bool elevated, IProgress<CleanupProgress>? progress, CancellationToken token)
    {
        if (!File.Exists(helperPath)) throw new IOException("缺少卸载与清理组件，请重新生成或修复安装。");
        if (elevated && request.Operation is CleanupOperation.ListSoftware or CleanupOperation.UninstallSoftware or CleanupOperation.ScanResidue)
            throw new IOException("软件识别与卸载使用当前用户身份；卸载程序自行请求需要的权限。");
        var name = "FileAssistant.Cleanup." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        var start = new ProcessStartInfo(Path.GetFullPath(helperPath))
        {
            UseShellExecute = elevated, CreateNoWindow = !elevated, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath))!,
            Arguments = $"--pipe {name} --parent {Environment.ProcessId}"
        };
        if (elevated) start.Verb = "runas";
        try
        {
            using var child = Process.Start(start) ?? throw new IOException("无法启动清理组件。");
            WaitForHandshakeAsync(pipe.WaitForConnectionAsync, child.WaitForExitAsync, TimeSpan.FromSeconds(30), token).GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || processId != child.Id)
                throw new IOException("清理组件身份校验失败。");
            CleanupWire.Write(pipe, 0, request);
            // Keep the response channel alive after cancellation so partial deletions are reported.
            using var cancel = token.Register(() =>
            {
                try { lock (pipe) { pipe.WriteByte(3); pipe.Flush(); } }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
            while (true)
            {
                var messageType = pipe.ReadByte();
                switch (messageType)
                {
                    case 1:
                        var update = CleanupWire.Read<CleanupProgress>(pipe);
                        progress?.Report(update);
                        break;
                    case 2: return CleanupWire.Read<CleanupResponse>(pipe);
                    default:
                        var exit = child.WaitForExit(1000) ? $"（退出码 {child.ExitCode}）" : "";
                        throw new IOException("清理组件连接已中断" + exit + $"（消息 {messageType}）；请检查实际处理结果后再重试。");
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    internal static async Task WaitForHandshakeAsync(Func<CancellationToken, Task> connect, Func<CancellationToken, Task> exited,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var connection = connect(deadline.Token);
        var exit = exited(deadline.Token);
        try
        {
            if (await Task.WhenAny(connection, exit).ConfigureAwait(false) == exit && !connection.IsCompletedSuccessfully)
            {
                await exit.ConfigureAwait(false);
                throw new IOException("清理组件提前退出，请检查随软件提供的组件。");
            }
            await connection.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待清理组件连接超时，请重试。", error); }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(connection, exit).ConfigureAwait(false); }
            catch (Exception) when (deadline.IsCancellationRequested) { }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
