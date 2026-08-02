using System.Runtime.InteropServices;

namespace WindowsDriverCore.Automation.Com;

/// <summary>
/// Creates IUIAutomation COM instances. Wraps CoCreateInstance for CUIAutomation.
/// </summary>
public static class UIAutomationFactory
{
    private static readonly Guid CUIAutomationClsid = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid iid,
        out IntPtr ppv);

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint CLSCTX_ALL = 0x17;

    /// <summary>
    /// Creates a new IUIAutomation instance via CoCreateInstance.
    /// </summary>
    public static IUIAutomation Create()
    {
        var iid = new Guid("14314595-B0AD-4A2C-B385-AC53C31A1D25");
        var clsid = CUIAutomationClsid;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        return (IUIAutomation)Marshal.GetObjectForIUnknown(ptr);
    }
}
