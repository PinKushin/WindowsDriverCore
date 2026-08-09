using System.Collections.Frozen;
using System.Collections.Generic;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// UI Automation properties, by the name a client sends to
/// <c>GET /element/{id}/attribute/{name}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The attribute route is an <b>open surface</b>, not a fixed list of supported
/// attributes: measured against WinAppDriver, 28 different names all answered,
/// including pattern-qualified ones like <c>Value.Value</c> and
/// <c>SelectionItem.IsSelected</c> and availability flags like
/// <c>IsInvokePatternAvailable</c>. So the route is a lookup, and this is the
/// table.
/// </para>
/// <para>
/// Naming follows the UIA constants with <c>UIA_</c> and <c>PropertyId</c>
/// stripped, and pattern properties spelled <c>Pattern.Property</c>. All three
/// spellings were measured; the dotted form is not a guess.
/// </para>
/// <para>
/// Ids come from Microsoft's published tables rather than from memory, for the
/// same reason as <see cref="UiaControlTypes"/>: a wrong number does not error,
/// it silently answers about a different property. Two entries correct obvious
/// typos in those tables — <c>ItemType</c> is documented as 300021 and
/// <c>Orientation</c> as 300023, which are outside the property id range and
/// are 30021 and 30023.
/// </para>
/// <para>
/// A name absent from this table answers <c>null</c>, which is also what an
/// unset property answers. That collision is WinAppDriver's, not an omission
/// here: measured, <c>HelpText</c> on an element without one and
/// <c>InvalidAttributeName</c> both come back <c>null</c> with HTTP 200, and a
/// caller cannot tell them apart.
/// </para>
/// </remarks>
internal static class UiaProperties
{
    /// <summary>ControlType, which renders as a prefixed name rather than a number.</summary>
    internal const int ControlType = 30003;

    /// <summary>Orientation, which renders as an enumeration name.</summary>
    internal const int Orientation = 30023;

    /// <summary>
    /// BoundingRectangle, which is rendered from UIA's integer rectangle rather
    /// than from this property's raw doubles, so it agrees with what
    /// <c>/location</c> and <c>/size</c> report.
    /// </summary>
    internal const int BoundingRectangle = 30001;

    private static readonly FrozenDictionary<string, int> ByName =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // Automation element properties.
            ["AcceleratorKey"] = 30006,
            ["AccessKey"] = 30007,
            ["AnnotationObjects"] = 30156,
            ["AnnotationTypes"] = 30155,
            ["AriaProperties"] = 30102,
            ["AriaRole"] = 30101,
            ["AutomationId"] = 30011,
            ["BoundingRectangle"] = 30001,
            ["CenterPoint"] = 30165,
            ["ClassName"] = 30012,
            ["ClickablePoint"] = 30014,
            ["ControllerFor"] = 30104,
            ["ControlType"] = ControlType,
            ["Culture"] = 30015,
            ["DescribedBy"] = 30105,
            ["FillColor"] = 30160,
            ["FillType"] = 30162,
            ["FlowsFrom"] = 30148,
            ["FlowsTo"] = 30106,
            ["FrameworkId"] = 30024,
            ["FullDescription"] = 30159,
            ["HasKeyboardFocus"] = 30008,
            ["HeadingLevel"] = 30173,
            ["HelpText"] = 30013,
            ["IsContentElement"] = 30017,
            ["IsControlElement"] = 30016,
            ["IsDataValidForForm"] = 30103,
            ["IsDialog"] = 30174,
            ["IsEnabled"] = 30010,
            ["IsKeyboardFocusable"] = 30009,
            ["IsOffscreen"] = 30022,
            ["IsPassword"] = 30019,
            ["IsPeripheral"] = 30150,
            ["IsRequiredForForm"] = 30025,
            ["ItemStatus"] = 30026,
            ["ItemType"] = 30021,
            ["LabeledBy"] = 30018,
            ["LandmarkType"] = 30157,
            ["Level"] = 30154,
            ["LiveSetting"] = 30135,
            ["LocalizedControlType"] = 30004,
            ["LocalizedLandmarkType"] = 30158,
            ["Name"] = 30005,
            ["NativeWindowHandle"] = 30020,
            ["OptimizeForVisualContent"] = 30111,
            ["Orientation"] = Orientation,
            ["OutlineColor"] = 30161,
            ["OutlineThickness"] = 30164,
            ["PositionInSet"] = 30152,
            ["ProcessId"] = 30002,
            ["ProviderDescription"] = 30107,
            ["Rotation"] = 30166,
            ["RuntimeId"] = 30000,
            ["Size"] = 30167,
            ["SizeOfSet"] = 30153,
            ["VisualEffects"] = 30163,

