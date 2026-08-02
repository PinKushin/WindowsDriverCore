using System.Runtime.InteropServices;
using WindowsDriverCore.Automation.Com;

namespace WindowsDriverCore.Automation.Raw;

/// <summary>
/// Managed wrapper around IUIAutomationElement COM pointer.
/// Provides clean C# API for element property access and tree navigation.
/// Cheat-tool-level control: no hidden behavior, no exception translation, direct COM calls.
/// </summary>
public sealed class RawAutomationElement : IDisposable
{
    private IntPtr _rawPtr;
    private IUIAutomationElement? _element;
    private bool _disposed;

    public RawAutomationElement(IntPtr rawPtr)
    {
        _rawPtr = rawPtr;
        _element = (IUIAutomationElement)Marshal.GetObjectForIUnknown(rawPtr);
    }

    public RawAutomationElement(IUIAutomationElement element)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _rawPtr = Marshal.GetIUnknownForObject(element);
    }

    /// <summary>
    /// The raw COM pointer. Safe to pass to other COM calls that expect IUIAutomationElement*.
    /// </summary>
    public IntPtr RawPtr => _disposed ? IntPtr.Zero : _rawPtr;

    /// <summary>
    /// The managed COM interface for calling methods. Throws if disposed.
    /// </summary>
    public IUIAutomationElement Element => _element ?? throw new ObjectDisposedException(nameof(RawAutomationElement));

    /// <summary>
    /// True if the COM pointer is still valid.
    /// </summary>
    public bool IsValid => _element is not null && !_disposed;

    // --- Tree Navigation ---

    public RawAutomationElement? FindFirst(int scope, IntPtr conditionPtr)
    {
        int hr = Element.FindFirst(scope, conditionPtr, out IntPtr childPtr);
        if (hr == 0 && childPtr != IntPtr.Zero)
            return new RawAutomationElement(childPtr);
        return null;
    }

    public List<RawAutomationElement> FindAll(int scope, IntPtr conditionPtr)
    {
        int hr = Element.FindAll(scope, conditionPtr, out IntPtr arrayPtr);
        if (hr != 0 || arrayPtr == IntPtr.Zero)
            return new List<RawAutomationElement>();

        var array = (IUIAutomationElementArray)Marshal.GetObjectForIUnknown(arrayPtr);
        Marshal.Release(arrayPtr);

        array.GetLength(out int count);
        var results = new List<RawAutomationElement>(count);
        for (int i = 0; i < count; i++)
        {
            array.GetElement(i, out IntPtr childPtr);
            if (childPtr != IntPtr.Zero)
                results.Add(new RawAutomationElement(childPtr));
        }
        Marshal.ReleaseComObject(array);
        return results;
    }

    public RawAutomationElement? GetParent()
    {
        int hr = Element.GetParent(out IntPtr parentPtr);
        if (hr == 0 && parentPtr != IntPtr.Zero)
            return new RawAutomationElement(parentPtr);
        return null;
    }

    public List<RawAutomationElement> GetChildren()
    {
        int hr = Element.GetChildren(out IntPtr arrayPtr);
        if (hr != 0 || arrayPtr == IntPtr.Zero)
            return new List<RawAutomationElement>();

        var array = (IUIAutomationElementArray)Marshal.GetObjectForIUnknown(arrayPtr);
        Marshal.Release(arrayPtr);

        array.GetLength(out int count);
        var results = new List<RawAutomationElement>(count);
        for (int i = 0; i < count; i++)
        {
            array.GetElement(i, out IntPtr childPtr);
            if (childPtr != IntPtr.Zero)
                results.Add(new RawAutomationElement(childPtr));
        }
        Marshal.ReleaseComObject(array);
        return results;
    }

    public int GetChildrenCount()
    {
        Element.GetChildrenCount(out int count);
        return count;
    }

    // --- Property Access ---

    public string GetName()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_NamePropertyId, out var value);
        return hr == 0 ? (value as string) ?? string.Empty : string.Empty;
    }

    public string GetAutomationId()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_AutomationIdPropertyId, out var value);
        return hr == 0 ? (value as string) ?? string.Empty : string.Empty;
    }

    public string GetClassName()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_ClassNamePropertyId, out var value);
        return hr == 0 ? (value as string) ?? string.Empty : string.Empty;
    }

    public int GetControlTypeId()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_ControlTypePropertyId, out var value);
        return hr == 0 && value is int v ? v : 0;
    }

    public string GetControlTypeName()
    {
        int typeId = GetControlTypeId();
        return typeId switch
        {
            UIAControlTypeIds.UIA_ButtonControlTypeId => "ControlType.Button",
            UIAControlTypeIds.UIA_CalendarControlTypeId => "ControlType.Calendar",
            UIAControlTypeIds.UIA_CheckBoxControlTypeId => "ControlType.CheckBox",
            UIAControlTypeIds.UIA_ComboBoxControlTypeId => "ControlType.ComboBox",
            UIAControlTypeIds.UIA_EditControlTypeId => "ControlType.Edit",
            UIAControlTypeIds.UIA_HyperlinkControlTypeId => "ControlType.Hyperlink",
            UIAControlTypeIds.UIA_ImageControlTypeId => "ControlType.Image",
            UIAControlTypeIds.UIA_ListItemControlTypeId => "ControlType.ListItem",
            UIAControlTypeIds.UIA_ListControlTypeId => "ControlType.List",
            UIAControlTypeIds.UIA_MenuControlTypeId => "ControlType.Menu",
            UIAControlTypeIds.UIA_MenuBarControlTypeId => "ControlType.MenuBar",
            UIAControlTypeIds.UIA_MenuItemControlTypeId => "ControlType.MenuItem",
            UIAControlTypeIds.UIA_ProgressBarControlTypeId => "ControlType.ProgressBar",
            UIAControlTypeIds.UIA_RadioButtonControlTypeId => "ControlType.RadioButton",
            UIAControlTypeIds.UIA_ScrollBarControlTypeId => "ControlType.ScrollBar",
            UIAControlTypeIds.UIA_SliderControlTypeId => "ControlType.Slider",
            UIAControlTypeIds.UIA_SpinnerControlTypeId => "ControlType.Spinner",
            UIAControlTypeIds.UIA_StatusBarControlTypeId => "ControlType.StatusBar",
            UIAControlTypeIds.UIA_TabControlTypeId => "ControlType.Tab",
            UIAControlTypeIds.UIA_TabItemControlTypeId => "ControlType.TabItem",
            UIAControlTypeIds.UIA_TextControlTypeId => "ControlType.Text",
            UIAControlTypeIds.UIA_ToolBarControlTypeId => "ControlType.ToolBar",
            UIAControlTypeIds.UIA_ToolTipControlTypeId => "ControlType.ToolTip",
            UIAControlTypeIds.UIA_TreeControlTypeId => "ControlType.Tree",
            UIAControlTypeIds.UIA_TreeItemControlTypeId => "ControlType.TreeItem",
            UIAControlTypeIds.UIA_CustomControlTypeId => "ControlType.Custom",
            UIAControlTypeIds.UIA_GroupControlTypeId => "ControlType.Group",
            UIAControlTypeIds.UIA_ThumbControlTypeId => "ControlType.Thumb",
            UIAControlTypeIds.UIA_DataGridControlTypeId => "ControlType.DataGrid",
            UIAControlTypeIds.UIA_DataItemControlTypeId => "ControlType.DataItem",
            UIAControlTypeIds.UIA_DocumentControlTypeId => "ControlType.Document",
            UIAControlTypeIds.UIA_SplitButtonControlTypeId => "ControlType.SplitButton",
            UIAControlTypeIds.UIA_PaneControlTypeId => "ControlType.Pane",
            UIAControlTypeIds.UIA_HeaderControlTypeId => "ControlType.Header",
            UIAControlTypeIds.UIA_HeaderItemControlTypeId => "ControlType.HeaderItem",
            UIAControlTypeIds.UIA_TableControlTypeId => "ControlType.Table",
            UIAControlTypeIds.UIA_TitleBarControlTypeId => "ControlType.TitleBar",
            UIAControlTypeIds.UIA_SeparatorControlTypeId => "ControlType.Separator",
            _ => "ControlType.Custom"
        };
    }

    public bool GetIsEnabled()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_IsEnabledPropertyId, out var value);
        return hr == 0 && value is bool b && b;
    }

    public bool GetHasKeyboardFocus()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_HasKeyboardFocusPropertyId, out var value);
        return hr == 0 && value is bool b && b;
    }

    public int GetNativeWindowHandle()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_NativeWindowHandlePropertyId, out var value);
        return hr == 0 && value is int v ? v : 0;
    }

    public RectD GetBoundingRectangle()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_BoundingRectanglePropertyId, out var value);
        if (hr == 0 && value is double[] arr && arr.Length == 4)
            return new RectD(arr[0], arr[1], arr[2], arr[3]);
        return default;
    }

    public int GetProcessId()
    {
        int hr = Element.GetCurrentPropertyValue(UIAPropertyIds.UIA_ProcessIdPropertyId, out var value);
        return hr == 0 && value is int v ? v : 0;
    }

    public int[] GetRuntimeId()
    {
        int hr = Element.GetRuntimeId(out IntPtr ptr);
        if (hr != 0 || ptr == IntPtr.Zero)
            return Array.Empty<int>();

        try
        {
            return SafeArrayToIntArray(ptr);
        }
        finally
        {
            Marshal.DestroyStructure<SAFEARRAY>(ptr);
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public string GetRuntimeIdString()
    {
        return string.Join(",", GetRuntimeId());
    }

    public object GetPropertyValue(int propertyId)
    {
        int hr = Element.GetCurrentPropertyValue(propertyId, out var value);
        return hr == 0 ? value : null!;
    }

    // --- Pattern Access ---

    public T? TryGetPattern<T>(int patternId) where T : class
    {
        int hr = Element.GetCurrentPattern(patternId, out var patternObject);
        if (hr != 0 || patternObject is null)
            return null;

        return patternObject as T;
    }

    // --- Focus ---

    public void SetFocus()
    {
        Element.SetFocus();
    }

    // --- Stale Detection ---

    /// <summary>
    /// Checks if the element is still alive by reading BoundingRectangle.
    /// Returns false if the element has been detached from the UIA tree.
    /// </summary>
    public bool IsAlive()
    {
        try
        {
            _ = GetBoundingRectangle();
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    // --- Helpers ---

    private static int[] SafeArrayToIntArray(IntPtr ptr)
    {
        var sa = Marshal.PtrToStructure<SAFEARRAY>(ptr);
        if (sa.cDims == 0)
            return Array.Empty<int>();

        IntPtr dataPtr = SafeArrayAccessData(ptr);
        if (dataPtr == IntPtr.Zero)
            return Array.Empty<int>();

        try
        {
            int count = (int)sa.rgsabound[0].cElements;
            var result = new int[count];
            Marshal.Copy(dataPtr, result, 0, count);
            return result;
        }
        finally
        {
            SafeArrayUnaccessData(ptr);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern IntPtr SafeArrayAccessData(IntPtr psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(IntPtr psa);

    [StructLayout(LayoutKind.Sequential)]
    private struct SAFEARRAY
    {
        public ushort cDims;
        public ushort fFeatures;
        public uint cbElements;
        public uint cLocks;
        public IntPtr pvData;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public SAFEARRAYBOUND[] rgsabound;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SAFEARRAYBOUND
    {
        public uint cElements;
        public int lLbound;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_element is not null)
            {
                Marshal.ReleaseComObject(_element);
                _element = null;
            }
            if (_rawPtr != IntPtr.Zero)
            {
                Marshal.Release(_rawPtr);
                _rawPtr = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}

public struct RectD
{
    public double X;
    public double Y;
    public double Width;
    public double Height;

    public RectD(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public bool IsEmpty => Width <= 0 || Height <= 0;
}
