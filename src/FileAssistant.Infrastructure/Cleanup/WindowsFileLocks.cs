using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FileAssistant.Core.Features.Cleanup;
using FileAssistant.Infrastructure.FileSystem;

namespace FileAssistant.Infrastructure.Cleanup;

internal static class WindowsFileLocks
{
    public static CleanupResponse Find(IEnumerable<string> targets, CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var selected = targets.Distinct(StringComparer.OrdinalIgnoreCase).Take(501).ToArray();
        if (selected.Length > 500) throw new IOException("一次最多检查 500 个目标。");
        var visited = 0;
        foreach (var path in selected)
        {
            try
            {
                var root = CleanupPathPolicy.Check(path);
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out var current))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 100_000) throw new IOException("占用检查已达 10 万项目上限，请缩小范围。");
                    var data = SpaceFileMetadata.Read(current);
                    if (data.Cloud || data.Stamp.IsReparsePoint) { warnings.Add("已跳过链接或云端项目：" + current); continue; }
                    if (data.Stamp.IsDirectory) { foreach (var child in Directory.EnumerateFileSystemEntries(current)) pending.Push(child); }
                    else files.Add(current);
                    if (files.Count >= 10_000) { warnings.Add("占用检查最多覆盖 1 万个文件；其余项目请缩小范围后检查。"); break; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add(path + "：" + ex.Message); }
            if (files.Count >= 10_000 || visited > 100_000) break;
        }
        if (files.Count == 0) return new() { Warnings = warnings.ToArray() };
        var key = new StringBuilder(33);
        var error = RmStartSession(out var session, 0, key);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            foreach (var batch in files.Chunk(128))
            {
                token.ThrowIfCancellationRequested();
                error = RmRegisterResources(session, (uint)batch.Length, batch, 0, 0, 0, 0);
                if (error != 0) throw new Win32Exception(error);
            }
            uint count = 0, needed = 0, reasons = 0;
            error = RmGetList(session, out needed, ref count, null, ref reasons);
            for (var attempt = 0; attempt < 4 && error == 234; attempt++)
            {
                if (needed > 10_000) throw new IOException("占用进程列表超出限制。");
                var info = new ProcessInfo[needed]; count = needed;
                error = RmGetList(session, out needed, ref count, info, ref reasons);
                if (error != 0) continue;
                var result = new List<CleanupLock>();
                foreach (var item in info.Take((int)count))
                {
                    var executable = "";
                    try { using var process = Process.GetProcessById(item.Process.Id); executable = process.MainModule?.FileName ?? ""; } catch { }
                    var canClose = item.Process.Id > 4 && item.Process.Id != Environment.ProcessId && item.Type != 1000 &&
                        string.IsNullOrEmpty(item.ServiceName) && executable.Length > 0 &&
                        !CleanupPathPolicy.Within(executable, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) &&
                        !CleanupPathPolicy.Within(executable, AppContext.BaseDirectory);
                    result.Add(new(item.Process.Id, item.Process.StartTime, item.Name ?? item.Process.Id.ToString(), executable, canClose));
                }
                return new() { Locks = result.ToArray(), Warnings = warnings.ToArray() };
            }
            if (error != 0) throw new Win32Exception(error);
            return new() { Warnings = warnings.ToArray() };
        }
        finally { RmEndSession(session); }
    }

    public static CleanupResponse Close(string[] paths, CleanupLock[] selected, bool terminate, CancellationToken token)
    {
        var current = Find(paths, token);
        var results = new List<CleanupResult>();
        if (selected.Length > 100) throw new IOException("一次最多关闭 100 个程序。");
        foreach (var item in selected.DistinctBy(item => (item.ProcessId, item.StartTime)))
        {
            if (token.IsCancellationRequested) { results.Add(new(item.Name, CleanupStatus.Cancelled, "未请求关闭。", [])); continue; }
            try
            {
                if (!current.Locks.Any(found => found.ProcessId == item.ProcessId && found.StartTime == item.StartTime && found.CanClose))
                    throw new IOException("该程序已不再占用所选文件，或不支持在这里关闭。");
                using var process = Process.GetProcessById(item.ProcessId);
                if (process.StartTime.ToUniversalTime().ToFileTimeUtc() != item.StartTime) throw new IOException("进程身份已变化，未关闭。");
                if (terminate) process.Kill();
                else if (!process.CloseMainWindow()) throw new IOException("程序没有可关闭的窗口。可在确认已保存工作后选择结束进程。");
                for (var i = 0; i < 20 && !process.WaitForExit(100) && !token.IsCancellationRequested; i++) { }
                results.Add(new(item.Name, process.HasExited ? CleanupStatus.Succeeded : CleanupStatus.NeedsAttention,
                    process.HasExited ? "程序已退出，请重新检查文件清单。" : "已请求关闭，请在程序中处理保存或确认窗口。", []));
            }
            catch (Exception ex) { results.Add(new(item.Name, CleanupStatus.Failed, ex.Message, [])); }
        }
        return new() { Results = results.ToArray(), Warnings = current.Warnings, Cancelled = token.IsCancellationRequested };
    }

    // Win32 FILETIME is two DWORDs (4-byte alignment), not an aligned 64-bit field.
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct UniqueProcess { public int Id; public long StartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)] private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public int Type; public uint Status, Session; [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint files, string[] paths, uint apps, nint processes, uint services, nint names);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? info, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
}
