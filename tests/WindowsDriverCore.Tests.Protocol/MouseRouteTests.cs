using System;
using System.Net;
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
/// The mouse family: <c>/moveto</c>, <c>/click</c>, <c>/buttondown</c>,
/// <c>/buttonup</c>, <c>/doubleclick</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every expected status here was recorded from WinAppDriver 1.2.1</b>, in
/// <c>Recordings/winappdriver-mouse-xpath.json</c>, rather than read off the
/// JSON Wire Protocol document. The two disagree in a way that matters: the
/// protocol describes <c>moveto</c> as taking an element OR offsets, and the
/// real server accepts offsets alone, an element alone, both together, and a
/// <i>null</i> element with offsets — but rejects an empty body.
/// </para>
/// <para>
/// <b>Why these exist at all.</b> The suite's alarm cleanup is an XPath find
/// followed by <c>Mouse.ContextClick</c> inside a bare <c>catch { break; }</c>.
/// With these routes missing, every delete was a silent no-op and alarms
/// accumulated until Alarms &amp; Clock refused new ones, which broke nine tests
/// that never touch the mouse.
/// </para>
/// </remarks>
[TestFixture]
public sealed class MouseRouteTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IPointerInput _pointer = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        IWindowLocator windows = Substitute.For<IWindowLocator>();
        windows.Exists(Arg.Any<nint>()).Returns(true);

        _pointer = Substitute.For<IPointerInput>();
        _pointer.MoveTo(Arg.Any<int>(), Arg.Any<int>()).Returns(true);
        _pointer.Click(Arg.Any<PointerButton>()).Returns(true);
        _pointer.Press(Arg.Any<PointerButton>()).Returns(true);
        _pointer.Release(Arg.Any<PointerButton>()).Returns(true);
        _pointer.DoubleClick(Arg.Any<PointerButton>()).Returns(true);
        _pointer.TryGetPosition(out Arg.Any<int>(), out Arg.Any<int>())
            .Returns(call =>
            {
                call[0] = 100;
                call[1] = 200;
                return true;
            });

        IElementInspector inspector = Substitute.For<IElementInspector>();
        inspector.ScreenBounds(Arg.Any<nint>(), "known")
            .Returns(ElementRead.Success(new ElementBounds(10, 20, 100, 50)));
        inspector.ScreenBounds(Arg.Any<nint>(), Arg.Is<string>(id => id != "known"))
            .Returns(ElementRead.Failed<ElementBounds>(ElementReadOutcome.NotFound));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(windows);
                services.AddSingleton(_pointer);
                services.AddSingleton(inspector);
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

    private async Task<HttpResponseMessage> Post(string suffix, string json)
    {
        string sessionId = await NewSession();

        return await _client.PostAsync(
            new Uri($"/session/{sessionId}/{suffix}", UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    [Test]
    public async Task MoveTo_WithAnElement_AimsAtItsCentre()
    {
        HttpResponseMessage response = await Post("moveto", """{"element":"known"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The element is at 10,20 and is 100x50, so its centre is 60,45.
        // Asserting the coordinate rather than "MoveTo was called" — the centre
        // rule and the top-left rule both call MoveTo, and only the value tells
        // them apart.
        _pointer.Received(1).MoveTo(60, 45);
    }

    [Test]
    public async Task MoveTo_WithAnElementAndOffsets_AimsFromItsTopLeft()
    {
        HttpResponseMessage response = await Post(
            "moveto", """{"element":"known","xoffset":5,"yoffset":7}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _pointer.Received(1).MoveTo(15, 27);
    }

    [Test]
    public async Task MoveTo_WithOffsetsAndNoElement_IsRelativeToTheCursor()
    {
        // The fake cursor sits at 100,200.
        HttpResponseMessage response = await Post("moveto", """{"xoffset":10,"yoffset":10}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _pointer.Received(1).MoveTo(110, 210);
    }

    [Test]
    public async Task MoveTo_WithAnUnknownElement_IsNoSuchElement()
    {
        HttpResponseMessage response = await Post("moveto", """{"element":"99999"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MoveTo_WithAnEmptyBody_IsRejected()
    {
        // Recorded: {} is 400 while {"xoffset":10,"yoffset":10} is 200, so the
        // discriminator is "did the caller say where", not "was an element named".
        HttpResponseMessage response = await Post("moveto", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Click_WithNoButton_IsTheLeftOne()
    {
        HttpResponseMessage response = await Post("click", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _pointer.Received(1).Click(PointerButton.Left);
    }

    [Test]
    public async Task Click_WithButtonTwo_IsTheRightOne()
    {
        // The one the suite's alarm cleanup depends on: ContextClick sends 2.
        HttpResponseMessage response = await Post("click", """{"button":2}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _pointer.Received(1).Click(PointerButton.Right);

        // The control. Without it, a handler that ignored the field entirely and
        // always clicked left would pass the assertion above.
        _pointer.DidNotReceive().Click(PointerButton.Left);
    }

    [Test]
    public async Task Click_WithAButtonThatDoesNotExist_IsRejected()
    {
        HttpResponseMessage response = await Post("click", """{"button":9}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _pointer.DidNotReceive().Click(Arg.Any<PointerButton>());
    }

    [Test]
    public async Task ButtonDownAndButtonUp_PressAndReleaseWithoutClicking()
    {
        (await Post("buttondown", """{"button":2}""")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Post("buttonup", """{"button":2}""")).StatusCode.ShouldBe(HttpStatusCode.OK);

        _pointer.Received(1).Press(PointerButton.Right);
        _pointer.Received(1).Release(PointerButton.Right);

        // Held-then-released is a drag, not a click. If buttondown were wired to
        // Click the sequence would still answer 200 twice.
        _pointer.DidNotReceive().Click(Arg.Any<PointerButton>());
    }

    [Test]
    public async Task DoubleClick_IsItsOwnCommand()
    {
        HttpResponseMessage response = await Post("doubleclick", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _pointer.Received(1).DoubleClick(PointerButton.Left);

        // Two separate Click calls would let the user's own movement land
        // between them and turn the pair into two single clicks.
        _pointer.DidNotReceive().Click(Arg.Any<PointerButton>());
    }
}
