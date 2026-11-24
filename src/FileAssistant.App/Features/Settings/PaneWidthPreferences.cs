using System.Diagnostics;
using System.Text.Json;
using FileAssistant.Core.Abstractions;

namespace FileAssistant.App.Features.Settings;

/// <summary>Stores preferred pane widths from the UI thread, independently of the window size.</summary>
internal sealed class PaneWidthPreferences(ISettingsStore store)
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private Dictionary<string, double> widths = [];
    private Task pendingSave = Task.CompletedTask;
    private long lastChange;
    private bool dirty;

    public async Task LoadAsync()
    {
        try
        {
            if (await store.ReadAsync() is { } json &&
                JsonSerializer.Deserialize<Dictionary<string, double>>(json) is { } saved)
                widths = saved.Where(entry => double.IsFinite(entry.Value) && entry.Value > 0).ToDictionary();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine(error);
        }
    }

    public event EventHandler? Reset;

    public async Task ResetAsync()
    {
        widths.Clear();
        dirty = true;
        lastChange = 0;
        await FlushAsync();
        if (dirty) throw new IOException("分栏宽度暂时无法保存。");
        Reset?.Invoke(this, EventArgs.Empty);
    }

    public double? GetWidth(string? key)
        => key is not null && widths.TryGetValue(key, out var width) ? width : null;

    public void SetWidth(string? key, double? width)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (width is { } value)
        {
            if (!double.IsFinite(value) || value <= 0 || GetWidth(key) == value) return;
            widths[key] = value;
        }
        else if (!widths.Remove(key)) return;

        dirty = true;
        lastChange = Stopwatch.GetTimestamp();
        if (pendingSave.IsCompleted) pendingSave = SaveAfterIdleAsync();
    }

    public async Task FlushAsync()
    {
        await pendingSave;
        if (dirty && pendingSave.IsCompleted) pendingSave = SaveAfterIdleAsync();
        await pendingSave;
    }

    private async Task SaveAfterIdleAsync()
    {
        while (dirty)
        {
            var delay = SaveDelay - Stopwatch.GetElapsedTime(lastChange);
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            else if (!await TrySaveAsync()) return;
        }
    }

    private async Task<bool> TrySaveAsync()
    {
        dirty = false;
        try
        {
            await store.WriteAsync(JsonSerializer.Serialize(widths));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            dirty = true;
            Debug.WriteLine(error);
            return false;
        }
    }
}
