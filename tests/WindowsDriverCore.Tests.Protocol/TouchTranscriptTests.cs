using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A touch gesture records where it aimed.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="IPointerLog"/> existed for exactly this and was wired into the
/// mouse routes only.</b> Its own contract says why: "two 200s and no effect is
/// the case this exists for".
/// </para>
/// <para>
/// <b>The test that needs it.</b> <c>TouchDoubleTap</c> fails at line 61 in
/// seven of seven full runs — the route answers 200 in 61 ms and the window does
/// not maximize. Driven directly on the guest the same route DOES maximize,
/// twice, with a single tap as the control, so the difference is context. The
/// transcript could say the gesture was dispatched and nothing whatever about
/// where it landed, which is the one fact that separates a wrong coordinate from
/// a coordinate the system delivered elsewhere.
/// </para>
/// <para>
/// <b>Every input path is substituted.</b> <c>WebApplicationFactory</c> boots
/// the real container, so a missing substitute here does not fail the test — it
/// injects real touch contacts onto whatever desktop the suite is running on.
/// That has happened twice in this repository.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TouchTranscriptTests : IDisposable
{
    private const int ElementLeft = 300;
    private const int ElementTop = 160;
    private const int ElementWidth = 54;
    private const int ElementHeight = 16;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IElementInspector _inspector = null!;
    private ISyntheticPointer _injector = null!;
    private IPointerLog _log = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _injector = Substitute.For<ISyntheticPointer>();
        _inspector = Substitute.For<IElementInspector>();
        _log = Substitute.For<IPointerLog>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_injector);
                services.AddSingleton(_inspector);
                services.AddSingleton(_log);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _injector.ClearReceivedCalls();
        _inspector.ClearReceivedCalls();
        _log.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // The session window owns every point, so the guard permits the gesture
        // and this measures the ROUTE. Left at the substitute's default of
        // false, the guard refuses and there is nothing to log.
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);

        _injector.CanInject(Arg.Any<SyntheticPointerKind>()).Returns(true);
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(true);

        _inspector.ScreenBounds(Arg.Any<nint>(), Arg.Any<string>())
            .Returns(new ElementRead<ElementBounds>(
                new ElementBounds(ElementLeft, ElementTop, ElementWidth, ElementHeight),
                ElementReadOutcome.Read));
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        using System.Text.Json.JsonDocument body =
            System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task Gesture(string suffix)
    {
        string session = await NewSession();

        await _client.PostAsJsonAsync(
            new Uri($"/session/{session}/{suffix}", UriKind.Relative),
            new { element = "42.1.2.3" });
    }

    /// <summary>The double tap names the point it aimed at.</summary>
    /// <remarks>
    /// The centre of the substituted rectangle, asserted exactly. A log call
    /// with the right shape and the wrong numbers would be worse than none,
    /// because a transcript is read as fact.
    /// </remarks>
    [Test]
    public async Task ADoubleTap_RecordsThePointItAimedAt()
    {
        await Gesture("touch/doubleclick");

        _log.Received(1).PointerTargeted(
            "touch doubleclick",
            ElementLeft + (ElementWidth / 2),
            ElementTop + (ElementHeight / 2),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<double>());
    }

    /// <summary>The long press names its point too, under its own name.</summary>
    /// <remarks>
    /// <b>The control for the command string.</b> Logging every gesture as
    /// "touch" would satisfy the test above and make a transcript unable to tell
    /// a long press from a double tap — which is the distinction
    /// <c>TouchLongTap</c> and <c>TouchDoubleTap</c> failing together would turn
    /// on.
    /// </remarks>
    [Test]
    public async Task ALongPress_RecordsItsOwnCommandName()
    {
        await Gesture("touch/longclick");

        _log.Received(1).PointerTargeted(
            "touch longclick",
            ElementLeft + (ElementWidth / 2),
            ElementTop + (ElementHeight / 2),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<double>());
    }
}
