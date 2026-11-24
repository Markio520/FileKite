using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.Core.Engines;

namespace FileAssistant.App.Features.Settings;

public enum EngineInstallStage { Idle, Downloading, Verifying, Ready, Cancelled, Failed }

public partial class EnginesViewModel(IFileProcessingService processing, IEnginePackageInstaller installer, EnginePackage videoPackage, EnginePackage officePackage) : ObservableObject
{
    private CancellationTokenSource? cancellation;
    private int refreshVersion;
    public ObservableCollection<EngineSettingItem> Engines { get; } = [];
    public EnginePackage VideoPackage => videoPackage;
    public EnginePackage OfficePackage => officePackage;
    public string DownloadDescription => $"{videoPackage.Name} {videoPackage.Version} · Windows x64 · {FileAssistant.Core.Settings.FileDisplay.Size(videoPackage.DownloadBytes)} · {videoPackage.License}";
    public string OfficeDownloadDescription => $"{officePackage.Name} {officePackage.Version} · Windows x64 · 下载 {FileAssistant.Core.Settings.FileDisplay.Size(officePackage.DownloadBytes)}，安装约 {FileAssistant.Core.Settings.FileDisplay.Size(1_200_000_000)} · {officePackage.License}";
    public string CleanupHostPath => Path.Combine(AppContext.BaseDirectory, "FileAssistant.CleanupHost.exe");
    public IReadOnlyList<string> MissingCleanupComponents { get; private set; } = [];
    public event EventHandler? AvailabilityChanged;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadVideoCommand), nameof(DownloadOfficeCommand), nameof(RefreshCommand))] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial EngineInstallStage Stage { get; set; }
    [ObservableProperty] public partial string LastPackageId { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFeedback))] public partial string Feedback { get; set; } = string.Empty;
    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
    private bool CanDownload => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownload))] private async Task RefreshAsync()
    {
        var version = ++refreshVersion;
        var snapshot = await Task.Run(() =>
        {
            string[] cleanupComponents = ["FileAssistant.CleanupHost.exe", "FileAssistant.CleanupHost.dll", "FileAssistant.CleanupHost.deps.json",
                "FileAssistant.CleanupHost.runtimeconfig.json", "FileAssistant.BcuCore.dll", "Microsoft.Win32.TaskScheduler.dll", "System.Management.dll"];
            return (Engines: processing.Engines, Missing: cleanupComponents.Where(name => !File.Exists(Path.Combine(AppContext.BaseDirectory, name))).ToArray());
        });
        if (version != refreshVersion) return;
        MissingCleanupComponents = snapshot.Missing;
        Engines.Clear();
        foreach (var engine in snapshot.Engines.OrderBy(engine => engine.Id == "libreoffice"))
            Engines.Add(engine.Id switch
            {
                "ffmpeg" => new(engine, DownloadVideoCommand, DownloadDescription),
                "libreoffice" => new(engine, DownloadOfficeCommand, OfficeDownloadDescription),
                _ => new(engine)
            });
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanDownload))] private Task DownloadVideoAsync() => DownloadAsync(videoPackage);
    [RelayCommand(CanExecute = nameof(CanDownload))] private Task DownloadOfficeAsync() => DownloadAsync(officePackage);

    private async Task DownloadAsync(EnginePackage package)
    {
        if (IsBusy) return;
        using var current = new CancellationTokenSource();
        cancellation = current;
        LastPackageId = package.Id; IsBusy = true; Progress = 0; Stage = EngineInstallStage.Downloading;
        Feedback = $"正在下载 {package.Name}…";
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (!ReferenceEquals(current, cancellation) || Stage is not (EngineInstallStage.Downloading or EngineInstallStage.Verifying)) return;
                Progress = value;
                if (value is >= 90 and < 100)
                {
                    Stage = EngineInstallStage.Verifying;
                    Feedback = "正在解包和检查引擎；取消时将等待解包结束后清理。";
                }
            });
            await Task.Run(() => installer.InstallAsync(package, progress, current.Token));
            await RefreshAsync();
            Progress = 100;
            Feedback = $"{package.Name} 已就绪。";
            Stage = EngineInstallStage.Ready;
        }
        catch (OperationCanceledException) { Feedback = "已取消下载，现有引擎保留。"; Stage = EngineInstallStage.Cancelled; }
        catch (Exception error) { Feedback = error.Message; Stage = EngineInstallStage.Failed; }
        finally { cancellation = null; IsBusy = false; }
    }
    [RelayCommand] private void Cancel() => cancellation?.Cancel();
}
