namespace WindowsDriverCore.Windows;

public interface IWindowFinder
{
    IntPtr FindWindowByProcessId(int processId);
    IntPtr FindDesktopWindow();
    bool IsWindowValid(IntPtr hWnd);
    int GetWindowProcessId(IntPtr hWnd);
    string GetWindowTitle(IntPtr hWnd);
    string GetWindowClassName(IntPtr hWnd);
    bool IsWindowVisible(IntPtr hWnd);
    bool IsTopLevelWindow(IntPtr hWnd);
    IntPtr FindNewApplicationFrameWindow(int processId, HashSet<IntPtr> excludeWindows);
}
