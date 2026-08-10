namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Answers questions about top-level windows using Win32.
/// </summary>
public sealed class WindowLocator : IWindowLocator
{
    /// <inheritdoc />
    public nint DesktopWindow => Win32.GetDesktopWindow();

    /// <inheritdoc />
    public bool Exists(nint handle) => handle != 0 && Win32.IsWindow(handle);

    /// <inheritdoc />
    public int GetOwningProcessId(nint handle)
    {
        if (!Exists(handle))
        {
            return 0;
        }

        // The return value is the THREAD id, not the process id — reading it as
        // the process id is the classic misuse of this call. The process arrives
        // through the out parameter.
        Win32.GetWindowThreadProcessId(handle, out uint processId);
        return (int)processId;
    }

    /// <inheritdoc />
    public string GetTitle(nint handle)
    {
        if (!Exists(handle))
        {
            return string.Empty;
        }

        // Length first, then one read. A fixed buffer would truncate a long
        // title silently, which is the sort of thing that goes unnoticed until a
        // client matches on it.
        int length = Win32.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        int written = Win32.GetWindowText(handle, buffer, buffer.Length);

        return written > 0 ? new string(buffer, 0, written) : string.Empty;
    }

    /// <inheritdoc />
    public WindowBounds? GetBounds(nint handle)
    {
        if (!Exists(handle) || !Win32.GetWindowRect(handle, out Win32.Rect rect))
        {
            return null;
        }

        return new WindowBounds(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    /// <inheritdoc />
    public bool SetBounds(nint handle, WindowBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        return Exists(handle) &&
               Win32.SetWindowPos(
                   handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                   Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <inheritdoc />
    public bool Maximize(nint handle)
    {
        if (!Exists(handle))
        {
            return false;
        }

        // ShowWindow returns whether the window WAS visible before, not whether
        // the call worked, so its result says nothing useful here.
        Win32.ShowWindow(handle, Win32.SW_MAXIMIZE);
        return true;
    }

    /// <inheritdoc />
    public bool Close(nint handle) =>
        Exists(handle) && Win32.PostMessage(handle, Win32.WM_CLOSE, 0, 0);

    /// <inheritdoc />
    public bool BringToForeground(nint handle)
    {
        if (!Exists(handle))
        {
            return false;
        }

        nint foreground = Win32.GetForegroundWindow();
        if (foreground == handle)
        {
            return true;
        }

        // Windows grants the right to foreground a window to whoever owns the
        // foreground input queue. A driver running in the background has to join
        // that queue, take the window, and detach — SetForegroundWindow alone
        // is silently ignored, which is why the Focus rung has been failing.
        uint us = Win32.GetCurrentThreadId();
        uint them = foreground == 0 ? 0 : Win32.GetWindowThreadProcessId(foreground, out _);
        bool attached = them != 0 && them != us && Win32.AttachThreadInput(us, them, true);

        try
        {
            Win32.ShowWindow(handle, Win32.SW_RESTORE);
            Win32.BringWindowToTop(handle);
            Win32.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                Win32.AttachThreadInput(us, them, false);
            }
        }

        // Reports what actually happened rather than what was attempted. The
        // shell can still refuse, and a caller that types into a window it only
        // ASKED to foreground would type into somebody else's.
        return Win32.GetForegroundWindow() == handle;
    }
}
