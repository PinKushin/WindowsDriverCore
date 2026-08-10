using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Shouldly;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A session's whole life, over HTTP, against a real application.
/// </summary>
/// <remarks>
/// <para>
/// <b>The only integration test that goes over HTTP</b>, and deliberately so.
/// Every other fixture here drives the automation layer directly because that is
/// what it is measuring. This one measures the thing none of them can: that
/// creating a session really starts an application and deleting it really ends
/// it.
/// </para>
/// <para>
/// The protocol-level test for this substitutes the terminator, so it proves the
/// wiring and nothing about whether a process dies. That gap is exactly how a
/// <c>Kill(entireProcessTree: true)</c> reached the Windows 10 guest and cost 5
/// tests before anyone noticed what it could take down with it.
/// </para>
/// <para>
/// <b>Process names are matched loosely on purpose.</b> Windows 10 names
/// Calculator's process <c>Calculator</c> and Windows 11 names it
/// <c>CalculatorApp</c>. Matching one exactly is what made an earlier probe
/// count zero on both sides of the measurement and report a verdict anyway.
/// </para>
/// </remarks>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class SessionLifecycleTests : IDisposable
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private WebApplicationFactory<WindowsDriverCore.Host.Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void StartServer()
    {
        _factory = new WebApplicationFactory<WindowsDriverCore.Host.Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void StopServer() => Dispose();

    /// <summary>Disposes the in-memory server.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private static int CalculatorProcesses() =>
        Process.GetProcesses()
            .Count(process =>
            {
                try
                {
                    return process.ProcessName.Contains("alculator", StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    process.Dispose();
                }
            });

    [Test]
    public async Task CreatingASessionStartsTheApplication_AndDeletingItEndsIt()
    {
        int before = CalculatorProcesses();

        HttpResponseMessage created = await _client.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app = CalculatorAumid } });

        if (!created.IsSuccessStatusCode)
        {
            Assert.Ignore($"Calculator is not available: {await created.Content.ReadAsStringAsync()}");
        }

        JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string sessionId = body.RootElement.GetProperty("sessionId").GetString()!;

        int during = CalculatorProcesses();

        // The guard the first quit probe lacked. With nothing launched, "it was
        // closed" and "it was never there" predict the same observation and the
        // assertion below would pass for the wrong reason.
        during.ShouldBeGreaterThan(
            before, "creating a session must actually start the application");

        HttpResponseMessage deleted = await _client.DeleteAsync(
            new Uri($"/session/{sessionId}", UriKind.Relative));
        deleted.IsSuccessStatusCode.ShouldBeTrue();

        // The process does not vanish the instant the handler returns.
        for (int attempt = 0; attempt < 40 && CalculatorProcesses() >= during; attempt++)
        {
            await Task.Delay(250);
        }

        CalculatorProcesses().ShouldBeLessThan(
            during, "deleting the session must close the application it started");
    }
}
