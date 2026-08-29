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
