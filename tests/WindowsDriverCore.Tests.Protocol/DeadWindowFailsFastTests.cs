using System;
using System.Linq;
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
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A find against a window that is already gone must not burn the implicit wait.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured 2026-08-10.</b> With the application killed underneath a live
/// session, every find took <b>2578 ms</b> and answered "no such element" — the
/// full implicit wait, 50 searches at 50 ms, against a window that could not
/// come back. Ten finds cost 25 seconds, and a cold-start compatibility run went
/// from six minutes to over thirty without finishing.
/// </para>
/// <para>
/// The window check existed, but it ran <i>after</i> the retry loop, so it
/// reported the right answer at the wrong time. The truth was available in under
/// a millisecond.
/// </para>
/// </remarks>
[TestFixture]
public sealed class DeadWindowFailsFastTests : IDisposable
{
    private const nint TheWindow = 0x1234;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementFinder _finder = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows = Substitute.For<IWindowLocator>();
        _windows.Exists(Arg.Any<nint>()).Returns(true);

        _finder = Substitute.For<IElementFinder>();
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([]));

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_finder);
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

    private async Task<string> NewSessionWithAnImplicitWait()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        string sessionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;

        // The compatibility suite sets 2.5 s, which is what made this expensive.
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/timeouts", UriKind.Relative),
            new StringContent("""{"type":"implicit","ms":2500}""", Encoding.UTF8, "application/json"));

        return sessionId;
    }

    private async Task<HttpResponseMessage> Find(string sessionId) =>
        await _client.PostAsync(
            new Uri($"/session/{sessionId}/element", UriKind.Relative),
            new StringContent(
                """{"using":"accessibility id","value":"anything"}""",
                Encoding.UTF8,
                "application/json"));

    [Test]
    public async Task WhenTheWindowIsGone_TheFindDoesNotSearchAtAll()
    {
        string sessionId = await NewSessionWithAnImplicitWait();

        // The window dies.
        _windows.Exists(TheWindow).Returns(false);

        HttpResponseMessage response = await Find(sessionId);

        // The measurement that matters: not one search was attempted. Asserting
        // elapsed time instead would be a clock-dependent test, and asserting
        // only the status would pass for the slow version too.
        _finder.DidNotReceive().FindFirst(
            Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>());

        // And it says the WINDOW is gone, not that the element is missing.
        // "no such element" would send a client hunting for a better locator for
        // something no locator can reach; 24 suite tests expect this message.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(23);
    }

    [Test]
    public async Task WhenTheWindowIsAlive_TheImplicitWaitStillRetries()
    {
        // The control. Without it, "never search" would pass by disabling the
        // implicit wait entirely, which is a feature 62 suite tests depend on.
        string sessionId = await NewSessionWithAnImplicitWait();

        HttpResponseMessage response = await Find(sessionId);

        // More than once: the retry loop ran, which is the whole point of an
        // implicit wait and what 62 suite tests depend on.
        _finder.ReceivedCalls().Count().ShouldBeGreaterThan(1);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
