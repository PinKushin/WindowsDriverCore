using System;
using System.Linq;
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
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The window inspection routes, against the recorded contract.
/// </summary>
/// <remarks>
/// Measured 2026-08-10 in the Windows 10 guest: <c>GET /title</c> blocked 14
/// tests on the compatibility suite, <c>GET /window_handle</c> 10 and
/// <c>GET /window/current/size</c> 11.
/// </remarks>
[TestFixture]
public sealed class WindowRouteTests : IDisposable
{
    private const nint Handle = 0x00551120;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, Handle)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);
        _windows.GetTitle(Arg.Any<nint>()).Returns("Calculator");
        _windows.GetBounds(Arg.Any<nint>()).Returns(new WindowBounds(54, 197, 502, 534));

        // A substitute defaults to false, which the routes correctly read as
        // "the window is gone". These model a window that is actually there.
        _windows.SetBounds(Arg.Any<nint>(), Arg.Any<WindowBounds>()).Returns(true);
        _windows.Maximize(Arg.Any<nint>()).Returns(true);

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

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<JsonElement> Get(string sessionId, string path)
    {
        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{sessionId}/{path}", UriKind.Relative));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Test]
    public async Task WindowHandle_IsEightDigitUppercaseHexWithAnLowercasePrefix()
    {
        // Not cosmetic. A client that round-trips this string back as a window id
        // will not match a differently-formatted one, so the format is contract.
        string sessionId = await NewSession();

        JsonElement body = await Get(sessionId, "window_handle");

        using JsonDocument recorded = JsonDocument.Parse(
            RecordedResponses.Named("window_handle").ResponseBody!);

        body.GetProperty("value").GetString()
            .ShouldBe(recorded.RootElement.GetProperty("value").GetString());
        body.GetProperty("status").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task WindowHandles_IsAnArrayOfTheOneHandle()
    {
        string sessionId = await NewSession();

        JsonElement body = await Get(sessionId, "window_handles");

        body.GetProperty("value").EnumerateArray().ShouldHaveSingleItem()
            .GetString().ShouldBe("0x00551120");
    }

    [Test]
    public async Task Title_IsTheWindowsTitleBarText()
    {
        string sessionId = await NewSession();

        JsonElement body = await Get(sessionId, "title");

        body.GetProperty("value").GetString().ShouldBe("Calculator");
    }

    [Test]
    public async Task Size_ReportsHeightAndWidth_InThatOrder()
    {
        string sessionId = await NewSession();

        JsonElement body = await Get(sessionId, "window/current/size");
        JsonElement value = body.GetProperty("value");

        value.GetProperty("height").GetInt32().ShouldBe(534);
        value.GetProperty("width").GetInt32().ShouldBe(502);

        // Property ORDER, which the recorded response has as height then width.
        value.EnumerateObject().Select(p => p.Name).ShouldBe(["height", "width"]);
    }

    [Test]
    public async Task Position_ReportsTheScreenCoordinatesOfTheTopLeft()
    {
        string sessionId = await NewSession();

        JsonElement value = (await Get(sessionId, "window/current/position")).GetProperty("value");

        value.GetProperty("x").GetInt32().ShouldBe(54);
        value.GetProperty("y").GetInt32().ShouldBe(197);
    }

    private Task<HttpResponseMessage> Post(string sessionId, string path, string json) =>
        _client.PostAsync(
            new Uri($"/session/{sessionId}/{path}", UriKind.Relative),
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    [Test]
    public async Task SettingTheSize_KeepsThePosition()
    {
        // The route sets SIZE. Moving the window as a side effect would be a
        // change the caller never asked for, and this is the only thing that
        // would notice.
        string sessionId = await NewSession();

        await Post(sessionId, "window/current/size", "{\"width\":800,\"height\":600}");

        _windows.Received(1).SetBounds(
            Arg.Any<nint>(),
            Arg.Is<WindowBounds>(b => b.X == 54 && b.Y == 197 && b.Width == 800 && b.Height == 600));
    }

    [Test]
    public async Task SettingThePosition_KeepsTheSize()
    {
        string sessionId = await NewSession();

        await Post(sessionId, "window/current/position", "{\"x\":10,\"y\":20}");

        _windows.Received(1).SetBounds(
            Arg.Any<nint>(),
            Arg.Is<WindowBounds>(b => b.X == 10 && b.Y == 20 && b.Width == 502 && b.Height == 534));
    }

    [Test]
    public async Task ASizeSentAsADouble_IsAccepted()
    {
        // A WebDriver client serialises sizes from doubles, so 800 arrives as
        // 800.0. Demanding an integer token would reject an ordinary request.
        string sessionId = await NewSession();

        HttpResponseMessage response = await Post(
            sessionId, "window/current/size", "{\"width\":800.0,\"height\":600.0}");

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Test]
    public async Task AMissingDimension_IsAMissingParameter_NotACrash()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await Post(sessionId, "window/current/size", "{\"width\":800}");

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Missing Command Parameter: width, height");
    }

    [Test]
    public async Task Maximize_AsksTheWindowToMaximize()
    {
        string sessionId = await NewSession();

        HttpResponseMessage response = await Post(sessionId, "window/current/maximize", "{}");

        response.IsSuccessStatusCode.ShouldBeTrue();
        _windows.Received(1).Maximize(Arg.Any<nint>());
    }

    [Test]
    public async Task DeletingTheWindow_ClosesItAndKeepsTheSession()
    {
        // The suite closes a window and keeps using the session id afterwards,
        // so this must not behave like DELETE /session.
        string sessionId = await NewSession();
        _windows.Close(Arg.Any<nint>()).Returns(true);

        HttpResponseMessage closed = await _client.DeleteAsync(
            new Uri($"/session/{sessionId}/window", UriKind.Relative));

        closed.IsSuccessStatusCode.ShouldBeTrue();
        _windows.Received(1).Close(Arg.Any<nint>());

        // The session survives.
        (await Get(sessionId, "title")).GetProperty("status").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task SwitchingToAWindowThatIsNotThere_IsNoSuchWindow()
    {
        string sessionId = await NewSession();
        _windows.Exists(Arg.Is<nint>(h => h == 0xDEADBEEF)).Returns(false);

        HttpResponseMessage response = await Post(sessionId, "window", "{\"name\":\"DEADBEEF\"}");

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("status").GetInt32().ShouldBe(23);
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("A request to switch to a window could not be satisfied because the window could not be found.");
    }

    [Test]
    public async Task SwitchingToARealWindow_RetargetsTheSession()
    {
        // The control for the test above: a handle that DOES exist must be
        // accepted, or "always refuse" would pass it and be useless.
        //
        // A REAL window is now more than one that exists: switching also
        // requires it to be top level and to belong to this application, which
        // is what SwitchWindowsError_NonTopLevelWindowHandle and
        // _ForeignWindowHandle ask for. NSubstitute answers false and 0 by
        // default, so without saying so here this control would describe a
        // child window owned by no process - and it caught exactly that when
        // those checks landed.
        _windows.IsTopLevel(Arg.Any<nint>()).Returns(true);
        _windows.GetOwningProcessId(Arg.Any<nint>()).Returns(4242);

        string sessionId = await NewSession();

        HttpResponseMessage response = await Post(sessionId, "window", "{\"name\":\"0x00551120\"}");

        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    [TestCase("window_handle")]
    [TestCase("title")]
    [TestCase("window/current/size")]
    [TestCase("window/current/position")]
    public async Task WhenTheWindowIsGone_EveryWindowRouteSaysSo(string path)
    {
        // The control for all of the above: with the window alive they answer
        // values, and a route that ignored liveness would pass every test above
        // while reporting a zero rectangle for a window that no longer exists.
        string sessionId = await NewSession();

        _windows.Exists(Arg.Any<nint>()).Returns(false);
        _windows.GetBounds(Arg.Any<nint>()).Returns((WindowBounds?)null);

        JsonElement body = await Get(sessionId, path);

        body.GetProperty("status").GetInt32().ShouldBe(23);
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");
    }
}
