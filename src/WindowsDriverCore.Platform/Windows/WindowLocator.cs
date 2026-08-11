using System.Runtime.InteropServices;

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
    public bool WaitForInputProcessed(nint handle)
    {
        if (!Exists(handle))
        {
            return false;
        }

        // SYNCHRONISE ON THE WINDOW WITH FOCUS, not on the session's window.
        //
        // Measured 2026-08-11: waiting on the session window changed nothing at
        // all - a 52-character string still read back as "ab". For a packaged
        // application the session window is the ApplicationFrameWindow, which
        // belongs to ApplicationFrameHost.exe, a DIFFERENT process and thread
        // from the application. A WM_NULL there is answered by a queue that never
        // saw the keystrokes, so it returns at once and waits for nothing.
        //
        // The keystrokes go to whatever has keyboard focus, so that is the queue
        // that has to drain.
        nint target = FocusedWindow();
        if (target == 0)
        {
            target = handle;
        }

        // A message queue is ordered, so a synchronous WM_NULL cannot be handled
        // until everything queued before it has been. No sleep and no guess.
        // ABORTIFHUNG and a bounded timeout so a wedged application fails the
        // command rather than hanging the driver.
        return Win32.SendMessageTimeout(
            target, Win32.WM_NULL, 0, 0, Win32.SMTO_ABORTIFHUNG, InputDrainTimeoutMs, out _) != 0;
    }

    /// <summary>The window with keyboard focus, or zero.</summary>
    /// <returns>The focused window on the foreground thread.</returns>
    private static nint FocusedWindow()
    {
        Win32.GuiThreadInfo info = default;
        info.Size = Marshal.SizeOf<Win32.GuiThreadInfo>();

        // Thread 0 means the foreground thread, which is where typed input goes.
        return Win32.GetGUIThreadInfo(0, ref info) ? info.Focus : 0;
    }

    /// <summary>How long to wait for an application to consume queued input.</summary>
    /// <remarks>
    /// Generous, because it bounds a failure rather than a success: a responsive
    /// application returns in microseconds and only a hung one waits this long.
    /// </remarks>
    private const uint InputDrainTimeoutMs = 5000;

    /// <inheritdoc />
    public nint FindMainWindow(int processId)
    {
        if (processId == 0)
        {
            return 0;
        }

        // The same search the launcher uses, minus the "is it new" stage: there
        // is no before-snapshot to compare against here, and the window being
        // looked for is by definition NOT new — it is the one that replaced the
        // handle the session was holding.
        return MainWindowWaiter.FindCurrentWindow(processId);
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
