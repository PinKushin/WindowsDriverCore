namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// UI Automation control pattern identifiers.
/// </summary>
/// <remarks>
/// From <c>UIAutomationClient.h</c>, checked against Microsoft's published
/// Control Pattern Identifiers table rather than typed from memory. These sit in
/// the 10000 range while property ids sit in the 30000 range, so a mix-up
/// produces <c>E_INVALIDARG</c> rather than a quietly wrong answer — but the
/// values within the range are one digit apart and a wrong one asks for the
/// wrong pattern.
/// </remarks>
internal static class UiaPatternIds
{
    /// <summary>Invoke — buttons, links, menu items.</summary>
    internal const int Invoke = 10000;

    /// <summary>Value — text boxes and anything with a settable string.</summary>
    internal const int Value = 10002;

    /// <summary>ExpandCollapse — combo boxes, tree items, menus.</summary>
    internal const int ExpandCollapse = 10005;

    /// <summary>SelectionItem — list rows, tab items, radio buttons.</summary>
    internal const int SelectionItem = 10010;

    /// <summary>Toggle — check boxes and switches.</summary>
    internal const int Toggle = 10015;

    /// <summary>ScrollItem — bringing an element into view.</summary>
    internal const int ScrollItem = 10017;
}
