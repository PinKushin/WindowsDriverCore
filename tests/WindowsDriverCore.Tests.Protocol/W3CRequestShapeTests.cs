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

        _interactor = Substitute.For<IElementInteractor>();
        _interactor.SendKeys(Arg.Any<nint>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ElementAction.Performed("typed"));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_interactor);
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
