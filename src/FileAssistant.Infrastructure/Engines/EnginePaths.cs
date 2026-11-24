namespace FileAssistant.Infrastructure.Engines;

public sealed record EnginePaths(string BundledFfmpegExecutable, string CacheDirectory, string? ManagedEnginesDirectory = null, string? OfficeExecutableOverride = null)
{
    public Func<FileAssistant.Core.Settings.AppSettings>? Preferences { private get; init; }
    private string? ExternalFfmpeg => Preferences?.Invoke().FfmpegPath is { Length: > 0 } path ? path : null;
    public bool HasFfmpegOverride => ExternalFfmpeg is not null;
    public string? OfficeExecutable => (Preferences?.Invoke().OfficePath is { Length: > 0 } path ? path : OfficeExecutableOverride) ?? (ManagedEnginesDirectory is { } directory
        ? ResolveInstalled(directory, EnginePackageCatalog.LibreOffice) : null);
    public string FfmpegExecutable => ExternalFfmpeg ?? (ManagedEnginesDirectory is { } directory
        ? ResolveInstalled(directory, EnginePackageCatalog.Ffmpeg) ?? BundledFfmpegExecutable : BundledFfmpegExecutable);
    public string FfprobeExecutable
    {
        get
        {
            var adjacent = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FfmpegExecutable))!, "ffprobe.exe");
            return File.Exists(adjacent) ? adjacent : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(BundledFfmpegExecutable))!, "ffprobe.exe");
        }
    }
    private static string? ResolveInstalled(string directory, FileAssistant.Core.Engines.EnginePackage package)
    {
        var installed = EnginePackageInstaller.FindInstalled(directory, package);
#if SHARED_INDEX_TRIAL
        if (installed is null && directory == ExtensionsDirectory)
            return EnginePackageInstaller.FindInstalled(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileAssistant", "Engines"), package);
#endif
        return installed;
    }

    public static EnginePaths Installed { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "Engines", "ffmpeg", "bin", "ffmpeg.exe"),
        Path.Combine(FileAssistant.Core.AppProfile.Directory, "Previews"), ExtensionsDirectory);
    public static string ExtensionsDirectory => Path.Combine(FileAssistant.Core.AppProfile.Directory, "Engines");
}
