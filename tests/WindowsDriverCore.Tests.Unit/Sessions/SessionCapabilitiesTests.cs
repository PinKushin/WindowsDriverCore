using System;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Unit.Sessions;

/// <summary>
/// Capability parsing, measured against real WinAppDriver rather than the W3C
/// specification. Four of these rules were guessed wrong before being recorded:
/// supplying both app and appTopLevelWindow is rejected rather than preferred,
/// an empty app has its own message, the W3C capabilities.alwaysMatch shape is
/// not understood at all, and unrecognised capabilities are dropped from the
/// echo rather than passed through.
/// </summary>
[TestFixture]
public sealed class SessionCapabilitiesTests
{
    private static CapabilityParseResult Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return SessionCapabilities.Parse(document.RootElement);
    }

    [Test]
    public void App_IsAccepted()
    {
        CapabilityParseResult result = Parse(
            """{"desiredCapabilities":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}""");

        result.Fault.ShouldBeNull();
        result.Capabilities!.App.ShouldBe("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        result.Capabilities.AppTopLevelWindow.ShouldBeNull();
    }

    [Test]
    public void AppTopLevelWindow_IsAccepted()
    {
        CapabilityParseResult result = Parse(
            """{"desiredCapabilities":{"appTopLevelWindow":"B822E2"}}""");

        result.Fault.ShouldBeNull();
        result.Capabilities!.AppTopLevelWindow.ShouldBe("B822E2");
        result.Capabilities.App.ShouldBeNull();
    }

    [Test]
    public void NeitherAppNorTopLevelWindow_IsRejected()
    {
        CapabilityParseResult result = Parse("""{"desiredCapabilities":{}}""");

        result.Fault.ShouldBe(WebDriverFault.InvalidArgument);
        result.Message.ShouldBe(
            "Bad capabilities. Specify either app or appTopLevelWindow to create a session");
    }

    [Test]
    public void BothAppAndTopLevelWindow_IsRejected()
    {
        // Measured. The plausible alternative — prefer one and ignore the other —
        // would look reasonable and be wrong: WinAppDriver treats this as an
        // exclusive choice and rejects it with the same message as supplying
        // neither.
        CapabilityParseResult result = Parse(
            """{"desiredCapabilities":{"app":"Calculator","appTopLevelWindow":"DEADBEEF"}}""");

        result.Fault.ShouldBe(WebDriverFault.InvalidArgument);
        result.Message.ShouldBe(
            "Bad capabilities. Specify either app or appTopLevelWindow to create a session");
    }

    [Test]
    public void EmptyApp_IsRejectedWithItsOwnMessage()
    {
        // A distinct message, so asserting only the fault would not separate this
        // from the missing-capability case above. Both are InvalidArgument.
        CapabilityParseResult result = Parse("""{"desiredCapabilities":{"app":""}}""");

        result.Fault.ShouldBe(WebDriverFault.InvalidArgument);
        result.Message.ShouldBe("Capability: app cannot be empty");
    }

    [Test]
    public void W3CAlwaysMatchShape_IsNotUnderstood()
    {
        // WinAppDriver reads desiredCapabilities and nothing else. Supporting the
        // W3C shape as well would accept sessions the real server rejects, which
        // is a compatibility difference in the direction that hides bugs: code
        // written against us would fail against WinAppDriver.
        CapabilityParseResult result = Parse(
            """{"capabilities":{"alwaysMatch":{"app":"Calculator"}}}""");

        result.Fault.ShouldBe(WebDriverFault.InvalidArgument);
        result.Message.ShouldBe(
            "Bad capabilities. Specify either app or appTopLevelWindow to create a session");
    }

    [Test]
    public void EmptyBody_IsRejected()
    {
        CapabilityParseResult result = Parse("{}");

        result.Fault.ShouldBe(WebDriverFault.InvalidArgument);
    }

    [Test]
    public void RootApp_IsAcceptedAsADesktopSession()
    {
        CapabilityParseResult result = Parse("""{"desiredCapabilities":{"app":"Root"}}""");

        result.Fault.ShouldBeNull();
        result.Capabilities!.App.ShouldBe("Root");
        result.Capabilities.IsDesktopSession.ShouldBeTrue();
    }

    [TestCase("root")]
    [TestCase("ROOT")]
    public void RootApp_IsRecognisedRegardlessOfCase(string app)
    {
        CapabilityParseResult result = Parse($$$"""{"desiredCapabilities":{"app":"{{{app}}}"}}""");

        result.Capabilities!.IsDesktopSession.ShouldBeTrue();
    }

    [Test]
    public void OrdinaryApp_IsNotADesktopSession()
    {
        // The control for the two cases above. Without it, an implementation that
        // reported every session as a desktop session would pass them both.
        CapabilityParseResult result = Parse("""{"desiredCapabilities":{"app":"Calculator"}}""");

        result.Capabilities!.IsDesktopSession.ShouldBeFalse();
    }

    [Test]
    public void Echo_KeepsRecognisedCapabilities_AndDropsUnknownOnes()
    {
        // Measured: sending app + platformName + deviceName echoed back only app
        // and platformName. An implementation that echoed the request verbatim
        // would return deviceName too and differ from WinAppDriver on the wire.
        CapabilityParseResult result = Parse(
            """
            {"desiredCapabilities":{
                "app":"Calculator",
                "platformName":"Windows",
                "deviceName":"WindowsPC"
            }}
            """);

        result.Capabilities!.Echo.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(["app", "platformName"]);
        result.Capabilities.Echo["app"].ShouldBe("Calculator");
        result.Capabilities.Echo["platformName"].ShouldBe("Windows");
        result.Capabilities.Echo.ContainsKey("deviceName").ShouldBeFalse();
    }

    [Test]
    public void Echo_CarriesEveryRecognisedCapability()
    {
        // The bystander for the test above: dropping unknown keys must not drop
        // known ones. A parser that echoed only "app" would pass that test.
        CapabilityParseResult result = Parse(
            """
            {"desiredCapabilities":{
                "app":"C:\\Windows\\System32\\notepad.exe",
                "appArguments":"file.txt",
                "appWorkingDir":"C:\\Temp",
                "platformName":"Windows",
                "platformVersion":"1.0"
            }}
            """);

        result.Capabilities!.Echo.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(
            ["app", "appArguments", "appWorkingDir", "platformName", "platformVersion"]);
        result.Capabilities.AppArguments.ShouldBe("file.txt");
        result.Capabilities.AppWorkingDirectory.ShouldBe(@"C:\Temp");
    }
}
