using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileAssistant.App.Controls;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.Search;

public partial class SearchViewModel
{
    private readonly List<SavedSearch> presets = [];
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private CancellationTokenSource? saveCancellation;
    private Task pendingSave = Task.CompletedTask;
    private int preferencesVersion;
    private bool loadingPreferences;
    private bool preferencesDirty;
    public ObservableCollection<SelectionOption> PresetOptions { get; } = [new(-1, "选择常用条件")];
    [ObservableProperty] public partial bool ShowDetails { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRemovePreset))] public partial int PresetIndex { get; set; } = -1;
    [ObservableProperty] public partial string PresetName { get; set; } = string.Empty;
    [ObservableProperty] public partial string PresetMessage { get; set; } = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreferencesError))] public partial string PreferencesError { get; set; } = string.Empty;
    public bool HasPreferencesError => PreferencesError.Length > 0;
    public bool CanRemovePreset => PresetIndex >= 0 && PresetIndex < presets.Count;

    public IReadOnlyList<SavedSearch> ExportPresets() => presets.ToArray();

    public static void ValidatePresets(IReadOnlyList<SavedSearch> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in value)
        {
            if (preset?.Name is not { Length: > 0 and <= 60 } name || name != name.Trim() || !names.Add(name) ||
                preset.Conditions is not { IsValid: true } conditions)
                throw new ArgumentException("常用搜索条件包含重复名称或无效选项。");
            if (conditions.ScopeIndex == (int)SearchScope.Folder && !Path.IsPathFullyQualified(conditions.Folder))
                throw new ArgumentException("目录搜索条件需要完整的文件夹路径。");
            if (conditions.SizeIndex == 6 &&
                (!TrySize(conditions.CustomMinimum, conditions.SizeUnitIndex, conditions.SizeConvention, true, out var minimum) ||
                !TrySize(conditions.CustomMaximum, conditions.SizeUnitIndex, conditions.SizeConvention, false, out var maximum) ||
                minimum is null && maximum is null || minimum > maximum))
                throw new ArgumentException("常用搜索条件的大小范围无效。");
            if (conditions.DateFrom is { } from) _ = LocalMidnight(from.ToDateTime(TimeOnly.MinValue));
            if (conditions.DateThrough is { } through) _ = LocalMidnight(through.ToDateTime(TimeOnly.MinValue));
            if (conditions.DateIndex == 4 && (conditions.DateFrom is null && conditions.DateThrough is null ||
                conditions.DateFrom > conditions.DateThrough || conditions.DateThrough == DateOnly.MaxValue))
                throw new ArgumentException("常用搜索条件的日期范围无效。");
            try { _ = SearchRules.Compile(new() { Text = conditions.QueryText, ContentLimitMB = 50 }); }
            catch (SearchException error) { throw new ArgumentException(error.Message, error); }
        }
    }

    public async Task ImportPresetsAsync(IReadOnlyList<SavedSearch> value)
    {
        ValidatePresets(value);
        if (settings is null) throw new InvalidOperationException("搜索设置存储尚未就绪。");
        var incoming = value.ToArray();
        await saveGate.WaitAsync();
        try
        {
            var version = preferencesVersion;
            await settings.WriteAsync(JsonSerializer.Serialize(CapturePreferences(incoming)));
            presets.Clear();
            presets.AddRange(incoming);
            RebuildPresetOptions();
            if (version == preferencesVersion) { preferencesDirty = false; PreferencesError = ""; }
        }
        finally { saveGate.Release(); }
    }

    private SearchPreferences CapturePreferences(IReadOnlyList<SavedSearch> items) => new() { SortIndex = SortIndex,
        ShowDetails = ShowDetails, LoadIndexOnDemand = LoadIndexOnDemand, Columns = ColumnWidths.Capture(), Presets = [.. items] };

    [ObservableProperty] public partial bool LoadIndexOnDemand { get; set; }
    partial void OnLoadIndexOnDemandChanged(bool value) => QueuePreferencesSave();
    partial void OnShowDetailsChanged(bool value) => QueuePreferencesSave();
    partial void OnPresetIndexChanged(int value)
    {
        if (updatingConditions || value < 0 || value >= presets.Count) return;
        var preset = presets[value];
        ApplyConditions(preset.Conditions);
        PresetName = preset.Name;
        PresetMessage = $"已套用“{preset.Name}”。";
    }

    private SearchConditions CaptureConditions() => new()
    {
        QueryText = QueryText, ScopeIndex = ScopeIndex, Folder = Folder, IncludeSubfolders = IncludeSubfolders,
        MatchPath = MatchPath, KindIndex = KindIndex, SizeIndex = SizeIndex, CustomMinimum = CustomMinimum,
        CustomMaximum = CustomMaximum, SizeUnitIndex = SizeUnitIndex, SizeConvention = FilterUnits, DateIndex = DateIndex,
        DateFrom = CustomDateFrom is { } from ? DateOnly.FromDateTime(from.Date) : null,
        DateThrough = CustomDateThrough is { } through ? DateOnly.FromDateTime(through.Date) : null, SortIndex = SortIndex
    };

    private void ApplyConditions(SearchConditions conditions)
    {
        updatingConditions = true;
        QueryText = conditions.QueryText; ScopeIndex = conditions.ScopeIndex; Folder = conditions.Folder;
        IncludeSubfolders = conditions.IncludeSubfolders; MatchPath = conditions.MatchPath; KindIndex = conditions.KindIndex;
        SizeIndex = conditions.SizeIndex; CustomMinimum = conditions.CustomMinimum; CustomMaximum = conditions.CustomMaximum;
        SizeUnitIndex = conditions.SizeUnitIndex; DateIndex = conditions.DateIndex;
        if (conditions.SizeIndex > 0 && SizeFactor(1, conditions.SizeConvention) != SizeFactor(1, FilterUnits))
        {
            // Preserve the saved byte interval, including the exclusive upper edge of a predefined range.
            var bounds = PresetSize(conditions.SizeIndex, conditions.SizeConvention);
            if (conditions.SizeIndex == 6)
            {
                TrySize(conditions.CustomMinimum, conditions.SizeUnitIndex, conditions.SizeConvention, true, out bounds.Min);
                TrySize(conditions.CustomMaximum, conditions.SizeUnitIndex, conditions.SizeConvention, false, out bounds.Max);
            }
            SizeIndex = 6; SizeUnitIndex = 0;
            CustomMinimum = bounds.Min?.ToString() ?? ""; CustomMaximum = bounds.Max?.ToString() ?? "";
        }
        CustomDateFrom = conditions.DateFrom is { } from ? LocalMidnight(from.ToDateTime(TimeOnly.MinValue)) : null;
        CustomDateThrough = conditions.DateThrough is { } through ? LocalMidnight(through.ToDateTime(TimeOnly.MinValue)) : null;
        SortIndex = conditions.SortIndex;
        updatingConditions = false;
        OnPropertyChanged(nameof(FilterError)); OnPropertyChanged(nameof(HasFilterError));
        NotifyResultActions();
        if (active && !composing) _ = RunAsync(false, true);
    }

    [RelayCommand] private async Task SavePresetAsync()
    {
        var name = PresetName.Trim();
        if (name.Length is 0 or > 60) { PresetMessage = "请输入 1–60 个字符的条件名称。"; return; }
        if (presets.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        { PresetMessage = "已有同名条件，请换一个名称，或先移除原条件。"; return; }
        if (!HasSearchSource) { PresetMessage = "请先填写搜索文件夹路径。"; return; }
        if (Request() is not { } request) { PresetMessage = FilterError; return; }
        try { _ = SearchRules.Compile(request); }
        catch (SearchException e) { PresetMessage = e.Message; return; }
        presets.Add(new(name, CaptureConditions()));
        RebuildPresetOptions();
        updatingConditions = true; PresetIndex = presets.Count - 1; updatingConditions = false;
        preferencesDirty = true;
        await SavePreferencesAsync();
        PresetMessage = HasPreferencesError ? "已保留在本次运行中，请重试保存。" : $"已保存“{name}”。";
    }

    [RelayCommand] private async Task RemovePresetAsync()
    {
        if (!CanRemovePreset) return;
        var name = presets[PresetIndex].Name;
        presets.RemoveAt(PresetIndex);
        RebuildPresetOptions();
        preferencesDirty = true;
        await SavePreferencesAsync();
        PresetMessage = HasPreferencesError ? "已从本次列表移除，请重试保存。" : $"已移除“{name}”，当前搜索不变。";
    }

    private void RebuildPresetOptions()
    {
        PresetIndex = -1;
        PresetOptions.Clear();
        PresetOptions.Add(new(-1, "选择常用条件"));
        for (var index = 0; index < presets.Count; index++) PresetOptions.Add(new(index, presets[index].Name));
        OnPropertyChanged(nameof(CanRemovePreset));
    }

    public async Task LoadPreferencesAsync()
    {
        if (settings is null) return;
        loadingPreferences = true;
        try
        {
            var json = await settings.ReadAsync();
            if (json is null) return;
            var preferences = JsonSerializer.Deserialize<SearchPreferences>(json);
            if (preferences is null || preferences.Version != 1 || preferences.SortIndex is < 0 or > 7) throw new JsonException();
            ValidatePresets(preferences.Presets);
            SortIndex = preferences.SortIndex;
            ShowDetails = preferences.ShowDetails;
            LoadIndexOnDemand = preferences.LoadIndexOnDemand;
            if (preferences.Columns is { } columns) ColumnWidths.Restore(columns);
            presets.Clear(); presets.AddRange(preferences.Presets);
            RebuildPresetOptions();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        { PreferencesError = "无法读取已保存的搜索设置，本次使用默认设置。修改后可重新保存。"; }
        finally { loadingPreferences = false; }
    }

    private void QueuePreferencesSave()
    {
        if (settings is null || loadingPreferences) return;
        preferencesDirty = true;
        StartPreferencesSave(debounce: true);
    }

    [RelayCommand] private Task SavePreferencesAsync()
    {
        preferencesDirty = true;
        return StartPreferencesSave(debounce: false);
    }

    public Task FlushPreferencesAsync() => preferencesDirty ? StartPreferencesSave(debounce: false) : pendingSave;

    private Task StartPreferencesSave(bool debounce)
    {
        if (settings is null) return Task.CompletedTask;
        saveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        saveCancellation = cancellation;
        var version = ++preferencesVersion;
        return pendingSave = WritePreferencesAsync(debounce, cancellation, version);
    }

    private async Task WritePreferencesAsync(bool debounce, CancellationTokenSource cancellation, int version)
    {
        using (cancellation)
        {
            try
            {
                if (debounce) await Task.Delay(400, cancellation.Token);
                await saveGate.WaitAsync(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var document = CapturePreferences(presets);
                    await settings!.WriteAsync(JsonSerializer.Serialize(document));
                    if (version == preferencesVersion) { preferencesDirty = false; PreferencesError = string.Empty; }
                }
                finally { saveGate.Release(); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { if (version == preferencesVersion) PreferencesError = "无法保存搜索设置；本次操作仍可继续，请重试保存。"; }
            finally { if (version == preferencesVersion) saveCancellation = null; }
        }
    }
}
