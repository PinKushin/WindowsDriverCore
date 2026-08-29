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
using WindowsDriverCore.Automation;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// The alert commands, in both dialects.
/// </summary>
/// <remarks>
/// <para>
/// <b>WinAppDriver serves none of these</b> — measured 2026-08-29, all six
/// spellings answer 404 there — so nothing about this can be checked against the
/// reference or against the compatibility suite, which has no alert test at all.
/// These assertions are the whole safety net.
/// </para>
/// <para>
/// What is under test here is the ROUTE: that both dialects reach the same
/// handler, that the outcomes map onto the right faults, and that the absence of
/// a dialog is reported as its own thing. Whether a real WinUI
/// <c>ContentDialog</c> is found is a claim about UIA and belongs to the
/// integration suite and the guest.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AlertRouteTests : IDisposable
{
    private const nint Handle = 0x6001;

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IAlertInspector _alerts = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, Handle)));

        IWindowLocator windows = Substitute.For<IWindowLocator>();
        windows.Exists(Arg.Any<nint>()).Returns(true);
        windows.WaitForInputProcessed(Arg.Any<nint>()).Returns(true);

        _alerts = Substitute.For<IAlertInspector>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(launcher);
                services.AddSingleton(windows);
                services.AddSingleton(_alerts);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void Rearm() => _alerts.ClearReceivedCalls();

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>Both dialects read the alert's text.</summary>
    /// <remarks>
    /// JSON Wire is <c>/alert_text</c>; W3C nests it under <c>/alert/</c>. One
    /// handler behind both, because two implementations of one question is how
    /// WinAppDriver's own XPath singular and plural drifted apart.
    /// </remarks>
    [TestCase("alert_text", TestName = "AlertText_JsonWireSpelling")]
    [TestCase("alert/text", TestName = "AlertText_W3CSpelling")]
    public async Task AlertText_IsServedUnderBothSpellings(string path)
    {
        _alerts.Text(Handle).Returns(ElementRead.Success("Do you want to save changes?"));

        string session = await NewSession();

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{session}/{path}", UriKind.Relative));

        ((int)response.StatusCode).ShouldBe(200);

        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("value").GetString()
            .ShouldBe("Do you want to save changes?");
    }

    /// <summary>Accept and dismiss reach the right half of the inspector.</summary>
    /// <remarks>
    /// <b>Both directions asserted, and the OTHER one asserted absent.</b> A
    /// handler wired to the wrong method presses "Don't Save" when the caller
    /// asked to accept — the two are one word apart in the route table and a
    /// world apart in effect, and only a DidNotReceive tells them apart.
    /// </remarks>
    [TestCase("accept_alert", TestName = "Accept_JsonWireSpelling")]
    [TestCase("alert/accept", TestName = "Accept_W3CSpelling")]
    public async Task Accept_PressesTheAffirmativeButton(string path)
    {
        _alerts.Accept(Handle).Returns(ElementAction.Performed("accept via Invoke"));

        string session = await NewSession();

        ((int)(await Post($"/session/{session}/{path}")).StatusCode).ShouldBe(200);

        _alerts.Received(1).Accept(Handle);
        _alerts.DidNotReceive().Dismiss(Arg.Any<nint>());
    }

    /// <summary>And dismiss is the other one.</summary>
    [TestCase("dismiss_alert", TestName = "Dismiss_JsonWireSpelling")]
    [TestCase("alert/dismiss", TestName = "Dismiss_W3CSpelling")]
    public async Task Dismiss_PressesTheNegativeButton(string path)
    {
        _alerts.Dismiss(Handle).Returns(ElementAction.Performed("dismiss via Invoke"));

        string session = await NewSession();

        ((int)(await Post($"/session/{session}/{path}")).StatusCode).ShouldBe(200);

        _alerts.Received(1).Dismiss(Handle);
        _alerts.DidNotReceive().Accept(Arg.Any<nint>());
    }

    /// <summary>No dialog is "no such alert", not "no such element".</summary>
    /// <remarks>
    /// <b>The fault name is the point.</b> Selenium maps status 27 to
    /// <c>NoAlertPresentException</c>, and a test catching that is asking a
    /// different question from one catching a missing element. Answering the
    /// wrong fault means a client's <c>catch</c> does not fire and the failure
    /// surfaces somewhere else entirely.
    /// </remarks>
    [Test]
    public async Task WithNoDialogOpen_TheFaultIsNoSuchAlert()
    {
        _alerts.Text(Handle).Returns(ElementRead.Failed<string>(ElementReadOutcome.NotFound));

        string session = await NewSession();

        HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/session/{session}/alert_text", UriKind.Relative));

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        body.GetProperty("status").GetInt32().ShouldBe(27, "no such alert, not no such element");
    }

    /// <summary>A dialog whose buttons are unrecognised is its own failure.</summary>
    /// <remarks>
    /// <b>The distinction that saves an investigation.</b> "No alert" and "an
    /// alert whose buttons I do not recognise" need different fixes — the first
    /// is a test that ran too early, the second is a dialog this driver cannot
    /// drive. Reporting the second as the first sends the reader hunting a race
    /// that is not there, which is a mistake this project has made before.
    /// </remarks>
    [Test]
    public async Task ADialogWithNoRecognisedButton_IsNotReportedAsNoAlert()
    {
        _alerts.Accept(Handle)
            .Returns(ElementAction.Failed(ElementActionOutcome.NotInteractable));

        string session = await NewSession();

        JsonElement body = JsonDocument
            .Parse(await (await Post($"/session/{session}/accept_alert")).Content.ReadAsStringAsync())
            .RootElement;

        body.GetProperty("status").GetInt32().ShouldNotBe(27, "the dialog IS there");

        (body.GetProperty("value").GetProperty("message").GetString() ?? string.Empty)
            .ShouldContain("no button");
    }

    /// <summary>Typing into an alert is not served, and says so.</summary>
    /// <remarks>
    /// <b>Deliberate.</b> Both dialects define <c>POST /alert_text</c> for a
    /// prompt's input field. A Windows message box has no input field, and a
    /// dialog with several has no canonical one — so picking one would type into
    /// a field the caller never named. A client that wants that has
    /// <c>/element</c> and can name it. The unknown-command fallback is the
    /// honest answer; silently accepting would not be.
    /// </remarks>
    [Test]
    public async Task TypingIntoAnAlert_IsNotServed()
    {
        string session = await NewSession();

        HttpResponseMessage response = await Post(
            $"/session/{session}/alert_text", """{"text":"typed"}""");

        (await response.Content.ReadAsStringAsync())
            .ShouldContain("Command not recognized");
    }

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        return JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> Post(string path, string json = "{}") =>
        _client.PostAsync(
            new Uri(path, UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"));
}
