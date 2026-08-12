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

    /// <summary>A Selenium 4 client can create a session at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>This is the gate, and it was shut.</b> Selenium 4 dropped JSON Wire
    /// entirely and sends <c>capabilities.alwaysMatch</c>; this parser read only
    /// <c>desiredCapabilities</c>, so such a client was refused at
    /// <c>POST /session</c> and never reached a single command. Serving it is
    /// the most requested thing in WinAppDriver's tracker — ~42 reactions across
    /// #1610, #1839, #1997 and #1543 — and a reason this driver exists.
    /// </para>
    /// <para>
    /// The previous behaviour was deliberate: WinAppDriver does not understand
    /// the W3C shape either. But matching the reference's LIMITATIONS is not the
    /// goal; matching its contract is.
    /// </para>
    /// </remarks>
    [Test]
    public void W3CCapabilities_CreateASession_AndAreMarkedW3C()
    {
        CapabilityParseResult result = Parse(
            """{"capabilities":{"alwaysMatch":{"app":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"}}}""");

        result.Fault.ShouldBeNull("a Selenium 4 client must get a session");
        result.Capabilities!.App.ShouldBe("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        result.Capabilities.Dialect.ShouldBe(ProtocolDialect.W3C);
    }

    /// <summary>
    /// The dialect is decided by which key was used, and nothing else.
    /// </summary>
    /// <remarks>
    /// THE CONTROL. Without it, "always report W3C" would satisfy the test above
    /// and silently change what every existing JSON Wire client is answered
    /// with — which is the compatibility suite and every WinAppDriver suite in
    /// the world.
    /// </remarks>
    [Test]
    public void DesiredCapabilities_StayJsonWire()
    {
        CapabilityParseResult result = Parse(
            """{"desiredCapabilities":{"app":"Calculator"}}""");

        result.Fault.ShouldBeNull();
        result.Capabilities!.Dialect.ShouldBe(ProtocolDialect.JsonWire);
    }

    /// <summary>A body with neither shape is still rejected.</summary>
    /// <remarks>
    /// <c>firstMatch</c> alone is not accepted: it offers ALTERNATIVES to
    /// negotiate between, and this driver has one platform to offer, so there is
    /// nothing to choose and pretending to choose would be a lie the client acts
    /// on.
    /// </remarks>
    [Test]
    public void W3CWithoutAlwaysMatch_IsRejected()
    {
        CapabilityParseResult result = Parse(
            """{"capabilities":{"firstMatch":[{"app":"whatever"}]}}""");

        result.Capabilities.ShouldBeNull();
        result.Fault.ShouldNotBeNull();
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

    /// <summary>
    /// An empty <c>appTopLevelWindow</c> gets its own message, not the generic
    /// "cannot find" fault a genuinely missing window gets.
    /// </summary>
    /// <remarks>
    /// Measured against WinAppDriver by the suite's own
    /// <c>CreateSessionFromExistingWindowHandleError_EmptyValue</c>: an empty
    /// string is a caller mistake at the capability level, distinct from a
    /// well-formed handle that names nothing.
    /// </remarks>
    [Test]
    public void EmptyTopLevelWindow_IsRejectedWithItsOwnMessage()
    {
        CapabilityParseResult result = Parse("""{"desiredCapabilities":{"appTopLevelWindow":""}}""");

        result.Fault.ShouldBe(WebDriverFault.InvalidArgument);
        result.Message.ShouldBe("Capability: appTopLevelWindow cannot be empty");
    }

    /// <summary>
    /// A W3C body still needs an application, like any other.
    /// </summary>
    /// <remarks>
    /// <b>THIS TEST USED TO ASSERT THE OPPOSITE</b>, and deleting that assertion
    /// is the point rather than an accident. It read
    /// <c>W3CAlwaysMatchShape_IsNotUnderstood</c> and reasoned that accepting the
    /// W3C shape "would accept sessions the real server rejects". True, and no
    /// longer the goal: Selenium 4 speaks only W3C, cannot drive WinAppDriver at
    /// all, and serving it is the most requested item in that tracker. Matching
    /// the reference's CONTRACT is the aim; matching its limitations is not.
    /// </remarks>
    [Test]
    public void W3CCapabilitiesWithoutAnApp_AreRejectedLikeAnyOther()
    {
        CapabilityParseResult result = Parse(
            """{"capabilities":{"alwaysMatch":{"platformName":"windows"}}}""");

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
