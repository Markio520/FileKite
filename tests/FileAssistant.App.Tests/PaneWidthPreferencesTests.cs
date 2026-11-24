using FileAssistant.App.Features.Settings;
using FileAssistant.Core.Abstractions;

namespace FileAssistant.App.Tests;

public sealed class PaneWidthPreferencesTests
{
    [Fact]
    public async Task Reset_uses_the_page_default_and_keeps_other_saved_widths()
    {
        var store = new MemoryStore { Contents = """{"Copy.Folders":240,"Copy.Details":360,"Search.Details":300}""" };
        var preferences = await ReopenAsync(store);
        Assert.Equal(240d, preferences.GetWidth("Copy.Folders"));

        preferences.SetWidth("Copy.Folders", null);
        preferences.SetWidth("Copy.Details", 400);
        await preferences.FlushAsync();

        var reopened = await ReopenAsync(store);
        Assert.Null(reopened.GetWidth("Copy.Folders"));
        Assert.Equal(400d, reopened.GetWidth("Copy.Details"));
        Assert.Equal(300d, reopened.GetWidth("Search.Details"));
    }

    [Fact]
    public async Task Changes_during_a_save_are_also_restored_after_reopening()
    {
        var store = new MemoryStore { HoldFirstWrite = true };
        var preferences = await ReopenAsync(store);
        preferences.SetWidth("Staging.Folders", 200);
        await store.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        preferences.SetWidth("Staging.Folders", 248);
        preferences.SetWidth("Staging.Folders", 260);
        preferences.SetWidth("Staging.Details", 380);
        store.ReleaseFirstWrite.SetResult();
        await preferences.FlushAsync();

        var reopened = await ReopenAsync(store);
        Assert.Equal(260d, reopened.GetWidth("Staging.Folders"));
        Assert.Equal(380d, reopened.GetWidth("Staging.Details"));
    }

    [Fact]
    public async Task Invalid_saved_widths_fall_back_and_do_not_block_a_later_save()
    {
        var store = new MemoryStore { Contents = """{"Copy.Folders":-1,"Search.Details":1e999}""" };
        var preferences = await ReopenAsync(store);
        Assert.Null(preferences.GetWidth("Copy.Folders"));
        Assert.Null(preferences.GetWidth("Search.Details"));

        preferences.SetWidth("Search.Details", 340);
        await preferences.FlushAsync();
        Assert.Equal(340d, (await ReopenAsync(store)).GetWidth("Search.Details"));
    }

    private static async Task<PaneWidthPreferences> ReopenAsync(MemoryStore store)
    {
        var preferences = new PaneWidthPreferences(store);
        await preferences.LoadAsync();
        return preferences;
    }

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Contents { get; set; }
        public bool HoldFirstWrite { get; init; }
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> ReadAsync() => Task.FromResult(Contents);

        public async Task WriteAsync(string contents)
        {
            if (HoldFirstWrite && FirstWriteStarted.TrySetResult())
                await ReleaseFirstWrite.Task;
            Contents = contents;
        }
    }
}
