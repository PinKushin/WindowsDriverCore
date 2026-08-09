using System.Collections.Frozen;
using System.Collections.Generic;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// UI Automation control types, by the programmatic name a client sends.
/// </summary>
/// <remarks>
/// <para>
/// The <c>tag name</c> locator matches <c>UIA_ControlTypePropertyId</c> against
/// the enum's programmatic name — <c>Button</c>, <c>ListItem</c>, <c>Text</c> —
/// <b>case-sensitively</b> and without the <c>ControlType.</c> prefix. Measured
/// against WinAppDriver 1.2.2009.02003: <c>Button</c> and <c>ListItem</c> both
/// return an element, <c>button</c> and <c>list item</c> both return
/// <c>no such element</c>.
/// </para>
/// <para>
/// Note the prefix asymmetry, which is easy to get backwards: the locator takes
/// <c>Button</c>, while <c>GET /element/{id}/name</c> answers
/// <c>ControlType.Button</c>.
/// </para>
/// <para>
/// Ids from <c>UIAutomationClient.h</c>, checked against Microsoft's published
/// Control Type Identifiers table rather than typed from memory. A single wrong
/// number here does not error — it silently matches a different control type,
/// which is the same failure mode that made the previous implementation match
/// LocalizedControlType for two years without anyone noticing.
/// </para>
/// </remarks>
internal static class UiaControlTypes
{
    private static readonly FrozenDictionary<string, int> ByName =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Button"] = 50000,
            ["Calendar"] = 50001,
            ["CheckBox"] = 50002,
            ["ComboBox"] = 50003,
            ["Edit"] = 50004,
            ["Hyperlink"] = 50005,
            ["Image"] = 50006,
            ["ListItem"] = 50007,
            ["List"] = 50008,
            ["Menu"] = 50009,
            ["MenuBar"] = 50010,
            ["MenuItem"] = 50011,
            ["ProgressBar"] = 50012,
            ["RadioButton"] = 50013,
            ["ScrollBar"] = 50014,
            ["Slider"] = 50015,
            ["Spinner"] = 50016,
            ["StatusBar"] = 50017,
            ["Tab"] = 50018,
            ["TabItem"] = 50019,
            ["Text"] = 50020,
            ["ToolBar"] = 50021,
            ["ToolTip"] = 50022,
            ["Tree"] = 50023,
            ["TreeItem"] = 50024,
            ["Custom"] = 50025,
            ["Group"] = 50026,
            ["Thumb"] = 50027,
            ["DataGrid"] = 50028,
            ["DataItem"] = 50029,
            ["Document"] = 50030,
            ["SplitButton"] = 50031,
            ["Window"] = 50032,
            ["Pane"] = 50033,
            ["Header"] = 50034,
            ["HeaderItem"] = 50035,
            ["Table"] = 50036,
            ["TitleBar"] = 50037,
            ["Separator"] = 50038,
            ["SemanticZoom"] = 50039,
            ["AppBar"] = 50040,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Looks up a control type by its programmatic name.</summary>
    /// <param name="name">The name as the client sent it.</param>
    /// <param name="controlTypeId">The UIA control type id, when known.</param>
    /// <returns><see langword="true"/> when the name is a control type.</returns>
    /// <remarks>
    /// An unknown name is not an error here. It has to become a find that
    /// matched nothing, so that <c>POST /element</c> answers <c>no such
    /// element</c> — which is what WinAppDriver does for
    /// <c>FindElementByTagName("InvalidTagName")</c>. The previous
    /// implementation fell back to <c>UIA_CustomControlTypeId</c>, which can
    /// succeed and hand back a real element for a name that means nothing.
    /// </remarks>
    internal static bool TryGetId(string name, out int controlTypeId) =>
        ByName.TryGetValue(name, out controlTypeId);
}
