using FileAssistant.Core.Abstractions;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Search;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    private readonly IFileInteraction? fileInteraction;
    public event EventHandler? CreationEditorRequested;
    public event EventHandler<IReadOnlyList<SearchFile>>? CreationCopyRequested;
    public event EventHandler<IReadOnlyList<SearchFile>>? CreationConversionRequested;
    public CreationContentRow? CreationContext(DirectoryDraft node)
    {
        var relative = node.IsDestination ? "" : DraftEntries().FirstOrDefault(x => ReferenceEquals(x.Node, node)).Path;
        if (relative is null) return null;
        var path = directoryItems.TryGetValue(node, out var item) ? item.DisplayPath : FullCreationPath(Destination, relative);
        return new(node.Name, path, node.IsDirectory, node.Preview, path, node.IsExisting && !node.IsMissing, node);
    }
    public void PrepareCreationAt(DirectoryDraft? directory, bool file)
    {
        if (!CanEdit) return;
        var target = directory is { IsDirectory: true } ? directory : CurrentCreationDirectory;
        SelectCreationParents([target]); KindValue = file ? 1 : 0; CreateMethod = 3; PanelValue = 0; EditingResults = true;
        CreationEditorRequested?.Invoke(this, EventArgs.Empty);
    }
    public void RenameCreationDraft(DirectoryDraft node, string name)
    {
        if (!CanEdit || node.IsExisting || node.IsDestination || node.IsMissing || Siblings(node) is not { } siblings) return;
        if (!node.IsDirectory)
        {
            var extension = Path.GetExtension(node.Name);
            if (Path.GetExtension(name).Length == 0) name += extension;
            if (!string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("更改文件类型请在右侧重新添加；此处保留原扩展名。");
        }
        NamingRulesEngine.ValidateName(name);
        if (siblings.Any(x => !ReferenceEquals(x, node) && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("同一位置已有这个名称，请换一个名称。");
        node.Name = name;
    }
    public void RemoveCreationDraft(DirectoryDraft node)
    {
        if (!CanEdit || node.IsExisting || node.IsDestination || Siblings(node) is not { } siblings) return;
        var entries = DraftEntries().ToArray();
        var parent = entries.FirstOrDefault(x => x.Node.Children.Contains(node)).Node ?? folderLocation;
        var path = entries.First(x => ReferenceEquals(x.Node, node)).Path;
        var current = entries.FirstOrDefault(x => ReferenceEquals(x.Node, CurrentCreationDirectory)).Path;
        siblings.Remove(node);
        if (current == path || current?.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) == true) SetCreationBrowser(parent);
        SelectCreationParents(creationParents.ToArray(), browse: false); Invalidate();
    }
    public void OpenCreationItem(CreationContentRow row, bool reveal = false)
    {
        if (!CanEdit) return;
        if (!reveal && row is { IsDirectory: true, Draft: { } folder }) { BrowseCreationDirectory(folder); return; }
        if (!row.IsExisting || row.Path.Length == 0) return;
        try
        {
            var file = new SearchFile(row.Path, row.IsDirectory, null, null);
            if (reveal) fileInteraction?.Reveal(file); else fileInteraction?.Open(file);
        }
        catch (FileInteractionException error) { Feedback = error.Message; }
    }
    public void CopyCreationText(CreationContentRow row, bool nameOnly = false)
    {
        if (!CanEdit || row.Path.Length == 0) return;
        try { fileInteraction?.CopyText(nameOnly ? row.Name : row.Path); }
        catch (FileInteractionException error) { Feedback = error.Message; }
    }
    public void TransferCreationItem(CreationContentRow row, bool conversion)
    {
        if (!CanEdit || !row.IsExisting || row.Path.Length == 0) return;
        var handler = conversion ? CreationConversionRequested : CreationCopyRequested;
        handler?.Invoke(this, [new(row.Path, row.IsDirectory, null, null)]);
    }
}
