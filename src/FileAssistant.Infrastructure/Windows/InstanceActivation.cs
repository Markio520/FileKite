using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace FileAssistant.Infrastructure.Windows;

// Hidden tray windows have no Process.MainWindowHandle. Activate the owner of this user's mutex via its pipe.
public sealed class InstanceActivation(string name)
{
    private const int MaximumMessage = 1024 * 1024;

    public static string GetInstanceName(string userSid, string? profileDirectory, bool indexTrial)
    {
        // Existing trial installs use the literal SID. An isolated path must never enter a Win32 object name.
        if (indexTrial && profileDirectory is null) return "FileAssistant.IndexTrial." + userSid;

        var identity = profileDirectory is null ? userSid : userSid + "|" + profileDirectory.ToUpperInvariant();
        var fingerprint = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return (indexTrial ? "FileAssistant.IndexTrial." : "FileAssistant-") + Convert.ToHexString(fingerprint)[..12];
    }

    public async Task SendAsync(string[] paths, CancellationToken token)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(6000, token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (bytes.Length > MaximumMessage) throw new IOException("拖入文件路径总长度超过限制。");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await client.WriteAsync(length, token).ConfigureAwait(false);
        await client.WriteAsync(bytes, token).ConfigureAwait(false);
        await client.FlushAsync(token).ConfigureAwait(false);
    }

    public async Task ListenAsync(Action<string[]> activate, Action<Exception> log, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                read.CancelAfter(TimeSpan.FromSeconds(4));
                var header = new byte[4];
                await server.ReadExactlyAsync(header, read.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaximumMessage) continue;
                var body = new byte[length];
                await server.ReadExactlyAsync(body, read.Token).ConfigureAwait(false);
                if (JsonSerializer.Deserialize<string[]>(body) is { } paths && paths.All(p => p is not null)) activate(paths);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException) { log(error); }
        }
    }
}
