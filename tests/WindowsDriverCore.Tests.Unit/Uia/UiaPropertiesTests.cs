using System;
using System.Collections.Generic;
using System.Linq;
using CsCheck;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation.Uia;

namespace WindowsDriverCore.Tests.Unit.Uia;

/// <summary>
/// The attribute-name to UIA-property-id table.
/// </summary>
/// <remarks>
/// <para>
/// <b>These do not try to pin every id, and that is deliberate.</b> A mutation
/// run showed 157 survivors here and zero kills, and the obvious response —
/// asserting all ~150 literals — would be a change-detector test: it fails
/// whenever the table is edited and detects no defect, because the assertion
/// would be a copy of the thing it checks.
/// </para>
/// <para>
/// What is worth asserting is the property a wrong id actually violates.
/// **Distinctness** is the one that matters: two names mapping to one id means
/// one of them silently reads the wrong property, which is the failure this
/// table can really have and the same class as the `tag name` locator reading
/// LocalizedControlType for two years. Ranges catch a transposed digit.
/// </para>
/// <para>
/// The values themselves came from Microsoft's published tables rather than
/// memory, and the ones that matter in practice are checked end to end against
/// real WinAppDriver by <c>ElementAttributeTests</c> — Name, AutomationId,
/// ClassName, FrameworkId, IsEnabled, RuntimeId, BoundingRectangle,
/// ClickablePoint, ControlType, LocalizedControlType, Orientation and the
/// pattern-qualified ones.
/// </para>
/// </remarks>
[TestFixture]
public sealed class UiaPropertiesTests
{
    /// <summary>Names measured against real WinAppDriver, so their ids are known-good.</summary>
    private static readonly (string Name, int Id)[] Measured =
    [
        ("Name", 30005),
        ("AutomationId", 30011),
        ("ClassName", 30012),
        ("FrameworkId", 30024),
        ("IsEnabled", 30010),
        ("IsOffscreen", 30022),
        ("ProcessId", 30002),
        ("RuntimeId", 30000),
        ("BoundingRectangle", 30001),
        ("ClickablePoint", 30014),
        ("ControlType", 30003),
        ("LocalizedControlType", 30004),
        ("Orientation", 30023),
        ("HelpText", 30013),
        ("IsValuePatternAvailable", 30043),
        ("IsSelectionItemPatternAvailable", 30036),
        ("Value.Value", 30045),
        ("Value.IsReadOnly", 30046),
        ("SelectionItem.IsSelected", 30079),
        ("LegacyName", 30092),
    ];

    private static readonly string[] KnownNames =
    [
        // Element properties.
        "AcceleratorKey", "AccessKey", "AutomationId", "BoundingRectangle", "ClassName",
        "ClickablePoint", "ControlType", "Culture", "FrameworkId", "FullDescription",
        "HasKeyboardFocus", "HelpText", "IsContentElement", "IsControlElement", "IsEnabled",
        "IsKeyboardFocusable", "IsOffscreen", "IsPassword", "ItemStatus", "ItemType",
        "LocalizedControlType", "Name", "NativeWindowHandle", "Orientation", "ProcessId",
        "RuntimeId",
        // Pattern availability.
        "IsInvokePatternAvailable", "IsTogglePatternAvailable", "IsValuePatternAvailable",
        "IsSelectionPatternAvailable", "IsSelectionItemPatternAvailable",
        "IsExpandCollapsePatternAvailable", "IsScrollItemPatternAvailable",
        "IsWindowPatternAvailable",
        // Pattern-qualified.
        "Value.Value", "Value.IsReadOnly", "SelectionItem.IsSelected", "Toggle.ToggleState",
        "ExpandCollapse.ExpandCollapseState", "LegacyIAccessible.Name",
        // WinAppDriver's own alias.
        "LegacyName",
    ];

    [Test]
    public void MeasuredNames_MapToTheIdsTheyWereMeasuredWith()
    {
        // Twenty names, each verified end to end against real WinAppDriver, so
        // the id behind them is known rather than transcribed. Pinning these is
        // not a change detector because changing one would change a measured
        // response.
        foreach ((string name, int id) in Measured)
        {
            UiaProperties.TryGetId(name, out int actual).ShouldBeTrue(name);
            actual.ShouldBe(id, name);
        }
    }

    [Test]
    public void NoTwoNamesShareAnId_ExceptTheOneDocumentedAlias()
    {
        // The defect this table can really have. Two names on one id means one
        // of them silently reads the wrong property — no error, just a wrong
        // answer, which is exactly how the tag name locator read
        // LocalizedControlType for two years.
        //
        // LegacyName is a deliberate alias for LegacyIAccessible.Name, measured
        // against WinAppDriver, so it is the one permitted collision.
        Dictionary<int, List<string>> byId = [];

        foreach (string name in KnownNames)
        {
            UiaProperties.TryGetId(name, out int id).ShouldBeTrue(name);
            if (!byId.TryGetValue(id, out List<string>? names))
            {
                names = [];
                byId[id] = names;
            }

            names.Add(name);
        }

        IEnumerable<KeyValuePair<int, List<string>>> collisions =
            byId.Where(entry => entry.Value.Count > 1);

        foreach (KeyValuePair<int, List<string>> collision in collisions)
        {
            string[] sharing = [.. collision.Value.OrderBy(name => name, StringComparer.Ordinal)];

            sharing.ShouldBe(
                ["LegacyIAccessible.Name", "LegacyName"],
                customMessage:
                    $"id {collision.Key} is shared by {string.Join(", ", sharing)}");
        }
    }

    [Test]
    public void EveryIdIsInTheUiaPropertyRange()
    {
        // A transposed digit lands outside the range and would otherwise sit
        // there returning null forever, indistinguishable from an unset
        // property. Microsoft's own tables contain two such typos — ItemType
        // documented as 300021 and Orientation as 300023 — so this is a mistake
        // the source material actually makes.
        foreach (string name in KnownNames)
        {
            UiaProperties.TryGetId(name, out int id).ShouldBeTrue(name);
            id.ShouldBeInRange(30000, 30200, $"{name} = {id} is outside the UIA property range");
        }
    }

    [Test]
    public void UnknownNames_NeverResolve()
    {
        // The complement: a table answering yes to everything would satisfy the
        // lookups above and quietly accept any attribute name.
        Gen.String.Sample(name =>
            UiaProperties.TryGetId(name, out _).ShouldBe(KnownNames.Contains(name), $"'{name}'"));
    }

    [Test]
    public void LookupIsCaseSensitive()
    {
        // Ordinal, like every other identifier comparison in this driver, and
        // measured: WinAppDriver answered null for "name" while answering "Five"
        // for "Name".
        UiaProperties.TryGetId("name", out _).ShouldBeFalse();
        UiaProperties.TryGetId("Name", out _).ShouldBeTrue();
    }
}
