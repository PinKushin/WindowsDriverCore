using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace WindowsDriverCore.Automation.Raw;

/// <summary>
/// Managed wrapper around Interop.UIAutomationClient.IUIAutomationElement.
/// Provides clean C# API for element property access and tree navigation.
/// </summary>
public sealed class RawAutomationElement : IDisposable
{
    private IUIAutomationElement? _element;
    private bool _disposed;

    public RawAutomationElement(IUIAutomationElement element)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
    }

    public IUIAutomationElement Element => _element ?? throw new ObjectDisposedException(nameof(RawAutomationElement));
    public bool IsValid => _element is not null && !_disposed;

    // --- Tree Navigation ---

    public RawAutomationElement? FindFirst(TreeScope scope, IUIAutomationCondition condition)
    {
        var result = _element!.FindFirst(scope, condition);
        return result is not null ? new RawAutomationElement(result) : null;
    }

    public List<RawAutomationElement> FindAll(TreeScope scope, IUIAutomationCondition condition)
    {
        var array = _element!.FindAll(scope, condition);
        var results = new List<RawAutomationElement>();
        if (array is null)
            return results;

        for (int i = 0; i < array.Length; i++)
        {
            var child = array.GetElement(i);
            if (child is not null)
                results.Add(new RawAutomationElement(child));
        }
        return results;
    }

    public RawAutomationElement? GetParent(IUIAutomation automation)
    {
        var walker = automation.ControlViewWalker;
        if (walker is null) return null;
        var parent = walker.GetParentElement(_element);
        return parent is not null ? new RawAutomationElement(parent) : null;
    }

    public List<RawAutomationElement> GetChildren(IUIAutomationCondition trueCondition)
    {
        return FindAll(TreeScope.TreeScope_Children, trueCondition);
    }

    // --- Property Access ---

    public string GetName() => _element!.CurrentName ?? string.Empty;

    public string GetAutomationId() => _element!.CurrentAutomationId ?? string.Empty;

    public string GetClassName() => _element!.CurrentClassName ?? string.Empty;

    public int GetControlTypeId() => _element!.CurrentControlType;

    public string GetControlTypeName()
    {
        int typeId = GetControlTypeId();
        return typeId switch
        {
            50000 => "ControlType.Button",
            50001 => "ControlType.Calendar",
            50002 => "ControlType.CheckBox",
            50003 => "ControlType.ComboBox",
            50004 => "ControlType.Edit",
            50005 => "ControlType.Hyperlink",
            50006 => "ControlType.Image",
            50007 => "ControlType.ListItem",
            50008 => "ControlType.List",
            50009 => "ControlType.Menu",
            50010 => "ControlType.MenuBar",
            50011 => "ControlType.MenuItem",
            50012 => "ControlType.ProgressBar",
            50013 => "ControlType.RadioButton",
            50014 => "ControlType.ScrollBar",
            50015 => "ControlType.Slider",
            50016 => "ControlType.Spinner",
            50017 => "ControlType.StatusBar",
            50018 => "ControlType.Tab",
            50019 => "ControlType.TabItem",
            50020 => "ControlType.Text",
            50021 => "ControlType.ToolBar",
            50022 => "ControlType.ToolTip",
            50023 => "ControlType.Tree",
            50024 => "ControlType.TreeItem",
            50025 => "ControlType.Custom",
            50026 => "ControlType.Group",
            50027 => "ControlType.Thumb",
            50028 => "ControlType.DataGrid",
            50029 => "ControlType.DataItem",
            50030 => "ControlType.Document",
            50031 => "ControlType.SplitButton",
            50032 => "ControlType.Pane",
            50033 => "ControlType.Header",
            50034 => "ControlType.HeaderItem",
            50035 => "ControlType.Table",
            50037 => "ControlType.TitleBar",
            50038 => "ControlType.Separator",
            _ => "ControlType.Custom"
        };
    }

    public bool GetIsEnabled() => _element!.CurrentIsEnabled != 0;

    public bool GetHasKeyboardFocus() => _element!.CurrentHasKeyboardFocus != 0;

    public int GetNativeWindowHandle()
    {
        var ptr = _element!.CurrentNativeWindowHandle;
        return ptr.ToInt32();
    }

    public RectD GetBoundingRectangle()
    {
        var rect = _element!.CurrentBoundingRectangle;
        return new RectD(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

    public int GetProcessId() => _element!.CurrentProcessId;

    public int[] GetRuntimeId()
    {
        var id = _element!.GetRuntimeId();
        return id ?? Array.Empty<int>();
    }

    public string GetRuntimeIdString()
    {
        return string.Join(",", GetRuntimeId());
    }

    public object GetPropertyValue(int propertyId)
    {
        return _element!.GetCurrentPropertyValue(propertyId);
    }

    // --- Pattern Access ---

    public T? TryGetPattern<T>(int patternId) where T : class
    {
        try
        {
            var pattern = _element!.GetCurrentPattern(patternId);
            return pattern as T;
        }
        catch
        {
            return null;
        }
    }

    // --- Focus ---

    public void SetFocus()
    {
        _element!.SetFocus();
    }

    // --- Stale Detection ---

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

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_element is not null)
            {
                try { Marshal.ReleaseComObject(_element); } catch { }
                _element = null;
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
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
