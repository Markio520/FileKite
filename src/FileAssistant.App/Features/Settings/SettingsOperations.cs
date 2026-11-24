using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FileAssistant.App.Features.CreateOrRename;
using FileAssistant.App.Features.Search;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Engines;
using FileAssistant.Infrastructure.Persistence;

namespace FileAssistant.App.Features.Settings;

public sealed record StorageUsage(long EnginesBytes, long PreviewBytes, long RecoveryBytes, long IndexBytes, long ConfigurationBytes, long OtherBytes)
{
    public long IndexLogicalBytes { get; init; }
    public long? LegacySearchBytes { get; init; }
}

internal static class SettingsOperations
{
    private static readonly HttpClient Updates = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim portableGate = new(1, 1);
    private sealed record PortableDocument(int Version, AppSettings Settings, List<OutlineTemplate> Templates, IReadOnlyList<SavedSearch>? Searches = null);
    private sealed record ImportJournal(AppSettings Target, List<OutlineTemplate> OldTemplates, List<OutlineTemplate> NewTemplates,
        IReadOnlyList<SavedSearch> OldSearches, IReadOnlyList<SavedSearch> NewSearches);
    internal static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private static readonly Lazy<string> runtimeSummary = new(ReadRuntimeSummary);
    internal static string RuntimeSummary => runtimeSummary.Value;
    internal static string RuntimeDetails => $"{InterfaceText.Brand} {AppVersion}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.ProcessArchitecture} · {RuntimeSummary}\n{Environment.ProcessorCount} logical processors";
    private static string ReadRuntimeSummary()
    {
        var details = new List<string> { RuntimeInformation.FrameworkDescription };
        try
        {
            using var dependencies = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FileAssistant.App.deps.json")));
            var sdk = dependencies.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name)
                .FirstOrDefault(p => p.StartsWith("Microsoft.WindowsAppSDK/", StringComparison.OrdinalIgnoreCase));
            details.Add(sdk is null ? "WinUI 3" : $"Windows App SDK {sdk.Split('/')[1]} (WinUI 3)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException) { details.Add("WinUI 3"); }
        try
        {
            using var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            database.Open();
            details.Add("SQLite " + database.ServerVersion);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or DllNotFoundException or TypeInitializationException) { App.ReportFailure(e); }
        return string.Join(" · ", details);
    }
    internal static string LicenseDirectory => Path.Combine(AppContext.BaseDirectory, "Licenses");
    internal static (long Items, int Ready, int Selected, bool Updating) GetIndexStatus() => App.Index?.GetStatus() ?? default;
    internal static IReadOnlyList<SpaceDrive> GetIndexDrives() => App.Index?.GetDrives() ?? [];
    internal static Task SynchronizeIndexAsync() => App.Index?.SynchronizeAsync(App.Stopping) ?? throw new InvalidOperationException("索引服务尚未就绪。");
    internal static Task RebuildIndexAsync() => App.Index?.RebuildAsync(App.Stopping) ?? throw new InvalidOperationException("索引服务尚未就绪。");

    internal static async Task<StorageUsage> GetStorageUsageAsync()
    {
        var result = await ProfileStorage.MeasureAsync(App.ProfileDirectory, App.Stopping);
        return new(result.EnginesBytes, result.PreviewBytes, result.RecoveryBytes, result.IndexBytes, result.ConfigurationBytes, result.OtherBytes)
            { IndexLogicalBytes = result.IndexLogicalBytes, LegacySearchBytes = result.LegacySearchBytes };
    }
    internal static Task ClearPreviewAsync() => ((LocalFileProcessingService)App.FileProcessing).ClearPreviewCacheAsync(App.Stopping);
    internal static Task ClearRecoveryAsync() => PruneAsync(DateTimeOffset.MaxValue);
    internal static Task PruneExpiredAsync() => App.Preferences.Current.BackupRetentionDays is > 0 and var days
        ? PruneAsync(DateTimeOffset.UtcNow.AddDays(-days)) : Task.CompletedTask;
    private static async Task PruneAsync(DateTimeOffset before)
    {
        await App.CopyService.PruneRecoveryAsync(before, App.Stopping);
        await App.NamingService.PruneRecoveryAsync(before, App.Stopping);
        await App.Copy.RefreshRecoveriesCommand.ExecuteAsync(null);
        await App.Naming.RefreshRecoveriesCommand.ExecuteAsync(null);
    }
    internal static async Task ResetPaneWidthsAsync()
    {
        await App.PaneWidths.ResetAsync();
        App.Search.ResetColumnWidths();
        await App.Search.FlushPreferencesAsync();
    }

    internal static void OpenDataFolder() => OpenFolder(App.ProfileDirectory);
    internal static void OpenLogFolder() => OpenFolder(Path.Combine(App.ProfileDirectory, "Logs"));
    private static void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
    }

