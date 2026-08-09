using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The error contract, measured rather than inferred.
///
/// Each case here is anchored to a recording of what real WinAppDriver sent for
/// that condition. The manipulation under test is the fault table; the condition
/// is the fault; the measurement is the exact numeric status, error string and
/// HTTP status the recording contains.
///
/// Reading the JSON Wire Protocol specification instead produced two confident
/// and wrong conclusions: that a find miss was not status 7, and that unknown
/// sessions returned an empty body. Both were false against the real server.
/// </summary>
[TestFixture]
public sealed class WebDriverFaultTests
{
    private static readonly (string Recording, WebDriverFault Fault)[] MeasuredFaults =
    [
        ("error.noSuchElement.accessibilityId", WebDriverFault.NoSuchElement),
        ("error.noSuchElement.name",            WebDriverFault.NoSuchElement),
        ("error.noSuchElement.tagName",         WebDriverFault.NoSuchElement),
        ("error.tagName.malformed",             WebDriverFault.NoSuchElement),
        ("error.element.staleAfterId",          WebDriverFault.NoSuchElement),
        ("error.unknownRoute",                  WebDriverFault.UnknownCommand),
        ("error.session.appNotFound",           WebDriverFault.UnknownError),
        ("error.xpath.lookupError",             WebDriverFault.XPathLookupError),
        ("error.window.switchBadHandle",        WebDriverFault.NoSuchWindow),
        ("error.session.badTopLevelWindow",     WebDriverFault.NoSuchWindow),
        ("error.timeouts.negativeMs",           WebDriverFault.InvalidArgument),
        ("error.session.badCapabilities",       WebDriverFault.InvalidArgument),
        ("error.invalidSessionId",              WebDriverFault.InvalidSessionId),
        ("error.element.stale.text",             WebDriverFault.StaleElementReference),
        ("error.element.stale.click",            WebDriverFault.NoSuchElement),
        ("error.element.stale.enabled",          WebDriverFault.NoSuchElement),
        ("error.element.stale.location",         WebDriverFault.NoSuchElement),
        ("error.element.staleAfterModeSwitch.click", WebDriverFault.ElementNotInteractable),
        ("error.element.afterWindowClosed.text",  WebDriverFault.NoSuchWindow),
        ("error.element.afterWindowClosed.click", WebDriverFault.NoSuchWindow),
        ("error.window.afterWindowClosed.handle", WebDriverFault.NoSuchWindow),
    ];

    private static IEnumerable<TestCaseData> MeasuredFaultCases() =>
        MeasuredFaults.Select(pair => new TestCaseData(pair.Recording, pair.Fault).SetName(
            $"Fault_{{{pair.Recording}}}_MatchesRecordedWinAppDriverResponse"));

    [TestCaseSource(nameof(MeasuredFaultCases))]
    public void Fault_MatchesRecordedWinAppDriverResponse(string recordingName, WebDriverFault fault)
    {
        RecordedResponse recorded = RecordedResponses.Named(recordingName);

        using JsonDocument document = JsonDocument.Parse(recorded.ResponseBody!);
        JsonElement root = document.RootElement;

        int recordedStatus = root.GetProperty("status").GetInt32();
        string recordedError = root.GetProperty("value").GetProperty("error").GetString()!;

        fault.Status.ShouldBe(recordedStatus);
        fault.Error.ShouldBe(recordedError);
        fault.HttpStatus.ShouldBe(recorded.HttpStatus);
    }

    [Test]
    public void Faults_HaveDistinctStatusCodes_ExceptWhereWinAppDriverReusesThem()
    {
        // A control on the table itself. Without it, a copy-paste that gave two
        // faults the same status would go unnoticed — every individual case above
        // would still pass, because each only checks its own row.
        WebDriverFault[] distinct =
        [
            WebDriverFault.NoSuchElement,
            WebDriverFault.UnknownCommand,
            WebDriverFault.UnknownError,
            WebDriverFault.XPathLookupError,
            WebDriverFault.NoSuchWindow,
            WebDriverFault.InvalidArgument,
            WebDriverFault.InvalidSessionId,
            WebDriverFault.StaleElementReference,
            WebDriverFault.ElementNotInteractable,
        ];

        distinct.Select(f => f.Status).Distinct().Count().ShouldBe(distinct.Length);
        distinct.Select(f => f.Error).Distinct().Count().ShouldBe(distinct.Length);
    }

    [Test]
    public void InvalidArgumentAndInvalidSessionId_UseWinAppDriverCodes_NotSeleniumEnumValues()
    {
        // Selenium's WebDriverResult enum has InvalidArgument = 61 and
        // NoSuchDriver = 6. WinAppDriver emits 100 and 101 instead. Matching the
        // enum here would look more "correct" and be wrong on the wire.
        //
        // This is the condition where correct and plausible-but-wrong differ; a
        // test that only asserted "some non-zero status" could not tell them apart.
        WebDriverFault.InvalidArgument.Status.ShouldBe(100);
        WebDriverFault.InvalidSessionId.Status.ShouldBe(101);

        WebDriverFault.InvalidArgument.Status.ShouldNotBe(61);
        WebDriverFault.InvalidSessionId.Status.ShouldNotBe(6);
    }

    [Test]
    public void XPathLookupError_ErrorString_KeepsWinAppDriverSpacingAndCasing()
    {
        // "XPath Lookup Error", not "xpath lookup error" or "XPathLookupError".
        // The client derives the suffix it appends to the message from this exact
        // string by removing spaces, so casing and spacing are load-bearing.
        WebDriverFault.XPathLookupError.Error.ShouldBe("XPath Lookup Error");
    }

    [Test]
    public void EveryRecordedErrorResponse_IsCoveredByTheFaultTable()
    {
        // Guards against the table silently falling behind the recordings when a
        // future recording pass adds a condition nothing maps.
        //
        // Everything excluded below is excluded for a stated reason, not because
        // it is unmeasured. Three of them turned out not to be faults at all once
        // measured, which is why they still carry "error." in their recording
        // names — the names record what was expected, the responses record what
        // happened.
        string[] notMappedDeliberately =
        [
            // HTTP 501 with a plain-text body and no JSON envelope, so there is
            // no status/error pair to map. Handled as its own response kind.
            "error.locator.cssSelector",
            "error.locator.linkText",
            "error.locator.partialLinkText",
            "error.timeouts.pageLoad",
            "error.timeouts.script",

            // Not an error at all: FindElements with no match is 200 + [].
            "error.elements.noSuchElement",

            // Succeeds with HTTP 200. Named "error." only because that is what
            // the recording pass expected before measuring; clicking a disabled
            // element is not a fault in WinAppDriver.
            "error.element.clickDisabled.ClearMemoryButton",
            "error.element.sendKeysDisabled.ClearMemoryButton",

            // Returned 200 with the text intact: switching Calculator modes does
            // not destroy the keypad node, so this is not a fault case.
            "error.element.staleAfterModeSwitch.text",
        ];

        IEnumerable<string> recordedErrorNames = RecordedResponses.All
            .Where(r => r.Name.StartsWith("error.", StringComparison.Ordinal))
            .Select(r => r.Name)
            .Where(name => !notMappedDeliberately.Contains(name));

        IEnumerable<string> mapped = MeasuredFaults.Select(pair => pair.Recording);

        recordedErrorNames.Except(mapped).ShouldBeEmpty(
            "every recorded error condition should map to a fault, or be listed as a known gap");
    }
}
