using FileAssistant.Core.Features.CreateOrRename;

namespace FileAssistant.App.Features.CreateOrRename;

public sealed record OutlineDraftLine(int Indent, string Name, bool? ForceIsFile = null);

public partial class NamingViewModel
{
    private bool syncingOutline;

    public event EventHandler? OutlineReloadRequested;

    public IReadOnlyList<CreationContentRow> ExistingCreationContents => CreationContents.Where(x => x.IsExisting).ToArray();
    public bool HasExistingCreationContents => ExistingCreationContents.Count > 0;
    public string ExistingCreationSummary => $"当前目录已有 {ExistingCreationContents.Count:N0} 项";
    public bool IsOutlineSyncing => syncingOutline;

    public static bool LooksLikeFileName(string rawName)
    {
        var leaf = rawName.Trim().Replace('/', '\\').Split('\\').LastOrDefault()?.Trim() ?? "";
        if (leaf.Length < 3) return false;
        var dot = leaf.LastIndexOf('.');
        if (dot <= 0 || dot >= leaf.Length - 1) return false;
        var ext = leaf[(dot + 1)..];
        if (ext.Length is < 1 or > 8) return false;
        foreach (var ch in ext)
            if (!char.IsLetterOrDigit(ch)) return false;
        return true;
    }

