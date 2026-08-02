using System.Runtime.InteropServices;

namespace WindowsDriverCore.Automation.Com;

/// <summary>
/// Minimal IUIAutomationElement — properties + tree navigation we use.
/// All out parameters returning interface pointers use IntPtr for manual marshaling.
/// </summary>
[Guid("D827F2C0-3771-4AD9-872E-F0246972138F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElement
{
    // IUnknown
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    // IUIAutomationElement — methods we use
    [PreserveSig] int SetFocus();
    [PreserveSig] int GetRuntimeId(out IntPtr runtimeId); // SAFEARRAY of int
    [PreserveSig] int FindFirst(int scope, IntPtr condition, out IntPtr element);
    [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr elements);
    [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr element);
    [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr elements);
    [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IntPtr element);
    [PreserveSig] int GetCurrentPropertyValue(int propertyId, out object value);
    [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int flags, out object value);
    [PreserveSig] int GetCachedPropertyValue(int propertyId, out object value);
    [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int flags, out object value);
    [PreserveSig] int GetCurrentPattern(int patternId, out object patternObject);
    [PreserveSig] int GetCachedPattern(int patternId, out object patternObject);
    [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
    [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
    [PreserveSig] int DisconnectPatterns();
    [PreserveSig] int GetSelection(out IntPtr elements);
    [PreserveSig] int GetFocusedDescendant(out IntPtr element);
    [PreserveSig] int GetParent(out IntPtr parent);
    [PreserveSig] int GetChildren(out IntPtr children);
    [PreserveSig] int GetCachedParent(out IntPtr parent);
    [PreserveSig] int GetChildrenCount(out int count);
}