            // Pattern availability.
            ["IsAnnotationPatternAvailable"] = 30118,
            ["IsCustomNavigationPatternAvailable"] = 30151,
            ["IsDockPatternAvailable"] = 30027,
            ["IsDragPatternAvailable"] = 30137,
            ["IsDropTargetPatternAvailable"] = 30141,
            ["IsExpandCollapsePatternAvailable"] = 30028,
            ["IsGridItemPatternAvailable"] = 30029,
            ["IsGridPatternAvailable"] = 30030,
            ["IsInvokePatternAvailable"] = 30031,
            ["IsItemContainerPatternAvailable"] = 30108,
            ["IsLegacyIAccessiblePatternAvailable"] = 30090,
            ["IsMultipleViewPatternAvailable"] = 30032,
            ["IsObjectModelPatternAvailable"] = 30112,
            ["IsRangeValuePatternAvailable"] = 30033,
            ["IsScrollItemPatternAvailable"] = 30035,
            ["IsScrollPatternAvailable"] = 30034,
            ["IsSelectionItemPatternAvailable"] = 30036,
            ["IsSelectionPatternAvailable"] = 30037,
            ["IsSpreadsheetPatternAvailable"] = 30128,
            ["IsSpreadsheetItemPatternAvailable"] = 30132,
            ["IsStylesPatternAvailable"] = 30127,
            ["IsSynchronizedInputPatternAvailable"] = 30110,
            ["IsTableItemPatternAvailable"] = 30039,
            ["IsTablePatternAvailable"] = 30038,
            ["IsTextChildPatternAvailable"] = 30136,
            ["IsTextEditPatternAvailable"] = 30149,
            ["IsTextPatternAvailable"] = 30040,
            ["IsTextPattern2Available"] = 30119,
            ["IsTogglePatternAvailable"] = 30041,
            ["IsTransformPatternAvailable"] = 30042,
            ["IsTransformPattern2Available"] = 30134,
            ["IsValuePatternAvailable"] = 30043,
            ["IsVirtualizedItemPatternAvailable"] = 30109,
            ["IsWindowPatternAvailable"] = 30044,

            // Control pattern properties, spelled Pattern.Property.
            ["Annotation.AnnotationTypeId"] = 30113,
            ["Annotation.AnnotationTypeName"] = 30114,
            ["Annotation.Author"] = 30115,
            ["Annotation.DateTime"] = 30116,
            ["Annotation.Target"] = 30117,
            ["Dock.DockPosition"] = 30069,
            ["Drag.DropEffect"] = 30139,
            ["Drag.DropEffects"] = 30140,
            ["Drag.GrabbedItems"] = 30144,
            ["Drag.IsGrabbed"] = 30138,
            ["DropTarget.DropTargetEffect"] = 30142,
            ["DropTarget.DropTargetEffects"] = 30143,
            ["ExpandCollapse.ExpandCollapseState"] = 30070,
            ["Grid.ColumnCount"] = 30063,
            ["Grid.RowCount"] = 30062,
            ["GridItem.Column"] = 30065,
            ["GridItem.ColumnSpan"] = 30067,
            ["GridItem.ContainingGrid"] = 30068,
            ["GridItem.Row"] = 30064,
            ["GridItem.RowSpan"] = 30066,
            ["LegacyIAccessible.ChildId"] = 30091,
            ["LegacyIAccessible.DefaultAction"] = 30100,
            ["LegacyIAccessible.Description"] = 30094,
            ["LegacyIAccessible.Help"] = 30097,
            ["LegacyIAccessible.KeyboardShortcut"] = 30098,
            ["LegacyIAccessible.Name"] = 30092,
            ["LegacyIAccessible.Role"] = 30095,
            ["LegacyIAccessible.Selection"] = 30099,
            ["LegacyIAccessible.State"] = 30096,
            ["LegacyIAccessible.Value"] = 30093,
            ["MultipleView.CurrentView"] = 30071,
            ["MultipleView.SupportedViews"] = 30072,
            ["RangeValue.IsReadOnly"] = 30048,
            ["RangeValue.LargeChange"] = 30051,
            ["RangeValue.Maximum"] = 30050,
            ["RangeValue.Minimum"] = 30049,
            ["RangeValue.SmallChange"] = 30052,
            ["RangeValue.Value"] = 30047,
            ["Scroll.HorizontallyScrollable"] = 30057,
            ["Scroll.HorizontalScrollPercent"] = 30053,
            ["Scroll.HorizontalViewSize"] = 30054,
            ["Scroll.VerticallyScrollable"] = 30058,
            ["Scroll.VerticalScrollPercent"] = 30055,
            ["Scroll.VerticalViewSize"] = 30056,
            ["Selection.CanSelectMultiple"] = 30060,
            ["Selection.IsSelectionRequired"] = 30061,
            ["Selection.Selection"] = 30059,
            ["SelectionItem.IsSelected"] = 30079,
            ["SelectionItem.SelectionContainer"] = 30080,
            ["Toggle.ToggleState"] = 30086,
            ["Transform.CanMove"] = 30087,
            ["Transform.CanResize"] = 30088,
            ["Transform.CanRotate"] = 30089,
            ["Transform2.CanZoom"] = 30133,
            ["Transform2.ZoomLevel"] = 30145,
            ["Transform2.ZoomMaximum"] = 30147,
            ["Transform2.ZoomMinimum"] = 30146,
            ["Value.IsReadOnly"] = 30046,
            ["Value.Value"] = 30045,
            ["Window.CanMaximize"] = 30073,
            ["Window.CanMinimize"] = 30074,
            ["Window.IsModal"] = 30077,
            ["Window.IsTopmost"] = 30078,
            ["Window.WindowInteractionState"] = 30076,
            ["Window.WindowVisualState"] = 30075,

            // WinAppDriver's own alias, measured: "LegacyName" answers the
            // LegacyIAccessible Name. It is not a UIA spelling, and no other
            // Legacy* shorthand has been measured, so no others are invented
            // here.
            ["LegacyName"] = 30092,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Looks up a property by the name a client sent.</summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="propertyId">The UIA property id, when known.</param>
    /// <returns><see langword="true"/> when the name is a property.</returns>
    internal static bool TryGetId(string name, out int propertyId) =>
        ByName.TryGetValue(name, out propertyId);
}
