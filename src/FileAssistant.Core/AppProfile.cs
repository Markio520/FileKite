namespace FileAssistant.Core;

public static class AppProfile
{
#if SHARED_INDEX_TRIAL
    public const string Name = "FileAssistant-IndexTrial";
#else
    public const string Name = "FileAssistant";
#endif
    // Isolated acceptance runs use a separate profile and instance mutex.
    public static string? OverrideDirectory { get; } = Environment.GetEnvironmentVariable("FILEASSISTANT_PROFILE_DIRECTORY") is { Length: > 0 } path && Path.IsPathFullyQualified(path)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
    public static string Directory => OverrideDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}
