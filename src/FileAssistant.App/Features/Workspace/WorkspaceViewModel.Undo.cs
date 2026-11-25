using CommunityToolkit.Mvvm.Input;
using FileAssistant.Core.Features.FileCollections;
using FileAssistant.Core.Features.FileInput;

namespace FileAssistant.App.Features.Workspace;

public partial class WorkspaceViewModel
{
    private FileCollectionSnapshot? undoSnapshot;
    private string undoAction = string.Empty;
    public bool HasUndo => undoSnapshot is not null;
    public bool CanUndo => HasUndo && !IsLoading;
    public string UndoMessage => HasUndo ? $"已{undoAction}" : string.Empty;

    private void RememberChange(FileCollectionSnapshot before, string action)
    {
        if (before.Folders.SequenceEqual(collection.Folders) && before.Entries.SequenceEqual(collection.Entries)) return;
        undoSnapshot = before; undoAction = action;
        NotifyUndoState();
    }
    private void NotifyUndoState()
    {
        OnPropertyChanged(nameof(HasUndo)); OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(UndoMessage));
        OnPropertyChanged(nameof(HasFooterMessage));
        UndoCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!CanUndo || undoSnapshot is not { } before) return;
        var action = undoAction;
        var restored = FileCollection.Restore(before);
        SearchText = string.Empty; FilterIndex = 0;
        collection = restored;
        CurrentFolderId = collection.HasFolder(before.CurrentFolderId) ? before.CurrentFolderId : Guid.Empty;
        undoSnapshot = null; undoAction = string.Empty;
        sampleFiles.Clear(); HasError = false;
        ImportReport = FileImportReport.Empty;
        CollectionChanged(); NotifyUndoState();
        InputFeedback = $"已撤销{action}。原文件位置不变。";
    }
}
