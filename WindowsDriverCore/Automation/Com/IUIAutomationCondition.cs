using System.Runtime.InteropServices;

namespace WindowsDriverCore.Automation.Com;

/// <summary>
/// Minimal IUIAutomationCondition — base interface for all conditions.
/// </summary>
[Guid("352FFBA8-0973-437C-A6E3-20FA2465F1AC")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationCondition
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();
}

/// <summary>
/// Minimal IUIAutomationElementArray — result of FindAll.
/// </summary>
[Guid("E22AD1C1-A6C6-42AB-BD33-F0DE48F8D512")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElementArray
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();
    [PreserveSig] int GetLength(out int length);
    [PreserveSig] int GetElement(int index, out IntPtr element);
}

/// <summary>
/// Minimal IUIAutomationCacheRequest.
/// </summary>
[Guid("352FFBA8-0973-437C-A6E3-20FA2465F1AC")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationCacheRequest
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();
    [PreserveSig] int AddProperty(int propertyId);
    [PreserveSig] int AddPattern(int patternId);
    [PreserveSig] int get_TreeScope(out int scope);
    [PreserveSig] int put_TreeScope(int scope);
    [PreserveSig] int get_TreeFilter(out IntPtr condition);
    [PreserveSig] int put_TreeFilter(IntPtr condition);
    [PreserveSig] int get_AutomationElementMode(out int mode);
    [PreserveSig] int put_AutomationElementMode(int mode);
}
