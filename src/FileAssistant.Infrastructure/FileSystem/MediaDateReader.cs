using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileAssistant.Infrastructure.FileSystem;

internal static class MediaDateReader
{
    private static readonly HashSet<string> SupportedPhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".tif", ".tiff", ".webp" };

    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v" };

    public static long? TryReadTakenUtcTicks(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return null;
        try
        {
            if (SupportedPhotoExtensions.Contains(ext))
                return ReadExifUtcTicks(path, ext);
            if (SupportedVideoExtensions.Contains(ext))
                return ReadMp4CreationUtcTicks(path);
        }
        catch
        {
            // Malformed or locked metadata never blocks scanning; caller falls back to ModifiedUtcTicks.
        }
        return null;
    }

    private static long? ReadExifUtcTicks(string path, string ext)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var limit = (int)Math.Min(stream.Length, 128 * 1024);
        if (limit < 12) return null;
        var buffer = new byte[limit];
        var read = stream.Read(buffer, 0, limit);
        var span = buffer.AsSpan(0, read);

        if (ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            return ParseTiffExif(span);

        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            if (span.Length < 20 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WEBP"u8))
                return null;
            var pos = 12;
            while (pos + 8 <= span.Length)
            {
                var chunkId = span.Slice(pos, 4);
                var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
                if (chunkSize < 0 || pos + 8 + chunkSize > span.Length) break;
                if (chunkId.SequenceEqual("EXIF"u8))
                {
                    var payload = span.Slice(pos + 8, chunkSize);
                    if (payload.Length >= 6 && payload[..6].SequenceEqual("Exif\0\0"u8))
                        payload = payload[6..];
                    return ParseTiffExif(payload);
                }
                pos += 8 + chunkSize + (chunkSize & 1);
            }
            return null;
        }

        // JPEG APP1 Exif
        if (span.Length < 4 || span[0] != 0xFF || span[1] != 0xD8) return null;
        var offset = 2;
        while (offset + 4 <= span.Length && span[offset] == 0xFF)
        {
            var marker = span[offset + 1];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker is 0x00 or 0xFF) { offset++; continue; }
            var segLen = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
            if (segLen < 2 || offset + 2 + segLen > span.Length) break;
            if (marker == 0xE1 && segLen >= 8)
            {
                var seg = span.Slice(offset + 4, segLen - 2);
                if (seg.Length >= 6 && seg[..6].SequenceEqual("Exif\0\0"u8))
                    return ParseTiffExif(seg[6..]);
            }
            offset += 2 + segLen;
        }
        return null;
    }

    private static long? ParseTiffExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') little = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') little = false;
        else return null;

        if (ReadU16(tiff, 2, little) != 42) return null;
        var ifd0 = (int)ReadU32(tiff, 4, little);
        if (ifd0 < 8 || ifd0 + 2 > tiff.Length) return null;

        int? exifIfdOffset = null;
        long? fallbackIfd0Date = null;
        ScanIfd(tiff, ifd0, little, ref exifIfdOffset, out var taken0);
        if (taken0 is not null) fallbackIfd0Date = taken0;

        if (exifIfdOffset is { } sub && sub > 0 && sub + 2 <= tiff.Length)
        {
            int? dummy = null;
            ScanIfd(tiff, sub, little, ref dummy, out var exifDate);
            if (exifDate is not null) return exifDate;
        }
        return fallbackIfd0Date;
    }

    private static void ScanIfd(ReadOnlySpan<byte> tiff, int ifdOffset, bool little, ref int? exifSubIfd, out long? bestDate)
    {
        bestDate = null;
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length) return;
        var count = ReadU16(tiff, ifdOffset, little);
        long? digitized = null, modified = null;
        for (var i = 0; i < count; i++)
        {
            var entry = ifdOffset + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            var tag = ReadU16(tiff, entry, little);
            var type = ReadU16(tiff, entry + 2, little);
            var valueCount = (int)ReadU32(tiff, entry + 4, little);
            if (tag == 0x8769 && exifSubIfd is null)
            {
                exifSubIfd = (int)ReadU32(tiff, entry + 8, little);
            }
            else if (tag is 0x9003 or 0x9004 or 0x0132 && type == 2 && valueCount is >= 19 and <= 64)
            {
                var strOffset = (int)ReadU32(tiff, entry + 8, little);
                if (strOffset >= 0 && strOffset + 19 <= tiff.Length)
                {
                    var parsed = ParseExifDateString(tiff.Slice(strOffset, 19));
                    if (parsed is not null)
                    {
                        if (tag == 0x9003) { bestDate = parsed; return; }
                        if (tag == 0x9004) digitized ??= parsed;
                        if (tag == 0x0132) modified ??= parsed;
                    }
                }
            }
        }
        bestDate = digitized ?? modified;
    }

    private static long? ParseExifDateString(ReadOnlySpan<byte> ascii19)
    {
        var text = Encoding.ASCII.GetString(ascii19);
        if (text.StartsWith("0000", StringComparison.Ordinal)) return null;
        if (DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local) &&
            local.Year is >= 1980 and <= 2100)
        {
            return local.ToUniversalTime().Ticks;
        }
        return null;
    }

    private static long? ReadMp4CreationUtcTicks(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[16];
        long pos = 0;
        while (pos + 8 <= stream.Length)
        {
            stream.Position = pos;
            if (stream.Read(header, 0, 8) < 8) break;
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = header.AsSpan(4, 4);
            var headerLen = 8;
            if (boxSize == 1)
            {
                if (stream.Read(header, 8, 8) < 8) break;
                boxSize = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8, 8));
                headerLen = 16;
            }
            else if (boxSize == 0)
            {
                boxSize = stream.Length - pos;
            }
            if (boxSize < headerLen || pos + boxSize > stream.Length) break;

            if (type.SequenceEqual("moov"u8))
            {
                var moovEnd = pos + boxSize;
                var inner = pos + headerLen;
                while (inner + 8 <= moovEnd)
                {
                    stream.Position = inner;
                    if (stream.Read(header, 0, 8) < 8) break;
                    long subSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                    if (subSize < 8 || inner + subSize > moovEnd) break;
                    if (header.AsSpan(4, 4).SequenceEqual("mvhd"u8) && subSize >= 20)
                    {
                        var mvhd = new byte[20];
                        if (stream.Read(mvhd, 0, 20) < 12) return null;
                        var version = mvhd[0];
                        ulong secondsSince1904 = version == 1
                            ? BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(4, 8))
                            : BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(4, 4));
                        if (secondsSince1904 == 0) return null;
                        var epoch1904 = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        if (secondsSince1904 > (ulong)(DateTime.MaxValue - epoch1904).TotalSeconds) return null;
                        var utc = epoch1904.AddSeconds(secondsSince1904);
                        return utc.Year is >= 1980 and <= 2100 ? utc.Ticks : null;
                    }
                    inner += subSize;
                }
                return null;
            }
            pos += boxSize;
        }
        return null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(offset, 2)) : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4)) : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4));
}
