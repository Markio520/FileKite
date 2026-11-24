using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    private CancellationTokenSource? targetSelectionCancellation;
    [ObservableProperty] public partial bool IsSelectingTargets { get; set; }
    [ObservableProperty] public partial bool IsChoosingCreationTargets { get; set; }
    public bool CanSelectCreationTargets => IsCreate && CanEdit && !IsBusy && !IsSelectingTargets;
    [RelayCommand(CanExecute = nameof(CanSelectCreationTargets))]
    private void ChooseCreationTargets() => IsChoosingCreationTargets = true;
    [RelayCommand(CanExecute = nameof(CanSelectCreationTargets))]
    private Task SelectDirectChildrenAsync() => SelectDescendantsAsync(false);
    [RelayCommand(CanExecute = nameof(CanSelectCreationTargets))]
    private Task SelectLeafFoldersAsync() => SelectDescendantsAsync(true);
    [RelayCommand(CanExecute = nameof(CanSelectCreationTargets))]
    private void UseCurrentCreationFolder() { IsChoosingCreationTargets = false; SelectCreationParents([CurrentCreationDirectory], browse: false); }
    [RelayCommand] private void CancelTargetSelection() => targetSelectionCancellation?.Cancel();
    private async Task SelectDescendantsAsync(bool leaves)
    {
        if (!CanSelectCreationTargets) return;
        contentsCancellation?.Cancel(); contentsRevision++; IsReadingCreationContents = false;
        using var current = new CancellationTokenSource(); targetSelectionCancellation = current;
        var selected = new List<DirectoryDraft>(); var seen = 0; var version = revision;
        var previousPath = CurrentCreationPath; var changed = false;
        IsSelectingTargets = true; Feedback = "正在读取子文件夹…";
        async Task Walk(DirectoryDraft parent, string path, bool includeSelf, int depth)
        {
            current.Token.ThrowIfCancellationRequested();
            if (parent.IsMissing) throw new IOException("此目录已删除，请刷新或选择其他位置。");
            if (parent.IsLink) throw new IOException("请进入实际目录后再批量选择，链接目录不会递归展开。");
            if (++seen > 100000 || depth > 128) throw new IOException("目录数量或层级过多，请进入较小的范围再选择。");
            if (parent.IsExisting && directoryReader is not null)
            {
                try
                {
                    var contents = await directoryReader.ReadAsync(path, current.Token);
                    current.Token.ThrowIfCancellationRequested();
                    if (version != revision) throw new OperationCanceledException();
                    if (ReferenceEquals(parent, CurrentCreationDirectory)) { diskContents = contents; CreationContentsNotice = ""; }
                    changed |= SyncExistingDirectories(parent, contents);
                }
                catch (DirectoryNotFoundException)
                {
                    current.Token.ThrowIfCancellationRequested();
                    if (version != revision) throw new OperationCanceledException();
                    MarkCreationDirectoryMissing(parent); changed = true;
                    if (ReferenceEquals(parent, CurrentCreationDirectory)) diskContents = [];
                    return;
                }
            }
            var folders = parent.FolderChildren.Where(x => !x.IsLink && !x.IsMissing).ToArray();
            if (!leaves) selected.AddRange(folders);
            else if (folders.Length == 0 && includeSelf) selected.Add(parent);
            else foreach (var child in folders) await Walk(child, Path.Combine(path, child.Name), true, depth + 1);
        }
        try
        {
            await Walk(CurrentCreationDirectory, CurrentCreationPath, false, 0);
            if (selected.Count > 100000) throw new IOException("目录数量过多，请缩小范围。");
            IsSelectingTargets = false;
            if (selected.Count > 0) { IsChoosingCreationTargets = true; SelectCreationParents(selected, browse: false); }
            Feedback = selected.Count == 0 ? "此处没有可选的子文件夹。" : $"已选中 {selected.Count} 个文件夹；右侧填写的内容将添加到每一处。";
        }
        catch (OperationCanceledException) { if (version == revision) Feedback = "已取消选择，原有效勾选保留。"; }
        catch (Exception error) { if (version == revision) Feedback = (changed ? "选择未完成，已清理失效位置；请检查访问权限：" : "未更改勾选，请缩小范围或检查访问权限：") + error.Message; }
        finally
        {
            targetSelectionCancellation = null; IsSelectingTargets = false;
            if (version == revision && changed)
            {
                ReconcileCreationNavigation(previousPath);
                InvalidateCreationPlan();
            }
            RefreshCreationContents(); NotifyCreationEditor();
        }
    }
    private void NotifyCreationTargets()
    {
        folderLocation.DisplayPath = Destination;
        folderLocation.ShowTargetCheckBox = IsChoosingCreationTargets;
        foreach (var (node, path) in DraftEntries()) { node.DisplayPath = path; node.ShowTargetCheckBox = IsChoosingCreationTargets; }
        foreach (var name in new[] { nameof(CreationParents), nameof(CanSelectCreationTargets) }) OnPropertyChanged(name);
        ChooseCreationTargetsCommand.NotifyCanExecuteChanged(); SelectDirectChildrenCommand.NotifyCanExecuteChanged(); SelectLeafFoldersCommand.NotifyCanExecuteChanged(); UseCurrentCreationFolderCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsSelectingTargetsChanged(bool value) => Notify();
    partial void OnIsChoosingCreationTargetsChanged(bool value) { if (folderLocation is not null) NotifyCreationTargets(); }
}
