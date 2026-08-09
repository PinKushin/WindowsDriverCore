using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation.Locators;

namespace WindowsDriverCore.Tests.Unit.Locators;

/// <summary>
/// Locator strategies, measured against WinAppDriver rather than read from the
/// W3C specification.
///
/// Two rules here are ones a reasonable reading gets wrong, and the previous
/// implementation got both wrong: "tag name" matches LocalizedControlType and
/// not ControlType, and "css selector" is a workaround for a client bug rather
/// than CSS support.
/// </summary>
[TestFixture]
public sealed class LocatorTests
{
    [TestCase("accessibility id", LocatorKind.AutomationId)]
    [TestCase("class name", LocatorKind.ClassName)]
    [TestCase("name", LocatorKind.Name)]
    [TestCase("id", LocatorKind.RuntimeId)]
    [TestCase("xpath", LocatorKind.XPath)]
    public void SupportedStrategy_MapsToItsProperty(string strategy, LocatorKind expected)
    {
        LocatorParseResult result = Locator.Parse(strategy, "whatever");

        result.Rejection.ShouldBe(LocatorRejection.None);
        result.Kind.ShouldBe(expected);
        result.Value.ShouldBe("whatever");
    }

    [Test]
    public void TagName_MatchesLocalizedControlType_NotControlType()
    {
        // The distinction the previous implementation lost. LocalizedControlType
        // is a display string; ControlType is an integer id. Matching the wrong
        // one does not error, it silently finds nothing or finds the wrong thing.
        LocatorParseResult result = Locator.Parse("tag name", "Button");

        result.Kind.ShouldBe(LocatorKind.LocalizedControlType);
        result.Value.ShouldBe("Button");
    }

    [Test]
    public void CssSelector_WithDotPrefix_IsTreatedAsAClassName()
    {
        // Not CSS support. The old Appium .NET client sends "css selector" with a
        // dot-prefixed value when the caller asked for a class name
        // (appium/dotnet-client#265), and WinAppDriver accommodates it. The dot
        // is stripped, which is the part that matters: keeping it would search
        // for a class literally named ".Edit".
        LocatorParseResult result = Locator.Parse("css selector", ".Edit");

        result.Rejection.ShouldBe(LocatorRejection.None);
        result.Kind.ShouldBe(LocatorKind.ClassName);
        result.Value.ShouldBe("Edit");
    }

    [TestCase("css selector", "div.thing")]
    [TestCase("css selector", "#identifier")]
    [TestCase("css selector", ".")]
    public void CssSelector_WithoutAUsableDotPrefix_IsUnsupported(string strategy, string value)
    {
        // The control for the accommodation above: it applies only to the shape
        // the buggy client produces. A real CSS selector is refused, and a lone
        // dot has no class name left after stripping.
        LocatorParseResult result = Locator.Parse(strategy, value);

        result.Rejection.ShouldBe(LocatorRejection.Unsupported);
        result.UnsupportedStrategy.ShouldBe("css selector");
    }

    [TestCase("link text")]
    [TestCase("partial link text")]
    [TestCase("tag")]
    [TestCase("")]
    public void UnknownStrategy_IsUnsupported_AndQuotesWhatWasAsked(string strategy)
    {
        LocatorParseResult result = Locator.Parse(strategy, "value");

        result.Rejection.ShouldBe(LocatorRejection.Unsupported);

        // The message names the strategy, so the client sees what it sent. A
        // constant would satisfy an assertion on the rejection alone.
        result.UnsupportedStrategy.ShouldBe(strategy);
    }

    [TestCase("Accessibility Id")]
    [TestCase("ACCESSIBILITY ID")]
    [TestCase("AccessibilityId")]
    public void StrategyNames_AreMatchedExactly(string strategy)
    {
        // The protocol spells these in lower case with spaces. Accepting variants
        // would mean this driver takes requests WinAppDriver refuses, so code
        // written against it would break against the server it replaces.
        LocatorParseResult result = Locator.Parse(strategy, "value");

        result.Rejection.ShouldBe(LocatorRejection.Unsupported);
    }

    [Test]
    public void SupportedStrategy_IsNotReportedAsUnsupported()
    {
        // The bystander for the rejection cases: a parser that refused everything
        // would pass every test above.
        Locator.Parse("accessibility id", "AppNameTitle").Rejection
            .ShouldBe(LocatorRejection.None);
        Locator.Parse("accessibility id", "AppNameTitle").UnsupportedStrategy
            .ShouldBeNull();
    }
}
