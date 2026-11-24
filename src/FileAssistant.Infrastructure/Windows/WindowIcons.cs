using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FileAssistant.Infrastructure.Windows;

/// <summary>Owns the two HICONs attached to this HWND. Call on the window thread.</summary>
public sealed class WindowIcons(nint window) : IDisposable
{
    private const uint SetIconMessage = 0x80, GetIconMessage = 0x7F;
    private nint small, large, originalSmall, originalLarge;
    private string? currentPath;
    private uint currentDpi;
    private bool disposed;
    private Action<nint>? selectHandle;

    public void SetIcon(string path, Action<string> selectFile, Action<nint> selectOwnedHandle)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var dpi = GetDpiForWindow(window);
        if (currentPath == path && currentDpi == dpi) return;
        var beforeSmall = Get(0);
        var beforeLarge = Get(1);
        var nextSmall = LoadFile(path, dpi, smallIcon: true);
        nint nextLarge = 0, fileSmall = 0, fileLarge = 0;
        try
        {
            nextLarge = LoadFile(path, dpi, smallIcon: false);
            selectFile(path);
            // The path overload installs a fresh file HICON, often the same handle in both slots.
            // Give AppWindow our owned IconId before detaching and releasing that temporary icon.
            fileSmall = Get(0);
            fileLarge = Get(1);
            try { selectOwnedHandle(nextLarge); }
            catch
            {
                try { selectOwnedHandle(beforeLarge != 0 ? beforeLarge : LoadIcon(0, 32512)); }
                finally { Set(0, beforeSmall); Set(1, beforeLarge); }
                throw;
            }
            Set(0, nextSmall);
            Set(1, nextLarge);
            if (currentPath is null) { originalSmall = beforeSmall; originalLarge = beforeLarge; }
            Release(small);
            Release(large);
            small = nextSmall;
            large = nextLarge;
            nextSmall = nextLarge = 0;
            currentPath = path;
            currentDpi = dpi;
            selectHandle = selectOwnedHandle;
        }
        finally
        {
            // Only the handles introduced synchronously by selectFile belong to this load.
            // Pre-existing, shared, and still-attached window icons remain with their owners.
            ReleaseFileIcon(fileSmall);
            if (fileLarge != fileSmall) ReleaseFileIcon(fileLarge);
            Release(nextSmall);
            Release(nextLarge);
        }

        void ReleaseFileIcon(nint handle)
        {
            if (handle != 0 && handle != beforeSmall && handle != beforeLarge && handle != originalSmall &&
                handle != originalLarge && handle != Get(0) && handle != Get(1)) Release(handle);
        }
    }

    private nint Get(int slot) => SendMessage(window, GetIconMessage, slot, 0);
    private void Set(int slot, nint handle) => SendMessage(window, SetIconMessage, slot, handle);

    internal static nint LoadFile(string path, uint dpi, bool smallIcon)
    {
        if (dpi == 0) dpi = 96;
        var cx = GetSystemMetricsForDpi(smallIcon ? 49 : 11, dpi);
        var cy = GetSystemMetricsForDpi(smallIcon ? 50 : 12, dpi);
        var handle = LoadImage(0, path, 1, cx, cy, 0x10); // IMAGE_ICON, LR_LOADFROMFILE (caller owns it)
        return handle != 0 ? handle : throw new Win32Exception(Marshal.GetLastWin32Error(), path);
    }

    internal static void Release(nint handle) { if (handle != 0) DestroyIcon(handle); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (small != 0 && IsWindow(window))
        {
            var restoreSmall = Get(0) == small;
            var restoreLarge = Get(1) == large;
            if (restoreSmall && restoreLarge && selectHandle is not null)
            {
                try { selectHandle(originalLarge != 0 ? originalLarge : LoadIcon(0, 32512)); }
                catch (Exception error) when (error is COMException or InvalidOperationException) { } // WinUI may already have detached.
            }
            if (restoreSmall) Set(0, originalSmall);
            if (restoreLarge) Set(1, originalLarge);
        }
        selectHandle = null;
        Release(small);
        Release(large);
        small = large = 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string path, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
