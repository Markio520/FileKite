using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FileAssistant.App.Features.CreateOrRename;
using FileAssistant.App.Features.Search;
using FileAssistant.App.Features.Settings;
using FileAssistant.App.Features.Workspace;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Engines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileAssistant.Settings.UiProbe;

internal sealed partial class Probe
{
    private static async Task CheckPortability(WorkspacePage workspace)
    {
        await Workspace.LoadCollectionAsync();
        var token = Guid.NewGuid().ToString("N");
        string name = "Fixture 李/用户".Replace('/', '-') + token[..6];
        var group = Workspace.CreateFolder(name);
        await Workspace.FlushCollectionAsync();
        var templates = OutlineTemplateStore.Load().Where(t => t.Id != "test-" + token).ToList();
        templates.Add(new OutlineTemplate("test-" + token, name, [new(0, "文档", false), new(1, "样本.txt", true)]));
        OutlineTemplateStore.Save(templates);
        Naming.ReloadOutlineTemplates();
        var search = new SavedSearch("Fixture 搜索 " + token, new SearchConditions { QueryText = "蓝图-" + token[..6] });
        await Search.ImportPresetsAsync([search]);
        var engineRoot = EnginePaths.ExtensionsDirectory;
        Require(SamePath(Path.GetDirectoryName(engineRoot), profile), "The engine fixture would be written outside the isolated profile.");
        var engine = Directory.CreateDirectory(Path.Combine(engineRoot, "settings-probe-" + token));
        var marker = Path.Combine(engine.FullName, "engine-sentinel.txt");
        await File.WriteAllTextAsync(marker, "保留原有引擎内容 " + token);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(marker)));
        await Preferences.UpdateAsync(s => s with { Language = InterfaceLanguage.SimplifiedChinese, ImageQuality = 73, MaxParallel = 4,
            ConfirmNonemptyGroup = false, BackupRegistry = true, AppIcon = "vermilion" });
        var portable = Path.Combine(profile, "portable-roundtrip.json");
        await Operation("ExportSettingsAsync", portable);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(portable)))
        {
            var file = document.RootElement;
            Require(file.GetProperty("Version").GetInt32() == 1 && file.GetProperty("Settings").GetProperty("ImageQuality").GetInt32() == 73
                && file.GetProperty("Settings").GetProperty("AppIcon").GetString() == "vermilion",
                "Portable export omitted the current settings.");
            Require(file.GetProperty("Templates").EnumerateArray().Any(item => item.GetProperty("Id").GetString() == "test-" + token)
                && file.GetProperty("Searches").EnumerateArray().Any(item => item.GetProperty("Name").GetString() == search.Name),
                "Portable export omitted a user template or saved search.");
        }
        await Preferences.UpdateAsync(s => s with { ImageQuality = 96, MaxParallel = 2, AppIcon = "pine" });
        OutlineTemplateStore.Save(OutlineTemplateStore.DefaultTemplates());
        Naming.ReloadOutlineTemplates();
        await Search.ImportPresetsAsync([]);
        await Operation("ImportSettingsAsync", portable);
        Require(Preferences.Current.ImageQuality == 73 && Preferences.Current.MaxParallel == 4 && Preferences.Current.AppIcon == "vermilion"
            && Naming.OutlineTemplates.Any(t => t.Id == "test-" + token) && Search.ExportPresets().Single().Conditions.QueryText == search.Conditions.QueryText,
            "Imported settings, user template cache or saved search differs from the export.");
        Require(!File.Exists(Path.Combine(profile, "preferences-import.pending")), "The completed import left a transaction journal.");

        var opening = workspace.OpenSettingsAsync(initialPage: "storage");
        var dialog = await Opened(workspace, opening);
        var reset = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<Button>().Single(b => (b.Content as string)?.StartsWith("重置所有设置") == true);
        InvokeButton(reset);
        await Until(() => Part<Grid>(dialog, "ConfirmationLayer").Visibility == Visibility.Visible, "Factory reset offered no confirmation.");
        InvokeButton(Part<Button>(dialog, "ConfirmCancel"));
        await Until(() => Part<Grid>(dialog, "ConfirmationLayer").Visibility == Visibility.Collapsed, "Reset cancellation did not dismiss the confirmation.");
        Require(Preferences.Current.ImageQuality == 73, "Cancelling reset changed existing settings.");
        await Navigate(dialog, "about");
        var check = Elements(Part<StackPanel>(dialog, "SectionsHost")).OfType<Button>().Single(b => b.Content as string == "检查更新");
        Require(check.IsEnabled, "The public version check action was disabled.");
        dialog.Hide(); await opening;

        await Operation("ResetPreferencesAsync");
        var defaults = new AppSettings();
        var actual = JsonSerializer.Serialize(Preferences.Current, AppPreferences.JsonOptions);
        Require(actual == JsonSerializer.Serialize(defaults, AppPreferences.JsonOptions), "Factory reset differs from the application defaults.");
        var onDisk = AppPreferences.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "settings-v6.json")));
        Require(JsonSerializer.Serialize(onDisk, AppPreferences.JsonOptions) == actual, "Reset did not persist the application defaults.");
        Require(Workspace.HasFolder(group) && Workspace.FolderName(group) == name, "Factory reset removed or renamed the user's staging group.");
        Require(OutlineTemplateStore.Load().Any(t => t.Id == "test-" + token) && Search.ExportPresets().Any(s => s.Name == search.Name),
            "Factory reset removed a user template or saved search.");
        Require(hash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(marker))), "Factory reset modified engine files.");
        evidence.Add(new { check = "portable round-trip and factory reset", exportedQuality = 73, exportedParallel = 4,
            defaultQuality = onDisk.ImageQuality, defaultParallel = onDisk.MaxParallel, savedSearch = search.Name, userGroup = name, templateCount = templates.Count,
            cancelledResetPreservedValues = true, engineHashPreserved = true, completionJournalRemoved = true,
            updateButtonEnabledWithoutNetwork = true, packagePath = portable });
        // Restore the exported fixture so that the independent reopening probe keeps validating the same three fields.
        await Operation("ImportSettingsAsync", portable);
    }

    private static Task Operation(string method, params object?[] args)
    {
        var operations = typeof(SettingsDialog).Assembly.GetType("FileAssistant.App.Features.Settings.SettingsOperations", true)!;
        return (Task)operations.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    }
}
