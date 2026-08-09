using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CsCheck;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation.Uia;

namespace WindowsDriverCore.Tests.Unit.Uia;

/// <summary>
/// Properties of the element id format, over generated input.
/// </summary>
/// <remarks>
/// <para>
/// The example-based tests assert that one known runtime id formats to one known
/// string. These assert things that must hold for <i>every</i> runtime id, which
/// is a different question and catches a different class of defect.
/// </para>
/// <para>
/// The id format is the contract between issuing an element and resolving it
/// again. Both sides call <see cref="UiaRuntimeId.Format"/>, so they cannot
/// disagree about spelling — but they can both be wrong together, and
/// injectivity is exactly the property that a shared implementation does not
/// give you for free.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RuntimeIdPropertyTests
{
    /// <summary>
    /// Runtime ids as UIA produces them: a short array, first element usually
    /// the 42 marker, the rest process and provider values.
    /// </summary>
    private static readonly Gen<int[]> RuntimeIds =
        Gen.Int.Array[1, 6];

    [Test]
    public void Format_DistinguishesIdsThatWouldCollideWithoutASeparator()
    {
        // Two different runtime ids must never format to the same string, or two
        // elements share an id and a command aimed at one reaches the other.
        //
        // **The condition is constructed, not sampled, and that is the point.**
        // An earlier version of this test drew two independent random arrays and
        // asserted they collide only when equal. It passes against a formatter
        // with NO separator at all — verified by removing one — because two
        // independently generated arrays essentially never share a digit
        // concatenation. The region where correct and broken differ has measure
        // zero under that generator, so the test was insensitive to the one
        // defect it was written for.
        //
        // Here each pair is built to collide if the separator is missing: the
        // same digits, partitioned two ways. [1, 2, 3] against [12, 3] both
        // concatenate to "123".
        Gen.Int[1, 9].Array[2, 5].Sample(digits =>
        {
            int[] split = digits;
            int[] merged = [(digits[0] * 10) + digits[1], .. digits[2..]];

            string splitId = UiaRuntimeId.Format(split);
            string mergedId = UiaRuntimeId.Format(merged);

            splitId.ShouldNotBe(
                mergedId,
                $"[{string.Join(',', split)}] and [{string.Join(',', merged)}] are different " +
                "elements and must not share an id");
        });
    }

    [Test]
    public void Format_OfEqualIds_IsEqual()
    {
        // The other half of injectivity, and the cheap half: the same id always
        // formats the same way. The resolver compares formatted strings, so an
        // unstable format would make an element unreachable through the id it
        // was issued under.
        RuntimeIds.Sample(runtimeId =>
            UiaRuntimeId.Format(runtimeId).ShouldBe(UiaRuntimeId.Format([.. runtimeId])));
    }

    [Test]
    public void Format_ProducesOnlyDigitsDotsAndSigns()
    {
        // Element ids travel in a URL path segment. A separator that needed
        // escaping would work in every test here and fail against a real client,
        // which is the kind of thing that shows up as "the driver is broken" a
        // long way from its cause.
        RuntimeIds.Sample(runtimeId =>
        {
            string formatted = UiaRuntimeId.Format(runtimeId);

            formatted.All(character =>
                char.IsAsciiDigit(character) || character == '.' || character == '-')
                .ShouldBeTrue($"'{formatted}' contains something that needs escaping in a URL");
        });
    }

    [Test]
    public void Format_KeepsEveryPart_InOrder()
    {
        // Splitting the result must give back exactly what went in. This is the
        // round-trip the resolver depends on, stated over all inputs rather than
        // one.
        RuntimeIds.Sample(runtimeId =>
        {
            string formatted = UiaRuntimeId.Format(runtimeId);
            string[] parts = formatted.Split('.');

            parts.Length.ShouldBe(runtimeId.Length, formatted);
            parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture))
                .ShouldBe(runtimeId, formatted);
        });
    }

    [Test]
    public void Format_OfAnEmptyId_IsEmpty()
    {
        // The boundary the generator above deliberately excludes, because UIA
        // returning a zero-length runtime id means the element has no identity
        // and the callers skip it.
        UiaRuntimeId.Format([]).ShouldBe(string.Empty);
    }

    [Test]
    public void ControlTypeNames_RoundTripThroughTheirIds()
    {
        // Two tables, one derived from the other, and they must agree in both
        // directions: the locator takes "Button" and /name answers
        // "ControlType.Button". A hand-maintained reverse map is exactly where
        // an entry goes missing.
        IReadOnlyList<string> names =
        [
            "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image",
            "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton",
            "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text",
            "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb",
            "DataGrid", "DataItem", "Document", "SplitButton", "Window", "Pane",
            "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
        ];

        Gen.OneOfConst([.. names]).Sample(name =>
        {
            UiaControlTypes.TryGetId(name, out int controlTypeId).ShouldBeTrue(name);
            UiaControlTypes.TagName(controlTypeId).ShouldBe($"ControlType.{name}");
        });
    }

    [Test]
    public void ControlTypeIds_AreDistinct()
    {
        // A duplicated id would make the reverse lookup answer with whichever
        // name happened to win, silently renaming a control type.
        List<int> ids = [];

        foreach (string name in new[]
        {
            "Button", "Calendar", "CheckBox", "ComboBox", "Edit", "Hyperlink", "Image",
            "ListItem", "List", "Menu", "MenuBar", "MenuItem", "ProgressBar", "RadioButton",
            "ScrollBar", "Slider", "Spinner", "StatusBar", "Tab", "TabItem", "Text",
            "ToolBar", "ToolTip", "Tree", "TreeItem", "Custom", "Group", "Thumb",
            "DataGrid", "DataItem", "Document", "SplitButton", "Window", "Pane",
            "Header", "HeaderItem", "Table", "TitleBar", "Separator", "SemanticZoom", "AppBar",
        })
        {
            UiaControlTypes.TryGetId(name, out int id).ShouldBeTrue(name);
            ids.Add(id);
        }

        ids.Distinct().Count().ShouldBe(ids.Count, "two control types share an id");
    }

    [Test]
    public void UnknownControlTypeNames_NeverResolve()
    {
        // The complement of the round trip. Without it, a table that answered
        // "yes" to everything would pass the round-trip test for the names it
        // knows and quietly match arbitrary tag names against Custom.
        Gen.String.Sample(name =>
            UiaControlTypes.TryGetId(name, out _).ShouldBe(
                IsKnownControlType(name), $"'{name}'"));
    }

    private static bool IsKnownControlType(string name) => name is
        "Button" or "Calendar" or "CheckBox" or "ComboBox" or "Edit" or "Hyperlink" or
        "Image" or "ListItem" or "List" or "Menu" or "MenuBar" or "MenuItem" or
        "ProgressBar" or "RadioButton" or "ScrollBar" or "Slider" or "Spinner" or
        "StatusBar" or "Tab" or "TabItem" or "Text" or "ToolBar" or "ToolTip" or
        "Tree" or "TreeItem" or "Custom" or "Group" or "Thumb" or "DataGrid" or
        "DataItem" or "Document" or "SplitButton" or "Window" or "Pane" or "Header" or
        "HeaderItem" or "Table" or "TitleBar" or "Separator" or "SemanticZoom" or "AppBar";
}
