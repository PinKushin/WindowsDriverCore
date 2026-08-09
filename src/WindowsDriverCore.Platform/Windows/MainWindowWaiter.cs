using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Waits for a launched application to show a top-level window.
/// </summary>
/// <remarks>
/// <para>
/// Harder than reading <c>Process.MainWindowHandle</c>, because for several
/// common applications the process that was launched never owns a window.
/// </para>
/// <para>
/// A packaged application's window belongs to <c>ApplicationFrameHost</c>. On
/// Windows 11 <c>C:\Windows\System32\notepad.exe</c> is a stub that starts the
/// packaged Notepad and exits, so the launched process is gone before a window
/// appears — and because Notepad is WinUI 3, that window is not an
/// <c>ApplicationFrameWindow</c> either. Looking only for windows owned by the
/// launched process, or only for new frame windows, misses both.
/// </para>
/// <para>
/// So the search widens in stages: a window owned by the launched process, then
/// one owned by any of its descendants, then any top-level window that did not
/// exist before the launch. The last stage is the loosest and is deliberately
/// last — on a busy desktop it could in principle adopt an unrelated window that
/// happened to open at the same moment.
/// </para>
/// </remarks>
public sealed class MainWindowWaiter
{
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

    /// <summary>Visible top-level windows present right now.</summary>
    /// <returns>
    /// The snapshot to pass to <see cref="WaitAsync"/>. Taken before launching,
    /// so a window that appears afterwards can be recognised as new even when it
    /// belongs to a process this driver never started.
    /// </returns>
    public static IReadOnlySet<nint> SnapshotTopLevelWindows()
    {
        HashSet<nint> windows = [];

        Win32.EnumWindows((handle, _) =>
        {
            if (Win32.IsWindowVisible(handle))
            {
                windows.Add(handle);
            }

            return true;
        }, 0);

        return windows;
    }

    /// <summary>Waits for the application's window to appear.</summary>
    /// <param name="processId">The launched process.</param>
    /// <param name="windowsBeforeLaunch">Snapshot from <see cref="SnapshotTopLevelWindows"/>.</param>
    /// <param name="timeout">How long to keep looking.</param>
    /// <param name="pollInterval">How often to look.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The window handle, or zero if none appeared in time.</returns>
    /// <remarks>
    /// Polling rather than sleeping on a guess: the loop returns the moment a
    /// window appears, and the interval only bounds how long it might wait past
    /// that. The delay goes through the injected <see cref="TimeProvider"/>, so a
    /// test can drive the timeout instantly rather than actually waiting.
    /// </remarks>
    public async Task<nint> WaitAsync(
        int processId,
        IReadOnlySet<nint> windowsBeforeLaunch,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(windowsBeforeLaunch);

        long deadline = _time.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (_time.GetTimestamp() < deadline)
        {
            nint window = FindWindow(processId, windowsBeforeLaunch);
            if (window != 0)
            {
                return window;
            }

            await Task.Delay(pollInterval, _time, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static nint FindWindow(int processId, IReadOnlySet<nint> before)
    {
        nint owned = FindVisibleWindowOwnedByAnyOf([processId]);
        if (owned != 0)
        {
            return owned;
        }

        HashSet<int> descendants = DescendantsOf(processId);
        if (descendants.Count > 0)
        {
            nint byDescendant = FindVisibleWindowOwnedByAnyOf(descendants);
            if (byDescendant != 0)
            {
                return byDescendant;
            }
        }

        return FindNewTopLevelWindow(before);
    }

    /// <summary>
    /// Processes whose parent is <paramref name="processId"/>, transitively.
    /// </summary>
    /// <remarks>
    /// A launcher stub starts the real application as a child and exits, so by
    /// the time the window exists the parent may already be gone. The snapshot
    /// is taken from the live process table, which still records the parent id
    /// for a short while afterwards.
    /// </remarks>
    private static HashSet<int> DescendantsOf(int processId)
    {
        Dictionary<int, int> parents = ProcessTable.ParentsByProcessId();
        HashSet<int> descendants = [];
        Queue<int> pending = new();
        pending.Enqueue(processId);

        while (pending.Count > 0)
        {
            int current = pending.Dequeue();
            foreach ((int child, int parent) in parents)
            {
                if (parent == current && descendants.Add(child))
                {
                    pending.Enqueue(child);
                }
            }
        }

        return descendants;
    }

    private static nint FindVisibleWindowOwnedByAnyOf(IReadOnlyCollection<int> processIds)
    {
        List<nint> unowned = [];
        List<nint> owned = [];

        Win32.EnumWindows((handle, _) =>
        {
            Win32.GetWindowThreadProcessId(handle, out uint windowProcess);
            if (!processIds.Contains((int)windowProcess) || !Win32.IsWindowVisible(handle))
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

        return PreferTitled(unowned.Count > 0 ? unowned : owned);
    }

    private static nint FindNewTopLevelWindow(IReadOnlySet<nint> before)
    {
        List<nint> appeared = [];

        Win32.EnumWindows((handle, _) =>
        {
            if (!before.Contains(handle) &&
                Win32.IsWindowVisible(handle) &&
                Win32.GetWindow(handle, Win32.GW_OWNER) == 0)
            {
                appeared.Add(handle);
            }

            return true;
        }, 0);

        return PreferTitled(appeared);
    }

    /// <summary>
    /// Prefers a window with a title.
    /// </summary>
    /// <remarks>
    /// A splash screen usually has none, which avoids the failure WinAppDriver
    /// documents: adopting the splash as the main window, after which every
    /// command reports "no such window" once it vanishes. Not a complete answer
    /// — a splash with a title still wins — and stated as such rather than
    /// presented as a fix.
    /// </remarks>
    private static nint PreferTitled(List<nint> candidates)
    {
        nint titled = candidates.FirstOrDefault(handle => Win32.GetWindowTextLength(handle) > 0);
        return titled != 0 ? titled : candidates.FirstOrDefault();
    }
}
