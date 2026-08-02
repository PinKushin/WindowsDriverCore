using System.Runtime.InteropServices;
using WindowsDriverCore.Automation.Com;

namespace WindowsDriverCore.Automation.Raw;

/// <summary>
/// Creates UIA conditions from the raw IUIAutomation factory.
/// All conditions are returned as RawCondition wrapping IntPtr.
/// </summary>
public static class ConditionFactory
{
    private static IUIAutomation? _automation;

    public static void Initialize(IUIAutomation automation)
    {
        _automation = automation;
    }

    public static RawCondition CreatePropertyCondition(int propertyId, string value)
    {
        EnsureInitialized();
        int hr = _automation!.CreatePropertyCondition(propertyId, value, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    public static RawCondition CreatePropertyCondition(int propertyId, int value)
    {
        EnsureInitialized();
        int hr = _automation!.CreatePropertyCondition(propertyId, value, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    public static RawCondition CreatePropertyCondition(int propertyId, bool value)
    {
        EnsureInitialized();
        int hr = _automation!.CreatePropertyCondition(propertyId, value, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    public static RawCondition CreateAndCondition(RawCondition cond1, RawCondition cond2)
    {
        EnsureInitialized();
        int hr = _automation!.CreateAndCondition(cond1.ConditionPtr, cond2.ConditionPtr, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    public static RawCondition CreateOrCondition(RawCondition cond1, RawCondition cond2)
    {
        EnsureInitialized();
        int hr = _automation!.CreateOrCondition(cond1.ConditionPtr, cond2.ConditionPtr, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    public static RawCondition CreateNotCondition(RawCondition condition)
    {
        EnsureInitialized();
        int hr = _automation!.CreateNotCondition(condition.ConditionPtr, out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    public static RawCondition CreateTrueCondition()
    {
        EnsureInitialized();
        int hr = _automation!.CreateTrueCondition(out IntPtr ptr);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);
        return new RawCondition(ptr);
    }

    private static void EnsureInitialized()
    {
        if (_automation is null)
            throw new InvalidOperationException("ConditionFactory not initialized. Call Initialize() first.");
    }
}
