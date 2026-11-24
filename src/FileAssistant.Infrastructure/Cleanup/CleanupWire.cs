using System.Text.Json;

namespace FileAssistant.Infrastructure.Cleanup;

internal static class CleanupWire
{
    private const int MaximumMessage = 64 * 1024 * 1024;
    public static void Write<T>(Stream stream, byte type, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessage) throw new IOException("清理数据超出单次处理上限。");
        lock (stream)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write(type); writer.Write(bytes.Length); writer.Write(bytes); writer.Flush();
        }
    }

    public static T Read<T>(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        var length = reader.ReadInt32();
        if (length < 1 || length > MaximumMessage) throw new IOException("清理消息长度无效。");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("清理消息内容无效。");
    }
}
