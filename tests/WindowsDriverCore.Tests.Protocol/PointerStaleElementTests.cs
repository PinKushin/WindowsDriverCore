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
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A pointer command against an element that has died answers
/// <c>stale element reference</c>, not a message of the driver's own invention.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured on the compatibility suite, 2026-08-11.</b>
/// <c>TouchLongTapError_StaleElement</c> asserts the message character for
/// character and got:
/// </para>
/// <code>
/// Expected:&lt;An element command failed because the referenced element is no longer attached to the DOM.&gt;
/// Actual:  &lt;The element could not be located: NotFound&gt;
/// </code>
/// <para>
/// The rule itself was never missing — <c>ElementFault</c> has had it, measured
/// against WinAppDriver, since the element routes were written. The
/// pointer routes simply did not ask it, and formatted an enum into a sentence
/// instead. That is the hazard this project keeps meeting: a second
/// implementation of a decision that already had one.
/// </para>
/// <para>
/// <b>The third test is the control and it is not decoration.</b> Without it,
/// "every pointer command faults" and "the failed reads fault" predict the same
/// two observations, and the fixture would pass on a route that refused
/// everything.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PointerStaleElementTests : IDisposable
{
    private const string StaleMessage =
        "An element command failed because the referenced element is no longer attached to the DOM.";

    private const string NoSuchElementMessage =
        "An element could not be located on the page using the given search parameters.";

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementInspector _inspector = null!;
    private IElementRegistry _registry = null!;
    private ISyntheticPointer _injector = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        IWindowLocator windows = Substitute.For<IWindowLocator>();
        windows.Exists(Arg.Any<nint>()).Returns(true);

        // Substituted for the same reason ActionsValidationTests substitutes it:
        // these routes now really inject, and the real one would put contacts on
        // whatever desktop the suite happens to be running on.
        _injector = Substitute.For<ISyntheticPointer>();
        _injector.CanInject(Arg.Any<SyntheticPointerKind>()).Returns(true);
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(true);

        _inspector = Substitute.For<IElementInspector>();
        _registry = Substitute.For<IElementRegistry>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(windows);
                services.AddSingleton(_injector);
                services.AddSingleton(_inspector);
                services.AddSingleton(_registry);
            }));

        _client = _factory.CreateClient();
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task LongClick_OnAnIssuedElementThatHasDied_IsStale()
    {
        ElementIsGone();

        // Issued once, so this is a stale element rather than an id invented by
        // the caller. TryConsume answers and forgets, which is what makes the
        // SECOND touch of the same id report differently.
        _registry.TryConsume(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        (HttpResponseMessage response, JsonDocument body) =
            await Post("touch/longclick").ConfigureAwait(false);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            body.RootElement.GetProperty("status").GetInt32().ShouldBe(10);
            Message(body).ShouldBe(StaleMessage);
        }
    }

    [Test]
    public async Task LongClick_OnAnIdThisServerNeverIssued_IsNoSuchElement()
    {
        ElementIsGone();
        _registry.TryConsume(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        (HttpResponseMessage response, JsonDocument body) =
            await Post("touch/longclick").ConfigureAwait(false);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            body.RootElement.GetProperty("status").GetInt32().ShouldBe(7);
            Message(body).ShouldBe(NoSuchElementMessage);
        }
    }

    [Test]
    public async Task Scroll_OnAnIssuedElementThatHasDied_IsStale()
    {
        ElementIsGone();
        _registry.TryConsume(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        (HttpResponseMessage response, JsonDocument body) =
            await Post("touch/scroll", xoffset: 0, yoffset: 100).ConfigureAwait(false);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            body.RootElement.GetProperty("status").GetInt32().ShouldBe(10);
            Message(body).ShouldBe(StaleMessage);
        }
    }

    /// <summary>
    /// THE CONTROL. An element that is still there is tapped, and the contact
    /// reaches the injector.
    /// </summary>
    [Test]
    public async Task LongClick_OnAnElementThatIsStillThere_Injects()
    {
        _inspector.ScreenBounds(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(new ElementRead<ElementBounds>(new ElementBounds(100, 200, 40, 20), ElementReadOutcome.Read));

        (HttpResponseMessage response, JsonDocument body) =
            await Post("touch/longclick").ConfigureAwait(false);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        }

        // The centre of the rectangle above, so a contact that lands anywhere
        // else is a failure rather than a pass with a shrug.
        _injector.Received().Inject(Arg.Is<IReadOnlyList<SyntheticContact>>(
            contacts => contacts.Count == 1 && contacts[0].X == 120 && contacts[0].Y == 210));
    }

    private void ElementIsGone() =>
        _inspector.ScreenBounds(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(new ElementRead<ElementBounds>(default, ElementReadOutcome.NotFound));

    private static string Message(JsonDocument body) =>
        body.RootElement.GetProperty("value").GetProperty("message").GetString()!;

    private async Task<(HttpResponseMessage Response, JsonDocument Body)> Post(
        string command, int xoffset = 0, int yoffset = 0)
    {
        string session = await NewSession().ConfigureAwait(false);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{session}/{command}", UriKind.Relative),
            new { element = "1.2.3.4", xoffset, yoffset }).ConfigureAwait(false);

        JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return (response, body);
    }

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } })
            .ConfigureAwait(false);

        using JsonDocument body = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync().ConfigureAwait(false));

        return body.RootElement.GetProperty("sessionId").GetString()!;
    }
}
