using System.Text.Json;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Infrastructure.FileSystem;

internal sealed record NamingStep(string From, string To, bool Directory, FileStamp? Stamp, bool Create = false, bool CreatedObject = false);
internal sealed class NamingJournal
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [System.Text.Json.Serialization.JsonRequired]
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public string Title { get; set; } = "批量改名";
    public List<NamingStep> Steps { get; set; } = [];
    public int Remaining { get; set; }
    public bool Restoring { get; set; }
    public int? UndoIndex { get; set; }
    public string? Error { get; set; }
}
internal sealed class NamingJournalStore(string directory)
{
    public void Save(NamingJournal journal)
    {
        CopyFileSystem.CheckPath(directory);
        Directory.CreateDirectory(directory);
        var path = PathFor(journal.Id);
        CopyFileSystem.CheckPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string PathFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(directory, id + ".json") : throw new ArgumentException("恢复记录编号无效。");
    public NamingJournal Read(string id)
    {
        var path = PathFor(id);
        CopyFileSystem.CheckPath(path);
        var journal = JsonSerializer.Deserialize<NamingJournal>(File.ReadAllText(path)) ?? throw new IOException("恢复记录为空。");
        if (journal.Version != 1 || journal.Id != id || journal.Steps is null || journal.Steps.Any(step => step is null) ||
            journal.Remaining < 0 || journal.Remaining > journal.Steps.Count) throw new IOException("恢复记录无效。");
        return journal;
    }
    public void Prune(DateTimeOffset before, CancellationToken token)
    {
        CopyFileSystem.CheckPath(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var stamp = NamingFileSystem.Read(path, true, token).Stamp!;
                var journal = Read(id);
                if (journal.Created == default || journal.Created >= before || journal.Error is not null || journal.UndoIndex is not null ||
                    journal.Restoring && journal.Remaining > 0 || journal.Steps.Count == 0 ||
                    journal.Steps.Any(step => !Path.IsPathFullyQualified(step.To) || !step.Create && !Path.IsPathFullyQualified(step.From) ||
                        string.IsNullOrWhiteSpace(step.Stamp?.Identity))) continue;
                foreach (var step in journal.Steps)
                {
                    token.ThrowIfCancellationRequested();
                    CopyFileSystem.CheckPath(step.To);
                    if (!step.Create) CopyFileSystem.CheckPath(step.From);
                }
                // Intermediate staging paths disappear on success. Each active object's last recorded name must still be its own.
                if (!HasCurrentTargets(journal, token)) continue;
                NamingFileSystem.DeleteOwned(path, stamp, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
            { /* Damaged, linked or incomplete recovery data stays available for inspection. */ }
        }
    }

    private static bool HasCurrentTargets(NamingJournal journal, CancellationToken token)
    {
        var active = journal.Steps.Take(journal.Remaining).Select((step, index) => (step, index)).ToArray();
        var directories = active.Where(entry => entry.step.Directory && !entry.step.Create)
            .Select(entry => (entry.index, move: new PathChange(entry.step.From, entry.step.To, true))).ToArray();
        foreach (var entries in active.GroupBy(entry => entry.step.Stamp!.Identity))
        {
            token.ThrowIfCancellationRequested();
            var (step, index) = entries.Last();
            if (NamingFileSystem.Matches(step.To, step.Stamp!)) continue;
            // A later parent rename can move the final child name without another journal step for the child.
            var later = directories.Where(entry => entry.index > index).Select(entry => entry.move).ToArray();
            var final = NamingRulesEngine.MapPath(step.To, later);
            if (final == step.To || !NamingFileSystem.Matches(final, step.Stamp!)) return false;
        }
        return true;
    }

    public IReadOnlyList<NamingRecovery> List()
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<NamingRecovery>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try { var journal = Read(id); if (journal.Remaining > 0) result.Add(new(journal.Id, journal.Created, journal.Title, journal.Remaining, journal.Error)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { result.Add(new(id, File.GetLastWriteTime(path), "记录无法读取", 0, e.Message)); }
        }
        return result.OrderByDescending(x => x.Created).ToArray();
    }
    public void Move(NamingJournal journal, string source, string target, bool directory, FileStamp stamp, List<PathChange> changes, bool createdObject = false)
    {
        journal.Steps.Add(new(source, target, directory, stamp, CreatedObject: createdObject)); journal.Remaining = journal.Steps.Count; Save(journal);
        NamingFileSystem.Move(source, target, stamp); changes.Add(new(source, target, directory));
    }
    public void UndoStep(NamingStep step, List<PathChange> changes, List<string> removed, bool replayCompleted = false)
    {
        if (step.Create)
        {
            if (!CopyFileSystem.Exists(step.To)) { removed.Add(step.To); return; }
            if (step.Stamp is null) throw new IOException("创建过程中中断，缺少对象身份凭据；已保留文件，请人工核对：" + step.To);
            NamingFileSystem.DeleteOwned(step.To, step.Stamp, step.Directory); removed.Add(step.To); return;
        }
        var stamp = step.Stamp ?? throw new IOException("缺少文件身份凭据。");
        if (NamingFileSystem.Matches(step.From, stamp))
        {
            // Replay only a persisted undo intent, never an unexecuted forward move into an occupied target.
            if (replayCompleted) changes.Add(new(step.To, step.From, step.Directory));
            return;
        }
        if (!NamingFileSystem.Matches(step.To, stamp)) throw new IOException("文件已移动或被替换，无法恢复：" + step.To);
        if (step.CreatedObject && (step.Directory ? Directory.EnumerateFileSystemEntries(step.To).Any() : !NamingFileSystem.Matches(step.To, stamp, contents: true)))
            throw new IOException("创建的文件已修改或目录含有新增内容，已保留：" + step.To);
        NamingFileSystem.Move(step.To, step.From, stamp); changes.Add(new(step.To, step.From, step.Directory));
    }
}
