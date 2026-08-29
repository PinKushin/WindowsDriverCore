using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The same command, spelled the W3C way.
/// </summary>
/// <remarks>
/// <para>
/// <b>NOTHING IN THE COMPATIBILITY SUITE CAN CATCH THESE.</b> It is a
/// Selenium 3.8 client, so it sends the JSON Wire spelling for every command
/// these cover — the routes can be wrong for every Selenium 4 client alive and
/// the score will not move. Selenium 4 support is a stated goal of this driver,
/// which makes a request shape it cannot read a gap in the goal.
/// </para>
/// <para>
/// <b>Found by audit rather than by a failure</b>, after the same class of
/// omission turned up on <c>/touch/flick</c>: the route ignored the
/// protocol's own <c>speed</c> parameter, so a caller asking for a slow flick
/// and one asking for a fast flick got the identical gesture. The response
/// dialect was translated once in a filter; the REQUEST shapes were left to be
/// discovered one at a time.
/// </para>
/// </remarks>
[TestFixture]
public sealed class W3CRequestShapeTests : IDisposable
{
    private const nint Handle = 0x2222;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementInteractor _interactor = null!;
    private IWindowLocator _windows = null!;
    private IElementInspector _inspector = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, Handle)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // A substitute answers false, and the switch route refuses a non
        // top-level handle with UnknownError - which is HTTP 500. Without this
        // the W3C test would fail for a reason that has nothing to do with the
        // spelling it exists to check.
        _windows.IsTopLevel(Arg.Any<nint>()).Returns(true);

        // And the window must belong to the session's process, or the route
        // refuses it - also as UnknownError, also 500. The launcher above
        // reports process 4242, so the substitute agrees.
        _windows.GetOwningProcessId(Arg.Any<nint>()).Returns(4242);
        _windows.Maximize(Arg.Any<nint>()).Returns(true);
        _windows.SetBounds(Arg.Any<nint>(), Arg.Any<WindowBounds>()).Returns(true);
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(10, 20, 300, 400));

        _inspector = Substitute.For<IElementInspector>();
        _inspector.Attribute(Arg.Any<nint>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ElementRead.Success<string?>("the same answer either way"));
        _inspector.FocusedElementId(Arg.Any<nint>())
            .Returns(ElementRead.Success("focused"));

        _interactor = Substitute.For<IElementInteractor>();
        _interactor.SendKeys(Arg.Any<nint>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ElementAction.Performed("typed"));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_interactor);
                services.AddSingleton(_inspector);
            }));

        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>W3C sends the typed text as one string called "text".</summary>
    /// <remarks>
    /// JSON Wire sends <c>{"value": ["h","i"]}</c>; W3C sends
    /// <c>{"text": "hi"}</c>. Reading only the first meant a Selenium 4 client
    /// typed NOTHING — the body parsed, the route answered 200, and an empty
    /// string reached the interactor. A silent success is the worst shape this
    /// failure could take.
    /// </remarks>
    [Test]
    public async Task SetValue_AcceptsTheW3CTextSpelling()
    {
        string session = await NewSession();

        await Post($"/session/{session}/element/e1/value", """{"text":"hi"}""");

        _interactor.Received(1).SendKeys(Handle, "e1", "hi");
    }

    /// <summary>And the JSON Wire spelling still works.</summary>
    /// <remarks>
    /// <b>The control.</b> The whole compatibility score rides on this one, so a
    /// change that taught the route W3C and forgot JSON Wire would trade every
    /// suite test for a dialect nothing in the suite speaks.
    /// </remarks>
    [Test]
    public async Task SetValue_StillAcceptsTheJsonWireArray()
    {
        string session = await NewSession();

        await Post($"/session/{session}/element/e2/value", """{"value":["h","i"]}""");

        _interactor.Received(1).SendKeys(Handle, "e2", "hi");
    }

    /// <summary>W3C switches windows by "handle", not by "name".</summary>
    /// <remarks>
    /// A Selenium 4 client sending a perfectly well-formed switch got
    /// <i>"Missing Command Parameter: name"</i>.
    /// </remarks>
    [Test]
    public async Task SwitchWindow_AcceptsTheW3CHandleSpelling()
    {
        string session = await NewSession();

        HttpResponseMessage response = await Post(
            $"/session/{session}/window", $$"""{"handle":"0x{{Handle:X}}"}""");

        ((int)response.StatusCode).ShouldBe(200);
    }

    /// <summary>
    /// An EMPTY "name" is still reported as missing, not read as a handle.
    /// </summary>
    /// <remarks>
    /// <b>The control for the fallback.</b> <c>SwitchWindowsError_EmptyValue</c>
    /// sends <c>{"name":""}</c> and asserts <i>"Missing Command Parameter:
    /// name"</i>. A fallback that fired whenever <c>name</c> was falsy — rather
    /// than whenever it was ABSENT — would answer a different error and lose a
    /// suite test to gain a dialect.
    /// </remarks>
    [Test]
    public async Task AnEmptyName_IsStillMissing_NotAFallbackToHandle()
    {
        string session = await NewSession();

        HttpResponseMessage response = await Post(
            $"/session/{session}/window", """{"name":""}""");

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        (body.RootElement.GetProperty("value").GetProperty("message").GetString() ?? string.Empty)
            .ShouldContain("name");
    }

    /// <summary>W3C asks for the current window at /window, not /window_handle.</summary>
    /// <remarks>
    /// Found by the by-ROUTE lens of the protocol audit. W3C renamed both handle
    /// endpoints and this driver served only the JSON Wire spellings, so a
    /// Selenium 4 client could not ask which window it was on OR what windows
    /// existed — two of the most ordinary questions a client has, both answered
    /// with the unknown-command fallback.
    /// </remarks>
    [Test]
    public async Task TheCurrentWindow_IsServedAtBothSpellings()
    {
        string session = await NewSession();

        string jsonWire = await GetValue($"/session/{session}/window_handle");
        string w3c = await GetValue($"/session/{session}/window");

        w3c.ShouldBe(jsonWire, "the same question at two spellings is the same answer");
    }

    /// <summary>And the handle list at /window/handles.</summary>
    [Test]
    public async Task TheWindowList_IsServedAtBothSpellings()
    {
        string session = await NewSession();

        string jsonWire = await GetValue($"/session/{session}/window_handles");
        string w3c = await GetValue($"/session/{session}/window/handles");

        w3c.ShouldBe(jsonWire);
    }

    /// <summary>
    /// Release Actions exists, and the suite has never once sent it.
    /// </summary>
    /// <remarks>
    /// <b>Measured: a full suite run makes 25 POSTs to /actions and ZERO
    /// DELETEs.</b> So nothing in the compatibility score could ever have caught
    /// this route's absence — a W3C client releasing its input state got the
    /// unknown-command fallback, silently, forever.
    /// </remarks>
    [Test]
    public async Task ReleaseActions_IsServed()
    {
        string session = await NewSession();

        HttpResponseMessage response = await _client.DeleteAsync(
            new Uri($"/session/{session}/actions", UriKind.Relative));

        ((int)response.StatusCode).ShouldBe(200);
    }

    private async Task<string> GetValue(string path)
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        ((int)response.StatusCode).ShouldBe(200, $"{path} should be served");

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("value").ToString();
    }

    /// <summary>W3C reads window geometry as one rectangle.</summary>
    /// <remarks>
    /// It replaced size and position with <c>/window/rect</c>, and there is NO
    /// JSON Wire route a W3C client can fall back to — so without this a
    /// Selenium 4 client cannot read or set window geometry at all.
    /// </remarks>
    [Test]
    public async Task WindowRect_IsReadable()
    {
        string session = await NewSession();

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{session}/window/rect", UriKind.Relative));

        ((int)response.StatusCode).ShouldBe(200);

        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement rect = body.RootElement.GetProperty("value");

        rect.GetProperty("x").GetInt32().ShouldBe(10);
        rect.GetProperty("y").GetInt32().ShouldBe(20);
        rect.GetProperty("width").GetInt32().ShouldBe(300);
        rect.GetProperty("height").GetInt32().ShouldBe(400);
    }

    /// <summary>
    /// A rect that names only some fields leaves the others alone.
    /// </summary>
    /// <remarks>
    /// <b>The control, and it guards a destructive default.</b> Every field is
    /// optional in W3C, so a null means "leave it" — not "set it to zero". A
    /// client nudging only the position must not have its window resized to
    /// nothing, which is exactly what a record of non-nullable ints would do.
    /// </remarks>
    [Test]
    public async Task APartialRect_KeepsTheFieldsItDidNotName()
    {
        string session = await NewSession();

        await Post($"/session/{session}/window/rect", """{"x":50,"y":60}""");

        _windows.Received(1).SetBounds(
            Handle, Arg.Is<WindowBounds>(b =>
                b.X == 50 && b.Y == 60 && b.Width == 300 && b.Height == 400));
    }

    /// <summary>Maximize is served at both spellings.</summary>
    /// <remarks>
    /// JSON Wire addresses a window as <c>/window/{handle}/maximize</c> with
    /// "current" as this driver's alias; W3C dropped the handle entirely.
    /// </remarks>
    [Test]
    public async Task Maximize_IsServedAtBothSpellings()
    {
        string session = await NewSession();

        ((int)(await Post($"/session/{session}/window/current/maximize", "{}")).StatusCode)
            .ShouldBe(200, "the JSON Wire spelling is what the whole score rides on");
        ((int)(await Post($"/session/{session}/window/maximize", "{}")).StatusCode)
            .ShouldBe(200, "and W3C dropped the handle from the path");
    }

    /// <summary>W3C asks for the focused element with GET, not POST.</summary>
    [Test]
    public async Task ActiveElement_IsServedAtBothVerbs()
    {
        string session = await NewSession();

        ((int)(await Post($"/session/{session}/element/active", "{}")).StatusCode)
            .ShouldBe(200, "JSON Wire uses POST");
        ((int)(await _client.GetAsync(
            new Uri($"/session/{session}/element/active", UriKind.Relative))).StatusCode)
            .ShouldBe(200, "W3C uses GET");
    }

    /// <summary>
    /// W3C's "property" is this tree's "attribute".
    /// </summary>
    /// <remarks>
    /// W3C split attribute from property for the DOM. There is no DOM on a UIA
    /// tree — an element has properties and nothing else — so both spellings
    /// answer the same question, and a Selenium 4 client asking for a property
    /// was getting the unknown-command fallback.
    /// </remarks>
    [Test]
    public async Task Property_AnswersTheSameAsAttribute()
    {
        string session = await NewSession();

        string viaAttribute = await GetValue($"/session/{session}/element/e9/attribute/Name");
        string viaProperty = await GetValue($"/session/{session}/element/e9/property/Name");

        viaProperty.ShouldBe(viaAttribute);
    }

    /// <summary>Timeouts are writable AND readable.</summary>
    /// <remarks>
    /// <para>
    /// W3C defines <c>GET /session/{id}/timeouts</c>; this driver served only
    /// the POST, so a Selenium 4 client asking what the timeouts are got the
    /// unknown-command fallback. Found by the by-route audit pass, not by a
    /// failure — the suite writes the implicit wait and never reads it back.
    /// </para>
    /// <para>
    /// The JSON Wire control is the SETTER: the value the GET reports has to be
    /// the one the suite's own <c>{type, ms}</c> POST stored, or this route has
    /// learned to read a number nothing writes.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Timeouts_ReportTheImplicitWaitThatJsonWireSet()
    {
        string session = await NewSession();

        HttpResponseMessage stored = await Post(
            $"/session/{session}/timeouts",
            "{\"type\":\"implicit\",\"ms\":1234}");

        ((int)stored.StatusCode).ShouldBe(200, "the JSON Wire setter is the control");

        string reported = await GetValue($"/session/{session}/timeouts");

        JsonElement timeouts = JsonDocument.Parse(reported).RootElement;

        timeouts.GetProperty("implicit").GetInt32()
            .ShouldBe(1234, "the GET must report what the POST stored");

        // Page load and script are REFUSED on the way in, so there is no value
        // to report and zero is what a client that never set one should see.
        // Asserted rather than ignored: a driver that invents a number here is
        // claiming state it does not hold.
        timeouts.GetProperty("pageLoad").GetInt32().ShouldBe(0);
        timeouts.GetProperty("script").GetInt32().ShouldBe(0);
    }

    /// <summary>The legacy JSON Wire spelling of the implicit wait.</summary>
    /// <remarks>
    /// <c>POST /timeouts/implicit_wait</c> predates the <c>{type, ms}</c> body
    /// and is what an older client sends. Selenium 3.8 uses the newer form,
    /// which is exactly why the compatibility suite could never have caught its
    /// absence — the same blind spot that hid <c>/touch/flick</c>'s
    /// <c>speed</c>.
    /// </remarks>
    [Test]
    public async Task ImplicitWait_HasItsLegacySpelling()
    {
        string session = await NewSession();

        HttpResponseMessage legacy = await Post(
            $"/session/{session}/timeouts/implicit_wait",
            "{\"ms\":777}");

        ((int)legacy.StatusCode).ShouldBe(200, "the legacy spelling must be served");

        JsonDocument.Parse(await GetValue($"/session/{session}/timeouts"))
            .RootElement.GetProperty("implicit").GetInt32()
            .ShouldBe(777, "the legacy route must actually store the wait");

        // THE CONTROL, and it is the half that matters: the modern spelling is
        // what the suite sends 290 times a run. A route that learns the legacy
        // form and breaks the current one trades the entire score.
        HttpResponseMessage modern = await Post(
            $"/session/{session}/timeouts",
            "{\"type\":\"implicit\",\"ms\":55}");

        ((int)modern.StatusCode).ShouldBe(200);

        JsonDocument.Parse(await GetValue($"/session/{session}/timeouts"))
            .RootElement.GetProperty("implicit").GetInt32()
            .ShouldBe(55);
    }

    /// <summary>Setting the orientation is answered, not fallen through.</summary>
    /// <remarks>
    /// <para>
    /// Both dialects define <c>POST /session/{id}/orientation</c> and this
    /// driver served only the GET.
    /// </para>
    /// <para>
    /// <b>PORTRAIT is refused rather than accepted and ignored.</b> A desktop
    /// has one orientation, so a 200 for a rotation that did not happen would
    /// report success for doing nothing — the defect this driver exists to fix,
    /// and the same rule that makes a page-load timeout answer 501. LANDSCAPE
    /// succeeds because a client asking for the state it is already in has not
    /// been refused anything.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Orientation_AcceptsLandscapeAndRefusesPortrait()
    {
        string session = await NewSession();

        HttpResponseMessage landscape = await Post(
            $"/session/{session}/orientation",
            "{\"orientation\":\"LANDSCAPE\"}");

        ((int)landscape.StatusCode).ShouldBe(200, "the state it is already in");

        HttpResponseMessage portrait = await Post(
            $"/session/{session}/orientation",
            "{\"orientation\":\"PORTRAIT\"}");

        // Not 404 and not the unknown-command fallback: refused ON PURPOSE, by a
        // route that read the payload and disagreed with it.
        ((int)portrait.StatusCode)
            .ShouldBe(400, "a desktop cannot be rotated, and saying so is not the same as not answering");

        // The JSON Wire control: reading it still works and still says LANDSCAPE.
        (await GetValue($"/session/{session}/orientation")).ShouldBe("LANDSCAPE");
    }

    private async Task<string> NewSession()
    {
        _interactor.ClearReceivedCalls();

        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> Post(string path, string json) =>
        _client.PostAsync(
            new Uri(path, UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));
}
