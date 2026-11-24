using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Core.Features.CreateOrRename;

public static partial class NamingPlanner
{
    public static NamingPlan Rename(NamingRequest request, IReadOnlyList<NamingSource> sources, Func<string, bool> exists)
    {
        var ordered = request.Rules.Order switch
        {
            NamingOrder.Name => sources.OrderBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            NamingOrder.Modified => sources.OrderBy(x => x.Stamp?.ModifiedUtcTicks).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => sources.ToArray()
        };
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var customNames = request.CustomNames?.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var items = new List<NamingPlanItem>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var source = ordered[index]; var target = source.Path;
            try
            {
                if (source.Error is not null) throw new ArgumentException(source.Error);
                var parent = Path.GetDirectoryName(source.Path) ?? throw new ArgumentException("不能修改磁盘或共享根目录名称。");
                var key = request.Rules.NumberPerDirectory ? parent : "";
                var sequence = counters.GetValueOrDefault(key); counters[key] = sequence + 1;
                var stem = source.IsDirectory ? Path.GetFileName(source.Path) : Path.GetFileNameWithoutExtension(source.Path);
                var date = source.Stamp is { } stamp ? new DateTimeOffset(new DateTime(stamp.ModifiedUtcTicks, DateTimeKind.Utc)).ToLocalTime() : DateTimeOffset.Now;
                var newStem = customNames is not null
                    ? customNames.GetValueOrDefault(source.Path, stem)
                    : NamingRulesEngine.Generate(stem, Path.GetFileName(parent), date, sequence, request.Rules);
                if (customNames is not null) NamingRulesEngine.ValidateName(newStem);
                var name = newStem + (source.IsDirectory ? "" : Path.GetExtension(source.Path));
                NamingRulesEngine.ValidateName(name); target = Path.Combine(parent, name);
                items.Add(new(Guid.NewGuid(), source, target, source.IsDirectory,
                    source.Path == target ? NamingAction.Skipped : NamingAction.Ready, source.Path == target ? "名称未变化。" : null));
            }
            catch (Exception e) when (e is ArgumentException or OverflowException)
            { items.Add(new(Guid.NewGuid(), source, target, source.IsDirectory, NamingAction.Invalid, e.Message)); }
        }
        return ResolveRename(request, items, exists);
    }

    public static NamingPlan ResolveRename(NamingRequest request, IReadOnlyList<NamingPlanItem> planned, Func<string, bool> exists)
    {
        var items = planned.ToList();
        ResolveConflicts(items, request.Conflict, exists);
        // Compute final paths once from the original hierarchy (do not accidentally cascade A↔B swaps).
        var directories = items.Where(x => x.CanRun && x.IsDirectory).OrderByDescending(x => x.Source!.Path.Length).ToArray();
        string Final(NamingPlanItem item)
        {
            var path = item.Target;
            foreach (var ancestor in directories.Where(x => x.Id != item.Id && CopyPlanner.Within(item.Source!.Path, x.Source!.Path)))
                path = ancestor.Target + path[ancestor.Source!.Path.Length..];
            return path;
        }
        return new(request, items.Select(x => x with { FinalTarget = Final(x) }).ToArray());
    }

    private static void ResolveConflicts(List<NamingPlanItem> items, NamingConflict policy, Func<string, bool> exists)
    {
        // Removing an item can make another item's target occupied, so resolve to a fixed point.
        bool changed;
        do
        {
            changed = false;
            var moving = items.Where(x => x.CanRun).Select(x => x.Source!.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i]; if (!item.CanRun) continue;
                bool Occupied(string p) => reserved.Contains(p) || (exists(p) && !moving.Contains(p));
                if (Occupied(item.Target))
                {
                    if (policy == NamingConflict.Number)
                    {
                        try { item = item with { Target = CopyPlanner.Number(item.Target, item.IsDirectory, Occupied), Action = NamingAction.Numbered }; }
                        catch (Exception e) when (e is ArgumentException or IOException) { item = item with { Action = NamingAction.Invalid, Message = e.Message }; changed = true; }
                    }
                    else { item = item with { Action = policy == NamingConflict.Skip ? NamingAction.Skipped : NamingAction.Invalid, Message = "目标名称已存在或本批次重名。" }; changed = true; }
                    items[i] = item;
                }
                if (item.CanRun) reserved.Add(item.Target);
            }
        } while (changed);
    }

    public static NamingPlan Create(NamingRequest request, Func<string, bool> exists, NamingSource? template, Func<string, bool>? directoryExists = null)
    {
        if (request.Entries is not null || request.DirectoryPaths is not null) return CreateDirectoryTree(request, exists, directoryExists);
        var destination = CopyPlanner.Normalize(request.Destination);
        if (request.Count is < 1 or > 100000) throw new ArgumentException("每批创建数量为 1–100000。");
        var names = request.Names.Replace("\r", "").Split('\n').Where(x => x.Length > 0).ToArray();
        if (names.Length > 100000) throw new ArgumentException("名称清单不能超过 100000 行。");
        var count = names.Length > 0 ? names.Length : request.Count;
        var items = new List<NamingPlanItem>(); var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extension = request.Kind switch { CreationKind.Text => ".txt", CreationKind.Markdown => ".md", CreationKind.Template => Path.GetExtension(request.Template), _ => "" };
        var children = request.Subdirectories.Replace("\r", "").Split('\n').Where(x => x.Length > 0).ToArray();
        for (var index = 0; index < count; index++)
        {
            var target = destination;
            try
            {
                var name = NamingRulesEngine.Generate(names.Length > 0 ? names[index] : "新建", Path.GetFileName(destination), DateTimeOffset.Now, index, request.Rules) + extension;
                NamingRulesEngine.ValidateName(name); target = Path.Combine(destination, name);
                var directory = request.Kind == CreationKind.Directory;
                var action = NamingAction.Ready;
                bool Occupied(string p) => exists(p) || reserved.Contains(p);
                if (Occupied(target))
                {
                    if (request.Conflict == NamingConflict.Number) { target = CopyPlanner.Number(target, directory, Occupied); action = NamingAction.Numbered; }
                    else action = request.Conflict == NamingConflict.Skip ? NamingAction.Skipped : NamingAction.Invalid;
                }
                var item = new NamingPlanItem(Guid.NewGuid(), null, target, directory, action,
                    action is NamingAction.Skipped or NamingAction.Invalid ? "目标名称已存在或清单重名。" : null);
                var childTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (directory && item.CanRun)
                    foreach (var child in children)
                    {
                        var parts = child.Replace('/', '\\').Split('\\');
                        var childPath = target;
                        foreach (var part in parts) { NamingRulesEngine.ValidateName(part); childPath = Path.Combine(childPath, part); childTargets.Add(childPath); }
                    }
                items.Add(item);
                if (!item.CanRun) continue;
                reserved.Add(target);
                foreach (var child in childTargets.OrderBy(x => x.Length)) { reserved.Add(child); items.Add(new(Guid.NewGuid(), null, child, true)); }
            }
            catch (Exception e) when (e is ArgumentException or OverflowException or System.IO.IOException)
            { items.Add(new(Guid.NewGuid(), null, target, request.Kind == CreationKind.Directory, NamingAction.Invalid, e.Message)); }
        }
        return new(request with { Destination = destination }, items.Select(x => x with { RelativeTarget = Path.GetRelativePath(destination, x.Target) }).ToArray(), template);
    }
}
