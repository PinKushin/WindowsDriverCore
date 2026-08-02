namespace WindowsDriverCore.Automation.Com;

/// <summary>
/// UIA property IDs from UIAutomationClient.h.
/// </summary>
public static class UIAPropertyIds
{
    public const int UIA_ControlTypePropertyId = 30003;
    public const int UIA_NamePropertyId = 30005;
    public const int UIA_AutomationIdPropertyId = 30011;
    public const int UIA_ClassNamePropertyId = 30012;
    public const int UIA_HasKeyboardFocusPropertyId = 30026;
    public const int UIA_IsEnabledPropertyId = 30010;
    public const int UIA_NativeWindowHandlePropertyId = 30020;
    public const int UIA_BoundingRectanglePropertyId = 30001;
    public const int UIA_ProcessIdPropertyId = 30002;
    public const int UIA_RuntimeIdPropertyId = 30007;
    public const int UIA_IsOffscreenPropertyId = 30029;
    public const int UIA_OrientationPropertyId = 30009;
    public const int UIA_IsKeyboardFocusablePropertyId = 30018;
    public const int UIA_IsPasswordPropertyId = 30019;
    public const int UIA_HelpTextPropertyId = 30059;
    public const int UIA_IsDialogPropertyId = 30167;
}

/// <summary>
/// UIA control type IDs from UIAutomationClient.h.
/// </summary>
public static class UIAControlTypeIds
{
    public const int UIA_ButtonControlTypeId = 50000;
    public const int UIA_CalendarControlTypeId = 50001;
    public const int UIA_CheckBoxControlTypeId = 50002;
    public const int UIA_ComboBoxControlTypeId = 50003;
    public const int UIA_EditControlTypeId = 50004;
    public const int UIA_HyperlinkControlTypeId = 50005;
    public const int UIA_ImageControlTypeId = 50006;
    public const int UIA_ListItemControlTypeId = 50007;
    public const int UIA_ListControlTypeId = 50008;
    public const int UIA_MenuControlTypeId = 50009;
    public const int UIA_MenuBarControlTypeId = 50010;
    public const int UIA_MenuItemControlTypeId = 50011;
    public const int UIA_ProgressBarControlTypeId = 50012;
    public const int UIA_RadioButtonControlTypeId = 50013;
    public const int UIA_ScrollBarControlTypeId = 50014;
    public const int UIA_SliderControlTypeId = 50015;
    public const int UIA_SpinnerControlTypeId = 50016;
    public const int UIA_StatusBarControlTypeId = 50017;
    public const int UIA_TabControlTypeId = 50018;
    public const int UIA_TabItemControlTypeId = 50019;
    public const int UIA_TextControlTypeId = 50020;
    public const int UIA_ToolBarControlTypeId = 50021;
    public const int UIA_ToolTipControlTypeId = 50022;
    public const int UIA_TreeControlTypeId = 50023;
    public const int UIA_TreeItemControlTypeId = 50024;
    public const int UIA_CustomControlTypeId = 50025;
    public const int UIA_GroupControlTypeId = 50026;
    public const int UIA_ThumbControlTypeId = 50027;
    public const int UIA_DataGridControlTypeId = 50028;
    public const int UIA_DataItemControlTypeId = 50029;
    public const int UIA_DocumentControlTypeId = 50030;
    public const int UIA_SplitButtonControlTypeId = 50031;
    public const int UIA_PaneControlTypeId = 50032;
    public const int UIA_HeaderControlTypeId = 50033;
    public const int UIA_HeaderItemControlTypeId = 50034;
    public const int UIA_TableControlTypeId = 50035;
    public const int UIA_TitleBarControlTypeId = 50037;
    public const int UIA_SeparatorControlTypeId = 50038;
}

/// <summary>
/// UIA pattern IDs from UIAutomationClient.h.
/// </summary>
public static class UIAPatternIds
{
    public const int UIA_InvokePatternId = 10000;
    public const int UIA_SelectionPatternId = 10001;
    public const int UIA_ValuePatternId = 10002;
    public const int UIA_ExpandCollapsePatternId = 10005;
    public const int UIA_ScrollPatternId = 10004;
    public const int UIA_SelectionItemPatternId = 10010;
    public const int UIA_TogglePatternId = 10015;
    public const int UIA_TransformPatternId = 10016;
    public const int UIA_DragPatternId = 10030;
    public const int UIA_DropTargetPatternId = 10031;
}

/// <summary>
/// TreeScope values for FindFirst/FindAll.
/// </summary>
public static class UIATreeScope
{
    public const int TreeScope_Element = 0x0001;
    public const int TreeScope_Children = 0x0002;
    public const int TreeScope_Descendants = 0x0004;
    public const int TreeScope_Parent = 0x0008;
    public const int TreeScope_Subtree = TreeScope_Element | TreeScope_Children | TreeScope_Descendants;
}

/// <summary>
/// ExpandCollapseState values.
/// </summary>
public static class UIAExpandCollapseState
{
    public const int ExpandCollapseState_Collapsed = 0;
    public const int ExpandCollapseState_Expanded = 1;
    public const int ExpandCollapseState_PartiallyExpanded = 2;
    public const int ExpandCollapseState_LeafNode = 3;
}

/// <summary>
/// Condition flags for CreatePropertyConditionEx.
/// </summary>
public static class UIAConditionFlags
{
    public const int PropertyConditionFlags_None = 0x00000000;
    public const int PropertyConditionFlags_IgnoreCase = 0x00000001;
}
