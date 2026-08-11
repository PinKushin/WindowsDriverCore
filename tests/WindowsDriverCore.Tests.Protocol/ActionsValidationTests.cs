using System;
using System.Collections.Generic;
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
/// <c>POST /session/{id}/actions</c> payload validation.
/// </summary>
/// <remarks>
/// <para>
/// <b>20 tests on the compatibility suite fail on the absence of validation, not
/// the absence of Actions.</b> Every <c>ActionsError_*</c> test sends a
/// deliberately malformed payload and asserts a specific message; they got
/// "Command not recognized" instead.
/// </para>
/// <para>
/// The expected strings are the suite's own constants, which it asserts against
/// real WinAppDriver and passes — a stricter source than a recording, because it
/// is what a real client compares against.
/// </para>
/// <para>
/// <b>A VALID payload is refused, and that is the point of the last test here.</b>
/// Accepting a well-formed action sequence and performing nothing would report
/// success for doing nothing.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ActionsValidationTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private ISyntheticPointer _injector = null!;

    /// <summary>
    /// Builds the server ONCE for the whole fixture, not per test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only what DI registration actually requires lives here.</b> A
    /// substitute's REGISTRATION is fixed at container-build time — it cannot be
    /// swapped after <c>WithWebHostBuilder</c> runs — so the instances have to be
    /// created here. Their <c>.Returns()</c> configuration does not have to be:
    /// that is rearmed per test in <see cref="ArrangeDefaults"/>, which is what
    /// keeps eight tests safe to share one instance instead of leaking
    /// configuration between each other.
    /// </para>
    /// <para>
    /// 21 of 21 protocol fixtures used to boot a fresh
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> — an ASP.NET Core host —
    /// per <c>[Test]</c>. Measured 2026-08-11: nothing here needs per-test
    /// isolation of the CONTAINER, only of the substitutes' configured behaviour
    /// and call history, both of which are cheap to reset without rebuilding the
    /// host.
    /// </para>
    /// </remarks>
    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();

        // THE INJECTOR IS SUBSTITUTED, AND THAT IS NOT OPTIONAL.
        //
        // /actions performs what it validates, and WebApplicationFactory boots
        // the REAL container - so a protocol test posting a valid payload
        // synthesises real touch onto whoever's desktop is running the suite.
        // Measured 2026-08-11, the hard way: a run of these tests clicked the
        // owner's browser.
        //
        // A protocol test is about the wire, not about the desktop. The fake
        // reports success so the route's own behaviour is what gets asserted.
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
    /// Puts every substitute back to its known-good default before each test.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes sharing the fixture safe, not a formality.</b>
    /// <c>AnInjectorThatRefuses_IsReportedAsAFailure_NotAsSuccess</c>
    /// reconfigures <c>_injector.Inject(...)</c> to return <c>false</c> inside
    /// its own test body. Without rearming the default here, that configuration
    /// would persist on the shared instance into whichever test happened to run
    /// next — an ORDER-DEPENDENT failure, which is worse than a deterministic
    /// one because it passes locally and fails on CI depending on how NUnit
    /// schedules the class. <c>ClearReceivedCalls()</c> is the other half:
    /// without it, <c>AValidPayload_IsPerformed_AndSaysSo</c>'s
    /// <c>_injector.Received()</c> — an implicit <c>Received(1)</c> — would see
    /// calls left over from earlier tests and fail even though ITS OWN request
    /// behaved correctly.
    /// </remarks>
    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _injector.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        // The session window owns every point, so the pointer guard permits the
        // gesture and these tests measure the ROUTE. Left at NSubstitute's
        // default false, the guard refuses everything and a payload test would
        // be reporting on the guard instead.
        _windows.OwnsThePointAt(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<nint>()).Returns(true);

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

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private async Task<HttpResponseMessage> PostActions(string json)
    {
        string sessionId = await NewSession();

        return await _client.PostAsync(
            new Uri($"/session/{sessionId}/actions", UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task<string?> MessageOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").GetProperty("message").GetString();

    private static string Pointer(string pointerType, string step) =>
        $$"""
        {"actions":[{"type":"pointer","id":"p1","parameters":{"pointerType":"{{pointerType}}"},
        "actions":[{{step}}]}]}
        """;

    [Test]
    public async Task APressureOutsideZeroToOne_IsRejectedByName()
    {
        HttpResponseMessage response = await PostActions(
            Pointer("touch", """{"type":"pointerDown","button":0,"pressure":2.5}"""));

        (await MessageOf(response))
            .ShouldBe("\"pressure\" attribute is not a floating point value between 0 and 1");
    }

    [Test]
    public async Task ATiltThatIsNotAWholeNumber_IsRejected()
    {
        // In range but fractional. The message says "integer", so the range check
        // alone would accept this and the test would pass for the wrong reason.
        HttpResponseMessage response = await PostActions(
            Pointer("pen", """{"type":"pointerMove","duration":0,"tiltX":45.5}"""));

        (await MessageOf(response))
            .ShouldBe("\"tiltX\" attribute is not an integer value between -90 and 90");
    }

    [Test]
    public async Task AWidthWithoutAHeight_IsItsOwnError()
    {
        HttpResponseMessage response = await PostActions(
            Pointer("touch", """{"type":"pointerDown","button":0,"width":10}"""));

        (await MessageOf(response))
            .ShouldBe("\"width\" and \"height\" attributes need to be specified together");
    }

    [Test]
    public async Task AMousePointer_IsNotSupported()
    {
        HttpResponseMessage response = await PostActions(
            Pointer("mouse", """{"type":"pointerDown","button":0}"""));

        (await MessageOf(response))
            .ShouldBe("Currently only pen and touch pointer input source types are supported");
    }

    [Test]
    public async Task TwoConcurrentPens_AreRejected()
    {
        HttpResponseMessage response = await PostActions(
            """
            {"actions":[
              {"type":"pointer","id":"p1","parameters":{"pointerType":"pen"},"actions":[]},
              {"type":"pointer","id":"p2","parameters":{"pointerType":"pen"},"actions":[]}]}
            """);

        (await MessageOf(response))
            .ShouldBe("Currently only a single (non-concurrent) pen input is supported");
    }

    [Test]
    public async Task ASinglePen_IsNotRejectedAsMultiple()
    {
        // The control for the test above. "Reject every pen" would pass it and
        // be badly wrong.
        HttpResponseMessage response = await PostActions(
            Pointer("pen", """{"type":"pointerDown","button":0}"""));

        ((int)response.StatusCode).ShouldBe(200, "a single pen is valid and is performed");
    }

    [Test]
    public async Task AValidPayload_IsPerformed_AndSaysSo()
    {
        // This asserted 501 until 2026-08-11, when /actions started performing
        // what it validates. The old contract was "refuse rather than silently
        // accept"; the new one is "perform, and report the injector's answer".
        // Both refuse to report success for doing nothing - which is the rule
        // that actually matters, and the reason this test still exists.
        HttpResponseMessage response = await PostActions(
            Pointer("touch", """{"type":"pointerDown","button":0,"pressure":0.5}"""));

        ((int)response.StatusCode).ShouldBe(200);
        _injector.Received().Inject(Arg.Any<IReadOnlyList<SyntheticContact>>());
    }

    [Test]
    public async Task AnInjectorThatRefuses_IsReportedAsAFailure_NotAsSuccess()
    {
        // The control, and the rule the old 501 was protecting. If the system
        // will not accept the contact, the caller must be told - a driver that
        // answers status 0 for input that never happened is the defect this
        // project exists to fix.
        _injector.Inject(Arg.Any<IReadOnlyList<SyntheticContact>>()).Returns(false);

        HttpResponseMessage response = await PostActions(
            Pointer("touch", """{"type":"pointerDown","button":0}"""));

        ((int)response.StatusCode).ShouldBe(500, "a refused contact is not a success");
    }
}
