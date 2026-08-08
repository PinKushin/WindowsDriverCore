using Interop.UIAutomationClient;
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
        var root = _automation.ElementFromHandle(windowHandle);
        if (root is null)
            throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

        using var condition = CreateCondition(usingStrategy, value);
        using var rawRoot = new RawAutomationElement(root);
        var element = rawRoot.FindFirst(TreeScope.TreeScope_Descendants, condition.Condition);

        if (element is null)
            throw new WebDriverException(ErrorType.NoSuchElement,
                "An element could not be located on the page using the given search parameters.");

        return _store.Store(element);
    }

    public string[] FindElements(IntPtr windowHandle, string usingStrategy, string value)
    {
        var root = _automation.ElementFromHandle(windowHandle);
        if (root is null)
            throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

        using var condition = CreateCondition(usingStrategy, value);
        using var rawRoot = new RawAutomationElement(root);
        var elements = rawRoot.FindAll(TreeScope.TreeScope_Descendants, condition.Condition);

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
        if (parent is null || !parent.IsAlive())
            throw new WebDriverException(ErrorType.StaleElementReference,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        using var condition = CreateCondition(usingStrategy, value);
        var element = parent.FindFirst(TreeScope.TreeScope_Descendants, condition.Condition);

        if (element is null)
            throw new WebDriverException(ErrorType.NoSuchElement,
                "An element could not be located on the page using the given search parameters.");

        return _store.Store(element);
    }

    public string[] FindElementsInElement(string parentElementId, string usingStrategy, string value)
    {
        var parent = _store.Get(parentElementId);
        if (parent is null || !parent.IsAlive())
            throw new WebDriverException(ErrorType.StaleElementReference,
                "An element command failed because the referenced element is no longer attached to the DOM.");

        using var condition = CreateCondition(usingStrategy, value);
        var elements = parent.FindAll(TreeScope.TreeScope_Descendants, condition.Condition);

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
            "id" => ConditionFactory.CreateRuntimeIdCondition(value),
            "tag name" => ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_ControlTypePropertyId, MapTagNameToControlTypeId(value)),
            "xpath" => throw new WebDriverException(ErrorType.InvalidArgument,
                string.Format("Invalid XPath expression: {0} (XPathLookupError)", value)),
            // Old Appium .NET client sends "css selector" for FindElementByClassName with
            // a "."-prefixed value (e.g. ".Edit"). WinAppDriver treated this as class name.
            "css selector" when value.StartsWith(".") =>
                ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_ClassNamePropertyId, value.Substring(1)),
            "css selector" =>
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: css selector locator strategy is not supported"),
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
