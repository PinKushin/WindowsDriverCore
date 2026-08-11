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
using WindowsDriverCore.Platform.Windows;
using NUnit.Framework;
using Shouldly;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Protocol.Sessions;
using WindowsDriverCore.Tests.Protocol.Recordings;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Click, clear and value over the wire, with UI Automation substituted.
/// </summary>
[TestFixture]
public sealed class ElementActionRouteTests : IDisposable
{
    private const string SessionId = "live-session";
    private const string ElementId = "42.19466560.4.73";
    private const nint Window = 0x9999;

    /// <summary>
    /// Text split across an array, as Selenium 3 and the Appium .NET client send
    /// it. Split deliberately: a single-element array would pass even against a
    /// route that read <c>value[0]</c> and dropped the rest.
    /// </summary>
    private static readonly string[] SplitText = ["print", "ers"];

    private static readonly string[] SingleText = ["hello"];

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementInteractor _interactor = null!;

    [SetUp]
    public void StartServer()
    {
        _interactor = Substitute.For<IElementInteractor>();

        // These fixtures use a made-up window handle, so the real
        // WindowLocator correctly says no such window exists — and an
        // element command now answers "the window has been closed" for
        // that, which outranks stale or unknown. They are about an element
        // being gone from a LIVE window, so the window has to be alive.
        IWindowLocator windowsAlive = Substitute.For<IWindowLocator>();
        windowsAlive.Exists(Arg.Any<nint>()).Returns(true);

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.AddSingleton(_interactor);
                    services.AddSingleton(windowsAlive);
                }));

        _client = _factory.CreateClient();

        _factory.Services.GetRequiredService<ISessionStore>().Add(new DriverSession(
            SessionId,
            new Dictionary<string, string> { ["app"] = "Calculator" },
            ProcessId: 1234,
            WindowHandle: Window));
    }

    [TearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private Task<HttpResponseMessage> Post(string suffix, object? body = null) =>
        _client.PostAsJsonAsync(
            new Uri($"/session/{SessionId}/element/{ElementId}/{suffix}", UriKind.Relative),
            body ?? new { });

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Test]
    public async Task Click_ThatSucceeds_MatchesTheRecordedBody()
    {
        _interactor.Click(Window, ElementId).Returns(ElementAction.Performed("Invoke"));

        HttpResponseMessage response = await Post("click");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Byte-for-byte against the recording. A successful action carries no
        // "value" property at all — not null — and a serializer left to its own
        // devices would emit one.
        body.ShouldBe(RecordedResponses.Named("element.click").ResponseBody!
            .Replace("{sessionId}", SessionId, StringComparison.Ordinal));
    }

    [Test]
    public async Task Click_OnSomethingWithNoWayToBeClicked_IsElementNotInteractable()
    {
        // The status the previous implementation never sent, because its ladder
        // ended in SetFocus() and reported success.
        _interactor.Click(Window, ElementId)
            .Returns(ElementAction.Failed(ElementActionOutcome.NotInteractable));

        HttpResponseMessage response = await Post("click");
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(400);
        body.GetProperty("status").GetInt32().ShouldBe(105);
        body.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe("element not interactable");
    }

    [Test]
    public async Task Clear_ThatSucceeds_AnswersTheSameShapeAsClick()
    {
        _interactor.Clear(Window, ElementId).Returns(ElementAction.Performed("NoValueToClear"));

        HttpResponseMessage response = await Post("clear");
        JsonElement body = await BodyOf(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("status").GetInt32().ShouldBe(0);
        body.TryGetProperty("value", out _).ShouldBeFalse("a void response carries no value");
    }

    [Test]
    public async Task Value_JoinsTheArrayTheClientSends()
    {
        // Selenium 3 and the Appium .NET client split the text across an array.
        // Taking value[0] would send "p" and pass a test written with a
        // single-element array — so the condition here is an array that is
        // actually split.
        _interactor.SendKeys(Window, ElementId, "printers")
            .Returns(ElementAction.Performed("keys"));

        HttpResponseMessage response = await Post("value", new { value = SplitText });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _interactor.Received(1).SendKeys(Window, ElementId, "printers");
    }

    /// <summary>
    /// The route TYPES rather than writing through ValuePattern.
    /// </summary>
    /// <remarks>
    /// <b>The negative half is the point.</b> Asserting only that TypeValue was
    /// called would still pass if the route called BOTH, and a stray ValuePattern
    /// write is exactly the defect being removed: it put the literal key codes
    /// U+E009 and U+E017 into the suite's edit box, which reads as an empty
    /// string in a failure message and is not one.
    /// </remarks>
    [Test]
    public async Task Value_Types_AndNeverWritesThroughValuePattern()
    {
        _interactor.SendKeys(Window, ElementId, "printers")
            .Returns(ElementAction.Performed("keys"));

        await Post("value", new { value = SplitText });

        _interactor.Received(1).SendKeys(Window, ElementId, "printers");
        _interactor.DidNotReceive().SetValue(
            Arg.Any<nint>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Value_OnSomethingThatCannotHoldOne_IsElementNotInteractable()
    {
        _interactor.SendKeys(Window, ElementId, "hello")
            .Returns(ElementAction.Failed(ElementActionOutcome.NotInteractable));

        HttpResponseMessage response = await Post("value", new { value = SingleText });
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(400);
        body.GetProperty("status").GetInt32().ShouldBe(105);
    }

    [Test]
    public async Task Click_OnAWindowThatWentAway_IsNoSuchWindow()
    {
        _interactor.Click(Window, ElementId)
            .Returns(ElementAction.Failed(ElementActionOutcome.NoSuchWindow));

        HttpResponseMessage response = await Post("click");
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(400);
        body.GetProperty("status").GetInt32().ShouldBe(23);
    }

    [Test]
    public async Task Click_OnAnIdThisServerNeverIssued_IsNoSuchElement()
    {
        _interactor.Click(Window, ElementId)
            .Returns(ElementAction.Failed(ElementActionOutcome.NotFound));

        HttpResponseMessage response = await Post("click");
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(404);
        body.GetProperty("status").GetInt32().ShouldBe(7);
    }

    [Test]
    public async Task ActionRoutes_RequireALiveSession()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            new Uri($"/session/not-a-session/element/{ElementId}/click", UriKind.Relative),
            new { });
        JsonElement body = await BodyOf(response);

        ((int)response.StatusCode).ShouldBe(404);
        body.GetProperty("status").GetInt32().ShouldBe(101);
    }
}
