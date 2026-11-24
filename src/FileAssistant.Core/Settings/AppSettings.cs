using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Core.Settings;

public enum InterfaceLanguage { System, SimplifiedChinese, TraditionalChinese, English }
public enum CloseBehavior { Tray, Exit, Ask }
public enum FileDoubleClick { Open, Preview, Reveal }
public enum SizeUnits { Classic, Binary, Decimal }
public enum AppearanceMode { System, Light, Dark }
public enum ListDensity { Compact, Standard, Spacious }
public enum DropDestination { Staging, CurrentModule, SilentStaging }
public enum OutputLocation { Source, Subfolder, Fixed }
public enum CompletionBehavior { InWindow, Tray, OpenFolder }
public enum ConversionCollision { Number, Skip, Overwrite }
public enum MediaPlayback { Play, Muted, Paused }
public enum ShortcutAction { ToggleWindow, GlobalSearch, QuickLook, Compare, Filter, Refresh, Settings }

public sealed record ShortcutBindings(
    string ToggleWindow = "Alt+Space", string GlobalSearch = "Ctrl+Shift+F",
    string QuickLook = "Space", string Compare = "Alt+C", string Filter = "Ctrl+F",
    string Refresh = "F5", string Settings = "Ctrl+,")
{
    public string Get(ShortcutAction action) => action switch
    {
        ShortcutAction.ToggleWindow => ToggleWindow, ShortcutAction.GlobalSearch => GlobalSearch,
        ShortcutAction.QuickLook => QuickLook, ShortcutAction.Compare => Compare,
        ShortcutAction.Filter => Filter, ShortcutAction.Refresh => Refresh,
        ShortcutAction.Settings => Settings, _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public ShortcutBindings With(ShortcutAction action, string chord) => action switch
    {
        ShortcutAction.ToggleWindow => this with { ToggleWindow = chord },
        ShortcutAction.GlobalSearch => this with { GlobalSearch = chord },
        ShortcutAction.QuickLook => this with { QuickLook = chord },
        ShortcutAction.Compare => this with { Compare = chord },
        ShortcutAction.Filter => this with { Filter = chord },
        ShortcutAction.Refresh => this with { Refresh = chord },
        ShortcutAction.Settings => this with { Settings = chord },
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public InterfaceLanguage Language { get; init; }
    public string StartupModule { get; init; } = "last";
    public string LastModule { get; init; } = "workspace";
    public bool StartAtLogin { get; init; }
    public CloseBehavior CloseAction { get; init; }
    public bool ConfirmBusyExit { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    public bool RememberWindow { get; init; } = true;
    public FileDoubleClick DoubleClick { get; init; }
    public SizeUnits Units { get; init; }
    public bool ConfirmNonemptyGroup { get; init; } = true;

    public AppearanceMode Mode { get; init; } = AppearanceMode.Dark;
    public string Palette { get; init; } = "charcoal";
    [System.Text.Json.Serialization.JsonConverter(typeof(AppIconJsonConverter))]
    public string AppIcon { get; init => field = AppIconCatalog.Normalize(value); } = AppIconCatalog.Default;
    public bool TopNavigation { get; init; }
    public bool Mica { get; init; } = true;
    public ListDensity Density { get; init; } = ListDensity.Standard;
    public bool ShowExtensions { get; init; } = true;
    public bool Animations { get; init; } = true;
    public ShortcutBindings Shortcuts { get; init; } = new();
    public DropDestination DropTarget { get; init; }

    public OutputLocation OutputLocation { get; init; }
    public string OutputFolder { get; init; } = "";
    public CompletionBehavior Completion { get; init; }
    public ConversionCollision ConversionConflict { get; init; }
    public NamingConflict NamingConflict { get; init; } = NamingConflict.Block;
    public CopyLayout CopyLayout { get; init; } = CopyLayout.Preserve;
    public DelimitedEncoding TextEncoding { get; init; }
    public int ImageQuality { get; init; } = 85;
    public int MaxParallel { get; init; }
    public bool PreferHardwareEncoding { get; init; } = true;
    public bool VerifyCopyHash { get; init; }
    public bool PermanentDelete { get; init; }
    public bool BackupRegistry { get; init; } = true;

    // Empty means no volumes selected. Null follows the engine's detected local fixed volumes.
    public string[]? IndexedVolumes { get; init; }
    public bool PrewarmIndex { get; init; } = true;
    public string[] ExcludedDirectories { get; init; } = ["$Recycle.Bin", "System Volume Information", "node_modules", ".git"];
    public bool PinyinSearch { get; init; } = true;
    public bool ShowHidden { get; init; } = true;
    public int ContentLimitMB { get; init; } = 50;
    public MediaPlayback Playback { get; init; }
    public bool LogicalSpaceSize { get; init; }
    public string FfmpegPath { get; init; } = "";
    public string OfficePath { get; init; } = "";
    public int BackupRetentionDays { get; init; } = 30;
    public bool CheckUpdatesAtStartup { get; init; }
}
