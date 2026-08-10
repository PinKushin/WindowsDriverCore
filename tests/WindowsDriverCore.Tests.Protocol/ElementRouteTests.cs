using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Protocol.Sessions;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Element search over the wire, with the finder substituted so the protocol
/// behaviour is separable from UI Automation.
///
/// The asymmetry between the two routes is the thing worth pinning: a singular
/// find that matches nothing is an error, the plural form is not.
/// </summary>
[TestFixture]
public sealed class ElementRouteTests : IDisposable
{
    private const string SessionId = "live-session";

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementFinder _finder = null!;

    [SetUp]
    public void StartServer()
    {
        _finder = Substitute.For<IElementFinder>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton(_finder)));

        _client = _factory.CreateClient();

        _factory.Services.GetRequiredService<ISessionStore>().Add(new DriverSession(
            SessionId,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 1234,
            WindowHandle: 0x9999));
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private Task<HttpResponseMessage> Find(string route, string @using, string value) =>
        _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/{route}", UriKind.Relative),
            new { @using, value });

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Test]
    public async Task FindElement_ReturnsTheFirstMatch_AsAnElementReference()
    {
        _finder.FindAll(0x9999, LocatorKind.AutomationId, "num5Button").Returns(FindResult.Matched(["42.19466560.4.73", "42.19466560.4.99"]));
        _finder.FindFirst(0x9999, LocatorKind.AutomationId, "num5Button").Returns(FindResult.Matched(["42.19466560.4.73", "42.19466560.4.99"]));

        HttpResponseMessage response = await Find("element", "accessibility id", "num5Button");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement produced = await BodyOf(response);
        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("element.find.byAccessibilityId").ResponseBody!);

        produced.GetProperty("status").GetInt32().ShouldBe(0);
        produced.GetProperty("value").GetProperty("ELEMENT").GetString()
            .ShouldBe("42.19466560.4.73");
        produced.GetProperty("value").EnumerateObject().Select(p => p.Name)
            .ShouldBe(recorded.RootElement.GetProperty("value").EnumerateObject().Select(p => p.Name));
    }

    [Test]
    public async Task FindElement_SearchesTheSessionWindow()
    {
        // The condition that separates a correct handler from one searching the
        // desktop or a hardcoded handle: the session was seeded with 0x9999.
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched(["1.2.3"]));
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched(["1.2.3"]));

        await Find("element", "class name", "Button");

        _finder.Received(1).FindFirst(0x9999, LocatorKind.ClassName, "Button");
    }

    [Test]
    public async Task FindElement_StopsAtTheFirstMatch_RatherThanWalkingEverything()
    {
        // The one test that cares which UIA call the singular route makes.
        //
        // Every other test here stubs both, deliberately: the protocol answer is
        // identical either way, so making them care would turn a performance
        // decision into a change detector. This one pins it on purpose —
        // measured at 10.4 ms against 12.5 ms on Calculator, and an exhaustive
        // walk to use element zero is the kind of thing that quietly comes back.
        _finder.FindFirst(0x9999, LocatorKind.AutomationId, "num5Button")
            .Returns(FindResult.Matched(["42.19466560.4.73"]));

        (await Find("element", "accessibility id", "num5Button")).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        _finder.Received(1).FindFirst(0x9999, LocatorKind.AutomationId, "num5Button");
        _finder.DidNotReceive().FindAll(0x9999, LocatorKind.AutomationId, "num5Button");
    }

    [Test]
    public async Task FindElements_WalksEverything_BecauseItReturnsEverything()
    {
        // The control for the test above. The plural route cannot stop early.
        _finder.FindAll(0x9999, LocatorKind.AutomationId, "row")
            .Returns(FindResult.Matched(["42.1.1", "42.1.2"]));

        (await Find("elements", "accessibility id", "row")).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        _finder.Received(1).FindAll(0x9999, LocatorKind.AutomationId, "row");
        _finder.DidNotReceive().FindFirst(0x9999, LocatorKind.AutomationId, "row");
    }

    [Test]
    public async Task FindElement_NoMatch_IsNoSuchElement()
    {
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched([]));
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched([]));

        HttpResponseMessage response = await Find("element", "accessibility id", "nope");

        RecordedResponse recorded = RecordedResponses.Named("error.noSuchElement.accessibilityId");
        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32().ShouldBe(7);
        produced.GetProperty("value").GetProperty("message").GetString().ShouldBe(
            "An element could not be located on the page using the given search parameters.");
    }

    [Test]
    public async Task FindElements_NoMatch_IsAnEmptyArrayAndNotAnError()
    {
        // The asymmetry. Measured: POST /elements with no match answers 200 with
        // an empty array, while POST /element answers 404. Treating them alike —
        // in either direction — is the obvious simplification and is wrong.
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched([]));
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched([]));

        HttpResponseMessage response = await Find("elements", "accessibility id", "nope");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32().ShouldBe(0);
        produced.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task FindElements_ReturnsEveryMatch_InOrder()
    {
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched(["1.1", "2.2", "3.3"]));
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Matched(["1.1", "2.2", "3.3"]));

        HttpResponseMessage response = await Find("elements", "class name", "Button");

        JsonElement produced = await BodyOf(response);
        JsonElement[] values = produced.GetProperty("value").EnumerateArray().ToArray();

        values.Length.ShouldBe(3);
        values.Select(v => v.GetProperty("ELEMENT").GetString()).ShouldBe(["1.1", "2.2", "3.3"]);
    }

    [TestCase("link text")]
    [TestCase("partial link text")]
    public async Task UnsupportedLocator_IsPlainText501_NotAJsonFault(string strategy)
    {
        HttpResponseMessage response = await Find("element", strategy, "whatever");

        ((int)response.StatusCode).ShouldBe(501);

        string body = await response.Content.ReadAsStringAsync();

        // Plain text, with no JSON envelope. The client cannot parse it and
        // prefixes its own "Unexpected error. ", which is why the compatibility
        // suite asserts on that prefix. Returning JSON here would produce a
        // different client-side message.
        body.ShouldBe($"Unimplemented Command: {strategy} locator strategy is not supported");
        Should.Throw<JsonException>(() => JsonDocument.Parse(body));
    }

    [Test]
    public async Task NestedFind_SearchesInsideTheContainer_NotTheWindow()
    {
        // The scope is the whole point, and the only way to see it here is which
        // SearchScope the route passed. A nested route that searched the window
        // would still answer 200 with an element.
        _finder.FindFirst(
            new SearchScope(0x9999, "42.1.1"), LocatorKind.AutomationId, "row")
            .Returns(FindResult.Matched(["42.1.2"]));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/element/42.1.1/element", UriKind.Relative),
            new { @using = "accessibility id", value = "row" });
        JsonElement body = await BodyOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("value").GetProperty("ELEMENT").GetString().ShouldBe("42.1.2");
        _finder.Received(1).FindFirst(
            new SearchScope(0x9999, "42.1.1"), LocatorKind.AutomationId, "row");
    }

    [Test]
    public async Task NestedFind_WithNoMatch_IsNoSuchElement()
    {
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([]));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/element/42.1.1/element", UriKind.Relative),
            new { @using = "accessibility id", value = "nope" });
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(404);
        body.GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task NestedFindAll_ReturnsEveryMatch_AndRecordsEachId()
    {
        // The plural nested route, and the registry side of it: every id handed
        // out must be recorded, or a later stale touch reports "no such element"
        // instead of "stale".
        _finder.FindAll(new SearchScope(0x9999, "42.1.1"), LocatorKind.ControlType, "Button")
            .Returns(FindResult.Matched(["42.1.2", "42.1.3"]));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/elements", UriKind.Relative).ToString()
                .Replace("/elements", "/element/42.1.1/elements", StringComparison.Ordinal),
            new { @using = "tag name", value = "Button" });
        JsonElement body = await BodyOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("value").GetArrayLength().ShouldBe(2);

        IElementRegistry registry = _factory.Services.GetRequiredService<IElementRegistry>();
        registry.CountFor(SessionId).ShouldBe(2);
    }

    [Test]
    public async Task NestedFindAll_WithNoMatch_IsAnEmptyArray_NotAnError()
    {
        // Same asymmetry as the top-level routes: plural finds nothing without
        // it being an error.
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([]));

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/element/42.1.1/elements", UriKind.Relative),
            new { @using = "accessibility id", value = "nope" });
        JsonElement body = await BodyOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("value").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task NestedFind_RequiresALiveSession()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri("/session/not-a-session/element/42.1.1/element", UriKind.Relative),
            new { @using = "accessibility id", value = "row" });
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(404);
        body.GetProperty("status").GetInt32().ShouldBe(101);
    }

    [Test]
    public async Task UnsupportedLocator_IsRejectedWithoutSearching()
    {
        await Find("element", "link text", "whatever");

        // Both, because the singular route uses FindFirst and the plural uses
        // FindAll — asserting only one would leave half the surface unguarded.
        _finder.DidNotReceive().FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>());
        _finder.DidNotReceive().FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>());
    }

    [Test]
    public async Task XPath_ReportsALookupError_WithoutTheClientSideSuffix()
    {
        _finder.FindAll(Arg.Any<SearchScope>(), LocatorKind.XPath, Arg.Any<string>()).Returns(FindResult.Failed(FindFailure.XPathLookupError));
        _finder.FindFirst(Arg.Any<SearchScope>(), LocatorKind.XPath, Arg.Any<string>()).Returns(FindResult.Failed(FindFailure.XPathLookupError));

        HttpResponseMessage response = await Find("element", "xpath", "//*[@bad=]");

        RecordedResponse recorded = RecordedResponses.Named("error.xpath.lookupError");
        ((int)response.StatusCode).ShouldBe(recorded.HttpStatus);

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32().ShouldBe(19);

        // The server sends only the expression. The client appends
        // " (XPathLookupError)" itself, so including it here would double it.
        string message = produced.GetProperty("value").GetProperty("message").GetString()!;
        message.ShouldBe("Invalid XPath expression: //*[@bad=]");
        message.ShouldNotContain("(XPathLookupError)");
    }

    [Test]
    public async Task ClosedWindow_ReportsNoSuchWindow()
    {
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Failed(FindFailure.NoSuchWindow));
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>()).Returns(FindResult.Failed(FindFailure.NoSuchWindow));

        HttpResponseMessage response = await Find("element", "name", "anything");

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32().ShouldBe(23);
        produced.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");
    }

    [Test]
    public async Task UnknownSession_IsRejectedBeforeSearching()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri("/session/not-a-session/element", UriKind.Relative),
            new { @using = "name", value = "x" });

        JsonElement produced = await BodyOf(response);
        produced.GetProperty("status").GetInt32().ShouldBe(101);

        _finder.DidNotReceive().FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>());
        _finder.DidNotReceive().FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>());
    }
}
