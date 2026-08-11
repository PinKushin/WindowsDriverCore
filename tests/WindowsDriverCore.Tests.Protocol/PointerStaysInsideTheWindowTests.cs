using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Synthesized input goes to the application under test, or nowhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two defects on the same path, found together on 2026-08-11 when Edge
/// windows appeared in the guest that no test had asked for.</b>
/// </para>
/// <para>
/// The first was arithmetic. A <c>viewport</c> origin was treated as absolute
/// screen pixels — "a desktop session's viewport IS the screen" — while the
/// suite feeds it <c>element.Location</c>, which this driver answers
/// window-relative, measured against WinAppDriver. So every viewport gesture
/// landed at the element's window offset measured from the DESKTOP origin: up
/// and to the left of the application, onto whatever was there.
/// </para>
/// <para>
/// The second is why the first could do damage. <c>UiaElementInteractor</c> has
/// asked <see cref="IWindowLocator.OwnsThePointAt"/> before every mouse click
/// since the click ladder was written — "an unguarded coordinate click is worse
/// than no click" — and the pointer path asked nothing. A wrong coordinate is a
/// failing test; a wrong coordinate with no guard is input delivered into
/// somebody else's application.
/// </para>
/// <para>
/// <b>Both tests are needed and neither implies the other.</b> Fixing the
/// arithmetic with no guard leaves the next arithmetic mistake free to escape;
/// adding the guard with no fix turns every viewport gesture into a refusal.
/// The guard is the one asserted here because it is the one that bounds the
/// damage.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PointerStaysInsideTheWindowTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private ISyntheticPointer _injector = null!;
    private IWindowLocator _windows = null!;

    /// <summary>Builds the server once. See <see cref="ArrangeDefaults"/> for per-test state.</summary>
    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _injector = Substitute.For<ISyntheticPointer>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_injector);
            }));

        _client = _factory.CreateClient();
    }

    /// <summary>
    /// Rearms every default before each test. <c>OwnsThePointAt</c> needs no
    /// rearming — both tests fully configure it inline (<c>true</c> and
    /// <c>false</c> respectively) regardless of which ran first.
    /// </summary>
    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _injector.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // The window sits at (500,300). A viewport point is measured from THERE,
        // which is the whole arithmetic under test.
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(500, 300, 800, 600));

        _injector.CanInject(Arg.Any<SyntheticPointerKind>()).Returns(true);
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(true);
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
    /// A viewport point is measured from the window, not from the desktop.
    /// </summary>
    [Test]
    public async Task AViewportOrigin_IsMeasuredFromTheWindow()
    {
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);

        await Tap(120, 40).ConfigureAwait(false);

        // (500,300) + (120,40). Predicted exactly: a coordinate that is merely
        // "not (120,40)" would also be satisfied by any other wrong answer.
        _injector.Received().Inject(Arg.Is<IReadOnlyList<SyntheticContact>>(
            contacts => contacts.Count == 1 && contacts[0].X == 620 && contacts[0].Y == 340));
    }

    /// <summary>
    /// THE GUARD. A point the window does not own is never dispatched.
    /// </summary>
    [Test]
    public async Task APointTheWindowDoesNotOwn_IsRefusedRatherThanDispatched()
    {
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(false);

        JsonDocument body = await Tap(120, 40).ConfigureAwait(false);

        using (body)
        {
            // Reported, not silently swallowed: a caller told the gesture
            // happened when nothing was dispatched is the defect this driver
            // exists to fix.
            body.RootElement.GetProperty("status").GetInt32().ShouldNotBe(0);
        }

        // The measurement that matters. The status could be right while the
        // contact went out anyway, and then the guard would be decoration.
        _injector.DidNotReceive().Inject(Arg.Any<IReadOnlyList<SyntheticContact>>());
    }

    private async Task<JsonDocument> Tap(int x, int y)
    {
        string session = await NewSession().ConfigureAwait(false);

        object payload = new
        {
            actions = new[]
            {
                new
                {
                    type = "pointer",
                    id = "finger1",
                    parameters = new { pointerType = "touch" },
                    actions = new object[]
                    {
                        new { type = "pointerMove", duration = 0, origin = "viewport", x, y },
                        new { type = "pointerDown", button = 0 },
                        new { type = "pointerUp", button = 0 },
                    },
                },
            },
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/{session}/actions", UriKind.Relative), payload).ConfigureAwait(false);

        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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
