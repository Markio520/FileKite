using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using FileAssistant.Core.Abstractions;

namespace FileAssistant.App.Features.Settings;

// These values are persisted. Value 3 is reserved for the retired dark acrylic style.
public enum WorkspaceAppearance { System = 0, Light = 1, CharcoalGlass = 2 }
public enum NavigationPlacement { Left, Top }

public sealed class AppearanceViewModel(ISettingsStore store) : ObservableObject
{
    private const int LegacySmokedGlassValue = 3;
    private WorkspaceAppearance style = WorkspaceAppearance.CharcoalGlass;
    private NavigationPlacement navigation = NavigationPlacement.Left;
    private string statusMessage = string.Empty;
    private Task pendingSave = Task.CompletedTask;
    private bool loading;
    private FileAssistant.Core.Settings.AppPreferences? shared;

    public void Connect(FileAssistant.Core.Settings.AppPreferences preferences)
    {
        shared = preferences;
        Apply(preferences.Current);
    }

    public void Apply(FileAssistant.Core.Settings.AppSettings value)
    {
        loading = true;
        try
        {
            Style = (WorkspaceAppearance)(int)value.Mode;
            Navigation = value.TopNavigation ? NavigationPlacement.Top : NavigationPlacement.Left;
        }
        finally { loading = false; }
    }

    private void SaveChange(bool mode)
    {
        if (shared is null) { pendingSave = SaveAfterAsync(pendingSave, SerializePreferences()); return; }
        var selectedStyle = (FileAssistant.Core.Settings.AppearanceMode)(int)Style;
        var top = IsTopNavigation;
        pendingSave = PersistAsync();
        async Task PersistAsync()
        {
            try { await shared.UpdateAsync(s => mode ? s with { Mode = selectedStyle } : s with { TopNavigation = top }); }
            catch (Exception error) { Apply(shared.Current); StatusMessage = error.Message; }
        }
    }

    public WorkspaceAppearance Style
    {
        get => style;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref style, value)) return;
            OnPropertyChanged(nameof(IsLightStyle));
            OnPropertyChanged(nameof(IsCharcoalStyle));
            OnPropertyChanged(nameof(IsSystemStyle));
            if (loading) return;
            StatusMessage = string.Empty;
            SaveChange(mode: true);
        }
    }

    public NavigationPlacement Navigation
    {
        get => navigation;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref navigation, value)) return;
            OnPropertyChanged(nameof(IsLeftNavigation));
            OnPropertyChanged(nameof(IsTopNavigation));
            if (loading) return;
            StatusMessage = string.Empty;
            SaveChange(mode: false);
        }
    }

    public bool IsLeftNavigation => Navigation == NavigationPlacement.Left;
    public bool IsTopNavigation => Navigation == NavigationPlacement.Top;
    public bool IsLightStyle => Style == WorkspaceAppearance.Light;
    public bool IsCharcoalStyle => Style == WorkspaceAppearance.CharcoalGlass;
    public bool IsSystemStyle => Style == WorkspaceAppearance.System;

    public string StatusMessage
    {
        get => statusMessage;
        private set { if (SetProperty(ref statusMessage, value)) OnPropertyChanged(nameof(HasStatusMessage)); }
    }
    public bool HasStatusMessage => StatusMessage.Length > 0;

    public async Task LoadPreferencesAsync()
    {
        loading = true;
        try
        {
            var contents = await store.ReadAsync();
            if (contents is null) return;
            var preferences = JsonSerializer.Deserialize<Preferences>(contents);
            if (preferences is not { Version: 1 } || !Enum.IsDefined(preferences.Navigation)) return;
            var restoredStyle = (int)preferences.Style == LegacySmokedGlassValue
                ? WorkspaceAppearance.CharcoalGlass
                : preferences.Style;
            if (!Enum.IsDefined(restoredStyle)) return;
            Style = restoredStyle;
            Navigation = preferences.Navigation;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { StatusMessage = "未能读取外观设置，已使用默认外观。"; }
        finally { loading = false; }
    }

    public Task FlushPreferencesAsync() => pendingSave;

    private string SerializePreferences() => JsonSerializer.Serialize(new Preferences { Style = style, Navigation = navigation });

    private async Task SaveAfterAsync(Task previous, string contents)
    {
        await previous;
        try { await store.WriteAsync(contents); StatusMessage = string.Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { StatusMessage = "外观已应用，但暂时无法保存；下次切换时会重试。"; }
    }

    private sealed record Preferences
    {
        public int Version { get; init; } = 1;
        public WorkspaceAppearance Style { get; init; } = WorkspaceAppearance.CharcoalGlass;
        public NavigationPlacement Navigation { get; init; } = NavigationPlacement.Left;
    }
}
