using System.Windows.Automation;
using WindowsDriverCore.ErrorHandling;

namespace WindowsDriverCore.Automation;

public class ElementFinder : IElementFinder
{
    private readonly ElementStore _store;

    public ElementFinder(ElementStore store)
    {
        _store = store;
    }

    public string FindElement(IntPtr windowHandle, string usingStrategy, string value)
    {
        var root = AutomationElement.FromHandle(windowHandle);
        if (root is null)
            throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

        var condition = CreateCondition(usingStrategy, value);
        var element = root.FindFirst(TreeScope.Descendants, condition);

        if (element is null)
            throw new WebDriverException(ErrorType.UnknownError,
                "An element could not be located on the page using the given search parameters.");

        return _store.Store(element);
    }

    public string[] FindElements(IntPtr windowHandle, string usingStrategy, string value)
    {
        var root = AutomationElement.FromHandle(windowHandle);
        if (root is null)
            throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

        var condition = CreateCondition(usingStrategy, value);
        var elements = root.FindAll(TreeScope.Descendants, condition);

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

        var condition = CreateCondition(usingStrategy, value);
        AutomationElement? element;
        try
        {
            element = parent.FindFirst(TreeScope.Descendants, condition);
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

        var condition = CreateCondition(usingStrategy, value);
        AutomationElementCollection elements;
        try
        {
            elements = parent.FindAll(TreeScope.Descendants, condition);
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

        var ids = new string[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            ids[i] = _store.Store(elements[i]);
        }

        return ids;
    }

    private static Condition CreateCondition(string usingStrategy, string value)
    {
        return usingStrategy.ToLowerInvariant() switch
        {
            "accessibility id" => new PropertyCondition(AutomationElement.AutomationIdProperty, value),
            "class name" => new PropertyCondition(AutomationElement.ClassNameProperty, value),
            "name" => new PropertyCondition(AutomationElement.NameProperty, value),
            "id" => new PropertyCondition(AutomationElement.RuntimeIdProperty, value),
            "tag name" => new PropertyCondition(AutomationElement.ControlTypeProperty, MapTagNameToControlType(value)),
            "xpath" => CreateXPathCondition(value),
            "css selector" => new PropertyCondition(AutomationElement.ClassNameProperty, value),
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

    private static Condition CreateXPathCondition(string xpath)
    {
        var originalXpath = xpath;

        if (xpath.StartsWith("//"))
            xpath = xpath[2..];

        string? tagName = null;
        string? attrName = null;
        string? attrValue = null;

        var atIndex = xpath.IndexOf('[');
        if (atIndex >= 0)
        {
            tagName = xpath[..atIndex];
            var attrPart = xpath[(atIndex + 1)..];

            var lastCloseBracket = attrPart.LastIndexOf(']');
            if (lastCloseBracket >= 0)
                attrPart = attrPart[..lastCloseBracket];

            var equalsIndex = attrPart.IndexOf('=');
            if (equalsIndex >= 0)
            {
                attrName = attrPart[..equalsIndex].TrimStart('@');
                attrValue = attrPart[(equalsIndex + 1)..].Trim('"', '\'');
            }
        }
        else
        {
            tagName = xpath;
        }

        if (tagName is null || string.IsNullOrWhiteSpace(tagName))
        {
            throw new WebDriverException(ErrorType.UnknownError,
                $"Invalid XPath expression: {originalXpath} (XPathLookupError)");
        }

        if (tagName.Contains('/') || tagName.Contains(']'))
        {
            throw new WebDriverException(ErrorType.UnknownError,
                $"Invalid XPath expression: {originalXpath} (XPathLookupError)");
        }

        var controlType = tagName.ToLowerInvariant() switch
        {
            "button" => ControlType.Button,
            "text" or "textblock" => ControlType.Text,
            "edit" or "textbox" => ControlType.Edit,
            "checkbox" => ControlType.CheckBox,
            "radiobutton" or "radio" => ControlType.RadioButton,
            "combobox" or "dropdown" => ControlType.ComboBox,
            "listitem" => ControlType.ListItem,
            "list" or "listview" => ControlType.List,
            "treeitem" => ControlType.TreeItem,
            "tree" => ControlType.Tree,
            "tabitem" => ControlType.TabItem,
            "tab" => ControlType.Tab,
            "menu" => ControlType.Menu,
            "menuitem" => ControlType.MenuItem,
            "toolbar" => ControlType.ToolBar,
            "scrollbar" => ControlType.ScrollBar,
            "slider" => ControlType.Slider,
            "progressbar" => ControlType.ProgressBar,
            "hyperlink" => ControlType.Hyperlink,
            "image" => ControlType.Image,
            "custom" => ControlType.Custom,
            "group" or "groupbox" => ControlType.Group,
            "thumb" => ControlType.Thumb,
            "datagrid" => ControlType.DataGrid,
            "dataitem" => ControlType.DataItem,
            "document" => ControlType.Document,
            "splitbutton" => ControlType.SplitButton,
            "window" or "pane" => ControlType.Pane,
            "spinner" => ControlType.Spinner,
            "statusbar" => ControlType.StatusBar,
            "table" => ControlType.Table,
            "titlebar" => ControlType.TitleBar,
            "separator" => ControlType.Separator,
            _ => null
        };

        var conditions = new List<Condition>();

        if (controlType is not null)
            conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));

        if (attrName is not null && attrValue is not null)
        {
            var property = attrName.ToLowerInvariant() switch
            {
                "automationid" or "automation-id" => AutomationElement.AutomationIdProperty,
                "name" => AutomationElement.NameProperty,
                "classname" or "class" => AutomationElement.ClassNameProperty,
                _ => throw new WebDriverException(ErrorType.UnknownError,
                    $"Invalid XPath expression: {originalXpath} (XPathLookupError)")
            };
            conditions.Add(new PropertyCondition(property, attrValue));
        }

        if (conditions.Count == 0 && controlType is null)
        {
            return new PropertyCondition(AutomationElement.AutomationIdProperty, "___NO_MATCH___");
        }

        if (conditions.Count == 0)
            return Condition.TrueCondition;

        if (conditions.Count == 1)
            return conditions[0];

        return new AndCondition(conditions.ToArray());
    }

    private static ControlType MapTagNameToControlType(string tagName)
    {
        return tagName.ToLowerInvariant() switch
        {
            "button" => ControlType.Button,
            "text" or "textblock" => ControlType.Text,
            "edit" or "textbox" => ControlType.Edit,
            "checkbox" => ControlType.CheckBox,
            "radiobutton" or "radio" => ControlType.RadioButton,
            "combobox" or "dropdown" => ControlType.ComboBox,
            "listitem" => ControlType.ListItem,
            "list" or "listview" => ControlType.List,
            "treeitem" => ControlType.TreeItem,
            "tree" => ControlType.Tree,
            "tabitem" => ControlType.TabItem,
            "tab" => ControlType.Tab,
            "menu" => ControlType.Menu,
            "menuitem" => ControlType.MenuItem,
            "toolbar" => ControlType.ToolBar,
            "scrollbar" => ControlType.ScrollBar,
            "slider" => ControlType.Slider,
            "progressbar" => ControlType.ProgressBar,
            "hyperlink" => ControlType.Hyperlink,
            "image" => ControlType.Image,
            "custom" => ControlType.Custom,
            "group" or "groupbox" => ControlType.Group,
            "thumb" => ControlType.Thumb,
            "datagrid" => ControlType.DataGrid,
            "dataitem" => ControlType.DataItem,
            "document" => ControlType.Document,
            "splitbutton" => ControlType.SplitButton,
            "window" or "pane" => ControlType.Pane,
            "spinner" => ControlType.Spinner,
            "statusbar" => ControlType.StatusBar,
            "table" => ControlType.Table,
            "titlebar" => ControlType.TitleBar,
            "separator" => ControlType.Separator,
            _ => ControlType.Custom
        };
    }
}
