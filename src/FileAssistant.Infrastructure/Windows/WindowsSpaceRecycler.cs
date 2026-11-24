using System.ComponentModel;
using System.Diagnostics;
using FileAssistant.Core.Features.Organize;
using FileAssistant.Core.Features.SpaceAnalysis;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Windows;

public sealed class WindowsSpaceRecycler(Func<nint>? getOwner = null) : ISpaceRecycler
{
    public Task<SpaceRecycleReport> RecycleAsync(SpaceSnapshot snapshot, IReadOnlyList<int> selection,
        IProgress<SpaceRecycleProgress>? progress, CancellationToken token)
    {
        var owner = getOwner?.Invoke() ?? 0;
        var selected = snapshot.CollapseSelection(selection);
        var result = new TaskCompletionSource<SpaceRecycleReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { result.SetResult(Recycle(snapshot, selected, progress, token, owner)); }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true, Name = "FileAssistant Recycle Bin" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return result.Task;
    }

    public void OpenRecycleBin() => Process.Start(new ProcessStartInfo("shell:RecycleBinFolder") { UseShellExecute = true });

    private static SpaceRecycleReport Recycle(SpaceSnapshot snapshot, int[] selected,
        IProgress<SpaceRecycleProgress>? progress, CancellationToken token, nint owner)
    {
        var before = SpaceScanner.ReadVolume(snapshot.Root)?.FreeBytes;
        var results = new List<SpaceRecycleItem>(selected.Length);
        string? availableRoot = null;
        foreach (var index in selected)
        {
            var path = snapshot.PathOf(index);
            var entry = snapshot.Entries[index];
            var status = SpaceRecycleStatus.Failed;
            string message;
            try
            {
                token.ThrowIfCancellationRequested();
                var root = CheckLocation(path);
                if (!string.Equals(root, availableRoot, StringComparison.OrdinalIgnoreCase))
                {
                    ShellRecycleOperation.CheckAvailability(root);
                    availableRoot = root;
                }
                VerifyTarget(snapshot, index, path);
                token.ThrowIfCancellationRequested();
                ShellRecycleOperation.Recycle(path, owner, shellPath =>
                {
                    token.ThrowIfCancellationRequested();
                    Verify(snapshot, index, shellPath, token);
                });
                status = SpaceRecycleStatus.Recycled;
                message = "已移入回收站";
            }
            catch (OperationCanceledException)
            {
                status = SpaceRecycleStatus.Cancelled;
                message = "已取消";
            }
            catch (Exception error) when (SpaceScanner.IsFileError(error) || error is System.Runtime.InteropServices.COMException)
            {
                message = error is Win32Exception { NativeErrorCode: 2 or 3 or 303 }
                    ? "项目已移动、删除或被替换，请重新扫描。" : error.Message;
            }
            results.Add(new(path, status, message, entry.CountedBytes));
            progress?.Report(new(results.Count, selected.Length, path));
        }
        return new(results.AsReadOnly(), before, SpaceScanner.ReadVolume(snapshot.Root)?.FreeBytes);
    }

    private static string CheckLocation(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (root.Length != 3 || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("此位置不支持应用的回收站操作。");
        if (!string.Equals(path, CopyPlanner.Normalize(path), StringComparison.Ordinal) ||
            Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(name => name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("此路径不支持移入回收站。");
        var running = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (CopyPlanner.Within(path, running) || CopyPlanner.Within(running, path))
            throw new IOException("此项目属于当前运行的应用，或包含当前运行目录。");
        return root;
    }

    private static void Verify(SpaceSnapshot snapshot, int index, string path, CancellationToken token)
    {
        VerifyTarget(snapshot, index, path);
        if (!snapshot.Entries[index].IsDirectory) return;

        // Recheck descendants too: editing a deeply nested file need not change the selected folder's timestamp.
        var current = new SpaceScanner().Scan(path, null, token);
        token.ThrowIfCancellationRequested();
        if (current.IsPartial || !Matches(snapshot.Entries[index], current.Entries[0]))
            throw new IOException("目录已变化或无法完整核对，请重新扫描后选择。");

        var pending = new Stack<(int Old, int Current)>();
        pending.Push((index, 0));
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            var children = current.Children(folder.Current).ToDictionary(child => current.Entries[child].Name, StringComparer.Ordinal);
            foreach (var child in snapshot.Children(folder.Old))
            {
                token.ThrowIfCancellationRequested();
                var expected = snapshot.Entries[child];
                if (!children.Remove(expected.Name, out var found) || !Matches(expected, current.Entries[found]))
                    throw new IOException("目录中的项目已变化，请重新扫描后选择。");
                if (expected.IsDirectory) pending.Push((child, found));
            }
            if (children.Count > 0) throw new IOException("目录中新增了项目，请重新扫描后选择。");
        }
    }

    private static void VerifyTarget(SpaceSnapshot snapshot, int index, string path)
    {
        SpaceScanner.CheckAncestors(path);
        var actual = SpaceFileMetadata.Read(path);
        var expected = snapshot.Entries[index];
        if (!expected.CanRecycle || actual.Cloud || expected.Stamp != actual.Stamp ||
            !expected.IsDirectory && (expected.LogicalBytes != actual.Length || expected.AllocatedBytes != actual.Allocation))
            throw new IOException("项目已被替换或修改，请重新扫描后选择。");

        // Check folder identities as well, even when a file's name and metadata still match.
        var parentPath = Path.GetDirectoryName(path)!;
        for (var parent = expected.Parent; parent >= 0; parent = snapshot.Entries[parent].Parent)
        {
            var stamp = SpaceFileMetadata.Read(parentPath).Stamp;
            if (!stamp.IsDirectory || stamp.IsReparsePoint || !stamp.Identity.IsKnown || stamp.Identity != snapshot.Entries[parent].Stamp.Identity)
                throw new IOException("上级目录已变化，请重新扫描后选择。");
            parentPath = Path.GetDirectoryName(parentPath)!;
        }
    }

    private static bool Matches(SpaceEntry expected, SpaceEntry actual) => expected.Stamp.Identity.IsKnown &&
        expected.Stamp == actual.Stamp && !actual.IsSkipped && !actual.IsPartial && !actual.IsCloud &&
        (expected.IsDirectory || expected.LogicalBytes == actual.LogicalBytes && expected.AllocatedBytes == actual.AllocatedBytes);
}