    public static IReadOnlyList<OutlineDraftLine> ParseOutlineText(string rawText, int baseIndent = 0)
    {
        var rawLines = rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var results = new List<OutlineDraftLine>();
        var spaceStep = 2;
        foreach (var raw in rawLines)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('\t')) continue;
            var spaces = 0;
            while (spaces < raw.Length && raw[spaces] == ' ') spaces++;
            if (spaces is >= 2 and <= 8) { spaceStep = spaces; break; }
        }

        var prevIndent = Math.Max(0, baseIndent);
        foreach (var raw in rawLines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var indent = 0;
            var i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '\t') { indent++; i++; }
                else if (raw[i] == ' ')
                {
                    var s = 0;
                    while (i < raw.Length && raw[i] == ' ') { s++; i++; }
                    indent += s / Math.Max(1, spaceStep);
                }
                else if (raw[i] is '│' or '├' or '└' or '─')
                {
                    // Support pasting ASCII/Unicode directory trees (e.g. from ChatGPT)
                    if (raw[i] == '│') indent++;
                    i++;
                }
                else break;
            }

            var cleaned = raw[i..].Trim();
            if (cleaned.StartsWith("- ", StringComparison.Ordinal) || cleaned.StartsWith("* ", StringComparison.Ordinal) || cleaned.StartsWith("• ", StringComparison.Ordinal))
                cleaned = cleaned[2..].Trim();
            if (cleaned.Length == 0) continue;

            var effectiveIndent = results.Count == 0
                ? Math.Max(0, baseIndent)
                : Math.Clamp(baseIndent + indent, 0, prevIndent + 1);

            var segments = cleaned.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) continue;

            for (var s = 0; s < segments.Length; s++)
            {
                var segIndent = effectiveIndent + s;
                var segName = segments[s];
                var isLast = s == segments.Length - 1;
                results.Add(new(segIndent, segName, isLast ? LooksLikeFileName(segName) : false));
                prevIndent = segIndent;
            }
        }
        return results;
    }

    public IReadOnlyList<OutlineDraftLine> ExportCurrentOutline()
    {
        var lines = new List<OutlineDraftLine>();
        if (folderLocation is null) return lines;
        void Walk(DirectoryDraft parent, int depth)
        {
            foreach (var child in parent.Children)
            {
                if (child.IsExisting || child.IsMissing) continue;
                lines.Add(new(depth, child.Name, !child.IsDirectory));
                if (child.IsDirectory && child.Children.Count > 0) Walk(child, depth + 1);
            }
        }
        Walk(CurrentCreationDirectory, 0);
        return lines;
    }

    private string ResolveOutlineFileTemplate(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (FileContentValue == 1 && !string.IsNullOrWhiteSpace(Template)
            && string.Equals(Path.GetExtension(Template), extension, StringComparison.OrdinalIgnoreCase))
            return Template.Trim();
        if (FileContentValue == 0 && extension is ".doc" or ".docx")
            return Path.Combine(AppContext.BaseDirectory, "Assets", "CreationTemplates", "blank" + extension);
        return "";
    }

    private sealed record DesiredDraftSpec(string Name, bool IsDirectory, string TemplatePath, List<DesiredDraftSpec> Children);

    private List<DesiredDraftSpec> BuildDesiredDraftTree(IReadOnlyList<(string[] Parts, bool IsDirectory)> items)
    {
        var roots = new List<DesiredDraftSpec>();
        var totalNodes = 0;
        foreach (var (parts, leafIsDir) in items)
        {
            var level = roots;
            for (var p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                var isDir = p < parts.Length - 1 || leafIsDir;
                var existing = level.FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    if (++totalNodes > 100000) throw new ArgumentException("最多安排 100000 个待创建项目。");
                    var template = isDir ? "" : ResolveOutlineFileTemplate(part);
                    existing = new DesiredDraftSpec(part, isDir, template, []);
                    level.Add(existing);
                }
                level = existing.Children;
            }
        }
        return roots;
    }

    private void ReconcileDraftChildren(DirectoryDraft parent, IReadOnlyList<DesiredDraftSpec> desired)
    {
        var existingDiskByName = parent.Children
            .Where(x => (x.IsExisting || x.IsMissing) && x.IsDirectory)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var oldDrafts = parent.Children.Where(x => !x.IsExisting && !x.IsMissing).ToList();
        var usedOld = new HashSet<DirectoryDraft>();

        // Separate specs that merge into an already-existing disk folder vs pure uncreated drafts
        var desiredDraftSpecs = new List<(DesiredDraftSpec Spec, DirectoryDraft? Matched)>(desired.Count);
        foreach (var spec in desired)
        {
            if (spec.IsDirectory && existingDiskByName.TryGetValue(spec.Name, out var diskFolder))
            {
                ReconcileDraftChildren(diskFolder, spec.Children);
                continue;
            }
            desiredDraftSpecs.Add((spec, null));
        }

        // Pass 1: Match by exact case-insensitive name + IsDirectory so unchanged folders keep their exact instance & expansion
        for (var i = 0; i < desiredDraftSpecs.Count; i++)
        {
            var spec = desiredDraftSpecs[i].Spec;
            var exact = oldDrafts.FirstOrDefault(x => !usedOld.Contains(x) && x.IsDirectory == spec.IsDirectory && x.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                usedOld.Add(exact);
                desiredDraftSpecs[i] = (spec, exact);
            }
        }

        // Pass 2: Positional match when editing/renaming in place (preserves DirectoryDraft.Id while typing!)
        if (oldDrafts.Count == desiredDraftSpecs.Count)
        {
            for (var i = 0; i < desiredDraftSpecs.Count; i++)
            {
                if (desiredDraftSpecs[i].Matched is not null) continue;
                var candidate = oldDrafts[i];
                if (!usedOld.Contains(candidate) && candidate.IsDirectory == desiredDraftSpecs[i].Spec.IsDirectory)
                {
                    usedOld.Add(candidate);
                    desiredDraftSpecs[i] = (desiredDraftSpecs[i].Spec, candidate);
                }
            }
        }

        // Pass 3: Update or create nodes, inserting/reordering BEFORE removing unused drafts so FolderChildren never drops to 0 mid-sync
        var orderedDraftNodes = new List<DirectoryDraft>(desiredDraftSpecs.Count);
        reconcilingNames = true;
        try
        {
            for (var i = 0; i < desiredDraftSpecs.Count; i++)
            {
                var (spec, node) = desiredDraftSpecs[i];
                if (node is null)
                {
                    node = MakeDraft(spec.Name, spec.IsDirectory, spec.TemplatePath);
                }
                else
                {
                    if (!string.Equals(node.Name, spec.Name, StringComparison.Ordinal))
                        node.Name = spec.Name;
                    node.TemplatePath = spec.TemplatePath;
                }
                orderedDraftNodes.Add(node);
            }
        }
        finally
        {
            reconcilingNames = false;
        }

        var fixedPrefixCount = parent.Children.Count(x => x.IsExisting || x.IsMissing);
        // Move any existing/missing disk entries to the front if needed, then place orderedDraftNodes in exact order
        var cursor = 0;
        foreach (var child in parent.Children.Where(x => x.IsExisting || x.IsMissing).ToArray())
        {
            var currentIdx = parent.Children.IndexOf(child);
            if (currentIdx != cursor) parent.Children.Move(currentIdx, cursor);
            cursor++;
        }

        for (var i = 0; i < orderedDraftNodes.Count; i++)
        {
            var desiredNode = orderedDraftNodes[i];
            var targetPos = fixedPrefixCount + i;
            var currentIdx = parent.Children.IndexOf(desiredNode);
            if (currentIdx < 0)
            {
                if (targetPos <= parent.Children.Count) parent.Children.Insert(targetPos, desiredNode);
                else parent.Children.Add(desiredNode);
            }
            else if (currentIdx != targetPos)
            {
                parent.Children.Move(currentIdx, targetPos);
            }
        }

        // Finally remove only obsolete draft nodes that are no longer present
        foreach (var stale in oldDrafts)
        {
            if (!usedOld.Contains(stale))
                parent.Children.Remove(stale);
        }

        // Recursively reconcile child subtrees
        for (var i = 0; i < orderedDraftNodes.Count; i++)
        {
            var node = orderedDraftNodes[i];
            var spec = desiredDraftSpecs[i].Spec;
            if (node.IsDirectory)
                ReconcileDraftChildren(node, spec.Children);
        }
    }

    public string SyncOutlineDrafts(IReadOnlyList<OutlineDraftLine> lines)
    {
        if (!CanEdit || folderLocation is null) return "";

        // Normalize into expanded path chains with hierarchy
        var items = new List<(string[] Parts, bool IsDirectory)>();
        var stack = new List<string>();

        try
        {
            var nonEmpty = lines.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToArray();
            for (var idx = 0; idx < nonEmpty.Length; idx++)
            {
                var line = nonEmpty[idx];
                var indent = Math.Clamp(line.Indent, 0, stack.Count);
                while (stack.Count > indent) stack.RemoveAt(stack.Count - 1);

                var segments = line.Name.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segments.Length == 0) continue;

                var hasChildNext = idx + 1 < nonEmpty.Length && nonEmpty[idx + 1].Indent > indent;
                for (var s = 0; s < segments.Length; s++)
                {
                    var part = segments[s];
                    NamingRulesEngine.ValidateName(part);
                    var isLast = s == segments.Length - 1;
                    var isFile = isLast && !hasChildNext && (line.ForceIsFile ?? LooksLikeFileName(part));
                    var chain = stack.Concat([part]).ToArray();
                    if (chain.Length > 128) throw new ArgumentException("目录层级不能超过 128 层。");
                    items.Add((chain, !isFile));
                    if (!isFile) stack.Add(part);
                }
            }
        }
        catch (ArgumentException error)
        {
            creationInputError = error.Message;
            Feedback = error.Message;
            NotifyCreationEditor();
            return error.Message;
        }

        syncingOutline = true;
        try
        {
            var desiredTree = BuildDesiredDraftTree(items);
            var targets = creationParents.Count > 0 ? creationParents : [CurrentCreationDirectory];
            foreach (var target in targets)
            {
                ReconcileDraftChildren(target, desiredTree);
            }

            creationNames = "";
            directoryInputs[CreationInputKey] = "";
            creationInputError = "";
            CreateMethod = 3;
            EditingResults = true;
            Invalidate();
            NotifyCreationEditor();
            return "";
        }
        catch (ArgumentException error)
        {
            creationInputError = error.Message;
            Feedback = error.Message;
            NotifyCreationEditor();
            return error.Message;
        }
        finally
        {
            syncingOutline = false;
        }
    }

    public string AppendNumberedToOutline(string baseName, string countText, bool isFile)
    {
        if (!CanEdit) return "";
        try
        {
            var count = Number(countText, "创建数量");
            if (count is < 1 or > 5000) throw new ArgumentException("快速批量填入支持 1–5000 项。");
            var start = Number(Start, "起始数字");
            var step = Number(Step, "每次递增");
            var digits = Number(Digits, "数字位数");
            if (digits is < 1 or > 12) throw new ArgumentException("数字位数需在 1–12 之间。");
            var ext = isFile ? NormalizedExtension() : "";
            var trimmedBase = baseName.Trim();

            var current = ExportCurrentOutline().ToList();
            for (var i = 0; i < count; i++)
            {
                var serial = (start + (long)i * step).ToString("D" + digits);
                var name = trimmedBase.Length == 0
                    ? serial
                    : NumberPosition == 0 ? serial + Separator + trimmedBase : trimmedBase + Separator + serial;
                if (isFile && Path.GetExtension(name).Length == 0) name += ext;
                current.Add(new(0, name, isFile));
            }
            var error = SyncOutlineDrafts(current);
            if (error.Length == 0) OutlineReloadRequested?.Invoke(this, EventArgs.Empty);
            return error;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            Feedback = error.Message;
            return error.Message;
        }
    }

    public void AppendOutlinePreset(IReadOnlyList<OutlineDraftLine> presetLines)
    {
        if (!CanEdit || presetLines.Count == 0) return;
        var current = ExportCurrentOutline().ToList();
        current.AddRange(presetLines);
        SyncOutlineDrafts(current);
        OutlineReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ClearCurrentOutlineDrafts()
    {
        if (!CanEdit) return;
        SyncOutlineDrafts([]);
        OutlineReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    // ==================== OUTLINE TEMPLATE MANAGEMENT (ADD / DELETE / RESET) ====================
    private List<OutlineTemplate>? outlineTemplates;
    public IReadOnlyList<OutlineTemplate> OutlineTemplates => outlineTemplates ??= OutlineTemplateStore.Load();
    public event EventHandler? OutlineTemplatesChanged;
    public void ReloadOutlineTemplates()
    {
        outlineTemplates = null;
        OnPropertyChanged(nameof(OutlineTemplates));
        OutlineTemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    public string SaveCurrentOutlineAsTemplate(string templateName, string? storePath = null)
    {
        var name = templateName.Trim();
        if (name.Length == 0) return "请输入模板名称。";
        var lines = ExportCurrentOutline();
        if (lines.Count == 0)
        {
            // Fall back to exporting top-level drafts from root if current subfolder has none
            lines = ExportOutlineUnder(folderLocation);
        }
        if (lines.Count == 0) return "当前大纲为空，请先输入至少一项再保存为模板。";

        try
        {
            var list = OutlineTemplateStore.ReadForUpdate(storePath);
            list.Add(new(Guid.NewGuid().ToString("N"), name, lines.ToArray()));
            PersistTemplates(list, storePath);
            Feedback = $"已保存模板「{name}」（共 {lines.Count} 项）。";
            return "";
        }
        catch (Exception error) when (IsTemplateStoreError(error)) { return Feedback = "未能保存模板：" + error.Message; }
    }

    public bool DeleteOutlineTemplate(string templateId, string? storePath = null)
    {
        try
        {
            var list = OutlineTemplateStore.ReadForUpdate(storePath);
            var index = list.FindIndex(item => string.Equals(item.Id, templateId, StringComparison.Ordinal));
            if (index < 0) return false;
            var removed = list[index];
            list.RemoveAt(index);
            PersistTemplates(list, storePath);
            Feedback = $"已删除模板「{removed.Name}」。";
            return true;
        }
        catch (Exception error) when (IsTemplateStoreError(error)) { Feedback = "未能删除模板：" + error.Message; return false; }
    }

    public bool ResetDefaultOutlineTemplates(string? storePath = null)
    {
        try
        {
            PersistTemplates(OutlineTemplateStore.DefaultTemplates().ToList(), storePath);
            Feedback = "已恢复默认常用模板。";
            return true;
        }
        catch (Exception error) when (IsTemplateStoreError(error)) { Feedback = "未能恢复默认模板：" + error.Message; return false; }
    }

    private void PersistTemplates(List<OutlineTemplate> templates, string? storePath)
    {
        OutlineTemplateStore.Save(templates, storePath);
        outlineTemplates = templates;
        OnPropertyChanged(nameof(OutlineTemplates));
        OutlineTemplatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsTemplateStoreError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException;

    private static IReadOnlyList<OutlineDraftLine> ExportOutlineUnder(DirectoryDraft? root)
    {
        var lines = new List<OutlineDraftLine>();
        if (root is null) return lines;
        void Walk(DirectoryDraft parent, int depth)
        {
            foreach (var child in parent.Children)
            {
                if (child.IsExisting || child.IsMissing) continue;
                lines.Add(new(depth, child.Name, !child.IsDirectory));
                if (child.IsDirectory && child.Children.Count > 0) Walk(child, depth + 1);
            }
        }
        Walk(root, 0);
        return lines;
    }

    // ==================== LEFT TREE DRAG & DROP (MOVE PENDING DRAFTS) ====================
    public bool CanMoveCreationDraft(DirectoryDraft? source, DirectoryDraft? target)
    {
        if (!CanEdit || source is null || target is null || folderLocation is null) return false;
        if (source.IsDestination || source.IsExisting || source.IsMissing) return false;
        if (!target.IsDirectory || target.IsMissing) return false;
        if (ReferenceEquals(source, target)) return false;
        if (target.Children.Contains(source)) return false;
        if (IsDescendantOf(target, source)) return false;
        if (target.Children.Any(x => x.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    public bool MoveCreationDraft(DirectoryDraft source, DirectoryDraft target)
    {
        if (!CanMoveCreationDraft(source, target)) return false;
        var oldSiblings = Siblings(source);
        if (oldSiblings is null) return false;

        syncingOutline = true;
        try
        {
            oldSiblings.Remove(source);
            target.Children.Add(source);
            EditingResults = true;
            Invalidate();
            Feedback = $"已将「{source.Name}」移动到「{(target.IsDestination ? CreationRootName : target.Name)}」。";
            NotifyCreationEditor();
        }
        finally
        {
            syncingOutline = false;
        }
        OutlineReloadRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool IsDescendantOf(DirectoryDraft candidate, DirectoryDraft ancestor)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate) || IsDescendantOf(candidate, child))
                return true;
        }
        return false;
    }

    // ==================== CENTER OUTLINE MULTI-SELECT MOVE / REPARENT ====================
    public static int FindSubtreeEndExclusive(IReadOnlyList<OutlineDraftLine> lines, int startIndex)
    {
        if (startIndex < 0 || startIndex >= lines.Count) return startIndex;
        var baseIndent = lines[startIndex].Indent;
        var end = startIndex + 1;
        while (end < lines.Count && lines[end].Indent > baseIndent) end++;
        return end;
    }

    /// <summary>
    /// Moves one or more selected outline rows (including each selected folder's nested descendants)
    /// either inside a target folder (as children) or directly after a target row at its indent level.
    /// Returns null if the move is invalid (e.g. moving a folder into itself or its own descendant).
    /// </summary>
    public static IReadOnlyList<OutlineDraftLine>? MoveOutlineSelection(
        IReadOnlyList<OutlineDraftLine> lines,
        IReadOnlyCollection<int> selectedIndices,
        int targetIndex,
        bool dropIntoFolder)
    {
        if (lines.Count == 0 || selectedIndices.Count == 0 || targetIndex < 0 || targetIndex >= lines.Count)
            return null;

        // Expand selected indices to full subtrees, keeping only top-most selected roots to avoid double-moving
        var sorted = selectedIndices.Where(i => i >= 0 && i < lines.Count).Distinct().OrderBy(i => i).ToArray();
        if (sorted.Length == 0) return null;

        var blocks = new List<(int Start, int End, int BaseIndent)>();
        var covered = new HashSet<int>();
        foreach (var idx in sorted)
        {
            if (covered.Contains(idx)) continue;
            var end = FindSubtreeEndExclusive(lines, idx);
            blocks.Add((idx, end, lines[idx].Indent));
            for (var k = idx; k < end; k++) covered.Add(k);
        }

        // Cannot drop onto any row that is part of the dragged selection/subtrees
        if (covered.Contains(targetIndex)) return null;

        var targetIndent = lines[targetIndex].Indent;
        var newBaseIndent = dropIntoFolder ? targetIndent + 1 : targetIndent;

        // Determine insertion index in the original list:
        // If dropping into a folder, place right at the end of that folder's existing children;
        // otherwise place right after the target row's subtree.
        var insertAnchorExclusive = FindSubtreeEndExclusive(lines, targetIndex);

        var movedLines = new List<OutlineDraftLine>();
        foreach (var (start, end, baseIndent) in blocks)
        {
            for (var k = start; k < end; k++)
            {
                var rel = Math.Max(0, lines[k].Indent - baseIndent);
                movedLines.Add(lines[k] with { Indent = newBaseIndent + rel });
            }
        }

        var result = new List<OutlineDraftLine>(lines.Count);
        var inserted = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == insertAnchorExclusive && !inserted)
            {
                result.AddRange(movedLines);
                inserted = true;
            }
            if (!covered.Contains(i)) result.Add(lines[i]);
        }
        if (!inserted) result.AddRange(movedLines);
        return result;
    }

    public static IReadOnlyList<OutlineDraftLine>? MoveOutlineSelectionToRoot(
        IReadOnlyList<OutlineDraftLine> lines,
        IReadOnlyCollection<int> selectedIndices,
        bool appendToEnd = true)
    {
        if (lines.Count == 0 || selectedIndices.Count == 0) return null;
        var sorted = selectedIndices.Where(i => i >= 0 && i < lines.Count).Distinct().OrderBy(i => i).ToArray();
        if (sorted.Length == 0) return null;

        var blocks = new List<(int Start, int End, int BaseIndent)>();
        var covered = new HashSet<int>();
        foreach (var idx in sorted)
        {
            if (covered.Contains(idx)) continue;
            var end = FindSubtreeEndExclusive(lines, idx);
            blocks.Add((idx, end, lines[idx].Indent));
            for (var k = idx; k < end; k++) covered.Add(k);
        }

        var movedLines = new List<OutlineDraftLine>();
        foreach (var (start, end, baseIndent) in blocks)
            for (var k = start; k < end; k++)
                movedLines.Add(lines[k] with { Indent = Math.Max(0, lines[k].Indent - baseIndent) });

        var remaining = lines.Where((_, i) => !covered.Contains(i)).ToList();
        if (appendToEnd) remaining.AddRange(movedLines);
        else remaining.InsertRange(0, movedLines);
        return remaining;
    }

    public bool CanDropOutlineLinesIntoDirectory(IReadOnlyList<OutlineDraftLine> draggedLines, IReadOnlyList<OutlineDraftLine> remainingCurrentLines, DirectoryDraft? targetFolder)
    {
        if (!CanEdit || targetFolder is null || !targetFolder.IsDirectory || targetFolder.IsMissing || draggedLines.Count == 0)
            return false;
        if (ReferenceEquals(targetFolder, CurrentCreationDirectory))
            return false;

        // If targetFolder is under CurrentCreationDirectory, ensure it is not being removed as part of draggedLines
        if (!targetFolder.IsDestination && IsDescendantOf(targetFolder, CurrentCreationDirectory))
        {
            var curPrefix = CurrentCreationDirectory.IsDestination
                ? ""
                : DirectoryEntries().FirstOrDefault(x => ReferenceEquals(x.Node, CurrentCreationDirectory)).Path ?? "";
            var targetFull = DirectoryEntries().FirstOrDefault(x => ReferenceEquals(x.Node, targetFolder)).Path;
            if (targetFull is null) return false;
            var relTarget = curPrefix.Length == 0 ? targetFull : targetFull[(curPrefix.Length + 1)..];

            // Verify relTarget still exists in remainingCurrentLines (or is an existing disk directory)
            if (!targetFolder.IsExisting)
            {
                var stack = new List<string>();
                var foundInRemaining = false;
                foreach (var line in remainingCurrentLines.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
                {
                    var indent = Math.Clamp(line.Indent, 0, stack.Count);
                    while (stack.Count > indent) stack.RemoveAt(stack.Count - 1);
                    stack.Add(line.Name.Trim());
                    if (string.Equals(string.Join("\\", stack), relTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        foundInRemaining = true;
                        break;
                    }
                }
                if (!foundInRemaining) return false;
            }
        }
        return true;
    }

    /// <summary>Moves dragged outline lines directly into a folder in the left creation tree.</summary>
    public bool MoveOutlineLinesToDirectory(IReadOnlyList<OutlineDraftLine> draggedLines, IReadOnlyList<OutlineDraftLine> remainingCurrentLines, DirectoryDraft targetFolder)
    {
        if (!CanDropOutlineLinesIntoDirectory(draggedLines, remainingCurrentLines, targetFolder))
            return false;

        var targetPath = targetFolder.IsDestination
            ? ""
            : DirectoryEntries().FirstOrDefault(x => ReferenceEquals(x.Node, targetFolder)).Path;

        // First update current directory without the dragged items
        var err = SyncOutlineDrafts(remainingCurrentLines);
        if (err.Length > 0) return false;

        var liveTarget = targetFolder.IsDestination
            ? folderLocation
            : DirectoryEntries().FirstOrDefault(x => ReferenceEquals(x.Node, targetFolder)).Node
              ?? (targetPath is not null ? DirectoryEntries().FirstOrDefault(x => string.Equals(x.Path, targetPath, StringComparison.OrdinalIgnoreCase)).Node : null);
        if (liveTarget is null) return false;

        // Now attach draggedLines under liveTarget
        var stack = new List<DirectoryDraft> { liveTarget };
        syncingOutline = true;
        try
        {
            for (var idx = 0; idx < draggedLines.Count; idx++)
            {
                var line = draggedLines[idx];
                var indent = Math.Clamp(line.Indent, 0, stack.Count - 1);
                while (stack.Count > indent + 1) stack.RemoveAt(stack.Count - 1);

                var parent = stack[^1];
                var hasChild = idx + 1 < draggedLines.Count && draggedLines[idx + 1].Indent > indent;
                var isDir = hasChild || !(line.ForceIsFile ?? LooksLikeFileName(line.Name));
                var existing = parent.Children.FirstOrDefault(x => x.Name.Equals(line.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var template = isDir ? "" : ResolveOutlineFileTemplate(line.Name);
                    existing = MakeDraft(line.Name, isDir, template);
                    parent.Children.Add(existing);
                }
                if (isDir) stack.Add(existing);
            }
            EditingResults = true;
            Invalidate();
            Feedback = $"已将 {draggedLines.Count} 项移动到「{(liveTarget.IsDestination ? CreationRootName : liveTarget.Name)}」。";
            NotifyCreationEditor();
        }
        finally { syncingOutline = false; }

        OutlineReloadRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
