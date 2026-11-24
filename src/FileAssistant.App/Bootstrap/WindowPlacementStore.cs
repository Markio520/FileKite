using System.Runtime.InteropServices;
using System.Text.Json;
using FileAssistant.Infrastructure.Persistence;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace FileAssistant.App.Bootstrap;

internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

internal sealed class WindowPlacementStore(string profile)
{
    private readonly JsonSettingsStore store = new(Path.Combine(profile, "window-placement.json"));
    private WindowPlacement? lastNormal;

    public async Task RestoreAsync(AppWindow window)
    {
        if (!App.Preferences.Current.RememberWindow) return;
        try
        {
            if (await store.ReadAsync() is not { } json || JsonSerializer.Deserialize<WindowPlacement>(json) is not { } saved ||
                saved.Width < 120 || saved.Height < 96) return;
            var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
            var width = Math.Clamp(saved.Width, 120, Math.Max(120, area.Width));
            var height = Math.Clamp(saved.Height, 96, Math.Max(96, area.Height));
            window.MoveAndResize(new(Math.Clamp(saved.X, area.X, area.X + Math.Max(0, area.Width - width)),
                Math.Clamp(saved.Y, area.Y, area.Y + Math.Max(0, area.Height - height)), width, height));
            lastNormal = saved with { Maximized = false };
            if (saved.Maximized && window.Presenter is OverlappedPresenter p) p.Maximize();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or COMException) { App.ReportFailure(e); }
    }

    public void Observe(AppWindow window)
    {
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && window.Size.Width > 0)
            lastNormal = new(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height, false);
    }

    public Task SaveAsync(AppWindow window)
    {
        if (!App.Preferences.Current.RememberWindow || lastNormal is null) return Task.CompletedTask;
        var state = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        return store.WriteAsync(JsonSerializer.Serialize(lastNormal with { Maximized = state }));
    }
}
