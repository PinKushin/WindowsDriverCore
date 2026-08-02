using System.Runtime.InteropServices;

namespace WindowsDriverCore.Automation.Com;

/// <summary>
/// Minimal IUIAutomation COM interface — only the methods we actually call.
/// Vtable layout matches UIAutomationClient.dll for the methods defined.
/// All out parameters returning interface pointers use IntPtr for manual marshaling.
/// </summary>
[Guid("14314595-B0AD-4A2C-B385-AC53C31A1D25")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomation
{
    // IUnknown
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    // IUIAutomation — methods we use, in vtable order
    // All interface-pointer out params are IntPtr for manual QI + marshaling.
    [PreserveSig] int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    [PreserveSig] int CompareRuntimeIds([In] int[] runtimeId1, [In] int[] runtimeId2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    [PreserveSig] int GetRootElement(out IntPtr element);
    [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr element);
    [PreserveSig] int ElementFromPoint(int x, int y, out IntPtr element);
    [PreserveSig] int GetFocusedElement(out IntPtr element);
    [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IntPtr condition);
    [PreserveSig] int CreatePropertyConditionEx(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, int flags, out IntPtr condition);
    [PreserveSig] int CreateAndCondition(IntPtr condition1, IntPtr condition2, out IntPtr condition);
    [PreserveSig] int CreateOrCondition(IntPtr condition1, IntPtr condition2, out IntPtr condition);
    [PreserveSig] int CreateNotCondition(IntPtr condition, out IntPtr notCondition);
    [PreserveSig] int CreatePropertyConditionFromArray(int propertyId, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct)] object[] valueArray, out IntPtr condition);
    [PreserveSig] int CreatePropertyConditionFromInterfaceArray(int propertyId, [In, MarshalAs(UnmanagedType.LPArray)] object[] nativeArray, out IntPtr condition);
    [PreserveSig] int CreateAndConditionFromArray([In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] conditions, out IntPtr andCondition);
    [PreserveSig] int CreateAndConditionFromNativeArray([In, MarshalAs(UnmanagedType.LPArray)] object[] conditions, out IntPtr andCondition);
    [PreserveSig] int CreateOrConditionFromArray([In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] conditions, out IntPtr orCondition);
    [PreserveSig] int CreateOrConditionFromNativeArray([In, MarshalAs(UnmanagedType.LPArray)] object[] conditions, out IntPtr orCondition);
    [PreserveSig] int CreateTrueCondition(out IntPtr condition);
    [PreserveSig] int CreateFalseCondition(out IntPtr condition);
}
