using FileAssistant.App.Features.Settings;
using FileAssistant.Core.Engines;

namespace FileAssistant.App.Tests;

public sealed class EngineSettingsTests
{
    private static readonly EnginePackage Video = Package("ffmpeg", "视频引擎");
    private static readonly EnginePackage Office = Package("libreoffice", "文档引擎");

    [Fact]
    public async Task Checks_show_actual_availability_and_offer_only_supported_install_actions()
    {
        var processing = new Processing();
        var model = new EnginesViewModel(processing, new Installer(processing), Video, Office);
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(5, model.Engines.Count);
        Assert.Equal("libreoffice", model.Engines[^1].Info.Id);
        var image = model.Engines.Single(x => x.Info.Id == "magick");
        Assert.Equal("随软件提供", image.StatusLabel);
        Assert.False(image.CanInstall);
        var pdf = model.Engines.Single(x => x.Info.Id == "pdfium");
        Assert.Equal("组件缺失", pdf.StatusLabel);
        Assert.False(pdf.CanInstall);
        var office = model.Engines.Single(x => x.Info.Id == "libreoffice");
        Assert.Equal("未安装", office.StatusLabel);
        Assert.Equal("下载", office.InstallLabel);
        Assert.Same(model.DownloadOfficeCommand, office.InstallCommand);
        Assert.True(office.ShowDownloadDescription);
        Assert.Contains(Office.Version, office.DownloadDescription);
        Assert.Equal(processing.Engines.Single(x => x.Id == "pdfium"), pdf.Info);
    }

    [Fact]
    public async Task Active_install_blocks_other_actions_and_cancellation_allows_retry_with_refreshed_status()
    {
        var processing = new Processing();
        var installer = new Installer(processing) { WaitForCancellation = true };
        var model = new EnginesViewModel(processing, installer, Video, Office);
        var availabilityEvents = 0;
        model.AvailabilityChanged += (_, _) => availabilityEvents++;
        await model.RefreshCommand.ExecuteAsync(null);

        var install = model.DownloadOfficeCommand.ExecuteAsync(null);
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsBusy);
        Assert.False(model.DownloadVideoCommand.CanExecute(null));
        Assert.False(model.DownloadOfficeCommand.CanExecute(null));
        Assert.False(model.RefreshCommand.CanExecute(null));
        await model.DownloadVideoCommand.ExecuteAsync(null);
        Assert.Single(installer.Requests);
        model.CancelCommand.Execute(null);
        await install.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(model.IsBusy);
        Assert.Contains("已取消", model.Feedback);
        Assert.True(model.HasFeedback);
        Assert.True(model.DownloadOfficeCommand.CanExecute(null));
        Assert.Equal("未安装", model.Engines.Single(x => x.Info.Id == "libreoffice").StatusLabel);

        installer.WaitForCancellation = false;
        await model.DownloadOfficeCommand.ExecuteAsync(null);
        Assert.All(installer.Requests, package => Assert.Same(Office, package));
        Assert.Equal(2, installer.Requests.Count);
        Assert.Equal(2, availabilityEvents);
        var installed = model.Engines.Single(x => x.Info.Id == "libreoffice");
        Assert.Equal("已就绪", installed.StatusLabel);
        Assert.Equal("修复", installed.InstallLabel);
        Assert.False(installed.ShowDownloadDescription);
        Assert.False(model.IsBusy);
        Assert.Equal(100, model.Progress);
    }

    [Fact]
    public async Task Failed_repair_keeps_previous_availability_and_can_be_retried()
    {
        var processing = new Processing();
        var installer = new Installer(processing) { Failure = new FileProcessingException("测试下载失败，请重试。") };
        var model = new EnginesViewModel(processing, installer, Video, Office);
        await model.RefreshCommand.ExecuteAsync(null);
        await model.DownloadVideoCommand.ExecuteAsync(null);

        Assert.Equal("测试下载失败，请重试。", model.Feedback);
        Assert.False(model.IsBusy);
        Assert.True(model.RefreshCommand.CanExecute(null));
        Assert.True(model.DownloadVideoCommand.CanExecute(null));
        Assert.True(model.Engines.Single(x => x.Info.Id == "ffmpeg").IsAvailable);
        Assert.Same(Video, Assert.Single(installer.Requests));
    }

    private static EnginePackage Package(string id, string name) => new(id, name, "1.0", new Uri("https://example.invalid/engine.zip"), "test", "", "engine.exe", 10_000_000);

    [Fact]
    public async Task Slow_refresh_cannot_replace_status_from_a_completed_install()
    {
        var processing = new Processing();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        processing.ReadSnapshot = snapshot =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            { started.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            return snapshot;
        };
        var vm = new EnginesViewModel(processing, new Installer(processing), Video, Office);
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.DownloadOfficeCommand.ExecuteAsync(null);
        }
        finally { release.Set(); }
        await refresh;
        Assert.True(vm.Engines.Single(x => x.Info.Id == "libreoffice").IsAvailable);
    }

    private sealed class Installer(Processing processing) : IEnginePackageInstaller
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<EnginePackage> Requests { get; } = [];
        public bool WaitForCancellation { get; set; }
        public FileProcessingException? Failure { get; set; }
        public async Task<string> InstallAsync(EnginePackage package, IProgress<int>? progress, CancellationToken token)
        {
            Requests.Add(package);
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            if (Failure is not null) throw Failure;
            processing.OfficeAvailable = true;
            return "test-engine";
        }
    }

    private sealed class Processing : IFileProcessingService
    {
        public bool OfficeAvailable { get; set; }
        public Func<IReadOnlyList<ProcessingEngineInfo>, IReadOnlyList<ProcessingEngineInfo>> ReadSnapshot { get; set; } = snapshot => snapshot;
        public IReadOnlyList<ProcessingEngineInfo> Engines => ReadSnapshot(
        [
            new("magick", "Magick.NET", "1", "图片", true, "image-engine"),
            new("ffmpeg", "FFmpeg", "1", "音视频", true, "video-engine"),
            new("libreoffice", "LibreOffice", "1", "文档", OfficeAvailable, "office-engine"),
            new("pdfium", "PDFium", "1", "PDF 预览", false, "missing-pdf-engine"),
            new("pdfpig", "PdfPig", "1", "PDF 文本", true, "pdf-text-engine")
        ]);
        public Task<PreviewResult> PreviewAsync(string path, int maximumEdge, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConversionPlan> CreatePlanAsync(ConversionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConversionReport> ConvertAsync(ConversionPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
