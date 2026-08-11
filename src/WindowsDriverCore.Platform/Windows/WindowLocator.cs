namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Answers questions about top-level windows using Win32.
/// </summary>
public sealed class WindowLocator : IWindowLocator
{
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

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
    public bool OwnsThePointAt(int x, int y, nint handle)
    {
        if (!Exists(handle))
        {
            return false;
        }

        nint atPoint = Win32.WindowFromPoint(new Win32.Point { X = x, Y = y });
        if (atPoint == 0)
        {
            return false;
        }

        // Compared by ROOT, because WindowFromPoint answers with the deepest child
        // at that point — for any real application that is a control inside the
        // window, never the window itself. Comparing handles directly would refuse
        // every legitimate click.
        return Win32.GetAncestor(atPoint, Win32.GA_ROOT) == Win32.GetAncestor(handle, Win32.GA_ROOT);
    }

    /// <summary>The process an input drain must wait on.</summary>
    /// <param name="handle">The session's window.</param>
    /// <returns>The hosted application's process id.</returns>
    /// <remarks>
    /// <para>
    /// <b>The HOSTED process, never the window's owner.</b> Since a session's
    /// window became the <c>ApplicationFrameWindow</c>, its owner is
    /// <c>ApplicationFrameHost</c> — a broker shared by every UWP window on the
    /// machine, whose idleness says nothing about the application and which may
    /// never be idle at all. Waiting on it is not a wait; it is a formality that
    /// returns.
    /// </para>
    /// <para>
    /// <b>Internal purely so a test can see the choice.</b> It regressed silently
    /// when frame rooting landed — <c>WaitForInputIdle</c> on the broker returns
    /// promptly, so the drain kept reporting success while the input it existed
    /// to wait for went on arriving late. Every test that asserted only on the
    /// drain's return value stayed green, because a wait that always returns is
    /// indistinguishable from no wait at all. The only way to catch that is to
    /// assert which process was chosen.
    /// </para>
    /// </remarks>
    internal int InputTargetProcess(nint handle) => GetHostedProcessId(handle);

    /// <inheritdoc />
    public int GetHostedProcessId(nint handle)
    {
        int owner = GetOwningProcessId(handle);
        if (owner == 0)
        {
            return 0;
        }

        // A frame shows somebody else's content. Its CoreWindow child is the only
        // place the hosted application's process id appears — measured on the
        // Windows 10 guest: frame owned by ApplicationFrameHost 3704, CoreWindow
        // child owned by CalculatorApp 10832, one window.
        int hosted = 0;

        Win32.EnumChildWindows(handle, (child, _) =>
        {
            if (ClassNameOf(child) != CoreWindowClass)
            {
                return true;
            }

            Win32.GetWindowThreadProcessId(child, out uint childProcess);
            hosted = (int)childProcess;
            return false;
        }, 0);

        // The window's own owner when it hosts nothing, which is every classic
        // Win32 and WinUI 3 application — they create and own their window
        // outright and there is no broker in the path at all.
        return hosted != 0 ? hosted : owner;
    }

    /// <summary>The window class, or empty when the handle names nothing.</summary>
    /// <remarks>
    /// Internal rather than private because the launch transcript reports it:
    /// "ApplicationFrameWindow" against "Windows.UI.Core.CoreWindow" is the exact
    /// discriminator behind three claims about the window search that were
    /// credited to the wrong mechanism, and the handle alone cannot show it.
    /// </remarks>
    internal static string ClassNameOf(nint window)
    {
        char[] buffer = new char[256];
        int length = Win32.GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
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

        // WaitForInputIdle, because the alternatives do not work. Measured
        // 2026-08-11 against 52 typed characters:
        //
        //   no wait                          read back 1 of 52
        //   WM_NULL via SendMessageTimeout   read back 1 of 52  (sent messages
        //                                    are delivered AHEAD of queued input)
        //   AttachThreadInput+GetQueueStatus read back 2 of 52  (reported zero
        //                                    pending while 51 were queued)
        //   WaitForInputIdle                 read back 52 of 52, five for five
        //
        // It is process-grained rather than input-grained - it answers "is this
        // process idle", not "has my input been consumed" - so an application
        // busy for its own reasons makes this wait longer than strictly needed.
        // That is a known imprecision, not a proxy for something unobservable.
        int processId = InputTargetProcess(handle);
        if (processId == 0)
        {
            return false;
        }

        nint process = Win32.OpenProcess(
            Win32.PROCESS_QUERY_INFORMATION | Win32.SYNCHRONIZE, false, (uint)processId);

        if (process == 0)
        {
            // An elevated or protected target this driver may not open. The
            // caller carries on rather than failing the command: a missing wait
            // is a race, and refusing the read outright is a certainty.
            return false;
        }

        try
        {
            return Win32.WaitForInputIdle(process, InputDrainTimeoutMs) == 0;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    /// <summary>How long to wait for an application to consume queued input.</summary>
    /// <remarks>
    /// Generous, because it bounds a failure rather than a success: a responsive
    /// application returns in microseconds and only a hung one waits this long.
    /// </remarks>
    private const uint InputDrainTimeoutMs = 5000;

    /// <inheritdoc />
    public bool Close(nint handle) =>
        Exists(handle) && Win32.PostMessage(handle, Win32.WM_CLOSE, 0, 0);

    /// <inheritdoc />
    public bool WaitUntilGone(nint handle)
    {
        // Synchronise on the window actually going, not on a guess about how
        // long that takes. SpinUntil escalates from spinning to yielding, so a
        // fast machine returns at once and a loaded one is not starved by the
        // waiter - the application being closed needs the CPU more than we do.
        return SpinWait.SpinUntil(() => !Exists(handle), CloseTimeoutMs);
    }

    /// <summary>How long to wait for a closing window to disappear.</summary>
    /// <remarks>
    /// Bounds a failure, not a success: a window that closes does so in
    /// milliseconds, and only one that refuses - a "save changes?" prompt -
    /// waits this out.
    /// </remarks>
    private const int CloseTimeoutMs = 5000;

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
