using System.Text.Json;
using FileAssistant.Core.Engines;

namespace FileAssistant.Infrastructure.Engines.Media;

internal delegate Task<string> FfmpegCommand(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token, string failure);

internal sealed class FfmpegConverter(string executable, string? probePath = null, FfmpegCommand? command = null)
{
    private readonly FfmpegCommand run = command ?? ((file, args, timeout, token, failure) => LocalEngineProcess.RunAsync(file, args, timeout, token, failure));
    private readonly SemaphoreSlim probeGate = new(1, 1);
    private readonly Dictionary<string, bool> hardware = new(StringComparer.Ordinal);
    private HashSet<string>? installedEncoders;

    internal async Task<IReadOnlyList<string>> CheckAsync(string path, ConversionRequest request, CancellationToken token)
    {
        var probe = probePath ?? Path.Combine(Path.GetDirectoryName(executable)!, "ffprobe.exe");
        var json = await run(probe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_entries", "stream=codec_type,codec_name,channels:stream_tags=language,title:stream_disposition=attached_pic", "-of", "json", path],
            TimeSpan.FromSeconds(30), token, "无法读取音视频文件，文件可能损坏或格式不支持。");
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var required = ConversionCapabilities.IsVideoTarget(request.Target) ? "video" : "audio";
        if (!streams.Any(stream =>
            stream.TryGetProperty("codec_type", out var type) && type.GetString() == required &&
            (required != "video" || !stream.TryGetProperty("disposition", out var disposition) ||
                !disposition.TryGetProperty("attached_pic", out var cover) || cover.GetInt32() == 0)))
            throw new FileProcessingException(required == "video" ? "文件没有可转换的视频流。" : "文件没有音轨，无法提取或转换音频。");
        var audio = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
        if (request.Options.AudioTrack > audio.Length && (audio.Length > 0 || required == "audio" || request.Options.AudioTrack != 1))
            throw new FileProcessingException($"所选第 {request.Options.AudioTrack} 音轨不存在；此文件共 {audio.Length} 条音轨。");
        return audio.Select((stream, index) => $"音轨 {index + 1} / {audio.Length} · {stream.GetProperty("codec_name").GetString()}" +
            (stream.TryGetProperty("tags", out var tags) ? " · " + string.Join(" / ", tags.EnumerateObject().Select(tag => tag.Value.GetString())) : "")).ToArray();
    }

