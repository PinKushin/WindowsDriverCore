using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// <c>DELETE /session/{id}/window</c> does not answer until the window is gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED:</b> the compatibility suite builds its "orphaned element" by
/// closing a window and then using an element from it, expecting "Currently
/// selected window has been closed". Thirteen tests get an element error
/// instead.
/// </para>
/// <para>
/// <b>As far as we can tell, the cause is that <c>WM_CLOSE</c> is POSTED.</b>
/// <c>Close</c> returns as soon as the message is queued, so the window is still
/// alive when the client's next command arrives and <c>Exists</c> is still true.
/// The compatibility run is what settles whether this is the whole of it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ClosingAWindowWaitsForItTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.Close(Arg.Any<nint>()).Returns(true);
        _windows.WaitUntilGone(Arg.Any<nint>()).Returns(true);

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
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

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    [Test]
    public async Task ClosingAWindow_WaitsUntilItHasActuallyGone()
    {
        string sessionId = await NewSession();

        await _client.DeleteAsync(new Uri($"/session/{sessionId}/window", UriKind.Relative));

        _windows.Received(1).WaitUntilGone(TheWindow);
    }

    [Test]
    public async Task WhenTheWindowWasAlreadyGone_NothingIsWaitedFor()
    {
        // The control. Close() returning false means there was nothing to close,
        // and waiting on an absent window would be five seconds spent proving
        // what is already known.
        string sessionId = await NewSession();
        _windows.Close(Arg.Any<nint>()).Returns(false);

        await _client.DeleteAsync(new Uri($"/session/{sessionId}/window", UriKind.Relative));

        _windows.DidNotReceive().WaitUntilGone(Arg.Any<nint>());
    }
}
