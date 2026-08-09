namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// UI Automation property identifiers.
/// </summary>
/// <remarks>
/// From <c>UIAutomationClient.h</c>. Named constants rather than literals at the
/// call site, because 30004 and 30003 differ by one digit and mean
/// LocalizedControlType and ControlType — the exact confusion that made the
/// previous implementation's <c>tag name</c> locator match the wrong property.
/// </remarks>
internal static class UiaPropertyIds
{
    /// <summary>Bounding rectangle, as a double[4].</summary>
    internal const int BoundingRectangle = 30001;

    /// <summary>Owning process id.</summary>
    internal const int ProcessId = 30002;

    /// <summary>Control type, as an integer id.</summary>
    internal const int ControlType = 30003;

    /// <summary>Control type as a localized display string.</summary>
    internal const int LocalizedControlType = 30004;

    /// <summary>Name.</summary>
    internal const int Name = 30005;

    /// <summary>Runtime id, as an int[].</summary>
    internal const int RuntimeId = 30007;

    /// <summary>Whether the element is enabled.</summary>
    internal const int IsEnabled = 30010;

    /// <summary>Automation id.</summary>
    internal const int AutomationId = 30011;

    /// <summary>Class name.</summary>
    internal const int ClassName = 30012;

    /// <summary>Native window handle.</summary>
    internal const int NativeWindowHandle = 30020;

    /// <summary>Whether the element is off-screen.</summary>
    internal const int IsOffscreen = 30022;

    /// <summary>Whether the element has keyboard focus.</summary>
    internal const int HasKeyboardFocus = 30026;

    /// <summary>Whether the element exposes the ExpandCollapse pattern.</summary>
    internal const int IsExpandCollapsePatternAvailable = 30028;

    /// <summary>Whether the element exposes the Invoke pattern.</summary>
    internal const int IsInvokePatternAvailable = 30031;

    /// <summary>Whether the element exposes the ScrollItem pattern.</summary>
    internal const int IsScrollItemPatternAvailable = 30035;

    /// <summary>Whether the element exposes the SelectionItem pattern.</summary>
    internal const int IsSelectionItemPatternAvailable = 30036;

    /// <summary>Whether the element exposes the Toggle pattern.</summary>
    internal const int IsTogglePatternAvailable = 30041;

    /// <summary>Whether the element exposes the Selection pattern.</summary>
    internal const int IsSelectionPatternAvailable = 30037;

    /// <summary>Whether the element exposes the Value pattern.</summary>
    internal const int IsValuePatternAvailable = 30043;

    /// <summary>ValuePattern's value, as a string.</summary>
    internal const int ValueValue = 30045;

    /// <summary>SelectionPattern's selected children, as an element array.</summary>
    internal const int SelectionSelection = 30059;

    /// <summary>Whether a SelectionItem is selected.</summary>
    internal const int SelectionItemIsSelected = 30079;
}
