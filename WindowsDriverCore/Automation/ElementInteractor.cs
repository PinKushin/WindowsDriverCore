using System.Runtime.InteropServices;
using WindowsDriverCore.Automation.Com;
using WindowsDriverCore.Automation.Raw;
using WindowsDriverCore.ErrorHandling;

namespace WindowsDriverCore.Automation;

public class ElementInteractor : IElementInteractor
{
    private readonly ElementStore _store;

    public ElementInteractor(ElementStore store)
    {
        _store = store;
    }

    private RawAutomationElement GetAndVerifyAlive(string elementId)
    {
        var element = _store.Get(elementId);
        if (element is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        if (!element.IsAlive())
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        return element;
    }

    public void Click(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        // Try InvokePattern
        var invoke = element.TryGetPattern<IUIAutomationInvokePattern>(UIAPatternIds.UIA_InvokePatternId);
        if (invoke is not null)
        {
            invoke.Invoke();
            return;
        }

        // Try SelectionItemPattern
        var selectItem = element.TryGetPattern<IUIAutomationSelectionItemPattern>(UIAPatternIds.UIA_SelectionItemPatternId);
        if (selectItem is not null)
        {
            selectItem.Select();
            return;
        }

        // Try ExpandCollapsePattern
        var expand = element.TryGetPattern<IUIAutomationExpandCollapsePattern>(UIAPatternIds.UIA_ExpandCollapsePatternId);
        if (expand is not null)
        {
            expand.get_ExpandCollapseState(out int state);
            if (state == UIAExpandCollapseState.ExpandCollapseState_Collapsed)
                expand.Expand();
            else
                expand.Collapse();
            return;
        }

        throw new WebDriverException(ErrorType.UnknownError,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public void SendKeys(string elementId, string text)
    {
        var element = GetAndVerifyAlive(elementId);

        var value = element.TryGetPattern<IUIAutomationValuePattern>(UIAPatternIds.UIA_ValuePatternId);
        if (value is not null)
        {
            value.SetValue(text);
            return;
        }

        throw new WebDriverException(ErrorType.UnknownError,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public string GetText(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        var value = element.TryGetPattern<IUIAutomationValuePattern>(UIAPatternIds.UIA_ValuePatternId);
        if (value is not null)
        {
            value.get_Value(out string text);
            return text ?? string.Empty;
        }

        return element.GetName();
    }

    public bool GetEnabled(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        return element.GetIsEnabled();
    }

    public bool GetDisplayed(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.GetBoundingRectangle();
        return !bounds.IsEmpty;
    }

    public string GetTagName(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        return element.GetControlTypeName();
    }

    public string? GetAttribute(string elementId, string attributeName)
    {
        var element = GetAndVerifyAlive(elementId);

        return attributeName.ToLowerInvariant() switch
        {
            "name" => element.GetName(),
            "automationid" => element.GetAutomationId(),
            "classname" => element.GetClassName(),
            "controltype" => element.GetControlTypeName(),
            "isenabled" => element.GetIsEnabled().ToString(),
            "haskeyboardfocus" => element.GetHasKeyboardFocus().ToString(),
            "nativewindowhandle" => element.GetNativeWindowHandle().ToString(),
            "boundingrectangle" => element.GetBoundingRectangle().ToString(),
            "processid" => element.GetProcessId().ToString(),
            "runtimeid" => element.GetRuntimeIdString(),
            _ => null
        };
    }

    public void Clear(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        var value = element.TryGetPattern<IUIAutomationValuePattern>(UIAPatternIds.UIA_ValuePatternId);
        if (value is not null)
        {
            value.SetValue(string.Empty);
            return;
        }

        throw new WebDriverException(ErrorType.UnknownError,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public string GetSelected(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        var selectItem = element.TryGetPattern<IUIAutomationSelectionItemPattern>(UIAPatternIds.UIA_SelectionItemPatternId);
        if (selectItem is not null)
        {
            selectItem.get_IsSelected(out bool isSelected);
            return isSelected.ToString();
        }

        return bool.FalseString;
    }

    public string GetCoordinates(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.GetBoundingRectangle();
        return $"{(int)bounds.X},{(int)bounds.Y}";
    }

    public string GetSize(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.GetBoundingRectangle();
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
