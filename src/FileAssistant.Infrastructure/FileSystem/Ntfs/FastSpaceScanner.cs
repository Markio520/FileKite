using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.SpaceAnalysis;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.FileSystem.Ntfs;

/// <summary>Only the read-only helper is elevated. All ordinary file operations retain their existing token.</summary>
public sealed class FastSpaceScanner(string helperPath, string? excludedDirectory = null) : ISpaceScanner, IStreamingSpaceScanner
{
    private readonly SpaceScanner fallback = new(excludedDirectory);
    public IReadOnlyList<SpaceDrive> GetDrives() => fallback.GetDrives();

    public Task<SpaceSnapshot> ScanAsync(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token)
        => ScanCoreAsync(root, progress, token, true, SpaceScanWire.Read, (path, notice) =>
        {
            var snapshot = fallback.Scan(path, progress, token);
            snapshot.ScanNotice = notice;
            return snapshot;
        });

    public Task<T> ScanToAsync<T>(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token,
        Func<SpaceScanMetadata, int, IEnumerable<SpaceEntry>, T> receive)
        => ScanCoreAsync(root, progress, token, false, (r, attributes) => SpaceScanWire.Read(r, receive, token, attributes),
            (path, notice) => fallback.ScanTo(path, progress, token, (metadata, count, rows) =>
                receive(metadata with { ScanNotice = notice }, count, rows)));

    private async Task<T> ScanCoreAsync<T>(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token,
        bool retrySnapshotRead, Func<BinaryReader, long, T> readWire, Func<string, string, T> readDirectory)
    {
        root = SpaceScanner.Normalize(root);
        // Folder scans use the existing precise directory scope; whole local NTFS volumes use the MFT.
        if (!IsNtfsRoot(root)) return await Task.Run(() => readDirectory(root, string.Empty), token).ConfigureAwait(false);
        string reason;
        var receiving = false;
        try
        {
            return await Task.Run(() => RunHelper(root, progress, token, (reader, attributes) =>
            {
                receiving = true;
                return readWire(reader, attributes);
            }), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        // After a streaming consumer starts writing, a destination failure must roll back and surface.
        catch (Exception error) when ((!receiving || retrySnapshotRead) &&
            (SpaceScanner.IsFileError(error) || error is TimeoutException or InvalidOperationException))
        {
            token.ThrowIfCancellationRequested();
            reason = error is Win32Exception { NativeErrorCode: 1223 }
                ? "未授予快速扫描权限，已使用目录扫描；受保护目录可能无法统计。"
                : "快速扫描不可用，已使用目录扫描：" + error.Message;
        }
        progress?.Report(new(0, 0, 0, 0, TimeSpan.Zero, []) { Stage = reason });
        return await Task.Run(() => readDirectory(root, reason), token).ConfigureAwait(false);
    }

    internal static bool IsNtfsRoot(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') return false;
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (SpaceScanner.IsFileError(error)) { return false; }
    }

    private T RunHelper<T>(string root, IProgress<SpaceScanProgress>? progress, CancellationToken token, Func<BinaryReader, long, T> readWire)
    {
        if (!File.Exists(helperPath)) throw new IOException("缺少随软件附带的扫描程序。");
        var name = "FileAssistant.Space." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        using var registration = token.Register(() => pipe.Dispose());
        progress?.Report(new(0, 0, 0, 0, TimeSpan.Zero, []) { Stage = "正在请求磁盘快速扫描权限" });
        var start = new ProcessStartInfo(Path.GetFullPath(helperPath))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath))!,
            Arguments = $"--pipe {name} --parent {Environment.ProcessId} --drive {root[0]}"
        };
        try
        {
            token.ThrowIfCancellationRequested();
            using var child = Process.Start(start) ?? throw new IOException("无法启动快速扫描程序。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            var exited = child.WaitForExitAsync(timeout.Token);
            var completed = Task.WhenAny(connected, exited).GetAwaiter().GetResult();
            if (completed == exited && !connected.IsCompletedSuccessfully) throw new IOException("快速扫描程序提前退出。");
            try { connected.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("快速扫描程序连接超时。"); }
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != child.Id)
                throw new IOException("快速扫描进程身份校验失败。");
            using var reader = new BinaryReader(new BufferedStream(pipe, 65536));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var message = reader.ReadByte();
                switch (message)
                {
                    case 1:
                    case 5:
                        var update = SpaceScanWire.ReadProgress(reader, message == 5);
                        progress?.Report(update);
                        break;
                    case 2: return readWire(reader, 0);
                    case 4:
                        long attributes = reader.ReadInt64();
                        if (attributes < 0) throw new IOException("扫描程序返回了无效的空间占用。");
                        return readWire(reader, attributes);
                    case 3: throw new IOException(reader.ReadString());
                    default: throw new IOException($"快速扫描返回了未知数据（{message}）。");
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
