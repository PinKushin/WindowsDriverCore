using System;
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
/// <c>POST /session/{id}/element/active</c>.
/// </summary>
/// <remarks>
/// Worth four suite tests, and previously absent — it answered
/// <c>404 jwp 9</c> five times in one measured run.
/// </remarks>
[TestFixture]
public sealed class ActiveElementRouteTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IElementInspector _inspector = null!;
    private IElementRegistry _registry = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _inspector = Substitute.For<IElementInspector>();
        _registry = Substitute.For<IElementRegistry>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_inspector);
                services.AddSingleton(_registry);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _inspector.ClearReceivedCalls();
        _registry.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);
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

    private async Task<HttpResponseMessage> ActiveElement(string sessionId) =>
        await _client.PostAsync(new Uri($"/session/{sessionId}/element/active", UriKind.Relative), null);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Test]
    public async Task TheFocusedElement_IsReturnedAsAnElementReference()
    {
        string sessionId = await NewSession();

        _inspector.FocusedElementId(TheWindow).Returns(ElementRead.Success("42.7.1"));

        HttpResponseMessage response = await ActiveElement(sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(response)).GetProperty("value").GetProperty("ELEMENT").GetString()
            .ShouldBe("42.7.1");
    }

    [Test]
    public async Task TheFocusedElement_IsRecordedSoItCanGoStaleLater()
    {
        // Without this the id is one this server handed out but has no record
        // of, so a later command against a dead element would answer "no such
        // element" where the suite asserts "stale element reference".
        string sessionId = await NewSession();

        _inspector.FocusedElementId(TheWindow).Returns(ElementRead.Success("42.7.1"));

        await ActiveElement(sessionId);

        _registry.Received().Record(Arg.Any<string>(), "42.7.1");
    }

    [Test]
    public async Task WhenFocusIsElsewhere_TheIdIsEmpty_AndItIsNotAFault()
    {
        // GetActiveElement_Empty opens the start menu to steal focus and then
        // requires a non-null element carrying string.Empty. A fault would be
        // the wrong SHAPE of response, not merely the wrong text.
        string sessionId = await NewSession();

        _inspector.FocusedElementId(TheWindow).Returns(ElementRead.Success(string.Empty));

        HttpResponseMessage response = await ActiveElement(sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await BodyOf(response);
        body.GetProperty("status").GetInt32().ShouldBe(0);
        body.GetProperty("value").GetProperty("ELEMENT").GetString().ShouldBe(string.Empty);
    }

    [Test]
    public async Task AnEmptyId_IsNotRecordedAsAnIssuedElement()
    {
        // Recording it would put a value in the issued-id set that no client can
        // use, and would make the NEXT empty answer look like a stale element.
        string sessionId = await NewSession();

        _inspector.FocusedElementId(TheWindow).Returns(ElementRead.Success(string.Empty));

        await ActiveElement(sessionId);

        _registry.DidNotReceive().Record(Arg.Any<string>(), string.Empty);
    }

    [Test]
    public async Task WhenTheWindowIsGone_ItSaysSo()
    {
        string sessionId = await NewSession();

        _windows.Exists(Arg.Any<nint>()).Returns(false);
        _inspector.FocusedElementId(TheWindow)
            .Returns(ElementRead.Failed<string>(ElementReadOutcome.NoSuchWindow));

        HttpResponseMessage response = await ActiveElement(sessionId);

        (await BodyOf(response)).GetProperty("status").GetInt32().ShouldBe(23);
    }
}
