using FileAssistant.Core.Engines;
using FileAssistant.Infrastructure.Engines.Media;

namespace FileAssistant.Infrastructure.Tests;

public sealed class BatchMediaPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Supported_hardware_is_probed_and_unavailable_or_failed_hardware_falls_back_to_software(bool preferHardware)
    {
        var directory = Directory.CreateTempSubdirectory("FileAssistant-gpu-policy-").FullName;
        var encoded = new List<string>();
        var probed = new List<string>();
        var listings = 0;
        async Task<string> Run(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token, string failure)
        {
            var args = arguments.ToList();
            Assert.False(string.IsNullOrWhiteSpace(executable));
            if (executable == "selected-probe")
                return """{"streams":[{"codec_type":"video","codec_name":"h264"}]}""";
            Assert.Equal("selected-ffmpeg", executable);
            if (args.Contains("-encoders"))
            {
                listings++;
                return " V....D h264_nvenc NVIDIA\n V..... h264_qsv Intel\n V....D h264_amf AMD\n V....D libx264 H.264\n";
            }
            var codec = args[args.IndexOf("-c:v") + 1];
            if (args.Contains("lavfi"))
            {
                probed.Add(codec);
                if (codec != "h264_qsv") throw new FileProcessingException("device unavailable");
                return "";
            }
            encoded.Add(codec);
            await File.WriteAllTextAsync(args[^1], codec, token);
            if (codec == "h264_qsv") throw new FileProcessingException("session exhausted after probe");
            return "";
        }
        try
        {
            var converter = new FfmpegConverter("selected-ffmpeg", "selected-probe", Run);
            var request = new ConversionRequest(["source.mp4"], directory, ConversionTarget.Mp4, Settings: new(PreferHardwareEncoding: preferHardware));
            foreach (var name in new[] { "one.mp4", "two.mp4" })
            {
                var output = Path.Combine(directory, name);
                await converter.ConvertAsync("source.mp4", output, request, default);
                Assert.Equal("libx264", await File.ReadAllTextAsync(output));
            }
            Assert.Equal(1, listings);
            Assert.Equal(preferHardware ? ["h264_nvenc", "h264_qsv", "h264_amf"] : Array.Empty<string>(), probed);
            Assert.Equal(preferHardware ? ["h264_qsv", "libx264", "h264_qsv", "libx264"] : ["libx264", "libx264"], encoded);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Path.GetDirectoryName(directory) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
                Path.GetFileName(directory).StartsWith("FileAssistant-gpu-policy-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    }
}
