using System.Runtime.InteropServices;
using WindowsDriverCore.Automation.Com;

namespace WindowsDriverCore.Automation.Raw;

/// <summary>
/// Managed wrapper around IUIAutomationCondition COM pointer.
/// Stores raw IntPtr — conditions are opaque handles, we never call methods on them.
/// </summary>
public sealed class RawCondition : IDisposable
{
    private IntPtr _conditionPtr;
    private bool _disposed;

    public RawCondition(IntPtr conditionPtr)
    {
        _conditionPtr = conditionPtr;
    }

    /// <summary>
    /// The raw COM condition pointer. Safe to pass to IUIAutomationElement.FindFirst/FindAll.
    /// </summary>
    public IntPtr ConditionPtr => _disposed ? IntPtr.Zero : _conditionPtr;

    public void Dispose()
    {
        if (!_disposed && _conditionPtr != IntPtr.Zero)
        {
            Marshal.Release(_conditionPtr);
            _conditionPtr = IntPtr.Zero;
            _disposed = true;
        }
    }
}
