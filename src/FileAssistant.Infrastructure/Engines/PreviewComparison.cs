using FileAssistant.Core.Engines;
using FileAssistant.Infrastructure.Engines.Images;
using ImageMagick;

namespace FileAssistant.Infrastructure.Engines;

public sealed record PreviewComparison(byte[] DecodedPng, int CompressedBytes, uint Width, uint Height)
{
    // Compare the same preview pixels before and after a real encode/decode cycle.
    public static Task<PreviewComparison> EncodeAsync(byte[] png, ConversionTarget target, int quality, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var source = new MagickImage(png);
        using var encoded = new MemoryStream();
        MagickImageEngine.Write(source, encoded, target, Math.Clamp(quality, 1, 100));
        token.ThrowIfCancellationRequested();
        encoded.Position = 0;
        using var decoded = new MagickImage(encoded);
        return new PreviewComparison(decoded.ToByteArray(MagickFormat.Png), checked((int)encoded.Length), decoded.Width, decoded.Height);
    }, token);
}
