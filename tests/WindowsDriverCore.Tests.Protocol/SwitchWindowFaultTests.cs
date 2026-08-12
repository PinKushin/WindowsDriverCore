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
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The four ways <c>POST /session/{id}/window</c> refuses a handle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth four suite tests</b>, each asserting its message character for
/// character. All four previously answered the same sentence — "A request to
/// switch to a window could not be satisfied because the window could not be
/// found" — which is right for a missing window and wrong for the other three.
/// </para>
/// <para>
/// <b>The distinctions are not cosmetic.</b> A caller that sent a child window
/// needs to know it must climb to the frame; a caller that sent another
/// application's window needs to know it has the wrong session entirely. "Not
/// found" sends both of them looking for a window that is sitting right there.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SwitchWindowFaultTests : IDisposable
{
    private const nint TheWindow = 0x1234;
    private const int TheProcess = 4242;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(TheProcess, TheWindow)));

        // Every default says "this is a legitimate switch target", so each test
        // changes exactly one thing and the fault it triggers is unambiguous.
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.IsTopLevel(Arg.Any<nint>()).Returns(true);
        _windows.GetOwningProcessId(Arg.Any<nint>()).Returns(TheProcess);
        _windows.GetHostedProcessId(Arg.Any<nint>()).Returns(TheProcess);
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

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<string?> SwitchTo(string name)
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await _client.PostAsync(
            new Uri($"/session/{sessionId}/window", UriKind.Relative),
            new StringContent($$"""{"name":"{{name}}"}""", Encoding.UTF8, "application/json"));

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return body.TryGetProperty("value", out JsonElement value) &&
               value.TryGetProperty("message", out JsonElement message)
            ? message.GetString()
            : null;
    }

    [Test]
    public async Task AnEmptyName_IsAMissingParameter_NotAMissingWindow()
    {
        (await SwitchTo(string.Empty)).ShouldBe("Missing Command Parameter: name");
    }

    [Test]
    public async Task ANegativeHandle_ReportsTheParseFailure()
    {
        // Convert.ToInt32's own wording, which WinAppDriver lets through
        // verbatim and the suite asserts exactly.
        (await SwitchTo("-1")).ShouldBe("String cannot contain a minus sign if the base is not 10.");
    }

    [Test]
    public async Task AChildWindow_IsRefusedForBeingAChild()
    {
        // The suite hands over the CoreWindow's handle: it exists AND belongs to
        // this application, so every other check passes and only this one can
        // tell it apart from a legitimate switch.
        _windows.IsTopLevel(Arg.Any<nint>()).Returns(false);

        (await SwitchTo("880088")).ShouldEndWith("is not a top level window handle");
    }

    [Test]
    public async Task AnotherApplicationsWindow_IsRefusedByProcess()
    {
        _windows.GetOwningProcessId(Arg.Any<nint>()).Returns(9999);
        _windows.GetHostedProcessId(Arg.Any<nint>()).Returns(9999);

        (await SwitchTo("880088"))
            .ShouldBe("Window handle does not belong to the same process/application");
    }

    [Test]
    public async Task APackagedWindowHostedByThisApp_IsAccepted()
    {
        // THE CONTROL, and the one that keeps the process check honest. A
        // packaged app's frame is owned by ApplicationFrameHost while its
        // content belongs to the app, so comparing only the owning process would
        // reject the session's OWN window and break every legitimate switch.
        _windows.GetOwningProcessId(Arg.Any<nint>()).Returns(9999);
        _windows.GetHostedProcessId(Arg.Any<nint>()).Returns(TheProcess);

        (await SwitchTo("880088")).ShouldBeNull("a legitimate switch reports no fault");
    }

    [Test]
    public async Task AWindowThatIsGone_StillReportsNoSuchWindow()
    {
        // The behaviour that already worked and must survive the three new
        // refusals being inserted ahead of it.
        _windows.Exists(Arg.Any<nint>()).Returns(false);

        (await SwitchTo("880088")).ShouldBe(
            "A request to switch to a window could not be satisfied because the window could not be found.");
    }
}
