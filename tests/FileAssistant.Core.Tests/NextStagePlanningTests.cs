using FileAssistant.Core.Engines;

namespace FileAssistant.Core.Tests;

public sealed class NextStagePlanningTests
{
    [Fact]
    public void Skip_policy_distinguishes_existing_paths_and_in_batch_collisions_from_invalid_inputs()
    {
        var request = new ConversionRequest([@"C:\one\a.jpg", @"C:\two\A.jpg", @"C:\one\b.jpg"], @"C:\out", ConversionTarget.Png)
            { SkipConflictingOutputs = true };
        var sources = request.Paths.Select(path => new ProcessingSource(path, 1, 1)).ToArray();
        var plan = ConversionPlanner.Create(request, sources, path => Path.GetFileName(path) == "b.png");

        Assert.Equal(1, plan.ReadyCount);
        Assert.Equal(2, plan.SkippedCount);
        Assert.Equal(0, plan.RejectedCount);
        Assert.Equal(@"C:\out\a.png", plan.Items[0].OutputPath);
        Assert.All(plan.Items.Skip(1), item => Assert.Null(item.OutputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overlong_output_names_are_rejected_in_preview_without_blocking_other_inputs(bool conflict)
    {
        var longSource = @"C:\input\" + new string('a', 250) + ".png";
        var request = new ConversionRequest([longSource, @"C:\input\short.png"], @"C:\out", ConversionTarget.WebP);
        // 255 characters is valid until conflict numbering appends a suffix.
        var sources = new ProcessingSource[] { new(longSource, 1, 1), new(request.Paths[1], 1, 1) };
        var plan = ConversionPlanner.Create(request, sources, path => conflict && Path.GetFileName(path) == new string('a', 250) + ".webp");
        if (conflict)
        {
            Assert.Equal(1, plan.RejectedCount); Assert.Equal(1, plan.ReadyCount);
            Assert.Contains("255", plan.Items.Single(item => !item.CanRun).Error);
        }
        else Assert.Equal(2, plan.ReadyCount);
    }
    [Fact]
    public void Spreadsheet_presentation_and_image_batches_stay_separate()
    {
        Assert.Contains(ConversionTarget.Xls, ConversionCapabilities.GetCommonTargets(["a.xlsx", "b.ods"]));
        Assert.Contains(ConversionTarget.Ppt, ConversionCapabilities.GetCommonTargets(["a.pptx", "b.odp"]));
        Assert.Empty(ConversionCapabilities.GetCommonTargets(["a.xlsx", "b.pptx"]));
        Assert.Empty(ConversionCapabilities.GetCommonTargets(["a.ods", "b.png"]));
        Assert.Equal([ConversionTarget.Png, ConversionTarget.Jpeg, ConversionTarget.WebP], ConversionCapabilities.GetTargets("a.svg"));
    }
    [Fact]
    public void Process_mode_only_offers_actual_same_format_processing()
    {
        Assert.Equal([ConversionTarget.Jpeg], ConversionCapabilities.GetCommonTargets(["a.jpg", "b.jpeg"], ConversionMode.Process));
        Assert.Equal([ConversionTarget.Mp4], ConversionCapabilities.GetCommonTargets(["a.mp4"], ConversionMode.Process));
        Assert.Empty(ConversionCapabilities.GetCommonTargets(["a.pdf"], ConversionMode.Process));
        Assert.Empty(ConversionCapabilities.GetCommonTargets(["a.docx"], ConversionMode.Process));
        Assert.Empty(ConversionCapabilities.GetCommonTargets(["a.jpg", "b.png"], ConversionMode.Process));
    }
    [Fact]
    public void Pdf_text_is_one_output_and_csv_names_are_sanitized_and_reserved()
    {
        var request = new ConversionRequest([@"C:\a.pdf"], @"C:\out", ConversionTarget.Txt);
        Assert.Equal(1, ConversionPlanner.Create(request, [new(request.Paths[0], 1, 1, PdfPages: 4)], _ => false).ReadyCount);
        request = request with { Paths = [@"C:\a.xlsx"], Target = ConversionTarget.Csv };
        var plan = ConversionPlanner.Create(request, [new(request.Paths[0], 1, 1, Sheets: ["a/b", "a:b"])], _ => false);
        Assert.Equal(2, plan.ReadyCount); Assert.NotEqual(plan.Items[0].OutputPath, plan.Items[1].OutputPath);
        Assert.All(plan.Items, item => Assert.Equal(@"C:\out", Path.GetDirectoryName(item.OutputPath)));
        Assert.Equal(1, plan.Items[0].Sheet); Assert.Equal(2, plan.Items[1].Sheet);
    }
    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1280, 12345, 1)]
    [InlineData(1280, 44100, 0)]
    public void Invalid_engine_parameters_fail_before_planning(int size, int sampleRate, int track)
        => Assert.Throws<FileProcessingException>(() => new ConversionSettings(MaximumEdge: size, SampleRate: sampleRate, AudioTrack: track).Validate());
}
