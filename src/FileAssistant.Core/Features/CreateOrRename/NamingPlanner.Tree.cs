using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Core.Features.CreateOrRename;

public static partial class NamingPlanner
{
    private static NamingPlan CreateDirectoryTree(NamingRequest request, Func<string, bool> exists, Func<string, bool>? directoryExists)
    {
        var destination = CopyPlanner.Normalize(request.Destination);
        var paths = new Dictionary<string, CreationEntry>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<NamingPlanItem>();
        foreach (var entry in request.Entries ?? request.DirectoryPaths!.Select(x => new CreationEntry(x)).ToArray())
        {
            var input = entry.RelativePath;
            try
            {
                var parts = input.Replace('/', '\\').Split('\\');
                foreach (var part in parts) NamingRulesEngine.ValidateName(part);
                var relative = string.Join(Path.DirectorySeparatorChar, parts);
                if (!explicitPaths.Add(relative)) throw new ArgumentException("同一层级存在重名项目：" + relative);
                var parent = "";
                for (var index = 0; index < parts.Length; index++)
                {
                    var part = parts[index];
                    parent = parent.Length == 0 ? part : Path.Combine(parent, part);
                    var current = index == parts.Length - 1 ? entry with { RelativePath = relative } : new CreationEntry(parent);
                    if (paths.TryGetValue(parent, out var previous) && previous.IsDirectory != current.IsDirectory)
                        throw new ArgumentException("文件不能同时用作文件夹：" + parent);
                    paths[parent] = current;
                    if (paths.Count > 100000) throw new InvalidOperationException("每批最多创建 100000 项。");
                }
            }
            catch (ArgumentException error)
            {
                items.Add(new(Guid.NewGuid(), null, destination, entry.IsDirectory, NamingAction.Invalid,
                    error.Message, RelativeTarget: input));
            }
        }
        var mapped = new Dictionary<string, NamingPlanItem>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, entry) in paths.OrderBy(x => x.Key.Count(c => c == Path.DirectorySeparatorChar)))
        {
            var parent = Path.GetDirectoryName(relative);
            var ancestor = !string.IsNullOrEmpty(parent) ? mapped[parent] : null;
            var target = Path.Combine(ancestor?.Target ?? destination, Path.GetFileName(relative));
            var action = NamingAction.Ready;
            string? message = null;
            if (ancestor is { CanRun: false, Action: not NamingAction.Existing })
            {
                action = ancestor.Action == NamingAction.Skipped ? NamingAction.Skipped : NamingAction.Invalid;
                message = "上级文件夹未通过检查，此子文件夹不会创建。";
            }
            else if (entry.IsDirectory && request.ReuseExistingDirectories && directoryExists?.Invoke(target) == true)
            {
                action = NamingAction.Existing;
                message = "沿用已有文件夹，保留其中内容。";
            }
            else if (exists(target) || reserved.Contains(target))
            {
                if (request.Conflict == NamingConflict.Number)
                {
                    target = CopyPlanner.Number(target, entry.IsDirectory, p => exists(p) || reserved.Contains(p));
                    action = NamingAction.Numbered;
                }
                else
                {
                    action = request.Conflict == NamingConflict.Skip ? NamingAction.Skipped : NamingAction.Invalid;
                    message = "目标名称已存在。";
                }
            }
            var item = new NamingPlanItem(Guid.NewGuid(), null, target, entry.IsDirectory, action, message,
                RelativeTarget: Path.GetRelativePath(destination, target), RequestedRelativePath: relative,
                TemplatePath: entry.Template.Length > 0 ? entry.Template : null);
            mapped.Add(relative, item); items.Add(item);
            reserved.Add(target);
        }
        return new(request with { Destination = destination }, items);
    }
}
