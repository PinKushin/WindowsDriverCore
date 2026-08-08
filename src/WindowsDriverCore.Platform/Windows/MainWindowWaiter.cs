using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Waits for a launched application to show a top-level window.
/// </summary>
/// <remarks>
/// Two things make this harder than "read Process.MainWindowHandle".
///
/// A packaged application's window belongs to <c>ApplicationFrameHost</c>, not
/// to the process activation returned, so the launched process id never owns a
/// window. The frame window is found instead by snapshotting the frame windows
/// that existed before the launch and looking for a new one.
///
/// Splash screens appear before the real window, and WinAppDriver is documented
/// as mistaking them for the main window — every operation then fails with
/// "no such window" once the splash vanishes. Preferring a window with a title
/// avoids the common case; it is not a complete answer and is stated as such.
/// </remarks>
public sealed class MainWindowWaiter
{
    private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";

    private readonly TimeProvider _time;

    /// <summary>Creates the waiter.</summary>
    /// <param name="time">
    /// Clock, injected so a test can drive the timeout without waiting for it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is null.</exception>
    public MainWindowWaiter(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Frame windows present right now.</summary>
    /// <returns>
    /// The snapshot to pass to <see cref="WaitAsync"/>. Taken before launching so a
    /// packaged application's new frame window can be told from existing ones.
    /// </returns>
    public static IReadOnlySet<nint> SnapshotFrameWindows()
    {
        HashSet<nint> frames = [];

        Win32.EnumWindows((handle, _) =>
        {
            if (ClassNameOf(handle) == ApplicationFrameWindowClass)
            {
                frames.Add(handle);
            }

            return true;
        }, 0);

        return frames;
    }

    /// <summary>
    /// Waits for a window belonging to the process, or a newly appeared frame
    /// window when the process itself owns none.
    /// </summary>
    /// <param name="processId">The launched process.</param>
    /// <param name="framesBeforeLaunch">Snapshot from <see cref="SnapshotFrameWindows"/>.</param>
    /// <param name="timeout">How long to keep looking.</param>
    /// <param name="pollInterval">How often to look.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The window handle, or zero if none appeared in time.</returns>
    /// <remarks>
    /// Polling rather than sleeping on a guess: the loop returns the moment a
    /// window appears, and the interval only bounds how long it might wait past
    /// that. There is no UIA event for "this process acquired a window", so the
    /// alternative is a WinEvent hook — more machinery than this is worth, and a
    /// decision worth revisiting only if the poll shows up in a benchmark.
    ///
    /// The delay goes through the injected <see cref="TimeProvider"/>, so a test
    /// can drive the timeout instantly rather than actually waiting for it.
    /// </remarks>
    public async Task<nint> WaitAsync(
        int processId,
        IReadOnlySet<nint> framesBeforeLaunch,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(framesBeforeLaunch);

        long deadline = _time.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (_time.GetTimestamp() < deadline)
        {
            nint owned = FindVisibleWindowOwnedBy(processId);
            if (owned != 0)
            {
                return owned;
            }

            nint frame = FindNewFrameWindow(framesBeforeLaunch);
            if (frame != 0)
            {
                return frame;
            }

            await Task.Delay(pollInterval, _time, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static nint FindVisibleWindowOwnedBy(int processId)
    {
        if (processId <= 0)
        {
            return 0;
        }

        List<nint> unowned = [];
        List<nint> owned = [];

        Win32.EnumWindows((handle, _) =>
        {
            Win32.GetWindowThreadProcessId(handle, out uint windowProcess);
            if (windowProcess != (uint)processId || !Win32.IsWindowVisible(handle))
            {
                return true;
            }

            // A window with an owner is a dialog or tool window belonging to
            // something else; the main window is unowned. Both are collected so
            // an application that only has owned windows still yields one.
            if (Win32.GetWindow(handle, Win32.GW_OWNER) != 0)
            {
                owned.Add(handle);
            }
            else
            {
                unowned.Add(handle);
            }

            return true;
        }, 0);

        List<nint> candidates = unowned.Count > 0 ? unowned : owned;

        // Prefer a titled window. A splash screen usually has no title, so this
        // avoids the documented WinAppDriver failure where the splash is adopted
        // as the main window and every later command reports "no such window".
        nint titled = candidates.FirstOrDefault(handle => Win32.GetWindowTextLength(handle) > 0);

        return titled != 0 ? titled : candidates.FirstOrDefault();
    }

    private static nint FindNewFrameWindow(IReadOnlySet<nint> before)
    {
        nint found = 0;

        Win32.EnumWindows((handle, _) =>
        {
            if (before.Contains(handle) ||
                !Win32.IsWindowVisible(handle) ||
                ClassNameOf(handle) != ApplicationFrameWindowClass)
            {
                return true;
            }

            found = handle;
            return false;
        }, 0);

        return found;
    }

    private static string ClassNameOf(nint handle)
    {
        char[] buffer = new char[256];
        int length = Win32.GetClassName(handle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
