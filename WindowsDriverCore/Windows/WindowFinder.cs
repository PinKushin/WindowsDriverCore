using System.Text;

namespace WindowsDriverCore.Windows;

public class WindowFinder : IWindowFinder
{
    private const string ApplicationFrameWindowClass = "ApplicationFrameWindow";
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    public IntPtr FindWindowByProcessId(int processId)
    {
        if (processId <= 0)
            return IntPtr.Zero;

        // Step 1: Win32 path — find a visible top-level window owned by this process
        var win32Result = FindWin32WindowByProcessId(processId);
        if (win32Result != IntPtr.Zero)
            return win32Result;

        // Step 2: UWP path — find ApplicationFrameWindow hosting a CoreWindow for this process
        var uwpResult = FindUwpWindowByProcessId(processId);
        if (uwpResult != IntPtr.Zero)
            return uwpResult;

        return IntPtr.Zero;
    }

    public IntPtr FindDesktopWindow()
    {
        return Win32.GetDesktopWindow();
    }

    public bool IsWindowValid(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && Win32.IsWindow(hWnd);
    }

    public int GetWindowProcessId(IntPtr hWnd)
    {
        Win32.GetWindowThreadProcessId(hWnd, out var pid);
        return pid;
    }

    public string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return string.Empty;

        var length = Win32.GetWindowTextLength(hWnd);
        if (length == 0)
            return string.Empty;

        var sb = new StringBuilder(length + 1);
        Win32.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public bool IsWindowVisible(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && Win32.IsWindowVisible(hWnd);
    }

    public string GetWindowClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return string.Empty;

        var sb = new StringBuilder(256);
        Win32.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public bool IsTopLevelWindow(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && Win32.GetParent(hWnd) == IntPtr.Zero;
    }

    public bool IsOwnedWindow(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && Win32.GetWindow(hWnd, Win32.GW_OWNER) != IntPtr.Zero;
    }

    public bool IsApplicationFrameWindow(IntPtr hWnd)
    {
        return GetWindowClassName(hWnd) == ApplicationFrameWindowClass;
    }

    public IntPtr FindApplicationFrameWindowForProcess(int processId)
    {
        IntPtr result = IntPtr.Zero;

        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd))
                return true;

            if (GetWindowClassName(hWnd) != ApplicationFrameWindowClass)
                return true;

            // Enumerate children of this ApplicationFrameWindow looking for a CoreWindow
            // whose owning process matches our target
            var found = false;
            Win32.EnumChildWindows(hWnd, (childHWnd, _) =>
            {
                if (GetWindowClassName(childHWnd) != CoreWindowClass)
                    return true;

                Win32.GetWindowThreadProcessId(childHWnd, out var childPid);
                if (childPid == processId)
                {
                    found = true;
                    return false; // stop enumerating children
                }

                return true; // continue
            }, IntPtr.Zero);

            if (found)
            {
                result = hWnd;
                return false; // stop enumerating windows
            }

            return true; // continue
        }, IntPtr.Zero);

        return result;
    }

    private IntPtr FindWin32WindowByProcessId(int processId)
    {
        var unownedCandidates = new List<IntPtr>();
        var ownedCandidates = new List<IntPtr>();

        Win32.EnumWindows((hWnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid != processId)
                return true;

            if (!Win32.IsWindowVisible(hWnd))
                return true;

            if (Win32.GetWindow(hWnd, Win32.GW_OWNER) != IntPtr.Zero)
                ownedCandidates.Add(hWnd);
            else
                unownedCandidates.Add(hWnd);

            return true;
        }, IntPtr.Zero);

        var candidates = unownedCandidates.Count > 0 ? unownedCandidates : ownedCandidates;

        // Prefer the window with a title (typically the main window)
        foreach (var hWnd in candidates)
        {
            var length = Win32.GetWindowTextLength(hWnd);
            if (length > 0)
                return hWnd;
        }

        return candidates.FirstOrDefault(IntPtr.Zero);
    }

    private IntPtr FindUwpWindowByProcessId(int processId)
    {
        return FindApplicationFrameWindowForProcess(processId);
    }

    public IntPtr FindNewApplicationFrameWindow(int processId, HashSet<IntPtr> excludeWindows)
    {
        IntPtr result = IntPtr.Zero;

        Win32.EnumWindows((hWnd, _) =>
        {
            if (excludeWindows.Contains(hWnd))
                return true;

            if (!Win32.IsWindowVisible(hWnd))
                return true;

            if (GetWindowClassName(hWnd) != ApplicationFrameWindowClass)
                return true;

            // Check if this frame window has any child owned by our process or its children
            var isRelated = false;
            Win32.EnumChildWindows(hWnd, (childHWnd, _) =>
            {
                var childClass = GetWindowClassName(childHWnd);
                if (childClass == CoreWindowClass || childClass.Contains("Xaml", StringComparison.OrdinalIgnoreCase))
                {
                    Win32.GetWindowThreadProcessId(childHWnd, out var childPid);
                    if (childPid == processId)
                    {
                        isRelated = true;
                        return false;
                    }

                    // Check if childPid is a child of our process
                    try
                    {
                        var children = Win32.GetChildProcessIds(processId);
                        if (children.Contains(childPid))
                        {
                            isRelated = true;
                            return false;
                        }
                    }
                    catch { }
                }
                return true;
            }, IntPtr.Zero);

            if (isRelated)
            {
                result = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }
}