    private static string TemplatesPath => Path.Combine(App.ProfileDirectory, "outline-templates.json");
    private static string JournalPath => Path.Combine(App.ProfileDirectory, "preferences-import.pending");
    internal static async Task ExportSettingsAsync(string file)
    {
        await portableGate.WaitAsync();
        try
        {
            var templates = OutlineTemplateStore.ReadForUpdate(TemplatesPath);
            var document = new PortableDocument(1, App.Preferences.Current, templates, App.Search.ExportPresets());
            await new JsonSettingsStore(file).WriteAsync(JsonSerializer.Serialize(document, AppPreferences.JsonOptions));
        }
        finally { portableGate.Release(); }
    }

    internal static async Task ImportSettingsAsync(string file)
    {
        await portableGate.WaitAsync();
        try
        {
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("便携配置超过 8 MB。");
            var doc = JsonSerializer.Deserialize<PortableDocument>(await File.ReadAllTextAsync(file), AppPreferences.JsonOptions);
            if (doc is not { Version: 1, Settings: not null, Templates: not null } || doc.Templates.Count > 1000 ||
                doc.Templates.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name) ||
                t.Lines is null || t.Lines.Count > 20000 || t.Lines.Any(l => l is null || l.Indent is < 0 or > 64 || string.IsNullOrWhiteSpace(l.Name))) ||
                doc.Templates.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != doc.Templates.Count)
                throw new InvalidDataException("无法识别便携配置或大纲模板。");
            AppPreferences.Validate(doc.Settings);
            var old = OutlineTemplateStore.ReadForUpdate(TemplatesPath);
            var previousSearches = App.Search.ExportPresets();
            var searches = doc.Searches ?? previousSearches;
            SearchViewModel.ValidatePresets(searches);
            await new JsonSettingsStore(JournalPath).WriteAsync(JsonSerializer.Serialize(new ImportJournal(doc.Settings, old, doc.Templates, previousSearches, searches), AppPreferences.JsonOptions));
            try
            {
                OutlineTemplateStore.Save(doc.Templates, TemplatesPath);
                await App.Search.ImportPresetsAsync(searches);
                await App.Preferences.UpdateAsync(_ => doc.Settings);
            }
            catch
            {
                OutlineTemplateStore.Save(old, TemplatesPath);
                await App.Search.ImportPresetsAsync(previousSearches);
                File.Delete(JournalPath);
                throw;
            }
            File.Delete(JournalPath);
            App.Naming.ReloadOutlineTemplates();
        }
        finally { portableGate.Release(); }
    }

    internal static async Task FinishInterruptedImportAsync()
    {
        if (!File.Exists(JournalPath)) return;
        var journal = JsonSerializer.Deserialize<ImportJournal>(File.ReadAllText(JournalPath), AppPreferences.JsonOptions)
            ?? throw new InvalidDataException("无法读取中断的配置导入记录。");
        var applied = JsonSerializer.Serialize(App.Preferences.Current, AppPreferences.JsonOptions) == JsonSerializer.Serialize(journal.Target, AppPreferences.JsonOptions);
        OutlineTemplateStore.Save(applied ? journal.NewTemplates : journal.OldTemplates, TemplatesPath);
        await App.Search.ImportPresetsAsync(applied ? journal.NewSearches : journal.OldSearches);
        File.Delete(JournalPath);
    }

    internal static async Task ResetPreferencesAsync()
    {
        await App.Preferences.UpdateAsync(_ => new());
        await ResetPaneWidthsAsync();
        File.Delete(Path.Combine(App.ProfileDirectory, "window-placement.json"));
        if (App.Window is MainWindow main)
        {
            if (main.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Restore();
            main.SetWorkspaceSize(compact: false);
        }
    }

    internal static async Task<string> CheckUpdatesAsync() => (await QueryUpdateAsync()).Message;

    internal static async Task<(bool UpdateAvailable, string Message)> QueryUpdateAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Markio520/FileKite/releases/latest");
        request.Headers.UserAgent.ParseAdd("FileKite");
        using var response = await Updates.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, App.Stopping);
        if (response.StatusCode == HttpStatusCode.NotFound) return (false, InterfaceText.Get("此仓库尚无可读取的公开发布版本。", "This repository has no accessible public release."));
        response.EnsureSuccessStatusCode();
        using var release = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(App.Stopping), cancellationToken: App.Stopping);
        var tag = release.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var available)) throw new InvalidDataException(InterfaceText.Get("发布版本号无法识别。", "The release tag is not a version number."));
        var newer = available > new Version(AppVersion);
        return (newer, newer ? InterfaceText.Get("发现新版本 ", "Update available: ") + available : InterfaceText.Get("当前已是最新版本。", "You have the latest release."));
    }
}
