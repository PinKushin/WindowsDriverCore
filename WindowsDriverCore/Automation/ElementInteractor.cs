using System.Windows.Automation;
using WindowsDriverCore.ErrorHandling;

namespace WindowsDriverCore.Automation;

public class ElementInteractor : IElementInteractor
{
    private readonly ElementStore _store;

    public ElementInteractor(ElementStore store)
    {
        _store = store;
    }

    private AutomationElement GetAndVerifyAlive(string elementId)
    {
        var element = _store.Get(elementId);
        if (element is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        try
        {
            var _ = element.Current.BoundingRectangle;
        }
        catch (ElementNotAvailableException)
        {
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");
        }

        return element;
    }

    public void Click(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            ((InvokePattern)pattern).Invoke();
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectPattern))
        {
            ((SelectionItemPattern)selectPattern).Select();
            return;
        }

        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
        {
            var el = (ExpandCollapsePattern)expandPattern;
            if (el.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                el.Expand();
            else
                el.Collapse();
            return;
        }

        throw new WebDriverException(ErrorType.UnknownError,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public void SendKeys(string elementId, string text)
    {
        var element = GetAndVerifyAlive(elementId);

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            ((ValuePattern)pattern).SetValue(text);
            return;
        }

        throw new WebDriverException(ErrorType.UnknownError,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public string GetText(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            return ((ValuePattern)pattern).Current.Value ?? string.Empty;
        }

        return element.Current.Name ?? string.Empty;
    }

    public bool GetEnabled(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        return element.Current.IsEnabled;
    }

    public bool GetDisplayed(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.Current.BoundingRectangle;
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
    }

    public string GetTagName(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        return element.Current.ControlType.ProgrammaticName;
    }

    public string? GetAttribute(string elementId, string attributeName)
    {
        var element = GetAndVerifyAlive(elementId);

        return attributeName.ToLowerInvariant() switch
        {
            "name" => element.Current.Name ?? string.Empty,
            "automationid" => element.Current.AutomationId ?? string.Empty,
            "classname" => element.Current.ClassName ?? string.Empty,
            "controltype" => element.Current.ControlType.ProgrammaticName,
            "isenabled" => element.Current.IsEnabled.ToString(),
            "haskeyboardfocus" => element.Current.HasKeyboardFocus.ToString(),
            "nativewindowhandle" => element.Current.NativeWindowHandle.ToString(),
            "boundingrectangle" => element.Current.BoundingRectangle.ToString(),
            "processid" => element.Current.ProcessId.ToString(),
            "runtimeid" => string.Join(",", element.GetRuntimeId()),
            _ => null
        };
    }

    public void Clear(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            ((ValuePattern)pattern).SetValue(string.Empty);
            return;
        }

        throw new WebDriverException(ErrorType.UnknownError,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public string GetSelected(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
        {
            return ((SelectionItemPattern)pattern).Current.IsSelected.ToString();
        }

        return bool.FalseString;
    }

    public string GetCoordinates(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.Current.BoundingRectangle;
        return $"{(int)bounds.X},{(int)bounds.Y}";
    }

    public string GetSize(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.Current.BoundingRectangle;
        return $"{(int)bounds.Width},{(int)bounds.Height}";
    }

    public string GetLocationInView(string elementId)
    {
        return GetCoordinates(elementId);
    }

    public void ClickAt(string elementId, int x, int y)
    {
        Click(elementId);
    }

    public string GetElementId(string elementId)
    {
        GetAndVerifyAlive(elementId);
        return elementId;
    }
}
