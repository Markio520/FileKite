using System.Text.Json;
using FileAssistant.App.Features.Settings;
using FileAssistant.Core.Abstractions;

namespace FileAssistant.App.Tests;

public sealed class AppearancePreferencesTests
{
    [Theory]
    [InlineData("{\"Version\":1,\"Style\":3}", NavigationPlacement.Left)]
    [InlineData("{\"Version\":1,\"Style\":3,\"Navigation\":1}", NavigationPlacement.Top)]
    public async Task Retired_dark_style_loads_as_charcoal_without_changing_navigation_or_writing(string contents, NavigationPlacement expectedNavigation)
    {
        var store = new SettingsStore { Contents = contents };
        var appearance = new AppearanceViewModel(store);
        await appearance.LoadPreferencesAsync();
        Assert.Equal(WorkspaceAppearance.CharcoalGlass, appearance.Style);
        Assert.Equal(expectedNavigation, appearance.Navigation);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Navigation_placement_defaults_to_left_and_persists_top_selection_across_loads()
    {
        var store = new SettingsStore();
        var appearance = new AppearanceViewModel(store);
        Assert.Equal(NavigationPlacement.Left, appearance.Navigation);
        Assert.True(appearance.IsLeftNavigation);

        appearance.Navigation = NavigationPlacement.Top;
        await appearance.FlushPreferencesAsync();
        Assert.Single(store.Writes);

        var reloaded = new AppearanceViewModel(store);
        await reloaded.LoadPreferencesAsync();
        Assert.Equal(NavigationPlacement.Top, reloaded.Navigation);
        Assert.True(reloaded.IsTopNavigation);
        Assert.Equal(WorkspaceAppearance.CharcoalGlass, reloaded.Style);
        Assert.Single(store.Writes);
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Style\":99}")]
    [InlineData("{\"Version\":1,\"Style\":3,\"Navigation\":99}")]
    [InlineData("{\"Version\":2,\"Style\":3}")]
    [InlineData("invalid json")]
    public async Task Invalid_saved_settings_keep_the_default_and_do_not_overwrite_the_file(string contents)
    {
        var store = new SettingsStore { Contents = contents };
        var appearance = new AppearanceViewModel(store);
        await appearance.LoadPreferencesAsync();
        Assert.Equal(WorkspaceAppearance.CharcoalGlass, appearance.Style);
        Assert.Equal(contents, store.Contents);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Rapid_switches_are_saved_in_order_and_flush_keeps_the_last_choice()
    {
        var store = new SettingsStore { DelayFirstWrite = true };
        var appearance = new AppearanceViewModel(store) { Style = WorkspaceAppearance.System };
        await store.FirstWriteStarted.Task;
        appearance.Style = WorkspaceAppearance.Light;
        appearance.Style = WorkspaceAppearance.CharcoalGlass;
        store.ReleaseFirstWrite.SetResult();
        await appearance.FlushPreferencesAsync();
        Assert.Equal(3, store.Writes.Count);
        using var saved = JsonDocument.Parse(store.Contents!);
        Assert.Equal((int)WorkspaceAppearance.CharcoalGlass, saved.RootElement.GetProperty("Style").GetInt32());
    }

    [Fact]
    public async Task Save_failure_keeps_the_applied_style_and_a_later_switch_can_save_again()
    {
        var store = new SettingsStore { FailWrite = true };
        var appearance = new AppearanceViewModel(store) { Style = WorkspaceAppearance.Light };
        await appearance.FlushPreferencesAsync();
        Assert.Equal(WorkspaceAppearance.Light, appearance.Style);
        Assert.True(appearance.HasStatusMessage);
        store.FailWrite = false;
        appearance.Style = WorkspaceAppearance.CharcoalGlass;
        await appearance.FlushPreferencesAsync();
        Assert.False(appearance.HasStatusMessage);
        Assert.Single(store.Writes);
    }

    private sealed class SettingsStore : ISettingsStore
    {
        public string? Contents { get; set; }
        public List<string> Writes { get; } = [];
        public bool DelayFirstWrite { get; init; }
        public bool FailWrite { get; set; }
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> ReadAsync() => Task.FromResult(Contents);
        public async Task WriteAsync(string contents)
        {
            if (DelayFirstWrite && !FirstWriteStarted.Task.IsCompleted)
            { FirstWriteStarted.SetResult(); await ReleaseFirstWrite.Task; }
            if (FailWrite) throw new IOException("Test write failure");
            Contents = contents;
            Writes.Add(contents);
        }
    }
}
