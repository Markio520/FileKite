using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed partial class DirectoryDraft : ObservableObject
{
    public DirectoryDraft(string name, ObservableCollection<DirectoryDraft>? children = null)
    {
        Name = name; Children = children ?? [];
        foreach (var child in Children.Where(x => x.IsDirectory)) FolderChildren.Add(child);
        Children.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) { FolderChildren.Clear(); return; }
            if (e.OldItems is not null) foreach (DirectoryDraft child in e.OldItems) FolderChildren.Remove(child);
            if (e.NewItems is null) return;
            var index = e.NewStartingIndex == Children.Count - e.NewItems.Count ? FolderChildren.Count
                : Children.Take(Math.Max(0, e.NewStartingIndex)).Count(x => x.IsDirectory);
            foreach (DirectoryDraft child in e.NewItems) if (child.IsDirectory) FolderChildren.Insert(index++, child);
        };
    }
    public Guid Id { get; } = Guid.NewGuid();
    public bool IsDestination { get; init; }
    public bool IsDirectory { get; init; } = true;
    public bool IsLink { get; set; }
    public bool HasLoadedContents { get; set; }
    public string TemplatePath { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NavigationStatus)), NotifyPropertyChangedFor(nameof(CanDragDraft))] public partial bool IsExisting { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NavigationStatus)), NotifyPropertyChangedFor(nameof(CanDragDraft))] public partial bool IsMissing { get; set; }
    public bool CanDragDraft => !IsDestination && !IsExisting && !IsMissing;
    [ObservableProperty] public partial bool ShowTargetCheckBox { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial bool IsTarget { get; set; }
    [ObservableProperty] public partial string DisplayPath { get; set; } = "";
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NavigationStatus))] public partial string Preview { get; set; } = "";
    public string NavigationStatus => IsMissing ? "已删除" : IsExisting ? "" : Preview;
    public ObservableCollection<DirectoryDraft> Children { get; }
    public ObservableCollection<DirectoryDraft> FolderChildren { get; } = [];
}
