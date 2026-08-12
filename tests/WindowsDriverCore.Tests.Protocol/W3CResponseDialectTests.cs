using System;
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
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// A W3C client gets W3C bodies, and a JSON Wire client gets exactly what it
/// always got.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selenium 4 speaks W3C only</b>, which is why it cannot drive WinAppDriver
/// at all — and why serving it is a feature of this driver rather than a
/// courtesy. Three things differ, and each one alone is enough to make a client
/// fail: the envelope shape, the element key, and the error being a string.
/// </para>
/// <para>
/// <b>Every W3C test here is paired with a JSON Wire control.</b> That is not
/// symmetry for its own sake. The compatibility suite is a Selenium 3 client and
/// is the only measure of whether this driver works; a translation layer that
/// changes what IT sees costs suite tests, and the score would move without
/// anything naming the cause. One subject shows the translation happened, the
/// other shows it happened only to the client that asked for it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class W3CResponseDialectTests : IDisposable
{
    private const string W3CElementKey = "element-6066-11e4-a52e-4f735466cecf";
    private const string TheApp = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const nint TheWindow = 0x1234;
    private const string TheElementId = "42.19466560.4.73";
    private const string TheOtherElementId = "42.19466560.4.99";

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IApplicationLauncher _launcher = null!;
    private IWindowLocator _windows = null!;
    private IElementFinder _finder = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _launcher = Substitute.For<IApplicationLauncher>();
        _windows = Substitute.For<IWindowLocator>();
        _finder = Substitute.For<IElementFinder>();

        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton(_launcher);
                services.AddSingleton(_windows);
                services.AddSingleton(_finder);
            }));

        _client = _factory.CreateClient();
    }

    [SetUp]
    public void ArrangeDefaults()
    {
        _launcher.ClearReceivedCalls();
        _windows.ClearReceivedCalls();
        _finder.ClearReceivedCalls();

        _launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, TheWindow)));

        _windows.Exists(Arg.Any<nint>()).Returns(true);

        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([TheElementId]));
        _finder.FindAll(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([TheElementId, TheOtherElementId]));
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<JsonDocument> Post(string path, object body) =>
        JsonDocument.Parse(await (await _client
            .PostAsJsonAsync(new Uri(path, UriKind.Relative), body)
            .ConfigureAwait(false))
            .Content.ReadAsStringAsync().ConfigureAwait(false));

    private async Task<JsonDocument> Delete(string path) =>
        JsonDocument.Parse(await (await _client
            .DeleteAsync(new Uri(path, UriKind.Relative)).ConfigureAwait(false))
            .Content.ReadAsStringAsync().ConfigureAwait(false));

    /// <summary>Creates a session the way Selenium 4 does.</summary>
    private async Task<(string Id, JsonDocument Body)> NewW3CSession()
    {
        JsonDocument body = await Post(
            "/session",
            new { capabilities = new { alwaysMatch = new { app = TheApp } } }).ConfigureAwait(false);

        return (body.RootElement.GetProperty("value").GetProperty("sessionId").GetString()!, body);
    }

    /// <summary>Creates a session the way the compatibility suite does.</summary>
    private async Task<(string Id, JsonDocument Body)> NewJsonWireSession()
    {
        JsonDocument body = await Post(
            "/session",
            new { desiredCapabilities = new { app = TheApp } }).ConfigureAwait(false);

        return (body.RootElement.GetProperty("sessionId").GetString()!, body);
    }

    [Test]
    public async Task AW3CSessionCreation_CarriesTheIdAndCapabilitiesInsideValue()
    {
        (string id, JsonDocument body) = await NewW3CSession();

        // The id is INSIDE value. A Selenium 4 client reads it from there and
        // from nowhere else, so a JWP-shaped reply leaves it with no session id
        // at all - it fails on the next command, not on this one.
        JsonElement value = body.RootElement.GetProperty("value");
        value.GetProperty("sessionId").GetString().ShouldBe(id);
        value.GetProperty("capabilities").GetProperty("app").GetString().ShouldBe(TheApp);

        // And the JSON Wire members are GONE, not merely duplicated. A body
        // carrying both would pass every positive assertion above while telling
        // a strict client it is talking to a driver that cannot make up its mind.
        body.RootElement.TryGetProperty("status", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("sessionId", out _).ShouldBeFalse();
    }

    [Test]
    public async Task AJsonWireSessionCreation_IsUnchanged()
    {
        (string id, JsonDocument body) = await NewJsonWireSession();

        // The control. This is the shape the compatibility suite reads, and the
        // dialect work must not have touched it.
        id.ShouldNotBeNullOrEmpty();
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        body.RootElement.GetProperty("value").GetProperty("app").GetString().ShouldBe(TheApp);
        body.RootElement.GetProperty("value").TryGetProperty("sessionId", out _).ShouldBeFalse();
    }

    [Test]
    public async Task AW3CFind_KeysTheElementByUuid()
    {
        (string id, _) = await NewW3CSession();

        JsonDocument body = await Post(
            $"/session/{id}/element",
            new { @using = "accessibility id", value = "num5Button" });

        JsonElement element = body.RootElement.GetProperty("value");
        element.GetProperty(W3CElementKey).GetString().ShouldBe(TheElementId);

        // Not both keys. Selenium 4 would work either way, so an assertion that
        // only checked for the uuid could not tell "translated" from "added a
        // second key" - and the second key is what a JSON Wire client would then
        // silently keep reading after a change meant to move it off.
        element.TryGetProperty("ELEMENT", out _).ShouldBeFalse();
    }

    [Test]
    public async Task AJsonWireFind_StillKeysTheElementByELEMENT()
    {
        (string id, _) = await NewJsonWireSession();

        JsonDocument body = await Post(
            $"/session/{id}/element",
            new { @using = "accessibility id", value = "num5Button" });

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        body.RootElement.GetProperty("value").GetProperty("ELEMENT").GetString()
            .ShouldBe(TheElementId);
        body.RootElement.GetProperty("value").TryGetProperty(W3CElementKey, out _)
            .ShouldBeFalse();
    }

    [Test]
    public async Task AW3CFindAll_RekeysEveryElement_NotJustTheFirst()
    {
        (string id, _) = await NewW3CSession();

        JsonDocument body = await Post(
            $"/session/{id}/elements",
            new { @using = "accessibility id", value = "num5Button" });

        JsonElement elements = body.RootElement.GetProperty("value");
        elements.GetArrayLength().ShouldBe(2);

        // Two, deliberately. Translating a collection by rebuilding only its
        // head is a real mistake and a one-element array cannot see it.
        elements[0].GetProperty(W3CElementKey).GetString().ShouldBe(TheElementId);
        elements[1].GetProperty(W3CElementKey).GetString().ShouldBe(TheOtherElementId);
    }

    [Test]
    public async Task AW3CFault_NamesTheErrorAsAStringAndCarriesAStackTrace()
    {
        (string id, _) = await NewW3CSession();

        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([]));

        JsonDocument body = await Post(
            $"/session/{id}/element",
            new { @using = "accessibility id", value = "nothingHere" });

        JsonElement value = body.RootElement.GetProperty("value");

        // The STRING is what Selenium 4 maps to an exception type. Sent the
        // integer, every failure this driver reports becomes "unknown error" and
        // the client's own error handling stops working.
        value.GetProperty("error").GetString().ShouldBe("no such element");
        value.GetProperty("message").GetString().ShouldNotBeNullOrEmpty();
        value.TryGetProperty("stacktrace", out _).ShouldBeTrue();

        body.RootElement.TryGetProperty("status", out _).ShouldBeFalse();
    }

    [Test]
    public async Task AJsonWireFault_StillCarriesTheNumericStatus()
    {
        (string id, _) = await NewJsonWireSession();

        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(FindResult.Matched([]));

        JsonDocument body = await Post(
            $"/session/{id}/element",
            new { @using = "accessibility id", value = "nothingHere" });

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(7);
        body.RootElement.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe("no such element");
        body.RootElement.GetProperty("value").TryGetProperty("stacktrace", out _)
            .ShouldBeFalse();
    }

    [Test]
    public async Task AW3CCapabilityRejection_IsRefusedInW3CToo()
    {
        // No app and no appTopLevelWindow. The refusal happens before a session
        // exists, so the dialect has to come from the REQUEST - this is the one
        // case where reading it off the session is not an option, and getting it
        // wrong hands a Selenium 4 client a body it cannot parse for the one
        // message that would have told it what it did wrong.
        JsonDocument body = await Post(
            "/session",
            new { capabilities = new { alwaysMatch = new { platformName = "windows" } } });

        body.RootElement.GetProperty("value").GetProperty("error").GetString()
            .ShouldBe("invalid argument");
        body.RootElement.TryGetProperty("status", out _).ShouldBeFalse();
    }

    [Test]
    public async Task AW3CVoidCommand_CarriesAnExplicitNullValue()
    {
        (string id, _) = await NewW3CSession();

        JsonDocument body = await Delete($"/session/{id}");

        // W3C requires the member; JSON Wire omits it. A client that reads
        // response["value"] unconditionally throws on the missing key rather
        // than seeing a null.
        body.RootElement.TryGetProperty("value", out JsonElement value).ShouldBeTrue();
        value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task AJsonWireVoidCommand_StillOmitsValueEntirely()
    {
        (string id, _) = await NewJsonWireSession();

        JsonDocument body = await Delete($"/session/{id}");

        body.RootElement.GetProperty("status").GetInt32().ShouldBe(0);
        body.RootElement.TryGetProperty("value", out _).ShouldBeFalse();
    }
}
