using System.Diagnostics;
using FileAssistant.Core.Engines;
using FileAssistant.Core.Features.CreateOrRename;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Settings;

namespace FileAssistant.Infrastructure.FileSystem;

public interface ICopyFileTransfer
{
    void Copy(string source, string temporary, Action<long> progress, CancellationToken token);
}

public sealed class CopyService(string recoveryDirectory, ICopyFileTransfer? transfer = null) : ICopyService
{
    private const string BothEndsExistMarker = "两处均有文件";
    private readonly CopyRecoveryStore recovery = new(recoveryDirectory);
    private readonly SemaphoreSlim execution = new(1);

    public Task<CopyPlan> PlanAsync(CopyRequest request, IProgress<ProcessingProgress>? progress, CancellationToken token) => Task.Run(() =>
    {
        var sources = CopyScanner.Scan(request, progress, token);
        var targets = new Dictionary<string, CopyTarget?>(StringComparer.OrdinalIgnoreCase);
        CopyTarget? Target(string path)
        {
            token.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(path, out var state))
                targets[path] = state = CopyFileSystem.Target(path, request.Conflict == CopyConflictPolicy.Confirm, token);
            return state;
        }

        IReadOnlyCollection<string>? existingFolders = null;
        if (request.Layout == CopyLayout.Classify &&
            request.ClassifyBy == ClassifyPrimary.NameMatch &&
            request.NameMatchRule == NameMatchRule.ExistingFolderFirst &&
            request.Location == OutputLocation.Fixed)
        {
            var dest = CopyPlanner.Normalize(request.Destination);
            if (Directory.Exists(dest))
            {
                try
                {
                    existingFolders = Directory.EnumerateDirectories(dest)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Cast<string>()
                        .ToArray();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    existingFolders = [];
                }
            }
        }

        return CopyPlanner.Create(request, sources, Target, token, existingFolders);
    }, token);

    public Task<CopyPlan> DecideAsync(CopyPlan plan, Guid item, CopyDecision decision, CancellationToken token) => Task.Run(() =>
        CopyPlanner.Decide(plan, item, decision, p => CopyFileSystem.Target(p, false, token)), token);

    public Task<CopyPlan> RetryPlanAsync(CopyReport report, CancellationToken token) => Task.Run(() =>
    {
        var items = new List<CopyPlanItem>();
        foreach (var result in report.Items.Where(x => x.Retryable))
        {
            token.ThrowIfCancellationRequested();
            var item = result.Item;
            try
            {
                CopyFileSystem.CheckPath(item.Source.Path);
                var source = item.Source with
                {
                    Stamp = item.Source.IsDirectory ? null : CopyFileSystem.Stamp(item.Source.Path, false, token),
                    Error = null
                };

                // If a previous cross-volume Move wrote & verified the target, keep the reconcile marker instead of numbering a second copy.
                if (report.Plan.IsMove && !source.IsDirectory && result.Message.Contains(BothEndsExistMarker, StringComparison.Ordinal) && File.Exists(item.Target))
                {
                    items.Add(item with
                    {
                        Source = source,
                        Action = CopyAction.Copy,
                        Note = BothEndsExistMarker,
                        Message = "目标已存在上次写入副本，重试将核对两端一致后移除源文件。",
                        ApprovedTarget = null
                    });
                    continue;
                }

                var refreshed = item with { Source = source, Action = CopyAction.Copy, Message = null, ApprovedTarget = null };
                var resolved = CheckTarget(refreshed, report.Plan, token);
                if (report.Plan.Request.Conflict == CopyConflictPolicy.Confirm &&
                    report.Items.Any(x => x.Outcome == CopyOutcome.Success && CopyPlanner.Equal(x.Item.Target, resolved.Target)))
                {
                    resolved = resolved with
                    {
                        Action = CopyAction.Pending,
                        BatchConflict = true,
                        ApprovedTarget = null,
                        Message = "此路径属于本批已完成项目，请编号保留或跳过。"
                    };
                }
                items.Add(resolved);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                items.Add(item with { Action = CopyAction.Rejected, Message = CopyFileSystem.Friendly(error) });
            }
        }
        return report.Plan with { Items = items.ToArray() };
    }, token);

    public async Task<CopyReport> ExecuteAsync(CopyPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        if (plan.PendingCount > 0) throw new InvalidOperationException("请先处理计划中的待确认项。");
        await execution.WaitAsync(token);
        try { return await Task.Run(() => Execute(plan, progress, token), CancellationToken.None); }
        finally { execution.Release(); }
    }

    private CopyReport Execute(CopyPlan plan, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        var results = new List<CopyItemResult>();
        var changes = new List<PathChange>();
        var movedRecords = new List<MovedFileRecord>();
        var isMove = plan.IsMove;
        var isSkeleton = plan.IsSkeleton;
        var clock = Stopwatch.StartNew();
        long completedBytes = 0, transferred = 0, lastTick = -100;
        string? stopped = null;

        try
        {
            if (plan.Request.Location == OutputLocation.Fixed && plan.ReadyCount > 0)
            {
                CopyFileSystem.CheckPath(plan.Request.Destination);
                Directory.CreateDirectory(plan.Request.Destination);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stopped = CopyFileSystem.Friendly(error);
        }

        foreach (var planned in plan.Items)
        {
            var item = planned;
            if (item.Action == CopyAction.Skip)
            {
                results.Add(new(item, CopyOutcome.Skipped, item.Message ?? "按规则跳过项目。"));
                continue;
            }
            if (item.Action == CopyAction.Rejected)
            {
                results.Add(new(item, CopyOutcome.Failed, item.Message ?? "此项不可处理。"));
                continue;
            }
            if (token.IsCancellationRequested || stopped is not null)
            {
                results.Add(new(item, CopyOutcome.NotStarted, stopped ?? "取消后尚未开始。"));
                continue;
            }

            string? temporary = null;
            var commitDone = false;
            long fileBytes = 0;
            try
            {
                var destination = plan.Request.DirectoryFor(item.Source);
                CopyFileSystem.CheckPath(destination);
                CopyFileSystem.CheckPath(item.Source.Path);
                if (!CopyPlanner.Within(item.Target, destination)) throw new IOException("目标路径超出本次计划。");
                if (item.Source.Root.Length > 0 && CopyPlanner.Within(destination, item.Source.Root))
                    throw new IOException("目标目录位于源目录内部。");

                if (item.Source.IsDirectory)
                {
                    item = CheckTarget(item, plan, token);
                    if (!Directory.Exists(item.Source.Path)) throw new DirectoryNotFoundException("源目录已失效。");
                    Directory.CreateDirectory(item.Target);
                    var dirOut = isSkeleton
                        ? new ProducedFile(item.Target, 0, DateTimeOffset.UtcNow, [item.Source.Path], IsDirectory: true)
                        : null;
                    results.Add(new(item, CopyOutcome.Success, isSkeleton ? "空目录已创建。" : "目录已保留。", dirOut));
                    continue;
                }

                // Reconcile interrupted cross-volume move where target already matches source byte-for-byte.
                if (isMove && item.Note == BothEndsExistMarker && File.Exists(item.Target) && File.Exists(item.Source.Path))
                {
                    var reconciled = TryReconcilePendingMove(item, token);
                    if (reconciled is not null)
                    {
                        var (res, movedRec, pathChange) = reconciled.Value;
                        results.Add(res);
                        movedRecords.Add(movedRec);
                        changes.Add(pathChange);
                        commitDone = true;
                        continue;
                    }
                }

                item = CheckTarget(item, plan, token);
                if (item.Action == CopyAction.Skip)
                {
                    results.Add(new(item, CopyOutcome.Skipped, item.Message ?? "目标已存在，跳过。"));
                    continue;
                }
                if (item.Action == CopyAction.Pending)
                {
                    results.Add(new(item, CopyOutcome.Pending, item.Message ?? "执行时出现新冲突，等待确认。"));
                    continue;
                }

                var targetParent = Path.GetDirectoryName(item.Target)!;
                Directory.CreateDirectory(targetParent);

                if (isMove && transfer is null && WindowsCopyFile.SameVolume(item.Source.Stamp!, targetParent))
                {
                    Emit($"正在移动：{item.Source.Path}", 0, true);
                    string? note = null;
                    string? verifiedHash = null;
                    using (var lockedSource = WindowsCopyFile.LockForRename(item.Source.Path))
                    {
                        var liveStamp = WindowsCopyFile.Stamp(lockedSource);
                        if (!CopyFileSystem.Same(item.Source.Stamp, liveStamp))
                            throw new IOException("源文件在预览后已变化，请重试并核对新计划。");
                        if (plan.Request.VerifyHash || item.Action == CopyAction.Overwrite)
                            verifiedHash = CopyFileSystem.Hash(lockedSource, token);

                        token.ThrowIfCancellationRequested();
                        item = CheckTarget(item, plan, token);
                        if (item.Action is CopyAction.Pending or CopyAction.Skip)
                        {
                            results.Add(new(item, item.Action == CopyAction.Pending ? CopyOutcome.Pending : CopyOutcome.Skipped, item.Message ?? "目标已变化。"));
                            continue;
                        }

                        if (item.Action == CopyAction.Overwrite)
                        {
                            note = ReplaceLockedSource(item, lockedSource, verifiedHash!, token);
                            commitDone = true;
                        }
                        else
                        {
                            for (var attempt = 0; ; attempt++)
                            {
                                token.ThrowIfCancellationRequested();
                                CopyFileSystem.CheckPath(item.Target);
                                if (!CopyFileSystem.Exists(item.Target))
                                {
                                    try { WindowsCopyFile.RenameLocked(lockedSource, item.Target); commitDone = true; break; }
                                    catch (IOException) when (CopyFileSystem.Exists(item.Target) && attempt < 100) { }
                                }
                                if (attempt >= 100) throw new IOException("目标路径持续冲突，未移动文件。");
                                item = CheckTarget(item, plan, token);
                                if (item.Action is CopyAction.Pending or CopyAction.Skip) break;
                            }
                            if (!commitDone)
                            {
                                results.Add(new(item, item.Action == CopyAction.Pending ? CopyOutcome.Pending : CopyOutcome.Skipped, item.Message ?? "目标冲突。"));
                                continue;
                            }
                        }
                    }

                    var movedStamp = CopyFileSystem.Stamp(item.Target, false, CancellationToken.None);
                    var output = new ProducedFile(item.Target, movedStamp.Length, new DateTimeOffset(new DateTime(movedStamp.ModifiedUtcTicks, DateTimeKind.Utc)), [item.Source.Path]);
                    results.Add(new(item, CopyOutcome.Success, note ?? (item.Action == CopyAction.Number ? "已移动，目标名称已自动编号。" : "已移动。"), output));
                    changes.Add(new(item.Source.Path, item.Target, false));
                    movedRecords.Add(new(item.Source.Path, item.Target, movedStamp.Length, movedStamp.ModifiedUtcTicks, movedStamp.Identity, verifiedHash));
                    continue;
                }

                // Copy OR Cross-Volume Move (always verifies SHA-256 when moving across volumes before deleting source!)
                var mustHash = isMove || plan.Request.VerifyHash;
                string? sourceHash = null;
                using (var source = new FileStream(item.Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!CopyFileSystem.Same(item.Source.Stamp, WindowsCopyFile.Stamp(source)))
                        throw new IOException("源文件在预览后已变化，请重试并核对新计划。");

                    temporary = Path.Combine(targetParent, $".fileassistant-copy-{Guid.NewGuid():N}.tmp");
                    var verbLabel = isMove ? "正在跨盘写入" : "正在复制";
                    Emit($"{verbLabel}：{item.Source.Path}", 0, true);
                    void CopyProgress(long bytes)
                    {
                        var firstChunk = fileBytes == 0;
                        var bounded = Math.Clamp(bytes, 0, item.Source.Stamp!.Length);
                        transferred += Math.Max(0, bounded - fileBytes);
                        fileBytes = bounded;
                        Emit($"{verbLabel}：{item.Source.Path}", bounded, firstChunk || bounded == item.Source.Stamp.Length);
                    }

                    if (transfer is null) WindowsCopyFile.Copy(item.Source.Path, temporary, CopyProgress, token);
                    else transfer.Copy(item.Source.Path, temporary, CopyProgress, token);

                    Emit($"正在校验：{item.Source.Path}", item.Source.Stamp!.Length, true);
                    var copied = CopyFileSystem.Stamp(temporary, mustHash, token);
                    if (mustHash) sourceHash = CopyFileSystem.Hash(source, token);
                    if (copied.Length != item.Source.Stamp.Length || (mustHash && copied.Hash != sourceHash))
                        throw new IOException("内容校验未通过，未提交正式文件。");

                    token.ThrowIfCancellationRequested();
                    item = CheckTarget(item, plan, token);
                    if (item.Action is CopyAction.Pending or CopyAction.Skip)
                    {
                        results.Add(new(item, item.Action == CopyAction.Pending ? CopyOutcome.Pending : CopyOutcome.Skipped, item.Message ?? "目标已变化。"));
                        continue;
                    }

                    string? note = null;
                    if (item.Action == CopyAction.Overwrite)
                    {
                        var newHash = copied.Hash ?? CopyFileSystem.Stamp(temporary, true, token).Hash!;
                        note = Replace(item, temporary, newHash, token);
                        commitDone = true;
                    }
                    else
                    {
                        for (var attempt = 0; ; attempt++)
                        {
                            token.ThrowIfCancellationRequested();
                            CopyFileSystem.CheckPath(item.Target);
                            try { File.Move(temporary, item.Target, false); commitDone = true; break; }
                            catch (IOException) when (CopyFileSystem.Exists(item.Target) && attempt < 100)
                            {
                                item = CheckTarget(item, plan, token);
                                if (item.Action is CopyAction.Pending or CopyAction.Skip) break;
                            }
                        }
                        if (!commitDone)
                        {
                            results.Add(new(item, item.Action == CopyAction.Pending ? CopyOutcome.Pending : CopyOutcome.Skipped, item.Message ?? "目标冲突。"));
                            continue;
                        }
                    }

                    if (!isMove)
                    {
                        var output = new ProducedFile(item.Target, copied.Length, new DateTimeOffset(new DateTime(copied.ModifiedUtcTicks, DateTimeKind.Utc)), [item.Source.Path]);
                        results.Add(new(item, CopyOutcome.Success, note ?? (item.Action == CopyAction.Number ? "已复制，目标名称已编号。" : "已复制并校验。"), output));
                        continue;
                    }
                }

                // Cross-volume Move: target is now safely committed & verified; remove source under exclusive delete lock.
                try
                {
                    DeleteVerifiedSource(item.Source.Path, item.Source.Stamp!);
                    var finalStamp = CopyFileSystem.Stamp(item.Target, false, CancellationToken.None);
                    var output = new ProducedFile(item.Target, finalStamp.Length, new DateTimeOffset(new DateTime(finalStamp.ModifiedUtcTicks, DateTimeKind.Utc)), [item.Source.Path]);
                    results.Add(new(item, CopyOutcome.Success, item.Action == CopyAction.Number ? "已跨盘移动并校验，目标名称已编号。" : "已跨盘移动并校验。", output));
                    changes.Add(new(item.Source.Path, item.Target, false));
                    movedRecords.Add(new(item.Source.Path, item.Target, finalStamp.Length, finalStamp.ModifiedUtcTicks, finalStamp.Identity, sourceHash));
                }
                catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    results.Add(new(item with { Note = BothEndsExistMarker }, CopyOutcome.Failed,
                        $"目标已完整写入并校验，但源文件未移除（{BothEndsExistMarker}）：{CopyFileSystem.Friendly(deleteError)}"));
                }
            }
            catch (TargetChangedException error)
            {
                results.Add(new(item with { Action = CopyAction.Pending }, CopyOutcome.Pending, error.Message));
            }
            catch (OperationCanceledException)
            {
                results.Add(new(item, CopyOutcome.Cancelled, "已取消，未提交的临时文件将清理。"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                var message = CopyFileSystem.Friendly(error);
                results.Add(new(item, CopyOutcome.Failed, message));
                if (CopyFileSystem.Fatal(error) || plan.Request.Location == OutputLocation.Fixed && !Directory.Exists(plan.Request.Destination))
                    stopped = message;
            }
            finally
            {
                if (!commitDone && temporary is not null)
                {
                    try { CopyFileSystem.DeleteOwnedTemporary(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        var index = results.Count - 1;
                        if (index >= 0)
                            results[index] = results[index] with { Message = results[index].Message + $" 临时文件清理失败：{temporary}；{error.Message}" };
                    }
                }
                completedBytes += item.Source.Stamp?.Length ?? 0;
                Emit($"已处理 {results.Count:N0}/{plan.Items.Count:N0} 项", 0, true);
            }
        }

        // In Preserve Move mode, remove moved source directories bottom-up ONLY if they are now completely empty.
        if (isMove && plan.Request.Layout == CopyLayout.Preserve)
        {
            foreach (var dirItem in plan.Items.Where(x => x.Source.IsDirectory && x.CanRun).OrderByDescending(x => x.Source.Path.Length))
            {
                try
                {
                    if (Directory.Exists(dirItem.Source.Path) && !Directory.EnumerateFileSystemEntries(dirItem.Source.Path).Any())
                    {
                        Directory.Delete(dirItem.Source.Path, recursive: false);
                        changes.Add(new(dirItem.Source.Path, dirItem.Target, true));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { /* Non-empty or locked source folder stays safely in place. */ }
            }
        }

        if (isMove && movedRecords.Count > 0)
        {
            try
            {
                var moveRecoveryId = Guid.NewGuid();
                var summaryTarget = plan.Request.Location == OutputLocation.Fixed ? plan.Request.Destination : Path.GetDirectoryName(movedRecords[0].MovedPath)!;
                recovery.Save(new CopyRecovery(moveRecoveryId, summaryTarget, "", movedRecords[0].OriginalPath, "", "",
                    DateTimeOffset.UtcNow, "可撤回移动")
                {
                    Kind = CopyRecoveryKind.MoveUndo,
                    MovedItems = movedRecords.ToArray()
                });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                stopped ??= "文件已移动，但保存移动撤回记录失败：" + error.Message;
            }
        }

        return new(plan, results.ToArray(), token.IsCancellationRequested, stopped)
        {
            Changes = changes.ToArray()
        };

        void Emit(string message, long current, bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastTick < 100) return;
            lastTick = clock.ElapsedMilliseconds;
            progress?.Report(new(results.Count, plan.Items.Count, message, Math.Min(plan.CopyBytes, completedBytes + current), plan.CopyBytes,
                transferred / Math.Max(0.1, clock.Elapsed.TotalSeconds)));
        }
    }

    private static (CopyItemResult Result, MovedFileRecord Record, PathChange Change)? TryReconcilePendingMove(CopyPlanItem item, CancellationToken token)
    {
        var targetStamp = CopyFileSystem.Stamp(item.Target, true, token);
        using var lockedSource = WindowsCopyFile.LockForRename(item.Source.Path);
        var sourceStamp = WindowsCopyFile.Stamp(lockedSource);
        if (sourceStamp.Identity == targetStamp.Identity || sourceStamp.Length != targetStamp.Length)
            return null;
        var sourceHash = CopyFileSystem.Hash(lockedSource, token);
        if (sourceHash != targetStamp.Hash)
            return null;

        var tempDel = Path.Combine(Path.GetDirectoryName(item.Source.Path)!, $".fileassistant-movedel-{Guid.NewGuid():N}.tmp");
        WindowsCopyFile.RenameLocked(lockedSource, tempDel);
        lockedSource.Dispose();
        CopyFileSystem.DeleteOwnedTemporary(tempDel);

        var output = new ProducedFile(item.Target, targetStamp.Length, new DateTimeOffset(new DateTime(targetStamp.ModifiedUtcTicks, DateTimeKind.Utc)), [item.Source.Path]);
        return (
            new CopyItemResult(item with { Note = null }, CopyOutcome.Success, "已核对两端内容一致并移除原位置文件。", output),
            new MovedFileRecord(item.Source.Path, item.Target, targetStamp.Length, targetStamp.ModifiedUtcTicks, targetStamp.Identity, targetStamp.Hash),
            new PathChange(item.Source.Path, item.Target, false));
    }

    private static void DeleteVerifiedSource(string sourcePath, FileStamp expectedStamp)
    {
        using var lockedSource = WindowsCopyFile.LockForRename(sourcePath);
        var actual = WindowsCopyFile.Stamp(lockedSource);
        if (!CopyFileSystem.Same(expectedStamp, actual))
            throw new IOException("源文件在复制期间发生变化，已保留两处文件供核对。");
        var tempDel = Path.Combine(Path.GetDirectoryName(sourcePath)!, $".fileassistant-movedel-{Guid.NewGuid():N}.tmp");
        WindowsCopyFile.RenameLocked(lockedSource, tempDel);
        lockedSource.Dispose();
        CopyFileSystem.DeleteOwnedTemporary(tempDel);
    }

    private static CopyPlanItem CheckTarget(CopyPlanItem item, CopyPlan plan, CancellationToken token)
    {
        CopyFileSystem.CheckPath(item.Target);
        var target = CopyFileSystem.Target(item.Target, item.Action == CopyAction.Overwrite || plan.Request.Conflict == CopyConflictPolicy.Confirm, token);
        if (target?.Error is { } error) throw new IOException(error);
        if (item.Source.IsDirectory)
        {
            if (target is { IsDirectory: false }) throw new IOException("目录位置已被文件占用。");
            return item;
        }
        if (target?.Stamp is { } stamp)
        {
            if (stamp.Identity == item.Source.Stamp?.Identity || CopyPlanner.Equal(item.Source.Path, item.Target))
                return item with { Action = CopyAction.Skip, Message = "源和目标是同一个文件。" };
            if (plan.Items.Any(x => CopyPlanner.Equal(x.Source.Path, item.Target) || x.Source.Stamp?.Identity == stamp.Identity))
                throw new IOException("目标是本批次的源文件，请调整位置。");
        }
        if (target is null) return item with { Action = item.Action == CopyAction.Number ? CopyAction.Number : CopyAction.Copy, ApprovedTarget = null };
        if (item.Action == CopyAction.Overwrite && target.Stamp is { } current && CopyFileSystem.Same(item.ApprovedTarget, current)) return item;
        if (plan.Request.Conflict == CopyConflictPolicy.Skip) return item with { Action = CopyAction.Skip, Message = "执行时目标已存在，按规则跳过。" };
        if (plan.Request.Conflict == CopyConflictPolicy.Number || item.Action == CopyAction.Number)
        {
            var next = CopyPlanner.Number(item.Target, false, p => CopyFileSystem.Exists(p) || plan.Items.Any(x => x.Id != item.Id && x.Action != CopyAction.Skip && CopyPlanner.Equal(x.Target, p)));
            return item with { Target = next, Action = CopyAction.Number, ApprovedTarget = null };
        }
        return item with { Action = CopyAction.Pending, ApprovedTarget = target.Stamp, BatchConflict = false, Message = "执行时目标出现或已变化，请重新确认。" };
    }

    private string? Replace(CopyPlanItem item, string temporary, string hash, CancellationToken token)
    {
        using var old = WindowsCopyFile.LockForRename(item.Target);
        var oldStamp = WindowsCopyFile.Stamp(old) with { Hash = CopyFileSystem.Hash(old, token) };
        if (!CopyFileSystem.Same(item.ApprovedTarget, oldStamp)) throw new TargetChangedException("覆盖前目标已变化，等待重新确认。");
        var id = Guid.NewGuid();
        var record = new CopyRecovery(id, item.Target, CopyRecoveryStore.BackupPath(item.Target, id), item.Source.Path,
            hash, oldStamp.Hash!, DateTimeOffset.UtcNow, "准备替换");
        recovery.Save(record);
        token.ThrowIfCancellationRequested();
        WindowsCopyFile.RenameLocked(old, record.Backup);
        try
        {
            recovery.Save(record with { State = "旧目标已备份" });
            // Past this point finish or restore the old name before honoring cancellation.
            File.Move(temporary, item.Target, false);
        }
        catch
        {
            if (!CopyFileSystem.Exists(item.Target)) WindowsCopyFile.RenameLocked(old, item.Target);
            throw;
        }
        try { recovery.Save(record with { State = "已覆盖" }); return "已覆盖并校验；旧目标可在恢复记录中还原。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "已覆盖，备份和预写恢复记录已保留；记录更新失败：" + error.Message; }
    }

    private string? ReplaceLockedSource(CopyPlanItem item, FileStream lockedSource, string sourceHash, CancellationToken token)
    {
        using var old = WindowsCopyFile.LockForRename(item.Target);
        var oldStamp = WindowsCopyFile.Stamp(old) with { Hash = CopyFileSystem.Hash(old, token) };
        if (!CopyFileSystem.Same(item.ApprovedTarget, oldStamp)) throw new TargetChangedException("覆盖前目标已变化，等待重新确认。");
        var id = Guid.NewGuid();
        var record = new CopyRecovery(id, item.Target, CopyRecoveryStore.BackupPath(item.Target, id), item.Source.Path,
            sourceHash, oldStamp.Hash!, DateTimeOffset.UtcNow, "准备替换");
        recovery.Save(record);
        token.ThrowIfCancellationRequested();
        WindowsCopyFile.RenameLocked(old, record.Backup);
        try
        {
            recovery.Save(record with { State = "旧目标已备份" });
            WindowsCopyFile.RenameLocked(lockedSource, item.Target);
        }
        catch
        {
            if (!CopyFileSystem.Exists(item.Target)) WindowsCopyFile.RenameLocked(old, item.Target);
            throw;
        }
        try { recovery.Save(record with { State = "已覆盖" }); return "已移动并覆盖目标；旧目标可在恢复记录中还原。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "已移动并覆盖，备份和预写恢复记录已保留；记录更新失败：" + error.Message; }
    }

    public Task<IReadOnlyList<CopyRecovery>> RecoveriesAsync(CancellationToken token) => Task.Run(recovery.ReadAll, token);
    public Task RestoreAsync(Guid id, CancellationToken token) => RecoveryOperation(() => recovery.Restore(id, token), token);
    public async Task<IReadOnlyList<PathChange>> RestoreWithChangesAsync(Guid id, CancellationToken token)
    {
        await execution.WaitAsync(token);
        try { return await Task.Run(() => recovery.RestoreWithChanges(id, token), token); }
        finally { execution.Release(); }
    }
    public Task PruneRecoveryAsync(DateTimeOffset before, CancellationToken token) => RecoveryOperation(() => recovery.Prune(before, token), token);
    public Task DiscardBackupAsync(Guid id, CancellationToken token) => RecoveryOperation(() => recovery.Discard(id, token), token);
    private async Task RecoveryOperation(Action action, CancellationToken token)
    {
        await execution.WaitAsync(token);
        try { await Task.Run(action, token); }
        finally { execution.Release(); }
    }
    private sealed class TargetChangedException(string message) : IOException(message);
}
