using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static async Task SavePng(FrameworkElement element, string name, int minimumWidth = 350, int minimumHeight = 250)
    {
        // Include the native ContentDialog template surface, which supplies the opaque detail background.
        var surface = element;
        var ancestors = new List<string>();
        for (DependencyObject? node = element; node is FrameworkElement parent; node = VisualTreeHelper.GetParent(parent))
        {
            ancestors.Add($"{parent.GetType().Name}.{parent.Name}({parent.ActualWidth:0}x{parent.ActualHeight:0})");
            if (Math.Abs(parent.ActualWidth - element.ActualWidth) > 8 || Math.Abs(parent.ActualHeight - element.ActualHeight) > 8) break;
            surface = parent;
        }
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(surface);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[checked((int)reader.UnconsumedBufferLength)];
        reader.ReadBytes(bytes);
        var colors = new HashSet<uint>();
        int samples = 0, opaque = 0;
        for (int i = 0; i + 4 <= bytes.Length; i += 4 * 19)
        {
            samples++;
            if (bytes[i + 3] == 255) opaque++;
            if (bytes[i + 3] > 0) colors.Add(BitConverter.ToUInt32(bytes, i));
        }
        Require(bitmap.PixelWidth > minimumWidth && bitmap.PixelHeight > minimumHeight && colors.Count > 30, $"Blank or undersized native capture {name}: {bitmap.PixelWidth}x{bitmap.PixelHeight} / {colors.Count} colors.");
        var path = Path.Combine(output, name + ".png");
        using (File.Create(path)) { }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        double dpi = 96 * element.XamlRoot.RasterizationScale;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, bytes);
        await encoder.FlushAsync();
        evidence.Add(new { screenshot = Path.GetFileName(path), bitmap.PixelWidth, bitmap.PixelHeight, dpi, sampledColors = colors.Count, opaqueFraction = Math.Round((double)opaque / samples, 3), ancestors });
    }
}
