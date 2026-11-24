using System.ComponentModel;
using System.Runtime.InteropServices;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Infrastructure.FileSystem;
using FileAssistant.Infrastructure.Windows;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.Infrastructure.Cleanup;

internal sealed class FileCleanupEngine
{
    private const int MaximumNodes = 100_000;

    public CleanupPlan Plan(IEnumerable<string> paths, IProgress<CleanupProgress>? progress, CancellationToken token)
    {
        var roots = new List<CleanupRoot>();
        var issues = new List<CleanupIssue>();
        var candidates = new List<string>();
        var supplied = paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(501).ToArray();
        if (supplied.Length > 500) throw new IOException("一次最多加入 500 个目标。");
        foreach (var path in supplied)
        {
            token.ThrowIfCancellationRequested();
            try { candidates.Add(CleanupPathPolicy.Check(path)); }
            catch (Exception ex) { issues.Add(new(path, ex.Message)); }
        }
        var selected = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(path =>
            !candidates.Any(parent => !path.Equals(parent, StringComparison.OrdinalIgnoreCase) && CleanupPathPolicy.Within(path, parent))).ToArray();
        for (var i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("正在检查文件", i, selected.Length, selected[i]));
            try { roots.Add(Inspect(selected[i], token)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { issues.Add(new(selected[i], ex.Message)); }
        }
        return new(roots, issues, DateTimeOffset.UtcNow);
    }

    public CleanupResult[] Run(CleanupPlan plan, CleanupMethod method, IProgress<CleanupProgress>? progress, CancellationToken token)
    {
        if (!Enum.IsDefined(method) || plan.Roots.Count > 500) throw new IOException("清理方式或目标数量无效。");
        var results = new List<CleanupResult>();
        foreach (var target in plan.Roots)
        {
            var removed = new List<string>();
            if (token.IsCancellationRequested) { results.Add(new(target.Path, CleanupStatus.Cancelled, "未执行。", removed)); continue; }
            progress?.Report(new(method == CleanupMethod.Recycle ? "正在移入回收站" : "正在永久删除", results.Count, plan.Roots.Count, target.Path));
            try
            {
                CleanupPathPolicy.Check(target.Path);
                using var pinned = PinAncestors(target.Path);
                Verify(target, token);
                if (method == CleanupMethod.Recycle)
                {
                    ShellRecycleOperation.CheckAvailability(Path.GetPathRoot(target.Path)!);
                    ShellRecycleOperation.Recycle(target.Path, 0, path =>
                    {
                        if (!path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("回收目标发生变化。");
                        Verify(target, token);
                    });
                    removed.Add(target.Path);
                }
                else
                {
                    foreach (var node in target.Nodes.OrderByDescending(node => node.RelativePath.Count(ch => ch == Path.DirectorySeparatorChar)).ThenByDescending(node => node.RelativePath.Length))
                    {
                        token.ThrowIfCancellationRequested();
                        var path = node.RelativePath.Length == 0 ? target.Path : Path.Combine(target.Path, node.RelativePath);
                        DeleteNode(path, node);
                        if (!IsGone(path)) throw new IOException("Windows 已接受删除请求，项目仍在等待占用释放，请稍后重新检查。");
                        removed.Add(path);
                    }
                }
                results.Add(new(target.Path, CleanupStatus.Succeeded, method == CleanupMethod.Recycle ? "已移入回收站。" : "已永久删除。", removed));
            }
            catch (OperationCanceledException) { results.Add(new(target.Path, removed.Count == 0 ? CleanupStatus.Cancelled : CleanupStatus.Partial, "已停止；已处理部分不会自动撤销。", removed)); }
            catch (Exception ex) { results.Add(new(target.Path, removed.Count == 0 ? CleanupStatus.Failed : CleanupStatus.Partial, Describe(ex), removed)); }
        }
        return results.ToArray();
    }

    internal static CleanupRoot Inspect(string path, CancellationToken token)
    {
        var nodes = new List<CleanupNode>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            if (nodes.Count >= MaximumNodes) throw new IOException("目录超过本次核对上限（10 万项），请分批处理。");
            var data = SpaceFileMetadata.Read(current);
            if (!data.Stamp.Identity.IsKnown || data.Cloud || data.Stamp.IsReparsePoint)
                throw new IOException("包含链接、云端占位或无法核实身份的项目，未加入可执行清单。");
            var relative = current.Equals(path, StringComparison.Ordinal) ? "" : Path.GetRelativePath(path, current);
            nodes.Add(new(relative, data.Stamp, data.Length));
            if (!data.Stamp.IsDirectory) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child);
        }
        return new(path, nodes);
    }

    private static void Verify(CleanupRoot expected, CancellationToken token)
    {
        if (expected.Nodes.Count == 0 || expected.Nodes.Count > MaximumNodes) throw new IOException("清理清单无效。");
        var actual = Inspect(expected.Path, token);
        var entries = expected.Nodes.ToDictionary(node => node.RelativePath, StringComparer.Ordinal);
        if (actual.Nodes.Count != entries.Count || actual.Nodes.Any(node => !entries.TryGetValue(node.RelativePath, out var previous) || previous != node))
            throw new IOException("文件或目录内容已变化，请重新检查清单。");
    }

    private static void DeleteNode(string path, CleanupNode expected)
    {
        using var pinned = PinAncestors(path);
        using var handle = CreateFileW(@"\\?\" + path, 0x10000 | 0x80, 1, 0, 3, 0x02000000 | 0x00200000 | 0x00100000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var actual = SpaceFileMetadata.Read(handle);
        if (actual.Cloud || actual.Stamp.IsReparsePoint || actual.Stamp.Identity != expected.Stamp.Identity ||
            actual.Stamp.IsDirectory != expected.Stamp.IsDirectory || !actual.Stamp.IsDirectory && (actual.Stamp != expected.Stamp || actual.Length != expected.Length))
            throw new IOException("项目在执行前被替换或修改，未删除。");
        // Delete by the verified handle; do not reopen the path after checking its identity.
        uint flags = 0x1 | 0x10; // FILE_DISPOSITION_DELETE | FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE
        if (!SetFileInformationByHandle(handle, 21, ref flags, sizeof(uint))) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static IDisposable PinAncestors(string path)
    {
        var pins = new HandleSet();
        try
        {
            var parents = new Stack<string>();
            for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current)) parents.Push(current);
            foreach (var parent in parents)
            {
                var handle = CreateFileW(@"\\?\" + parent, 0x80, 3, 0, 3, 0x02000000 | 0x00200000, 0);
                if (handle.IsInvalid) { var error = new Win32Exception(Marshal.GetLastPInvokeError()); handle.Dispose(); throw error; }
                pins.Handles.Add(handle);
                var data = SpaceFileMetadata.Read(handle);
                if (data.Stamp.IsReparsePoint || data.Cloud) throw new IOException("上级目录已变为链接，未执行清理。");
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    internal static bool IsGone(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    internal static string Describe(Exception error) => error is Win32Exception { NativeErrorCode: 5 } || error is UnauthorizedAccessException
        ? "权限不足。可选择管理员权限重新检查后重试。" : error is Win32Exception { NativeErrorCode: 32 or 33 }
        ? "文件被程序占用。请先检查占用并关闭相关程序。" : error.Message;

    private sealed class HandleSet : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);
}
