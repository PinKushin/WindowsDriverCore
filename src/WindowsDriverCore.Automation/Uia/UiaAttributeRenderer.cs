using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Renders a UI Automation property value the way WinAppDriver's
/// <c>/attribute</c> route does.
/// </summary>
/// <remarks>
/// Every rule here is a measured response, and several of them are not what a
/// JSON serializer would produce on its own — booleans are the strings
/// <c>"True"</c> and <c>"False"</c>, a rectangle is
/// <c>"Left:257 Top:616 Width:97 Height:35"</c>, and a point is <c>"305,633"</c>.
/// The same properties read through <c>/enabled</c> or <c>/displayed</c> come
/// back as real JSON booleans, so the two routes genuinely disagree about the
/// same underlying value.
/// </remarks>
internal static class UiaAttributeRenderer
{
    private static readonly IReadOnlyList<string> Orientations = ["None", "Horizontal", "Vertical"];

    /// <summary>Renders a property value.</summary>
    /// <param name="propertyId">Which property was read, for the two that need it.</param>
    /// <param name="value">The value UIA returned.</param>
    /// <returns>The string to send, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Null covers both "no such property" and "property is unset", which a
    /// caller cannot distinguish through WinAppDriver either — measured, an
    /// element with no <c>HelpText</c> and the name
    /// <c>InvalidAttributeName</c> both answer null with HTTP 200.
    /// </remarks>
    internal static string? Render(int propertyId, object? value) => value switch
    {
        null => null,

        // An empty string reads as absent. Measured on three properties at once:
        // HelpText, AcceleratorKey and AccessKey all answer null on a Calculator
        // button, and Value.Value answers null for an empty text box whose
        // /text answers "". The two routes disagree, and this is the side that
        // collapses empty to null.
        string text => text.Length == 0 ? null : text,

        // Not the JSON booleans /enabled and /displayed emit. Capitalised, as
        // .NET's bool.ToString() produces and as WinAppDriver sends.
        bool flag => flag ? "True" : "False",

        int number when propertyId == UiaProperties.ControlType =>
            UiaControlTypes.TagName(number),

        int number when propertyId == UiaProperties.Orientation =>
            number >= 0 && number < Orientations.Count
                ? Orientations[number]
                : number.ToString(CultureInfo.InvariantCulture),

        int number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),

        // RuntimeId, and any other integer array, in the same dotted form the
        // element id uses.
        int[] parts => UiaRuntimeId.Format(parts),

        double[] { Length: 2 } point => string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)point[0]},{(int)point[1]}"),

        double[] { Length: 4 } rectangle => Rectangle(rectangle),

        double[] numbers => Join(numbers),

        // Element and element-array properties — LabeledBy, ControllerFor,
        // Selection.Selection and friends. What WinAppDriver sends for these has
        // not been measured, and inventing a spelling would be a divergence
        // written as if it were a contract. Null says "nothing to report" in the
        // one shape the route already produces. Recorded in docs/LIMITATIONS.md.
        _ => null,
    };

    private static string Rectangle(double[] rectangle) => string.Create(
        CultureInfo.InvariantCulture,
        $"Left:{(int)rectangle[0]} Top:{(int)rectangle[1]} " +
        $"Width:{(int)rectangle[2]} Height:{(int)rectangle[3]}");

    private static string Join(double[] numbers)
    {
        StringBuilder joined = new();

        for (int index = 0; index < numbers.Length; index++)
        {
            if (index > 0)
            {
                joined.Append(',');
            }

            joined.Append(numbers[index].ToString(CultureInfo.InvariantCulture));
        }

        return joined.ToString();
    }
}
