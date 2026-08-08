using System.Runtime.InteropServices;
using System.Text.Json;
using Interop.UIAutomationClient;
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
        if (element is null || !element.IsAlive())
            throw new WebDriverException(ErrorType.StaleElementReference,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        return element;
    }

    public void Click(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        try
        {
            var invoke = element.TryGetPattern<IUIAutomationInvokePattern>(UIAPatternIds.UIA_InvokePatternId);
            if (invoke is not null)
            {
                invoke.Invoke();
                return;
            }

            var selectItem = element.TryGetPattern<IUIAutomationSelectionItemPattern>(UIAPatternIds.UIA_SelectionItemPatternId);
            if (selectItem is not null)
            {
                selectItem.Select();
                return;
            }

            var expand = element.TryGetPattern<IUIAutomationExpandCollapsePattern>(UIAPatternIds.UIA_ExpandCollapsePatternId);
            if (expand is not null)
            {
                if (expand.CurrentExpandCollapseState == ExpandCollapseState.ExpandCollapseState_Collapsed)
                    expand.Expand();
                else
                    expand.Collapse();
                return;
            }

            element.SetFocus();
        }
        catch (COMException ex)
        {
            if (IsElementNotAvailable(ex))
                throw new WebDriverException(ErrorType.StaleElementReference,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
            if (IsElementNotEnabled(ex) || !element.GetIsEnabled())
                throw new WebDriverException(ErrorType.ElementNotVisible,
                    "Element is not enabled");
            throw new WebDriverException(ErrorType.UnknownError,
                $"An element command failed: 0x{ex.ErrorCode:X8}");
        }
    }

    public void SendKeys(string elementId, string text)
    {
        var element = GetAndVerifyAlive(elementId);

        try
        {
            var value = element.TryGetPattern<IUIAutomationValuePattern>(UIAPatternIds.UIA_ValuePatternId);
            if (value is not null)
            {
                value.SetValue(text);
                return;
            }
        }
        catch (COMException ex)
        {
            if (IsElementNotAvailable(ex))
                throw new WebDriverException(ErrorType.StaleElementReference,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
            if (IsElementNotEnabled(ex) || !element.GetIsEnabled())
                throw new WebDriverException(ErrorType.ElementNotVisible,
                    "Element is not enabled");
            throw new WebDriverException(ErrorType.UnknownError,
                $"An element command failed: 0x{ex.ErrorCode:X8}");
        }

        throw new WebDriverException(ErrorType.ElementNotVisible,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public string GetText(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        try
        {
            var value = element.TryGetPattern<IUIAutomationValuePattern>(UIAPatternIds.UIA_ValuePatternId);
            if (value is not null)
            {
                return value.CurrentValue ?? string.Empty;
            }
        }
        catch (COMException ex)
        {
            if (IsElementNotAvailable(ex))
                throw new WebDriverException(ErrorType.StaleElementReference,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
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

        try
        {
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
        catch (COMException ex)
        {
            if (IsElementNotAvailable(ex))
                throw new WebDriverException(ErrorType.StaleElementReference,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
            throw;
        }
    }

    public void Clear(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        try
        {
            var value = element.TryGetPattern<IUIAutomationValuePattern>(UIAPatternIds.UIA_ValuePatternId);
            if (value is not null)
            {
                value.SetValue(string.Empty);
                return;
            }
        }
        catch (COMException ex)
        {
            if (IsElementNotAvailable(ex))
                throw new WebDriverException(ErrorType.StaleElementReference,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
            if (IsElementNotEnabled(ex) || !element.GetIsEnabled())
                throw new WebDriverException(ErrorType.ElementNotVisible,
                    "Element is not enabled");
            throw new WebDriverException(ErrorType.UnknownError,
                $"An element command failed: 0x{ex.ErrorCode:X8}");
        }

        throw new WebDriverException(ErrorType.ElementNotVisible,
            "An element command could not be completed because the element is not pointer- or keyboard interactable.");
    }

    public string GetSelected(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);

        try
        {
            var selectItem = element.TryGetPattern<IUIAutomationSelectionItemPattern>(UIAPatternIds.UIA_SelectionItemPatternId);
            if (selectItem is not null)
            {
                return selectItem.CurrentIsSelected != 0 ? bool.TrueString : bool.FalseString;
            }
        }
        catch (COMException ex)
        {
            if (IsElementNotAvailable(ex))
                throw new WebDriverException(ErrorType.StaleElementReference,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
        }

        return bool.FalseString;
    }

    public string GetCoordinates(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.GetBoundingRectangle();
        return JsonSerializer.Serialize(new { x = (int)bounds.X, y = (int)bounds.Y });
    }

    public string GetSize(string elementId)
    {
        var element = GetAndVerifyAlive(elementId);
        var bounds = element.GetBoundingRectangle();
        return JsonSerializer.Serialize(new { width = (int)bounds.Width, height = (int)bounds.Height });
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

    private static bool IsElementNotAvailable(COMException ex)
    {
        // 0x80040200 = UIA_E_ELEMENTNOTAVAILABLE
        return unchecked((uint)ex.ErrorCode) == 0x80040200;
    }

    private static bool IsElementNotEnabled(COMException ex)
    {
        // 0x80040201 = UIA_E_ELEMENTNOTENABLED
        return unchecked((uint)ex.ErrorCode) == 0x80040201;
    }
}
