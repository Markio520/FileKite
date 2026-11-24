using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Engines;
using FileAssistant.Infrastructure.Engines.Images;
using ImageMagick;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Settings;
using FileAssistant.Infrastructure.Engines;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.Persistence;

namespace FileAssistant.Infrastructure.Tests;

public sealed class SettingsV6Tests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("FileAssistant-v6-tests-").FullName;

    [Fact]
    public async Task Disjoint_concurrent_settings_edits_survive_reopening_without_overwriting_each_other()
    {
        var store = new JsonSettingsStore(Path.Combine(root, "settings.json"));
        var preferences = new AppPreferences(store);
        await preferences.LoadAsync(new() { TopNavigation = true });
        Assert.True(preferences.Current.TopNavigation);
        await Task.WhenAll(preferences.UpdateAsync(s => s with { Language = InterfaceLanguage.English }),
            preferences.UpdateAsync(s => s with { ImageQuality = 94 }), preferences.UpdateAsync(s => s with
            { IndexedVolumes = [], ExcludedDirectories = ["build"], Shortcuts = s.Shortcuts.With(ShortcutAction.Compare, "Ctrl+F9") }));
        var restored = new AppPreferences(store);
        await restored.LoadAsync();
        Assert.Equal(InterfaceLanguage.English, restored.Current.Language);
        Assert.True(restored.Current.TopNavigation);
        Assert.Equal(94, restored.Current.ImageQuality);
        Assert.Equal("Ctrl+F9", restored.Current.Shortcuts.Compare);
        Assert.Empty(restored.Current.IndexedVolumes!);
        Assert.Equal(["build"], restored.Current.ExcludedDirectories);
    }

    [Theory]
    [InlineData("cobalt")]
    [InlineData("charcoal")]
    [InlineData("pine")]
    [InlineData("vermilion")]
    [InlineData("amber")]
    public async Task Icon_choice_round_trips_through_disk_and_portable_json(string id)
    {
        var store = new JsonSettingsStore(Path.Combine(root, "settings-v6.json"));
        var prefs = new AppPreferences(store);
        await prefs.UpdateAsync(s => s with { AppIcon = id, Palette = "studio", ImageQuality = 71 });
        var reopened = new AppPreferences(store);
        await reopened.LoadAsync();
        Assert.Equal(id, reopened.Current.AppIcon);
        var portable = AppPreferences.Parse((await store.ReadAsync())!);
        Assert.Equal(id, portable.AppIcon);
        Assert.Equal("studio", portable.Palette);
        Assert.Equal(71, portable.ImageQuality);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AppIcon\":null}")]
    [InlineData("{\"AppIcon\":\"unknown\"}")]
    [InlineData("{\"AppIcon\":\"../untrusted\"}")]
    [InlineData("{\"AppIcon\":{\"future\":[1,2]}}")]
    public async Task Old_or_unknown_icon_keeps_other_settings_and_is_saved_as_cobalt(string json)
    {
        var store = new JsonSettingsStore(Path.Combine(root, "settings-v6.json"));
        var contents = json.Insert(1, json.Length == 2 ? "\"ImageQuality\":61" : "\"ImageQuality\":61,");
        await store.WriteAsync(contents);
        var prefs = new AppPreferences(store);
        await prefs.LoadAsync();
        Assert.Null(prefs.LoadError);
        Assert.Equal("cobalt", prefs.Current.AppIcon);
        Assert.Equal(61, prefs.Current.ImageQuality);
        await prefs.UpdateAsync(s => s with { Animations = false });
        Assert.Equal("cobalt", AppPreferences.Parse((await store.ReadAsync())!).AppIcon);
    }

    [Fact]
    public async Task Failed_writes_rollback_external_changes_and_conflicting_hotkeys_never_reach_the_store()
    {
        var preferences = new AppPreferences(new JsonSettingsStore(root));
        List<bool> applied = [];
        preferences.PrepareChange = (_, next) => applied.Add(next.StartAtLogin);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => preferences.UpdateAsync(s => s with { StartAtLogin = true }));
        Assert.False(preferences.Current.StartAtLogin);
        Assert.Equal([true, false], applied);
        await Assert.ThrowsAsync<ArgumentException>(() => preferences.UpdateAsync(s => s with { Shortcuts = s.Shortcuts with { Refresh = "shift+ctrl+F" } }));
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public void Engine_overrides_apply_and_clear_without_recreating_the_resolver()
    {
        var settings = new AppSettings();
        var bundled = Path.Combine(root, "bundle", "ffmpeg.exe");
        var external = Path.Combine(root, "other", "ffmpeg.exe");
        var office = Path.Combine(root, "Office", "soffice.exe");
        var paths = new EnginePaths(bundled, root) { Preferences = () => settings };
        Assert.Equal(bundled, paths.FfmpegExecutable);
        settings = settings with { FfmpegPath = external, OfficePath = office };
        Assert.Equal(external, paths.FfmpegExecutable);
        Assert.True(paths.HasFfmpegOverride);
        Assert.Equal(office, paths.OfficeExecutable);
        Assert.Equal(Path.Combine(root, "bundle", "ffprobe.exe"), paths.FfprobeExecutable);
        settings = new();
        Assert.Equal(bundled, paths.FfmpegExecutable);
        Assert.Null(paths.OfficeExecutable);
    }

    [Fact]
    public async Task Invalid_collection_destinations_can_be_retried_instead_of_deadlocking()
    {
        var store = new JsonFileCollectionStore("bad\0path");
        var snapshot = new FileCollection().Snapshot(Guid.Empty);
        for (var pass = 0; pass < 2; pass++)
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync(snapshot, default).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_storage_segments_cover_each_profile_file_once(bool hasOldCache)
    {
        long Put(string name, int bytes)
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
            return SpaceFileMetadata.Read(path).Allocation;
        }
        var engines = Put("Engines/a", 2);
        var previews = Put("Previews/a.png", 3);
        var recovery = Put("CopyRecovery/a", 5) + Put("NamingRecovery/b", 7);
        var index = Put("Indexes/0/index.db", 4097) + Put("Indexes/0/index.db-wal", 8195) + Put("Indexes/0/index.db-shm", 32768)
            + Put("Indexes/other.SQLITE3-journal", 4099);
        var configuration = Put("Collections/batch.json", 13) + Put("settings-v6.json", 17) + Put("Indexes/0/options.json", 4099);
        var other = Put("Logs/log", 19) + Put("Indexes/notes.txt", 5120);
        Directory.CreateDirectory(Path.Join(root, "Search"));
        long? legacy = hasOldCache ? Put("Search/Everything.db", 16000) + Put("Search/Everything.ini", 8100) : null;
        var usage = await ProfileStorage.MeasureAsync(root, default);
        Assert.Equal(new StorageUsage(engines, previews, recovery, index, configuration, other)
            { IndexLogicalBytes = 49159, LegacySearchBytes = legacy }, usage);
        Assert.Equal(engines + previews + recovery + index + configuration + other + (legacy ?? 0),
            usage.EnginesBytes + usage.PreviewBytes + usage.RecoveryBytes + usage.IndexBytes + usage.ConfigurationBytes +
            usage.OtherBytes + (usage.LegacySearchBytes ?? 0));
    }

    [Theory]
    [InlineData(1000, SizeUnits.Decimal, "1 KB")]
    [InlineData(1024, SizeUnits.Binary, "1 KiB")]
    [InlineData(1048576, SizeUnits.Classic, "1 MB")]
    public void Display_units_and_extensions_preserve_underlying_identity(long bytes, SizeUnits unit, string expected)
    {
        Assert.Equal(expected, FileDisplay.Size(bytes, unit));
        Assert.Equal("photo.final", FileDisplay.Name("photo.final.png", false, false));
        Assert.Equal(".gitignore", FileDisplay.Name(".gitignore", false, false));
        Assert.Equal("folder.data", FileDisplay.Name("folder.data", true, false));
    }
    [Theory]
    [InlineData(ConversionTarget.Jpeg, 73)]
    [InlineData(ConversionTarget.WebP, 95)]
    [InlineData(ConversionTarget.Avif, 55)]
    public async Task Quality_comparison_encodes_the_same_pixels_as_the_chosen_converter(ConversionTarget target, int quality)
    {
        using var source = new MagickImage("gradient:navy-tomato", 96, 64);
        var pixels = source.ToByteArray(MagickFormat.Png);
        var preview = await PreviewComparison.EncodeAsync(pixels, target, quality, default);
        using var canonicalSource = new MagickImage(pixels);
        using var expected = new MemoryStream();
        MagickImageEngine.Write(canonicalSource, expected, target, quality);
        Assert.Equal(expected.Length, preview.CompressedBytes);
        using var after = new MagickImage(preview.DecodedPng);
        expected.Position = 0;
        using var encoded = new MagickImage(expected);
        Assert.Equal(0, after.Compare(encoded, ErrorMetric.RootMeanSquared));
        Assert.Equal((96u, 64u), (preview.Width, preview.Height));
    }

    public void Dispose() => Directory.Delete(root, true);
}
