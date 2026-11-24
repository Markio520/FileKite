using CommunityToolkit.Mvvm.ComponentModel;

namespace FileAssistant.App.Features.Workspace;

public sealed partial class FileNamePreview : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsVisible)), NotifyPropertyChangedFor(nameof(ShowDirectory)), NotifyPropertyChangedFor(nameof(ShowPlainPreview)), NotifyPropertyChangedFor(nameof(ShowPlainSuccess)), NotifyPropertyChangedFor(nameof(ShowPlainError))]
    public partial string Text { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";
    [ObservableProperty]
    public partial string Details { get; set; } = "";
    [ObservableProperty]
    public partial bool CanEditName { get; set; }
    [ObservableProperty]
    public partial string CustomName { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLeadUnchanged))]
    public partial string LeadUnchanged { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowRemovedMiddle))]
    public partial string RemovedMiddle { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAddedMiddle)), NotifyPropertyChangedFor(nameof(ShowRemovedMiddle))]
    public partial string AddedMiddle { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTrailUnchanged))]
    public partial string TrailUnchanged { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPlainPreview)), NotifyPropertyChangedFor(nameof(ShowPlainSuccess)), NotifyPropertyChangedFor(nameof(ShowPlainError)), NotifyPropertyChangedFor(nameof(HasAddedMiddle)), NotifyPropertyChangedFor(nameof(ShowRemovedMiddle))]
    public partial bool HasStructuredDiff { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPlainSuccess)), NotifyPropertyChangedFor(nameof(ShowPlainError))]
    public partial bool IsError { get; set; }

    public Action<string>? CustomNameEdited { get; set; }
    partial void OnCustomNameChanged(string value) => CustomNameEdited?.Invoke(value);
    public bool IsVisible => Text.Length > 0;
    public bool ShowDirectory => !IsVisible;
    public bool HasStatus => Status.Length > 0;
    public bool ShowPlainPreview => IsVisible && !HasStructuredDiff;
    public bool ShowPlainSuccess => ShowPlainPreview && !IsError;
    public bool ShowPlainError => ShowPlainPreview && IsError;
    public bool HasLeadUnchanged => LeadUnchanged.Length > 0;
    public bool HasAddedMiddle => HasStructuredDiff && AddedMiddle.Length > 0;
    public bool ShowRemovedMiddle => HasStructuredDiff && RemovedMiddle.Length > 0 && AddedMiddle.Length == 0 && RemovedMiddle.Length <= 24;
    public bool HasTrailUnchanged => TrailUnchanged.Length > 0;

    public void ApplyPreview(string displayText, string status = "", string details = "", bool isError = false)
    {
        ClearDiff();
        IsError = isError;
        Text = displayText;
        Status = status;
        Details = details;
    }

    public void ApplyDiffPreview(string originalName, string displayText, string status = "", string details = "", bool isError = false)
    {
        // Central file list always displays the full target name in green (WorkspaceSuccessBrush) without partial diff pills.
        _ = originalName;
        ApplyPreview(displayText, status, details, isError);
    }

    private void ClearDiff()
    {
        LeadUnchanged = "";
        RemovedMiddle = "";
        AddedMiddle = "";
        TrailUnchanged = "";
        HasStructuredDiff = false;
    }
}

/// <summary>Display-only information, separate from future Core file-operation models.</summary>
public sealed record WorkspaceFile(string Name, string Type, long Size, string Directory, string Modified, string Glyph, int Category,
    FileAssistant.Core.Features.Search.SearchFile? Source = null, Guid? EntryId = null, Guid? FolderId = null, string? ReferenceError = null) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshDisplay() => PropertyChanged?.Invoke(this, new(string.Empty));
    public FileNamePreview NamePreview { get; } = new();
    public static WorkspaceFile FromSource(FileAssistant.Core.Features.Search.SearchFile file)
    {
        var item = new Search.SearchResultItem(file);
        return new(file.Name, item.TypeLabel, file.Size ?? -1, file.Directory, item.ModifiedLabel, item.Glyph, item.FileCategory, file);
    }
    internal Guid OwnerFolderId { get; init; }
    public bool IsSample => Source is null && FolderId is null;
    public bool IsCollectionFolder => FolderId is not null;
    public int IconTone => IsCollectionFolder || Source?.IsFolder == true ? 1 : Category switch
    {
        1 => 2, // Images
        2 => 3, // Documents
        3 => 5, // Archives
        4 or 5 => 4, // Video / Audio
        _ => 0
    };
    public string LocationBadge => IsCollectionFolder ? Directory : FormatLocationBadge(Directory);
    public string SizeLabel => Source?.IsFolder == true ? "—" : Size < 0 ? "未索引" : FormatSize(Size);
    public string DisplayName => FileAssistant.Core.Settings.FileDisplay.Name(Name, IsCollectionFolder || Source?.IsFolder == true);
    public string PickerTypeLabel => IsCollectionFolder ? "软件文件夹" : Type;
    public string PickerSizeOrCountLabel => IsCollectionFolder && Type.IndexOf('·') is var sep and >= 0
        ? Type[(sep + 1)..].Trim()
        : SizeLabel;
    public string FullPath => Source?.FullPath ?? System.IO.Path.Combine(Directory, Name);

    public override string ToString() => $"{Name}，{Type}，{SizeLabel}";

    public static string FormatLocationBadge(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "—";
        var trimmed = directory.TrimEnd('\\', '/');
        var parts = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return directory;
        if (parts.Length == 1) return parts[0];
        if (parts.Length == 2) return $"{parts[0]} / {parts[1]}";
        return $"{parts[0]} / … / {parts[^1]}";
    }

    public static string FormatSize(long size) => FileAssistant.Core.Settings.FileDisplay.Size(size);
}
