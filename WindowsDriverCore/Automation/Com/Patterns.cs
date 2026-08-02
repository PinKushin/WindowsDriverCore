using System.Runtime.InteropServices;

namespace WindowsDriverCore.Automation.Com;

/// <summary>
/// COM interface for the InvokePattern — click/activate an element.
/// </summary>
[Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationInvokePattern
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    [PreserveSig] int Invoke();
}

/// <summary>
/// COM interface for the ValuePattern — get/set text values.
/// </summary>
[Guid("A9468346-2255-4FF4-A07C-75353AE7E3E5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationValuePattern
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    [PreserveSig] int SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int get_Value([MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int get_IsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool isReadOnly);
}

/// <summary>
/// COM interface for the SelectionItemPattern — select items in lists/combo boxes.
/// </summary>
[Guid("A8EFA66A-0FDA-421A-9194-38021F3578EA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationSelectionItemPattern
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    [PreserveSig] int Select();
    [PreserveSig] int AddToSelection();
    [PreserveSig] int RemoveFromSelection();
    [PreserveSig] int get_IsSelected([MarshalAs(UnmanagedType.Bool)] out bool isSelected);
    [PreserveSig] int get_SelectionContainer(out IntPtr element);
}

/// <summary>
/// COM interface for the ExpandCollapsePattern — expand/collapse dropdowns and tree items.
/// </summary>
[Guid("619B0F0D-0936-427C-8936-843FEE4CCFB0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationExpandCollapsePattern
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    [PreserveSig] int Expand();
    [PreserveSig] int Collapse();
    [PreserveSig] int get_ExpandCollapseState(out int state); // ExpandCollapseState enum
}

/// <summary>
/// COM interface for IUIAutomationTreeWalker — navigate the element tree.
/// </summary>
[Guid("7FF910BE-1F38-43AF-B85C-2544156AF937")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationTreeWalker
{
    [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] uint AddRef();
    [PreserveSig] uint Release();

    [PreserveSig] int get_Condition(out IntPtr condition);
    [PreserveSig] int GetParent(IntPtr element, out IntPtr parent);
    [PreserveSig] int GetFirstChild(IntPtr element, out IntPtr child);
    [PreserveSig] int GetLastChild(IntPtr element, out IntPtr child);
    [PreserveSig] int GetNextSibling(IntPtr element, out IntPtr sibling);
    [PreserveSig] int GetPreviousSibling(IntPtr element, out IntPtr sibling);
    [PreserveSig] int Normalize(IntPtr element, out IntPtr normalized);
}
