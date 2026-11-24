using System.Text.Json;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;

namespace FileAssistant.Infrastructure.FileSystem;

internal sealed class CopyRecoveryStore(string directory)
{
    public void Save(CopyRecovery recovery)
    {
        CopyFileSystem.CheckPath(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, recovery.Id.ToString("N") + ".json");
        CopyFileSystem.CheckPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, recovery);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<CopyRecovery> ReadAll()
    {
        if (!Directory.Exists(directory)) return [];
        var records = new List<CopyRecovery>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
            try
            {
                var record = Read(id);
                if (record.State is not ("已恢复" or "已删除备份")) records.Add(record);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                records.Add(new(id, path, "", "", "", "", DateTimeOffset.MinValue, "记录无法读取",
                    "原记录和备份已保留，请核对记录文件：" + error.Message));
            }
        }
        return records.OrderByDescending(x => x.Created).ToArray();
    }

    public CopyRecovery Read(Guid id)
    {
        var path = Path.Combine(directory, id.ToString("N") + ".json");
        CopyFileSystem.CheckPath(path);
        var record = JsonSerializer.Deserialize<CopyRecovery>(File.ReadAllText(path)) ?? throw new IOException("恢复记录为空。");
        if (record.Id != id) throw new IOException("恢复记录标识校验失败，未操作磁盘文件。");

        if (record.Kind == CopyRecoveryKind.MoveUndo)
        {
            if (record.MovedItems is not { Count: > 0 })
                throw new IOException("移动恢复记录缺少明细，未操作磁盘文件。");
            foreach (var item in record.MovedItems)
            {
                CopyFileSystem.CheckPath(item.OriginalPath);
                CopyFileSystem.CheckPath(item.MovedPath);
            }
            return record;
        }

        if (!CopyPlanner.Equal(record.Backup, BackupPath(record.Target, id)))
            throw new IOException("恢复记录路径校验失败，未操作磁盘文件。");
        return record;
    }

    public void Prune(DateTimeOffset before, CancellationToken token)
    {
        CopyFileSystem.CheckPath(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var journal in Directory.EnumerateFiles(directory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(journal), "N", out var id)) continue;
            try
            {
                var record = Read(id);
                if (record.Created == default || record.Created >= before) continue;

                if (record.Kind == CopyRecoveryKind.MoveUndo)
                {
                    if (record.State is "已恢复" or "已删除备份" or "可撤回移动")
                    {
                        var journalStamp = NamingFileSystem.Read(journal, true, token).Stamp!;
                        NamingFileSystem.DeleteOwned(journal, journalStamp, false);
                    }
                    continue;
                }

                if (!IsHash(record.OldHash) || !IsHash(record.NewHash)) continue;
                CopyFileSystem.CheckPath(record.Target);
                CopyFileSystem.CheckPath(record.Backup);
                var displaced = RestorePath(record);
                CopyFileSystem.CheckPath(displaced);
                if (!Directory.Exists(Path.GetDirectoryName(record.Target)) || CopyFileSystem.Exists(displaced)) continue;
                if (record.State == "已覆盖")
                {
                    if (!File.Exists(record.Backup) || !File.Exists(record.Target)) continue;
                    // Keep a verified copy of the new content in place while discarding the only backup.
                    using var target = new FileStream(record.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (CopyFileSystem.Hash(target, token) != record.NewHash) continue;
                    Discard(id, token, requireBackup: true);
                }
                else if (record.State is not ("已恢复" or "已删除备份")) continue;
                var stamp = NamingFileSystem.Read(journal, true, token).Stamp!;
                var finished = Read(id);
                if (finished.State is "已恢复" or "已删除备份" && !CopyFileSystem.Exists(finished.Backup) && !CopyFileSystem.Exists(displaced))
                    NamingFileSystem.DeleteOwned(journal, stamp, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
            { /* Retain unverifiable records and their files for manual recovery. */ }
        }

        static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    }

    public static string BackupPath(string target, Guid id) => Path.Combine(Path.GetDirectoryName(target)!, $".fileassistant-backup-{id:N}.bak");
    private static string RestorePath(CopyRecovery record) => Path.Combine(Path.GetDirectoryName(record.Target)!, $".fileassistant-restore-{record.Id:N}.bak");

    public IReadOnlyList<PathChange> RestoreWithChanges(Guid id, CancellationToken token)
    {
        var record = Read(id);
        if (record.Kind == CopyRecoveryKind.MoveUndo)
            return RestoreMoveBatch(record, token);
        Restore(id, token);
        return [];
    }

    public void Restore(Guid id, CancellationToken token)
    {
        var record = Read(id);
        if (record.Kind == CopyRecoveryKind.MoveUndo)
        {
            RestoreMoveBatch(record, token);
            return;
        }
        if (record.State is "已恢复" or "已删除备份") return;
        if (record.State == "正在删除备份") throw new IOException("这份备份已开始删除，请继续删除操作。");
        CopyFileSystem.CheckPath(record.Target); CopyFileSystem.CheckPath(record.Backup);
        var displaced = RestorePath(record);
        // Reconcile a crash after the old backup was already restored.
        if (!File.Exists(record.Backup))
        {
            if (File.Exists(record.Target) && CopyFileSystem.Stamp(record.Target, true, token).Hash == record.OldHash)
            { FinishRestore(record, displaced, token); return; }
            throw new IOException("备份尚不可用，请连接原目标设备并检查记录。");
        }
        using var backup = WindowsCopyFile.LockForRename(record.Backup);
        if (CopyFileSystem.Hash(backup, token) != record.OldHash) throw new IOException("备份内容已变化，未执行恢复。");
        FileStream? current = null;
        try
        {
            if (CopyFileSystem.Exists(record.Target))
            {
                current = WindowsCopyFile.LockForRename(record.Target);
                if (CopyFileSystem.Hash(current, token) != record.NewHash) throw new IOException("当前目标已被修改，未覆盖它。请先另存当前文件后再恢复。");
                if (File.Exists(displaced)) throw new IOException("存在上次恢复的保留文件，请先检查恢复记录。");
            }
            token.ThrowIfCancellationRequested();
            Save(record with { State = "正在恢复" });
            if (current is not null) WindowsCopyFile.RenameLocked(current, displaced);
            try { WindowsCopyFile.RenameLocked(backup, record.Target); }
            catch
            {
                if (current is not null && !CopyFileSystem.Exists(record.Target)) WindowsCopyFile.RenameLocked(current, record.Target);
                throw;
            }
        }
        finally { current?.Dispose(); }
        FinishRestore(record, displaced, CancellationToken.None);
    }

    private IReadOnlyList<PathChange> RestoreMoveBatch(CopyRecovery record, CancellationToken token)
    {
        if (record.State is "已恢复" or "已删除备份") return [];
        var items = record.MovedItems!;

        // Phase 1: Validate ALL moved files are intact/unmodified and ALL original paths are free before moving anything.
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            CopyFileSystem.CheckPath(item.MovedPath);
            CopyFileSystem.CheckPath(item.OriginalPath);
            if (!File.Exists(item.MovedPath))
                throw new IOException($"文件「{Path.GetFileName(item.MovedPath)}」已不在移动后的位置，未执行撤回。");
            if (CopyFileSystem.Exists(item.OriginalPath))
                throw new IOException($"原位置「{item.OriginalPath}」已存在同名文件或目录，为防止覆盖未执行撤回。");

            using var stream = new FileStream(item.MovedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = WindowsCopyFile.Stamp(stream);
            if (actual.Length != item.Length)
                throw new IOException($"文件「{Path.GetFileName(item.MovedPath)}」在移动后已被修改，未覆盖或撤回。");
            if (item.Hash is { Length: > 0 })
            {
                if (CopyFileSystem.Hash(stream, token) != item.Hash)
                    throw new IOException($"文件「{Path.GetFileName(item.MovedPath)}」内容已变化，未执行撤回。");
            }
            else if (actual.ModifiedUtcTicks != item.ModifiedUtcTicks)
            {
                throw new IOException($"文件「{Path.GetFileName(item.MovedPath)}」修改时间已变化，未执行撤回。");
            }
        }

        // Phase 2: Move back to original paths.
        Save(record with { State = "正在撤回移动" });
        var changes = new List<PathChange>(items.Count);
        foreach (var item in items)
        {
            var parent = Path.GetDirectoryName(item.OriginalPath)!;
            CopyFileSystem.CheckPath(parent);
            Directory.CreateDirectory(parent);

            using var locked = WindowsCopyFile.LockForRename(item.MovedPath);
            var stamp = WindowsCopyFile.Stamp(locked);
            if (WindowsCopyFile.SameVolume(stamp, parent))
            {
                WindowsCopyFile.RenameLocked(locked, item.OriginalPath);
            }
            else
            {
                var expectedHash = item.Hash ?? CopyFileSystem.Hash(locked, token);
                locked.Position = 0;
                var temp = Path.Combine(parent, $".fileassistant-undo-{Guid.NewGuid():N}.tmp");
                try
                {
                    WindowsCopyFile.Copy(item.MovedPath, temp, _ => { }, token);
                    var copied = CopyFileSystem.Stamp(temp, true, token);
                    if (copied.Length != item.Length || copied.Hash != expectedHash)
                        throw new IOException("跨盘撤回内容校验未通过。");
                    File.Move(temp, item.OriginalPath, false);
                }
                finally
                {
                    if (File.Exists(temp)) CopyFileSystem.DeleteOwnedTemporary(temp);
                }
                var delTemp = Path.Combine(Path.GetDirectoryName(item.MovedPath)!, $".fileassistant-movedel-{Guid.NewGuid():N}.tmp");
                WindowsCopyFile.RenameLocked(locked, delTemp);
                locked.Dispose();
                CopyFileSystem.DeleteOwnedTemporary(delTemp);
            }
            changes.Add(new(item.MovedPath, item.OriginalPath, false));
        }

        Save(record with { State = "已恢复", Message = null });
        return changes;
    }

    private void FinishRestore(CopyRecovery record, string displaced, CancellationToken token)
    {
        if (File.Exists(displaced))
        {
            CopyFileSystem.CheckPath(displaced);
            if (CopyFileSystem.Stamp(displaced, true, token).Hash != record.NewHash) throw new IOException("恢复前保留文件已变化，已保留供检查。");
            CopyFileSystem.DeleteOwnedTemporary(displaced);
        }
        Save(record with { State = "已恢复", Message = null });
    }

    public void Discard(Guid id, CancellationToken token, bool requireBackup = false)
    {
        var record = Read(id);
        if (record.Kind == CopyRecoveryKind.MoveUndo)
        {
            Save(record with { State = "已删除备份", Message = null });
            return;
        }
        if (!Directory.Exists(Path.GetDirectoryName(record.Target))) throw new IOException("目标目录不可用，请连接原设备后再删除备份。");
        var removal = RestorePath(record);
        if (requireBackup && !File.Exists(record.Backup)) throw new IOException("备份已缺失，保留恢复记录供检查。");
        if (File.Exists(removal))
        {
            if (record.State != "正在删除备份") throw new IOException("恢复尚未完成，请先完成恢复。");
            CopyFileSystem.CheckPath(removal);
            if (CopyFileSystem.Stamp(removal, true, token).Hash != record.OldHash) throw new IOException("待删除备份已变化，未删除。");
            CopyFileSystem.DeleteOwnedTemporary(removal);
        }
        if (File.Exists(record.Backup))
        {
            CopyFileSystem.CheckPath(record.Backup);
            using (var locked = WindowsCopyFile.LockForRename(record.Backup))
            {
                if (CopyFileSystem.Hash(locked, token) != record.OldHash) throw new IOException("备份内容已变化，未删除。");
                token.ThrowIfCancellationRequested();
                Save(record with { State = "正在删除备份" });
                // Move under the held handle first; deleting then cannot affect a substituted path.
                WindowsCopyFile.RenameLocked(locked, RestorePath(record));
            }
            CopyFileSystem.DeleteOwnedTemporary(RestorePath(record));
        }
        Save(record with { State = "已删除备份", Message = null });
    }
}
