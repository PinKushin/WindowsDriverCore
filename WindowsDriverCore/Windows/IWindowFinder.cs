namespace WindowsDriverCore.Windows;

public interface IWindowFinder
{
    IntPtr FindWindowByProcessId(int processId);
}
