using System.Text.Json;
using System.Text.Json.Serialization;
using FileAssistant.Core.Abstractions;

namespace FileAssistant.Core.Settings;

public sealed class AppPreferences(ISettingsStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public AppSettings Current { get; private set; } = new();
    public bool HasDocument { get; private set; }
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;
    // Apply reversible OS registrations before acknowledging a change.
    public Action<AppSettings, AppSettings>? PrepareChange { get; set; }
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task LoadAsync(AppSettings? legacyDefaults = null)
    {
        await gate.WaitAsync();
        try
        {
            var contents = await store.ReadAsync();
            HasDocument = contents is not null;
            Current = contents is null ? legacyDefaults ?? new() : Parse(contents);
            LoadError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Current = legacyDefaults ?? new();
            LoadError = error.Message;
        }
        finally { gate.Release(); }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> edit)
    {
        await gate.WaitAsync();
        try
        {
            var previous = Current;
            var next = edit(previous);
            Validate(next);
            if (next == previous && HasDocument && LoadError is null) return;
            var contents = JsonSerializer.Serialize(next, JsonOptions);
            PrepareChange?.Invoke(previous, next);
            try { await store.WriteAsync(contents); }
            catch
            {
                PrepareChange?.Invoke(next, previous);
                throw;
            }
            Current = next;
            HasDocument = true;
            LoadError = null;
        }
        finally { gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task FlushAsync()
    {
        await gate.WaitAsync();
        gate.Release();
    }

    public static AppSettings Parse(string json)
    {
        var value = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? throw new JsonException("设置文档为空。");
        Validate(value);
        return value;
    }

    public static void Validate(AppSettings s)
    {
        if (s.Version != 1 || s.ImageQuality is < 50 or > 100 || s.MaxParallel is not (0 or 2 or 4 or 8)
            || s.ContentLimitMB is not (0 or 20 or 50 or 200) || s.BackupRetentionDays is not (0 or 7 or 30)
            || s.Shortcuts is null || s.ExcludedDirectories is null || s.ExcludedDirectories.Length > 2048
            || s.ExcludedDirectories.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32767)
            || s.IndexedVolumes?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length != 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') == true
            || s.FfmpegPath is null || s.OfficePath is null || s.OutputFolder is null
            || s.OutputLocation == OutputLocation.Fixed && !Path.IsPathFullyQualified(s.OutputFolder)
            || s.Palette is not ("charcoal" or "ocean" or "forest" or "espresso" or "sand" or "frost" or "linen" or "studio")
            || (s.StartupModule != "last" && !IsModule(s.StartupModule)) || !IsModule(s.LastModule)
            || !Enum.IsDefined(s.Language) || !Enum.IsDefined(s.CloseAction) || !Enum.IsDefined(s.DoubleClick)
            || !Enum.IsDefined(s.Units) || !Enum.IsDefined(s.Mode) || !Enum.IsDefined(s.Density)
            || !Enum.IsDefined(s.DropTarget) || !Enum.IsDefined(s.OutputLocation) || !Enum.IsDefined(s.Completion)
            || !Enum.IsDefined(s.ConversionConflict) || !Enum.IsDefined(s.NamingConflict) || !Enum.IsDefined(s.CopyLayout)
            || !Enum.IsDefined(s.TextEncoding) || !Enum.IsDefined(s.Playback))
            throw new ArgumentException("设置包含不支持的选项、目录或范围。");
        var used = new HashSet<ShortcutChord>();
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            if (!ShortcutChord.TryParse(s.Shortcuts.Get(action), out var key) || !used.Add(key) ||
                action is ShortcutAction.ToggleWindow or ShortcutAction.GlobalSearch && key.Modifiers == 0)
                throw new ArgumentException("快捷键无效或重复；系统级热键至少需要一个修饰键。");
        }
    }

    public static bool IsModule(string? value) => value is "workspace" or "search" or "convert" or "organize" or "naming" or "space" or "cleanup" or "outputs";
}