    internal async Task ConvertAsync(string path, string output, ConversionRequest request, CancellationToken token)
    {
        var target = request.Target; var options = request.Options;
        await CheckAsync(path, request, token);
        var bitrate = options.AudioBitrateKbps + "k";
        var video = ConversionCapabilities.IsVideoTarget(target);
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-n", "-protocol_whitelist", "file,pipe", "-i", path };
        if (!video)
        {
            arguments.AddRange(["-map", $"0:a:{options.AudioTrack - 1}", "-vn", "-sn", "-dn", "-map_metadata", "-1"]);
            arguments.AddRange(target switch
            {
                ConversionTarget.Mp3 => ["-c:a", "libmp3lame", "-b:a", bitrate, "-f", "mp3"],
                ConversionTarget.Wav => ["-c:a", "pcm_s16le", "-f", "wav"],
                ConversionTarget.Flac => ["-c:a", "flac", "-f", "flac"],
                ConversionTarget.M4a => ["-c:a", "aac", "-b:a", bitrate, "-movflags", "+faststart", "-f", "ipod"],
                ConversionTarget.Aac => ["-c:a", "aac", "-b:a", bitrate, "-f", "adts"],
                ConversionTarget.Ogg => ["-c:a", "libvorbis", "-q:a", "5", "-f", "ogg"],
                _ => throw new FileProcessingException("不支持此音频输出格式。")
            });
        }
        else
        {
            var resize = options.MaximumEdge > 0 ? $"scale=w='min(iw,{options.MaximumEdge})':h='min(ih,{options.MaximumEdge})':force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1," : "";
            arguments.AddRange(["-map", "0:V:0", "-map", $"0:a:{options.AudioTrack - 1}?", "-sn", "-dn", "-map_metadata", "-1",
                "-vf", resize + "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p"]);
            arguments.AddRange(target == ConversionTarget.WebM
                ? ["-c:v", "libvpx-vp9", "-b:v", options.VideoBitrateKbps + "k", "-c:a", "libopus", "-b:a", bitrate, "-f", "webm"]
                : ["-c:v", "libopenh264", "-b:v", options.VideoBitrateKbps + "k", "-c:a", "aac", "-b:a", bitrate, "-f", target switch
                    { ConversionTarget.Mp4 => "mp4", ConversionTarget.Mov => "mov", ConversionTarget.Mkv => "matroska", _ => throw new FileProcessingException("不支持此视频输出格式。") }]);
        }
        if (options.SampleRate > 0 && target != ConversionTarget.WebM) arguments.AddRange(["-ar", options.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (!video)
        {
            arguments.Add(output);
            await EncodeAsync(arguments, output, token);
            return;
        }

        var encoders = await VideoEncodersAsync(target, options.PreferHardwareEncoding, token);
        var codecArgument = arguments.IndexOf("-c:v") + 1;
        foreach (var encoder in encoders)
        {
            token.ThrowIfCancellationRequested();
            var attempt = new List<string>(arguments) { [codecArgument] = encoder };
            if (encoder == "libvpx-vp9") attempt.AddRange(["-deadline", "good", "-cpu-used", "4"]);
            var trial = Path.Combine(Path.GetDirectoryName(output)!, $".fileassistant-codec-{Guid.NewGuid():N}.tmp");
            try
            {
                attempt.Add(trial);
                await EncodeAsync(attempt, trial, token);
                token.ThrowIfCancellationRequested();
                File.Move(trial, output, overwrite: false);
                return;
            }
            catch (FileProcessingException) when (encoder != encoders[^1]) { }
            finally { File.Delete(trial); }
        }
        throw new FileProcessingException("当前 FFmpeg 缺少此格式可用的视频编码器。");
    }

    private async Task EncodeAsync(List<string> arguments, string output, CancellationToken token)
    {
        await run(executable, arguments, TimeSpan.FromHours(24), token, "音视频转换失败，文件可能损坏或所选编码不支持此输入。");
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new FileProcessingException("音视频引擎没有生成有效输出。");
    }

    private async Task<List<string>> VideoEncodersAsync(ConversionTarget target, bool preferHardware, CancellationToken token)
    {
        await probeGate.WaitAsync(token);
        try
        {
            if (installedEncoders is null)
            {
                var list = await run(executable, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(15), token, "无法读取 FFmpeg 编码器列表。");
                installedEncoders = list.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V')
                    .Select(parts => parts[1]).ToHashSet(StringComparer.Ordinal);
            }
            string[] candidates = target == ConversionTarget.WebM ? ["vp9_qsv"] : ["h264_nvenc", "h264_qsv", "h264_amf"];
            var result = new List<string>();
            if (preferHardware)
            {
                foreach (var candidate in candidates.Where(installedEncoders.Contains))
                {
                    if (!hardware.TryGetValue(candidate, out var ready))
                    {
                        try
                        {
                            // A compiled encoder can still lack a driver, compatible GPU or a usable session.
                            await run(executable, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=black:size=128x128:rate=1",
                                "-frames:v", "1", "-pix_fmt", "yuv420p", "-c:v", candidate, "-f", "null", "-"],
                                TimeSpan.FromSeconds(6), token, "硬件编码器不可用。");
                            ready = true;
                        }
                        catch (FileProcessingException) { ready = false; }
                        hardware[candidate] = ready;
                    }
                    if (ready) result.Add(candidate);
                }
            }
            string[] software = target == ConversionTarget.WebM ? ["libvpx-vp9"] : ["libopenh264", "libx264"];
            result.AddRange(software.Where(installedEncoders.Contains));
            return result;
        }
        finally { probeGate.Release(); }
    }
}
