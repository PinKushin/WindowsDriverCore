using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Every read that dispatched input can change waits for it first.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's direction, 2026-08-30:</b> "no flake no flake no flake,
/// winappdriver doesnt flake we shouldnt and theres absolutely no reason for it
/// most of the time besides us not implementing something or diverging from
/// winappdriver in our actual behavior."
/// </para>
/// <para>
/// <b>A racing read IS such a divergence.</b> Per-test, measured on the guest:
/// </para>
/// <code>
///                             WinAppDriver   this driver
///   MouseClick                      3.90 s       0.067 s   we fail
///   ClickElement                    8.17 s       0.29 s    we fail
///   GetElementDisplayedState        9.64 s       1.51 s    we fail
/// </code>
/// <para>
/// Those tests carry no synchronisation — they act and then read. The reference
/// passes because a single find costs it ~1070 ms, so the application has caught
/// up by accident. Being fast is not a defence for answering before the
/// application reacted.
/// </para>
/// <para>
/// <b>The drain was measured and then applied in one place.</b> It sat in
/// <c>ElementPropertyRoutes</c> because that is where the race was first seen. An
/// audit of the routing layer found that no window-level read drains at all,
/// though a navigation renames a window, a drag moves one, a click can open one,
/// and a click can raise an alert.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ReadsWaitForDispatchedInputTests : IDisposable
{
    private const nint TheWindow = 0x4321;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private ISessionStore _store = null!;
    private IWindowLocator _windows = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _windows = Substitute.For<IWindowLocator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton(_windows)));

        _client = _factory.CreateClient();
        _store = _factory.Services.GetRequiredService<ISessionStore>();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _store.Clear();
        _windows.ClearReceivedCalls();

        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.GetTitle(Arg.Any<nint>()).Returns("Calculator");
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(0, 0, 800, 600));
        _windows.IsTopLevel(Arg.Any<nint>()).Returns(true);
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>Seeds a session that has just dispatched input.</summary>
    private string SessionWithInputInFlight()
    {
        string id = Guid.NewGuid().ToString("D");

        DriverSession session = new(
            id,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 4242,
            WindowHandle: TheWindow)
        {
            InputPending = true,
        };

        _store.Add(session);
        return id;
    }

    /// <summary>A window title read waits for the navigation that renames it.</summary>
    /// <remarks>
    /// <c>NavigateBack_SystemApp</c> reads <c>session.Title</c> IMMEDIATELY after
    /// <c>Navigate().Back()</c> — while the same test sleeps a full second after
    /// the navigation going the other way.
    /// </remarks>
    [Test]
    public async Task ATitleRead_WaitsForInputAlreadyDispatched()
    {
        string id = SessionWithInputInFlight();

        await _client.GetAsync(new Uri($"/session/{id}/title", UriKind.Relative));

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    /// <summary>A window rectangle read waits for the drag that moved it.</summary>
    [Test]
    public async Task AWindowPositionRead_WaitsForInputAlreadyDispatched()
    {
        string id = SessionWithInputInFlight();

        await _client.GetAsync(new Uri($"/session/{id}/window/current/position", UriKind.Relative));

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    /// <summary>A handle list waits for the click that may have opened a window.</summary>
    [Test]
    public async Task AWindowHandleListRead_WaitsForInputAlreadyDispatched()
    {
        string id = SessionWithInputInFlight();

        await _client.GetAsync(new Uri($"/session/{id}/window_handles", UriKind.Relative));

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    /// <summary>The page source waits for anything that changed the tree.</summary>
    /// <remarks>
    /// The source is a snapshot of the whole tree, so ANY interaction can change
    /// it — the widest case in this fixture rather than the narrowest.
    /// </remarks>
    [Test]
    public async Task APageSourceRead_WaitsForInputAlreadyDispatched()
    {
        string id = SessionWithInputInFlight();

        await _client.GetAsync(new Uri($"/session/{id}/source", UriKind.Relative));

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }

    /// <summary>With nothing dispatched, nothing waits.</summary>
    /// <remarks>
    /// <b>THE CONTROL, and the reason this is safe to widen.</b> The drain must
    /// cost nothing on the common path — a session that has dispatched no input
    /// pays for no wait. A version that always waited would pass every test above
    /// and add a hundred milliseconds to every read in every suite, which is the
    /// argument this project makes against the reference.
    /// </remarks>
    [Test]
    public async Task WithNoInputOutstanding_NoReadWaits()
    {
        string id = Guid.NewGuid().ToString("D");
        _store.Add(new DriverSession(
            id,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 4242,
            WindowHandle: TheWindow));

        await _client.GetAsync(new Uri($"/session/{id}/title", UriKind.Relative));
        await _client.GetAsync(new Uri($"/session/{id}/window/current/position", UriKind.Relative));
        await _client.GetAsync(new Uri($"/session/{id}/window_handles", UriKind.Relative));

        _windows.DidNotReceive().WaitForInputProcessed(Arg.Any<nint>());
    }

    /// <summary>The wait is spent once, not once per read.</summary>
    /// <remarks>
    /// <b>The second control.</b> The drain clears the flag, so a burst of reads
    /// after one click pays for one wait. Without this, "drains" and "waits every
    /// single time" predict the same observation in every test above.
    /// </remarks>
    [Test]
    public async Task TheWaitIsSpentOncePerInput_NotOncePerRead()
    {
        string id = SessionWithInputInFlight();

        await _client.GetAsync(new Uri($"/session/{id}/title", UriKind.Relative));
        await _client.GetAsync(new Uri($"/session/{id}/title", UriKind.Relative));
        await _client.GetAsync(new Uri($"/session/{id}/title", UriKind.Relative));

        _windows.Received(1).WaitForInputProcessed(TheWindow);
    }
}
