using System.Text;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Cleanup;
using FileAssistant.Infrastructure.Engines;
using FileAssistant.Infrastructure.FileSystem;
using ImageMagick;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FileAssistant.Infrastructure.Tests;

public sealed class BatchPolicyIoTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("FileAssistant-batch-policy-").FullName;
    private LocalFileProcessingService Images(BatchExecutionSlots? slots = null) => new(new("unused", Path.Combine(root, "cache")), slots);
    private string FileAt(string path, string text)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }
    private string Picture(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(MagickColors.Blue, 64, 40);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    [Theory]
    [InlineData(OutputLocation.Source)]
    [InlineData(OutputLocation.Subfolder)]
    public async Task Multisource_conversion_preserves_distinct_destinations_and_input_order(OutputLocation location)
    {
        var left = Picture("left/same.png");
        var right = Picture("right/same.png");
        var service = Images(new(4));
        var request = new ConversionRequest([right, left], "", ConversionTarget.Jpeg, 65) { Location = location };
        var plan = await service.CreatePlanAsync(request, default);
        var report = await service.ConvertAsync(plan, null, default);
        Assert.Equal([Path.Combine(request.DirectoryFor(right), "same.jpg"), Path.Combine(request.DirectoryFor(left), "same.jpg")],
            report.Outputs.Select(file => file.FullPath));
        foreach (var image in report.Outputs)
        {
            using var converted = new MagickImage(image.FullPath);
            Assert.Equal(MagickFormat.Jpeg, converted.Format);
            Assert.Equal(65u, converted.Quality);
        }
    }

    [Theory]
    [InlineData(ConversionCollision.Number)]
    [InlineData(ConversionCollision.Skip)]
    [InlineData(ConversionCollision.Overwrite)]
    public async Task Conversion_collision_policy_controls_actual_commit(ConversionCollision policy)
    {
        var input = Picture("in/photo.png");
        var old = FileAt("out/photo.jpg", "previous");
        var service = Images();
        var request = new ConversionRequest([input], Path.GetDirectoryName(old)!, ConversionTarget.Jpeg) { Conflict = policy };
        var plan = await service.CreatePlanAsync(request, default);
        var result = await service.ConvertAsync(plan, null, default);
        if (policy == ConversionCollision.Skip)
        {
            Assert.Empty(result.Outputs);
            Assert.Equal(1, result.SkippedCount);
        }
        else
        {
            var file = Assert.Single(result.Outputs);
            Assert.Equal(policy == ConversionCollision.Overwrite ? old : Path.Combine(root, "out", "photo (1).jpg"), file.FullPath);
            using var output = new MagickImage(file.FullPath);
            Assert.Equal(MagickFormat.Jpeg, output.Format);
        }
        if (policy != ConversionCollision.Overwrite) Assert.Equal("previous", File.ReadAllText(old));
        Assert.True(File.Exists(input));
    }

    [Fact]
    public async Task Late_numbering_respects_other_planned_names_and_overwrite_refuses_changed_files()
    {
        var a = Picture("in/photo.png");
        var b = Picture("in/photo (1).png");
        var destination = Path.Combine(root, "out");
        var service = Images(new(2));
        var request = new ConversionRequest([a, b], destination, ConversionTarget.Jpeg) { Conflict = ConversionCollision.Number };
        var numbered = await service.CreatePlanAsync(request, default);
        var protectedPath = FileAt("out/photo.jpg", "appeared later");
        var report = await service.ConvertAsync(numbered, null, default);
        Assert.Equal(["photo (2).jpg", "photo (1).jpg"], report.Outputs.Select(file => Path.GetFileName(file.FullPath)));
        Assert.Equal("appeared later", File.ReadAllText(protectedPath));
        var overwrite = await service.CreatePlanAsync(request with { Paths = [a], Conflict = ConversionCollision.Overwrite }, default);
        File.WriteAllText(protectedPath, "edited after approval");
        var rejected = await service.ConvertAsync(overwrite, null, default);
        Assert.Empty(rejected.Outputs);
        Assert.Contains("变化", Assert.Single(rejected.Items).Message);
        Assert.Equal("edited after approval", File.ReadAllText(protectedPath));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
    }

    [Fact]
    public async Task Conversion_waits_for_the_global_slots_and_cancellation_cannot_publish_waiting_work()
    {
        var input = Picture("one.png");
        var slots = new BatchExecutionSlots(2);
        var service = Images(slots);
        var destination = Path.Combine(root, "out");
        var plan = await service.CreatePlanAsync(new([input], destination, ConversionTarget.Jpeg), default);
        using var first = await slots.EnterAsync(default);
        using var second = await slots.EnterAsync(default);
        using var token = new CancellationTokenSource();
        var queued = service.ConvertAsync(plan, null, token.Token);
        token.Cancel();
        var cancelled = await queued.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(cancelled.Cancelled);
        Assert.Empty(cancelled.Outputs);
        Assert.False(Directory.Exists(destination));
        var running = service.ConvertAsync(plan, null, default);
        first.Dispose();
        Assert.Single((await running.WaitAsync(TimeSpan.FromSeconds(10))).Outputs);
    }

    [Theory]
    [InlineData(OutputLocation.Source, CopyLayout.Preserve)]
    [InlineData(OutputLocation.Source, CopyLayout.Flat)]
    [InlineData(OutputLocation.Source, CopyLayout.Extension)]
    [InlineData(OutputLocation.Subfolder, CopyLayout.Preserve)]
    [InlineData(OutputLocation.Subfolder, CopyLayout.Flat)]
    [InlineData(OutputLocation.Subfolder, CopyLayout.Extension)]
    public async Task Multisource_copy_uses_each_parent_and_numbers_beside_the_original(OutputLocation location, CopyLayout layout)
    {
        var inputs = new[] { FileAt("left/same.txt", "left"), FileAt("right/same.txt", "right") };
        var service = new CopyService(Path.Combine(root, "recovery"));
        var request = new CopyRequest(inputs, "", layout) { Location = location, VerifyHash = false };
        var plan = await service.PlanAsync(request, null, default);
        var report = await service.ExecuteAsync(plan, null, default);
        Assert.Equal(2, report.Outputs.Count);
        foreach (var file in report.Outputs)
        {
            var input = Assert.Single(file.SourcePaths);
            var relative = layout == CopyLayout.Extension ? Path.Combine("TXT", "same.txt")
                : location == OutputLocation.Source ? "same (1).txt" : "same.txt";
            Assert.Equal(Path.Combine(BatchOutputDirectory.Resolve(location, "", input), relative), file.FullPath);
            Assert.Equal(File.ReadAllText(input), File.ReadAllText(file.FullPath));
        }
    }

    [Theory]
    [InlineData(false, "bad!", true)]
    [InlineData(true, "bad!", false)]
    [InlineData(false, "x", false)]
    [InlineData(true, "x", false)]
    public async Task Quick_verification_checks_length_and_strict_verification_checks_bytes(bool strict, string data, bool accepts)
    {
        var source = FileAt("input.txt", "good");
        var destination = Path.Combine(root, "out");
        var service = new CopyService(Path.Combine(root, "recovery"), new AlteredTransfer(data));
        var plan = await service.PlanAsync(new CopyRequest([source], destination) { VerifyHash = strict }, null, default);
        var report = await service.ExecuteAsync(plan, null, default);
        Assert.Equal(accepts ? 1 : 0, report.Outputs.Count);
        Assert.Equal(accepts, File.Exists(Path.Combine(destination, "input.txt")));
        Assert.Equal("good", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
    }

    [Fact]
    public async Task Pdf_extraction_really_emits_gb18030_for_non_ascii_page_markers()
    {
        var source = Path.Combine(root, "sample.pdf");
        using (var doc = new PdfDocumentBuilder())
        {
            var font = doc.AddStandard14Font(Standard14Font.Helvetica);
            doc.AddPage(200, 200).AddText("Readable", 12, new PdfPoint(20, 100), font);
            doc.AddPage(200, 200);
            File.WriteAllBytes(source, doc.Build());
        }
        var service = Images();
        var request = new ConversionRequest([source], root, ConversionTarget.Txt, Settings: new(Encoding: DelimitedEncoding.Gb18030));
        var result = await service.ConvertAsync(await service.CreatePlanAsync(request, default), null, default);
        var bytes = File.ReadAllBytes(Assert.Single(result.Outputs).FullPath);
        Assert.Contains("第 2 页未提取到有效文字", CodePagesEncodingProvider.Instance.GetEncoding(54936)!.GetString(bytes));
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(bytes));
    }

    [Fact]
    public async Task Cleanup_connection_reports_timeout_user_cancel_or_early_exit_independently()
    {
        static Task Pending(CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        await Assert.ThrowsAsync<TimeoutException>(() => CleanupService.WaitForHandshakeAsync(Pending, Pending, TimeSpan.FromMilliseconds(45), default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CleanupService.WaitForHandshakeAsync(Pending, Pending, TimeSpan.FromSeconds(3), cancellation.Token));
        await Assert.ThrowsAsync<IOException>(() => CleanupService.WaitForHandshakeAsync(Pending, _ => Task.CompletedTask, TimeSpan.FromSeconds(3), default));
        await CleanupService.WaitForHandshakeAsync(_ => Task.CompletedTask, Pending, TimeSpan.FromSeconds(3), default);
    }

    private sealed class AlteredTransfer(string data) : ICopyFileTransfer
    {
        public void Copy(string source, string temporary, Action<long> progress, CancellationToken token) => File.WriteAllText(temporary, data);
    }

    public void Dispose()
    {
        if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(root).StartsWith("FileAssistant-batch-policy-", StringComparison.Ordinal))
            Directory.Delete(root, recursive: true);
    }
}
