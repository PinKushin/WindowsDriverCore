using System.Runtime.InteropServices;
using WindowsDriverCore.Automation.Com;
using WindowsDriverCore.Automation.Raw;
using WindowsDriverCore.ErrorHandling;

namespace WindowsDriverCore.Automation;

public class ElementFinder : IElementFinder
{
    private readonly ElementStore _store;
    private readonly IUIAutomation _automation;

    public ElementFinder(ElementStore store)
    {
        _store = store;
        _automation = UIAutomationFactory.Create();
        ConditionFactory.Initialize(_automation);
    }

    public string FindElement(IntPtr windowHandle, string usingStrategy, string value)
    {
        int hr = _automation.ElementFromHandle(windowHandle, out IntPtr rootPtr);
        if (hr != 0 || rootPtr == IntPtr.Zero)
            throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

        using var condition = CreateCondition(usingStrategy, value);
        var element = new RawAutomationElement(rootPtr).FindFirst(UIATreeScope.TreeScope_Descendants, condition.ConditionPtr);

        if (element is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element could not be located on the page using the given search parameters.");

        return _store.Store(element);
    }

    public string[] FindElements(IntPtr windowHandle, string usingStrategy, string value)
    {
        int hr = _automation.ElementFromHandle(windowHandle, out IntPtr rootPtr);
        if (hr != 0 || rootPtr == IntPtr.Zero)
            throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

        using var condition = CreateCondition(usingStrategy, value);
        var rawRoot = new RawAutomationElement(rootPtr);
        var elements = rawRoot.FindAll(UIATreeScope.TreeScope_Descendants, condition.ConditionPtr);

        var ids = new string[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            ids[i] = _store.Store(elements[i]);
        }

        return ids;
    }

    public string FindElementInElement(string parentElementId, string usingStrategy, string value)
    {
        var parent = _store.Get(parentElementId);
        if (parent is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        if (!parent.IsAlive())
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        using var condition = CreateCondition(usingStrategy, value);
        var element = parent.FindFirst(UIATreeScope.TreeScope_Descendants, condition.ConditionPtr);

        if (element is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element could not be located on the page using the given search parameters.");

        return _store.Store(element);
    }

    public string[] FindElementsInElement(string parentElementId, string usingStrategy, string value)
    {
        var parent = _store.Get(parentElementId);
        if (parent is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        if (!parent.IsAlive())
            throw new WebDriverException(ErrorType.UnknownError,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        using var condition = CreateCondition(usingStrategy, value);
        var elements = parent.FindAll(UIATreeScope.TreeScope_Descendants, condition.ConditionPtr);

        var ids = new string[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            ids[i] = _store.Store(elements[i]);
        }

        return ids;
    }

    private static RawCondition CreateCondition(string usingStrategy, string value)
    {
        return usingStrategy.ToLowerInvariant() switch
        {
            "accessibility id" => ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_AutomationIdPropertyId, value),
            "class name" => ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_ClassNamePropertyId, value),
            "name" => ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_NamePropertyId, value),
            "id" => throw new WebDriverException(ErrorType.InvalidArgument,
                "RuntimeId lookup not yet supported in raw COM mode"),
            "tag name" => ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_ControlTypePropertyId, MapTagNameToControlTypeId(value)),
            "xpath" => throw new WebDriverException(ErrorType.InvalidArgument,
                "XPath not yet supported in raw COM mode"),
            "css selector" => ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_ClassNamePropertyId, value),
            "link text" =>
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: link text locator strategy is not supported"),
            "partial link text" =>
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: partial link text locator strategy is not supported"),
            _ =>
                throw new WebDriverException(ErrorType.InvalidArgument,
                    $"Unexpected error. Unimplemented Command: {usingStrategy} locator strategy is not supported")
        };
    }

    private static int MapTagNameToControlTypeId(string tagName)
    {
        return tagName.ToLowerInvariant() switch
        {
            "button" => UIAControlTypeIds.UIA_ButtonControlTypeId,
            "text" or "textblock" => UIAControlTypeIds.UIA_TextControlTypeId,
            "edit" or "textbox" => UIAControlTypeIds.UIA_EditControlTypeId,
            "checkbox" => UIAControlTypeIds.UIA_CheckBoxControlTypeId,
            "radiobutton" or "radio" => UIAControlTypeIds.UIA_RadioButtonControlTypeId,
            "combobox" or "dropdown" => UIAControlTypeIds.UIA_ComboBoxControlTypeId,
            "listitem" => UIAControlTypeIds.UIA_ListItemControlTypeId,
            "list" or "listview" => UIAControlTypeIds.UIA_ListControlTypeId,
            "treeitem" => UIAControlTypeIds.UIA_TreeItemControlTypeId,
            "tree" => UIAControlTypeIds.UIA_TreeControlTypeId,
            "tabitem" => UIAControlTypeIds.UIA_TabItemControlTypeId,
            "tab" => UIAControlTypeIds.UIA_TabControlTypeId,
            "menu" => UIAControlTypeIds.UIA_MenuControlTypeId,
            "menuitem" => UIAControlTypeIds.UIA_MenuItemControlTypeId,
            "toolbar" => UIAControlTypeIds.UIA_ToolBarControlTypeId,
            "scrollbar" => UIAControlTypeIds.UIA_ScrollBarControlTypeId,
            "slider" => UIAControlTypeIds.UIA_SliderControlTypeId,
            "progressbar" => UIAControlTypeIds.UIA_ProgressBarControlTypeId,
            "hyperlink" => UIAControlTypeIds.UIA_HyperlinkControlTypeId,
            "image" => UIAControlTypeIds.UIA_ImageControlTypeId,
            "custom" => UIAControlTypeIds.UIA_CustomControlTypeId,
            "group" or "groupbox" => UIAControlTypeIds.UIA_GroupControlTypeId,
            "thumb" => UIAControlTypeIds.UIA_ThumbControlTypeId,
            "datagrid" => UIAControlTypeIds.UIA_DataGridControlTypeId,
            "dataitem" => UIAControlTypeIds.UIA_DataItemControlTypeId,
            "document" => UIAControlTypeIds.UIA_DocumentControlTypeId,
            "splitbutton" => UIAControlTypeIds.UIA_SplitButtonControlTypeId,
            "window" or "pane" => UIAControlTypeIds.UIA_PaneControlTypeId,
            "spinner" => UIAControlTypeIds.UIA_SpinnerControlTypeId,
            "statusbar" => UIAControlTypeIds.UIA_StatusBarControlTypeId,
            "table" => UIAControlTypeIds.UIA_TableControlTypeId,
            "titlebar" => UIAControlTypeIds.UIA_TitleBarControlTypeId,
            "separator" => UIAControlTypeIds.UIA_SeparatorControlTypeId,
            _ => UIAControlTypeIds.UIA_CustomControlTypeId
        };
    }
}
