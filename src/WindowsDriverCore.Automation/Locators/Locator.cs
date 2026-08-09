namespace WindowsDriverCore.Automation.Locators;

/// <summary>How an element is being looked for.</summary>
public enum LocatorKind
{
    /// <summary>Match on UIA AutomationId.</summary>
    AutomationId,

    /// <summary>Match on UIA ClassName.</summary>
    ClassName,

    /// <summary>Match on UIA Name.</summary>
    Name,

    /// <summary>Match on UIA RuntimeId.</summary>
    RuntimeId,

    /// <summary>Match on UIA ControlType, named by its programmatic name.</summary>
    ControlType,

    /// <summary>Evaluate an XPath expression over the tree.</summary>
    XPath,
}

/// <summary>Why a locator was refused.</summary>
public enum LocatorRejection
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>
    /// A strategy this driver does not implement, reported as HTTP 501 with a
    /// plain-text body rather than a JSON fault.
    /// </summary>
    Unsupported,
}

/// <summary>The result of reading a locator from a request.</summary>
/// <param name="Kind">What to match on, when accepted.</param>
/// <param name="Value">The value to match.</param>
/// <param name="Rejection">Why it was refused, when refused.</param>
/// <param name="UnsupportedStrategy">
/// The strategy name to quote back in the unsupported-command message.
/// </param>
public sealed record LocatorParseResult(
    LocatorKind Kind,
    string Value,
    LocatorRejection Rejection,
    string? UnsupportedStrategy);

/// <summary>
/// Reads the <c>using</c>/<c>value</c> pair a client sends.
/// </summary>
/// <remarks>
/// Every rule here is measured against WinAppDriver rather than taken from the
/// W3C specification, and two of them are surprising enough that a reasonable
/// reading gets them wrong. See <c>docs/PROJECT-KNOWLEDGE.md</c>.
/// </remarks>
public static class Locator
{
    /// <summary>Reads a locator.</summary>
    /// <param name="using">The strategy name from the request.</param>
    /// <param name="value">The value from the request.</param>
    /// <returns>The parsed locator, or the reason it was refused.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static LocatorParseResult Parse(string @using, string value)
    {
        ArgumentNullException.ThrowIfNull(@using);
        ArgumentNullException.ThrowIfNull(value);

        switch (@using)
        {
            case "accessibility id":
                return Accepted(LocatorKind.AutomationId, value);

            case "class name":
                return Accepted(LocatorKind.ClassName, value);

            case "name":
                return Accepted(LocatorKind.Name, value);

            case "id":
                return Accepted(LocatorKind.RuntimeId, value);

            // ControlType, by the enum's programmatic name, matched exactly:
            // "Button" and "ListItem" find elements through WinAppDriver while
            // "button" and "list item" both answer no such element. An earlier
            // version of this comment argued for LocalizedControlType and cited
            // "Button" as the example — the one input where both readings agree,
            // because a Button's localized type differs only by case.
            //
            // The value is passed through unresolved. An unknown name has to
            // become an empty find rather than a parse failure, and that
            // decision belongs to the finder.
            case "tag name":
                return Accepted(LocatorKind.ControlType, value);

            case "xpath":
                return Accepted(LocatorKind.XPath, value);

            // The old Appium .NET client sends "css selector" with a dot-prefixed
            // value when the caller asked for a class name — appium/dotnet-client
            // #265. WinAppDriver treats that as a class-name search, so a client
            // carrying the bug still works. Anything else under this strategy is
            // genuinely unsupported.
            case "css selector" when value.StartsWith('.') && value.Length > 1:
                return Accepted(LocatorKind.ClassName, value[1..]);

            default:
                return new LocatorParseResult(
                    default,
                    value,
                    LocatorRejection.Unsupported,
                    @using);
        }
    }

    private static LocatorParseResult Accepted(LocatorKind kind, string value) =>
        new(kind, value, LocatorRejection.None, UnsupportedStrategy: null);
}
