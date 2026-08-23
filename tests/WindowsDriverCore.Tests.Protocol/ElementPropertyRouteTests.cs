using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using WindowsDriverCore.Platform.Windows;
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
    private IWindowLocator _windowsAlive = null!;

    /// <summary>Builds the server once. See <see cref="ArrangeDefaults"/> for per-test state.</summary>
    [OneTimeSetUp]
    public void StartServer()
    {
        _inspector = Substitute.For<IElementInspector>();
        _finder = Substitute.For<IElementFinder>();

        // These fixtures use a made-up window handle, so the real
        // WindowLocator correctly says no such window exists — and an
        // element command now answers "the window has been closed" for
        // that, which outranks stale or unknown. They are about an element
        // being gone from a LIVE window, so the window has to be alive.
        _windowsAlive = Substitute.For<IWindowLocator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_inspector);
                services.AddSingleton(_windowsAlive);
                services.AddSingleton(_finder);
            }));

        _client = _factory.CreateClient();
    }

    /// <summary>
    /// Clears the real store AND the real element registry before each test,
    /// then reseeds the one session every test needs.
    /// </summary>
    /// <remarks>
    /// <c>ElementThisServerIssued_ThatIsGone_IsStaleOnce_ThenUnknown</c> drives a
    /// real <c>POST /element</c> find, which records into the real
    /// <see cref="IElementRegistry"/> — its "stale once, then unknown" claim
    /// depends on that registry starting empty for the element id it uses, or an
    /// earlier test's leftover consumption could change which of the two answers
    /// comes first.
    /// </remarks>
    [SetUp]
    public void ArrangeDefaults()
    {
        _inspector.ClearReceivedCalls();
        _finder.ClearReceivedCalls();
        _windowsAlive.ClearReceivedCalls();
        _windowsAlive.Exists(Arg.Any<nint>()).Returns(true);

        _factory.Services.GetRequiredService<IElementRegistry>().Clear();

        ISessionStore store = _factory.Services.GetRequiredService<ISessionStore>();
        store.Clear();
        store.Add(new DriverSession(
            SessionId,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 1234,
            WindowHandle: Window));
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// A read that follows input gives the application time to react first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MEASURED on the guest, per-test durations against the reference:</b>
    /// </para>
    /// <code>
    ///                              WinAppDriver    this driver
    ///   MouseClick                       3.90 s        0.067 s   fails
    ///   ClickElement                     8.17 s        0.29 s    fails
    ///   GetElementDisplayedState         9.64 s        1.51 s    fails
    /// </code>
    /// <para>
    /// Those tests carry no synchronisation of their own — they click and read.
    /// WinAppDriver passes because a single find costs it ~1070 ms, so the
    /// application has caught up by accident. We fail by being 10-60x faster,
    /// and answering before the application has reacted is reporting the wrong
    /// state rather than reporting it quickly.
    /// </para>
    /// <para>
    /// <c>WaitForInputProcessed</c> alone does not cover this: it asks "is this
    /// process waiting for input", and injected input sits in the SYSTEM queue
    /// for a moment before reaching the application's, during which the answer
    /// is yes. Measured repeatedly in the transcript as
    /// <c>drain -> waited 0.8 ms</c> immediately before a read of the old value.
    /// </para>
    /// <para>
    /// <b>Asserted by ELAPSED TIME because the end state is identical either
    /// way.</b> The substituted inspector answers the same value whether the
    /// floor ran or not, so nothing else distinguishes them.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AReadRightAfterInput_LetsTheApplicationReactFirst()
    {
        _inspector.Text(Window, ElementId).Returns(ElementRead.Success("8"));

        // WARMED FIRST, AND WITHOUT THIS THE TEST MEASURES THE WRONG THING.
        // The first request to a route pays routing and JIT setup - more than
        // the floor itself - so an unwarmed timing passes with the floor set to
        // ZERO. Caught by mutation: the assertion survived removing the very
        // behaviour it exists to check.
        await Get("text");

        ISessionStore store = _factory.Services.GetRequiredService<ISessionStore>();
        store.Find(SessionId)!.InputPending = true;

        Stopwatch clock = Stopwatch.StartNew();
        await Get("text");
        clock.Stop();

        // The floor is 120 ms; 80 is comfortably below it and comfortably above
        // the few milliseconds an unfloored read costs, so this cannot pass by
        // accident on a slow machine or fail by accident on a fast one.
        clock.ElapsedMilliseconds.ShouldBeGreaterThan(
            80L, "the application must get a moment to react before it is read");
    }

    /// <summary>
    /// A read with nothing outstanding is not delayed at all.
    /// </summary>
    /// <remarks>
    /// <b>THE CONTROL, and it is the whole reason the floor is affordable.</b>
    /// A driver that waited on every property read would spend this on hundreds
    /// of reads that depend on nothing — and this project's argument is speed: a
    /// find costs ~33 ms here against ~1070 ms through WinAppDriver. The floor
    /// is paid once per dispatched input, by the first read that depends on it.
    /// </remarks>
    [Test]
    public async Task AReadWithNoInputOutstanding_IsNotDelayed()
    {
        _inspector.Text(Window, ElementId).Returns(ElementRead.Success("8"));

        // WARMED FIRST, AND WITHOUT THIS THE TEST MEASURES THE WRONG THING.
        // The first request to a route pays routing and JIT setup - more than
        // the floor itself - so an unwarmed timing passes with the floor set to
        // ZERO. Caught by mutation: the assertion survived removing the very
        // behaviour it exists to check.
        await Get("text");

        Stopwatch clock = Stopwatch.StartNew();
        await Get("text");
        clock.Stop();

        clock.ElapsedMilliseconds.ShouldBeLessThan(
            80L, "nothing was dispatched, so there is nothing to wait for");
    }

    /// <summary>
    /// The floor is measured from the DISPATCH, not from the read.
    /// </summary>
    /// <remarks>
    /// A client that does other work between the click and the read has already
    /// given the application its moment. Measuring from the read would charge it
    /// again for time that has already passed, on every such pair.
    /// </remarks>
    [Test]
    public async Task TimeAlreadyElapsedSinceTheInput_CountsTowardsTheFloor()
    {
        _inspector.Text(Window, ElementId).Returns(ElementRead.Success("8"));

        // WARMED FIRST, AND WITHOUT THIS THE TEST MEASURES THE WRONG THING.
        // The first request to a route pays routing and JIT setup - more than
        // the floor itself - so an unwarmed timing passes with the floor set to
        // ZERO. Caught by mutation: the assertion survived removing the very
        // behaviour it exists to check.
        await Get("text");

        ISessionStore store = _factory.Services.GetRequiredService<ISessionStore>();
        store.Find(SessionId)!.InputPending = true;

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Stopwatch clock = Stopwatch.StartNew();
        await Get("text");
        clock.Stop();

        clock.ElapsedMilliseconds.ShouldBeLessThan(
            80L, "200 ms have already passed since the input; the floor is spent");
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

    /// <summary>
    /// <c>/rect</c> answers location and size together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This route replaced a 501.</b> WinAppDriver never implemented it, and
    /// this fixture previously asserted the 501 to match. Matching a NOT-answer
    /// is not compatibility: nothing in the compatibility suite requests
    /// <c>/rect</c>, so implementing it costs no suite test — while a Selenium 4
    /// client cannot read an element's position or size WITHOUT it, because W3C
    /// deleted <c>/size</c> and <c>/location</c> in its favour.
    /// </para>
    /// <para>
    /// <b>Window-relative, like <c>/location</c>.</b> W3C reports coordinates in
    /// the top-level browsing context's frame; for a desktop driver that is the
    /// window, not the screen. <c>/location</c> being window-relative is measured
    /// against WinAppDriver, so answering <c>/rect</c> in screen coordinates
    /// would have the same driver report two different positions for one element.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Rect_CarriesLocationAndSizeTogether_InWindowCoordinates()
    {
        _inspector.WindowRelativeBounds(Window, ElementId)
            .Returns(ElementRead.Success(new ElementBounds(203, 419, 97, 35)));

        JsonElement value = (await BodyOf(await Get("rect"))).GetProperty("value");

        value.GetProperty("x").GetInt32().ShouldBe(203);
        value.GetProperty("y").GetInt32().ShouldBe(419);
        value.GetProperty("width").GetInt32().ShouldBe(97);
        value.GetProperty("height").GetInt32().ShouldBe(35);
    }

    [Test]
    public async Task Rect_AgreesWithLocationAndSize_ForTheSameElement()
    {
        // The control on the paragraph above. Three routes read one rectangle,
        // and a mistake in rect's own projection - width and height swapped,
        // screen coordinates instead of window ones - shows up here as
        // disagreement rather than as a plausible set of numbers.
        _inspector.WindowRelativeBounds(Window, ElementId)
            .Returns(ElementRead.Success(new ElementBounds(203, 419, 97, 35)));

        JsonElement rect = (await BodyOf(await Get("rect"))).GetProperty("value");
        JsonElement location = (await BodyOf(await Get("location"))).GetProperty("value");
        JsonElement size = (await BodyOf(await Get("size"))).GetProperty("value");

        rect.GetProperty("x").GetInt32().ShouldBe(location.GetProperty("x").GetInt32());
        rect.GetProperty("y").GetInt32().ShouldBe(location.GetProperty("y").GetInt32());
        rect.GetProperty("width").GetInt32().ShouldBe(size.GetProperty("width").GetInt32());
        rect.GetProperty("height").GetInt32().ShouldBe(size.GetProperty("height").GetInt32());
    }

    [Test]
    public async Task Rect_ForAnElementThatIsNotThere_FaultsLikeEveryOtherRead()
    {
        _inspector.WindowRelativeBounds(Window, ElementId)
            .Returns(ElementRead.Failed<ElementBounds>(ElementReadOutcome.NotFound));

        HttpResponseMessage response = await Get("rect");

        // 404/7, the same answer /size and /location give. A new route that
        // reports its failures its own way is how one driver ends up with three
        // messages for one condition.
        ((int)response.StatusCode).ShouldBe(404);
        (await BodyOf(response)).GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task ElementThisServerIssued_ThatIsGone_IsStaleOnce_ThenUnknown()
    {
        // The destructive behaviour, end to end. Measured against WinAppDriver:
        // the first touch of a dead element answers 400/10 and every touch after
        // it answers 404/7, against the same id.
        _finder.FindAll(Window, LocatorKind.AutomationId, "num5Button").Returns(FindResult.Matched([ElementId]));
        _finder.FindFirst(Window, LocatorKind.AutomationId, "num5Button").Returns(FindResult.Matched([ElementId]));

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
