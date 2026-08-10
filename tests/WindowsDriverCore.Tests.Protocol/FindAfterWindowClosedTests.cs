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
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Protocol;

/// <summary>
/// Finding an element after the application's window has closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Worth 24 tests on the compatibility suite.</b> Measured 2026-08-10 in the
/// Windows 10 guest, the suite expected
/// <c>Currently selected window has been closed</c> and got
/// <c>An element could not be located on the page using the given search
/// parameters.</c>
/// </para>
/// <para>
/// That is a wrong fault, not a wrong string. "Not found" tells a client to look
/// again with a better locator; "the window is gone" tells it the session is
/// over. Answering the first when the second is true sends every client down the
/// wrong path, and a retry loop will keep searching a window that no longer
/// exists until it times out.
/// </para>
/// <para>
/// The read path already got this right. Only find did not, because an empty
/// result and a dead window are the same observation to a search that never asks
/// whether the window is still there.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FindAfterWindowClosedTests : IDisposable
{
    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;
    private IElementFinder _finder = null!;
    private IWindowLocator _windows = null!;

    [SetUp]
    public void StartServer()
    {
        IApplicationLauncher launcher = Substitute.For<IApplicationLauncher>();
        launcher.Launch(Arg.Any<ApplicationTarget>())
            .Returns(LaunchResult.Success(new LaunchedApplication(4242, 0x1234)));

        _finder = Substitute.For<IElementFinder>();
        _windows = Substitute.For<IWindowLocator>();

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

    private async Task<string> NewSession()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } });

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private Task<HttpResponseMessage> FindOne(string sessionId) =>
        // Raw JSON: the locator key is "using", which is a C# keyword and cannot
        // be an anonymous-type member name.
        _client.PostAsync(
            new Uri($"/session/{sessionId}/element", UriKind.Relative),
            new StringContent(
                """{"using":"accessibility id","value":"num5Button"}""",
                System.Text.Encoding.UTF8,
                "application/json"));

    [Test]
    public async Task WhenTheWindowIsGone_FindReportsTheWindowClosed_NotAMissingElement()
    {
        string sessionId = await NewSession();

        // The condition that separates the two hypotheses: nothing found AND the
        // window no longer exists. With the window alive, "not found" is correct
        // and both a right and a wrong implementation agree.
        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(new FindResult([], FindFailure.None));
        _windows.Exists(Arg.Any<nint>()).Returns(false);

        HttpResponseMessage response = await FindOne(sessionId);

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("Currently selected window has been closed");
        body.GetProperty("status").GetInt32().ShouldBe(23);
    }

    [Test]
    public async Task WhenTheWindowIsAlive_NothingFoundIsStillNoSuchElement()
    {
        // The control. Without it, "always report the window closed" would pass
        // the test above and be badly wrong.
        string sessionId = await NewSession();

        _finder.FindFirst(Arg.Any<SearchScope>(), Arg.Any<LocatorKind>(), Arg.Any<string>())
            .Returns(new FindResult([], FindFailure.None));
        _windows.Exists(Arg.Any<nint>()).Returns(true);

        HttpResponseMessage response = await FindOne(sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("value").GetProperty("message").GetString()
            .ShouldBe("An element could not be located on the page using the given search parameters.");
    }
}
