using System.Runtime.InteropServices;

namespace FileAssistant.Infrastructure.Windows;

internal static class ShellRecycleOperation
{
    private static readonly Guid FileOperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const uint Silent = 0x0004;
    private const uint AllowUndo = 0x0040;
    private const uint NoErrorUi = 0x0400;
    private const uint NoConnectedElements = 0x2000;
    private const uint WarnBeforePermanentDelete = 0x4000;
    private const uint RecycleOnDelete = 0x00080000;
    private const uint EarlyFailure = 0x00100000;
    private const uint TransferRecycle = 0x0080;
    private const int Aborted = unchecked((int)0x800704C7);
    private const int CopyEngineCancelled = unchecked((int)0x80270000);
    private const int StopOperation = unchecked((int)0x80004005);

    public static void CheckAvailability(string root)
    {
        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBinW(root, ref info) < 0)
            throw new IOException("Windows 无法为该磁盘提供回收站。");
    }

    public static void Recycle(string path, nint owner, Action<string> verify)
    {
        var iid = ShellItemInterface;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, in iid, out var item));
        IFileOperation? operation = null;
        var sink = new RecycleSink(path, verify);
        try
        {
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationClass, throwOnError: true)!)!;
            if (owner != 0) operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(Silent | AllowUndo | NoErrorUi | NoConnectedElements |
                WarnBeforePermanentDelete | RecycleOnDelete | EarlyFailure);
            operation.DeleteItem(item, sink);
            var result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);

            if (sink.Recycled) return;
            if (sink.Error is { } error) throw error;
            if (result is Aborted or CopyEngineCancelled || sink.Result is Aborted or CopyEngineCancelled)
                throw new OperationCanceledException();
            if (sink.Result < 0 || result < 0)
                throw new IOException($"Windows 未能移入回收站（0x{(sink.Result < 0 ? sink.Result : result):X8}）。");
            if (aborted) throw new OperationCanceledException();
            throw new IOException("Windows 未确认项目已进入回收站。请核对原位置。");
        }
        finally
        {
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
            Marshal.Release(item);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(string path, Action<string> verify) : IProgressSink
    {
        private bool started;
        public bool Recycled { get; private set; }
        public int Result { get; private set; }
        public Exception? Error { get; private set; }

        public int PreDeleteItem(uint flags, nint item)
        {
            try
            {
                if (started || (flags & TransferRecycle) == 0)
                    throw new IOException("Windows 无法将该项目移入回收站，已停止该操作。");
                started = true;
                // Reject recursion or Shell expansion to another item.
                var shellItem = (IShellItem)Marshal.GetObjectForIUnknown(item);
                string? shellPath;
                try
                {
                    shellItem.GetDisplayName(0x80058000, out var name);
                    try { shellPath = Marshal.PtrToStringUni(name); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Marshal.ReleaseComObject(shellItem); }
                if (shellPath is null || !string.Equals(path, shellPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(path), Path.GetFileName(shellPath), StringComparison.Ordinal))
                    throw new IOException("Windows 返回的项目路径已变化，已停止该操作。");
                verify(shellPath);
                return 0;
            }
            catch (Exception error)
            {
                Error = error;
                return StopOperation;
            }
        }

        public int PostDeleteItem(uint flags, nint item, int result, nint destination)
        {
            Result = result;
            Recycled = started && Error is null && result >= 0 && destination != 0;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, nint item, nint name) => StopOperation;
        public int PostRenameItem(uint flags, nint item, nint name, int result, nint created) => 0;
        public int PreMoveItem(uint flags, nint item, nint destination, nint name) => StopOperation;
        public int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created) => 0;
        public int PreCopyItem(uint flags, nint item, nint destination, nint name) => StopOperation;
        public int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created) => 0;
        public int PreNewItem(uint flags, nint destination, nint name) => StopOperation;
        public int PostNewItem(uint flags, nint destination, nint name, nint template, uint attributes, int result, nint created) => 0;
        public int UpdateProgress(uint total, uint done) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    // Method order follows the Windows Shell interfaces.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(nint message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint changes);
        void SetOwnerWindow(nint window);
        void ApplyPropertiesToItem(nint item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(nint item, nint name, nint sink);
        void RenameItems(nint items, nint name);
        void MoveItem(nint item, nint destination, nint name, nint sink);
        void MoveItems(nint items, nint destination);
        void CopyItem(nint item, nint destination, nint name, nint sink);
        void CopyItems(nint items, nint destination);
        void DeleteItem(nint item, IProgressSink sink);
        void DeleteItems(nint items);
        void NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint name);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint name, int result, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint created);
        [PreserveSig] int PreNewItem(uint flags, nint destination, nint name);
        [PreserveSig] int PostNewItem(uint flags, nint destination, nint name, nint template, uint attributes, int result, nint created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
        void GetParent(out nint parent);
        void GetDisplayName(uint kind, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(nint item, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinInfo { public uint Size; public long Bytes, Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBinW(string root, ref RecycleBinInfo info);
}
