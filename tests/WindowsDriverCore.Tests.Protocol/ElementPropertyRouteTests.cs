using System;
using System.Collections.Generic;
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
/// Element property routes over the wire, with UI Automation substituted.
/// </summary>
/// <remarks>
/// Every expectation here is a recorded WinAppDriver response, not a reading of
/// the specification. Two of them are shapes nobody would guess: <c>/name</c>
/// returns the tag name rather than the Name property, and <c>/size</c>
/// serialises height before width.
/// </remarks>
[TestFixture]
public sealed class ElementPropertyRouteTests : IDisposable
{
    private const string SessionId = "live-session";
    private const string ElementId = "42.19466560.4.73";
    private const nint Window = 0x9999;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementInspector _inspector = null!;
    private IElementFinder _finder = null!;

    [SetUp]
    public void StartServer()
    {
        _inspector = Substitute.For<IElementInspector>();
        _finder = Substitute.For<IElementFinder>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_inspector);
                services.AddSingleton(_finder);
            }));

        _client = _factory.CreateClient();

        _factory.Services.GetRequiredService<ISessionStore>().Add(new DriverSession(
            SessionId,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 1234,
            WindowHandle: Window));
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private Task<HttpResponseMessage> Get(string suffix) =>
        _client.GetAsync(new Uri($"/session/{SessionId}/element/{ElementId}/{suffix}", UriKind.Relative));

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Test]
    public async Task Text_ReturnsWhatTheInspectorRead()
    {
        _inspector.Text(Window, ElementId).Returns(ElementRead.Success("Five"));

        HttpResponseMessage response = await Get("text");
        JsonElement body = await BodyOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetInt32().ShouldBe(0);
        body.GetProperty("sessionId").GetString().ShouldBe(SessionId);
        body.GetProperty("value").GetString().ShouldBe("Five");
    }

    [Test]
    public async Task Text_OfAnEmptyValue_IsAnEmptyString_NotNull()
    {
        // The distinction /attribute/Value.Value does not make: measured, /text
        // answers "" for an empty ValuePattern while the attribute route answers
        // null for the same element in the same state.
        _inspector.Text(Window, ElementId).Returns(ElementRead.Success(string.Empty));

        JsonElement body = await BodyOf(await Get("text"));

        body.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.String);
        body.GetProperty("value").GetString().ShouldBe(string.Empty);
    }

    [Test]
    public async Task Name_ReturnsTheTagName_NotTheNameProperty()
    {
        // The route a reasonable reading gets wrong. Selenium's TagName maps to
        // GET /name, and WinAppDriver answers "ControlType.Button" — prefixed,
        // unlike the tag name locator, which takes "Button".
        _inspector.TagName(Window, ElementId)
            .Returns(ElementRead.Success("ControlType.Button"));

        JsonElement body = await BodyOf(await Get("name"));

        body.GetProperty("value").GetString()
            .ShouldBe(ExpectedValue("element.name.tagName").GetString());
    }

    [TestCase("enabled", true)]
    [TestCase("enabled", false)]
    [TestCase("displayed", true)]
    [TestCase("displayed", false)]
    [TestCase("selected", true)]
    [TestCase("selected", false)]
    public async Task BooleanRoutes_ReturnRealJsonBooleans(string suffix, bool value)
    {
        // Real booleans, not the "True"/"False" strings the attribute route
        // uses for the same underlying properties. Both polarities, because a
        // handler hard-coding either one would pass a single-value test.
        ConfigureFlag(suffix, value);

        JsonElement body = await BodyOf(await Get(suffix));

        body.GetProperty("value").ValueKind
            .ShouldBe(value ? JsonValueKind.True : JsonValueKind.False);
    }

    [Test]
    public async Task Location_ReturnsXAndY_Only()
    {
        _inspector.WindowRelativeBounds(Window, ElementId)
            .Returns(ElementRead.Success(new ElementBounds(203, 419, 97, 35)));

        JsonElement body = await BodyOf(await Get("location"));
        JsonElement value = body.GetProperty("value");

        value.GetProperty("x").GetInt32().ShouldBe(203);
        value.GetProperty("y").GetInt32().ShouldBe(419);
        value.TryGetProperty("width", out _).ShouldBeFalse("location carries no size");
    }

    [Test]
    public async Task LocationInView_AnswersTheSameAsLocation()
    {
        // Measured: identical bodies. WinAppDriver does not scroll for this.
        _inspector.WindowRelativeBounds(Window, ElementId)
            .Returns(ElementRead.Success(new ElementBounds(203, 419, 97, 35)));

        string location = await (await Get("location")).Content.ReadAsStringAsync();
        string inView = await (await Get("location_in_view")).Content.ReadAsStringAsync();

        inView.ShouldBe(location);
    }

    [Test]
    public async Task Size_SerialisesHeightBeforeWidth()
    {
        // Byte-for-byte against the recording, because property order is the
        // whole point of this test and a JSON comparison by value would not see
        // it. WinAppDriver emits {"height":35,"width":97}.
        _inspector.WindowRelativeBounds(Window, ElementId)
            .Returns(ElementRead.Success(new ElementBounds(203, 419, 97, 35)));

        string body = await (await Get("size")).Content.ReadAsStringAsync();

        body.ShouldContain("\"height\":35,\"width\":97");
    }

    [Test]
    public async Task Attribute_ReturnsTheRenderedValue()
    {
        _inspector.Attribute(Window, ElementId, "Name")
            .Returns(ElementRead.Success<string?>("Five"));

        JsonElement body = await BodyOf(await Get("attribute/Name"));

        body.GetProperty("status").GetInt32().ShouldBe(0);
        body.GetProperty("value").GetString().ShouldBe("Five");
    }

    [Test]
    public async Task Attribute_WithADottedPatternName_ReachesTheInspectorIntact()
    {
        // The dot is a route-value character, and a route template that split on
        // it would deliver "SelectionItem" here. Measured names include
        // Value.Value and SelectionItem.IsSelected.
        _inspector.Attribute(Window, ElementId, "SelectionItem.IsSelected")
            .Returns(ElementRead.Success<string?>("False"));

        JsonElement body = await BodyOf(await Get("attribute/SelectionItem.IsSelected"));

        body.GetProperty("value").GetString().ShouldBe("False");
    }

    [Test]
    public async Task Attribute_ThatIsUnknown_IsNull_WithStatusZero()
    {
        // Not an error. A caller cannot distinguish this from an unset property,
        // which is WinAppDriver's behaviour and is measured.
        _inspector.Attribute(Window, ElementId, "InvalidAttributeName")
            .Returns(ElementRead.Success<string?>(null));

        HttpResponseMessage response = await Get("attribute/InvalidAttributeName");
        JsonElement body = await BodyOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetInt32().ShouldBe(0);
        body.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task Attribute_WithNoName_IsInvalidArgument_NotAnUnknownCommand()
    {
        // The distinction that routing gets wrong by default: without an
        // explicit optional-name route this falls through to the fallback and
        // answers status 9. Measured, WinAppDriver answers 400 with status 100
        // and a message naming the argument.
        HttpResponseMessage response = await Get("attribute/");
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(400);
        body.GetProperty("status").GetInt32().ShouldBe(100);
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Attribute command takes exactly one argument namely the attribute name");
    }

    [Test]
    public async Task Rect_IsNotImplemented()
    {
        // W3C only. WinAppDriver answers 501 with a plain-text body, and the
        // client's own "Unexpected error. " prefix depends on it not being JSON.
        HttpResponseMessage response = await Get("rect");

        ((int)response.StatusCode).ShouldBe(501);
        (await response.Content.ReadAsStringAsync()).ShouldStartWith("Unimplemented Command:");
    }

    [Test]
    public async Task ElementThisServerIssued_ThatIsGone_IsStaleOnce_ThenUnknown()
    {
        // The destructive behaviour, end to end. Measured against WinAppDriver:
        // the first touch of a dead element answers 400/10 and every touch after
        // it answers 404/7, against the same id.
        _finder.FindAll(Window, LocatorKind.AutomationId, "num5Button")
            .Returns(FindResult.Matched([ElementId]));

        await _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/element", UriKind.Relative),
            new { @using = "accessibility id", value = "num5Button" });

        _inspector.Text(Window, ElementId)
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NotFound));

        HttpResponseMessage first = await Get("text");
        JsonElement firstBody = await BodyOf(first);

        ((int)first.StatusCode).ShouldBe(400);
        firstBody.GetProperty("status").GetInt32().ShouldBe(10);
        firstBody.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe("stale element reference");

        HttpResponseMessage second = await Get("text");
        JsonElement secondBody = await BodyOf(second);

        ((int)second.StatusCode).ShouldBe(404);
        secondBody.GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task ElementThisServerNeverIssued_IsUnknown_NotStale()
    {
        // The control for the test above. Without it, a handler that always
        // answered "stale" would pass the first assertion of that one.
        _inspector.Text(Window, "99999.99999.99999")
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NotFound));

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{SessionId}/element/99999.99999.99999/text", UriKind.Relative));
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(404);
        body.GetProperty("status").GetInt32().ShouldBe(7);
        body.GetProperty("value").GetProperty("error").GetString().ShouldBe("no such element");
    }

    [Test]
    public async Task WindowThatWentAway_IsNoSuchWindow()
    {
        _inspector.Text(Window, ElementId)
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NoSuchWindow));

        HttpResponseMessage response = await Get("text");
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(400);
        body.GetProperty("status").GetInt32().ShouldBe(23);
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");
    }

    [Test]
    public async Task PropertyRoutes_RequireALiveSession()
    {
        // The bystander for the whole fixture: these routes must not answer for
        // a session that does not exist, which they would if they read the
        // window handle from the route rather than from the session.
        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/not-a-session/element/{ElementId}/text", UriKind.Relative));
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(404);
        body.GetProperty("status").GetInt32().ShouldBe(101);
    }

    private void ConfigureFlag(string suffix, bool value)
    {
        ElementRead<bool> read = ElementRead.Success(value);

        switch (suffix)
        {
            case "enabled":
                _inspector.IsEnabled(Window, ElementId).Returns(read);
                break;

            case "displayed":
                _inspector.IsDisplayed(Window, ElementId).Returns(read);
                break;

            case "selected":
                _inspector.IsSelected(Window, ElementId).Returns(read);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(suffix), suffix, "No such flag route.");
        }
    }

    private static JsonElement ExpectedValue(string recordingName) =>
        JsonDocument.Parse(RecordedResponses.Named(recordingName).ResponseBody!)
            .RootElement.GetProperty("value").Clone();
}
