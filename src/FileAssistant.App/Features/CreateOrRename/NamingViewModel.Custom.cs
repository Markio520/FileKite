using FileAssistant.App.Features.Workspace;

namespace FileAssistant.App.Features.CreateOrRename;

public partial class NamingViewModel
{
    private readonly Dictionary<string, string> customNames = new(StringComparer.OrdinalIgnoreCase);
    public bool Manual => IsRename && RenameAction == 7;
    public int CustomNameCount => Input.SearchEntries.Count(x => customNames.ContainsKey(x.FullPath));
    public string CustomNameSummary => $"已填写 {CustomNameCount:N0} 项 · 未修改的文件保持原名";

    // A full source path identifies an edit, including across filtering and software groups.
    // Only explicitly edited inputs participate; clicking an editor must not narrow the batch.
    public void SetCustomName(string path, string name)
    {
        if (!Manual || !CanEdit) return;
        var source = Input.SearchEntries.FirstOrDefault(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;
        var original = source.IsFolder ? source.Name : Path.GetFileNameWithoutExtension(source.Name);
        if (name == original) { if (!customNames.Remove(source.FullPath)) return; }
        else { if (customNames.TryGetValue(source.FullPath, out var previous) && previous == name) return; customNames[source.FullPath] = name; }
        Invalidate();
    }

    private Dictionary<string, string> CustomNamesForRequest() => Input.SearchEntries
        .Where(x => customNames.ContainsKey(x.FullPath)).Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase)
        .ToDictionary(path => path, path => customNames[path], StringComparer.OrdinalIgnoreCase);

    private void UpdateCustomEditor(WorkspaceFile file)
    {
        var editor = file.NamePreview;
        editor.CustomNameEdited = null;
        editor.CanEditName = Manual && CanEdit && (!ShowResults || EditingResults) && file.Source is not null && !file.IsCollectionFolder;
        var original = file.Source?.IsFolder == true ? file.Name : Path.GetFileNameWithoutExtension(file.Name);
        editor.CustomName = customNames.GetValueOrDefault(file.FullPath, original);
        if (editor.CanEditName) editor.CustomNameEdited = value => SetCustomName(file.FullPath, value);
    }

    private void PruneCustomNames()
    {
        var paths = Input.SearchEntries.Select(x => x.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in customNames.Keys.Where(path => !paths.Contains(path)).ToArray()) customNames.Remove(path);
    }
}
