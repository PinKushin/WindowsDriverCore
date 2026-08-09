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
}
