using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace WindowsDriverCore.Tests.Integration;

/// <summary>
/// A minimal client for real WinAppDriver, used only as an experimental control.
/// </summary>
/// <remarks>
/// Deliberately not the Appium client. That library sits between the test and
/// the server and has its own defects — it crashes in <c>SendKeys</c>, it
/// mishandles <c>JObject</c> results — so a difference observed through it could
/// be the client rather than the driver. Raw HTTP keeps the measurement on the
/// thing being measured.
/// </remarks>
public sealed class WinAppDriverClient : IDisposable
{
    private const string DefaultInstallPath =
        @"C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe";

    private readonly HttpClient _http;
    private Process? _server;
    private string? _sessionId;

    /// <summary>Creates a client for a WinAppDriver on the given port.</summary>
    /// <param name="port">The port to talk to and, if started here, to listen on.</param>
    public WinAppDriverClient(int port)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Whether WinAppDriver is installed on this machine.</summary>
    public static bool IsInstalled => File.Exists(DefaultInstallPath);

    /// <summary>Starts the server and waits for it to answer.</summary>
    /// <param name="port">The port to listen on.</param>
    /// <returns>True when it is answering.</returns>
    public async Task<bool> StartAsync(int port)
    {
        if (!IsInstalled)
        {
            return false;
        }

        _server = Process.Start(new ProcessStartInfo
        {
            FileName = DefaultInstallPath,
            Arguments = $"127.0.0.1 {port}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // Poll for readiness rather than sleeping a guessed interval: the loop
        // ends the moment it answers.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await _http.GetAsync(
                    new Uri("/status", UriKind.Relative)).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Creates a session against an application.</summary>
    /// <param name="app">The app id or path.</param>
    /// <returns>True when the session was created.</returns>
    public async Task<bool> CreateSessionAsync(string app)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            new Uri("/session", UriKind.Relative),
            new { desiredCapabilities = new { app } }).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        _sessionId = body.RootElement.GetProperty("sessionId").GetString();
        return _sessionId is not null;
    }

    /// <summary>How many elements a search matched.</summary>
    /// <param name="using">Locator strategy.</param>
    /// <param name="value">Locator value.</param>
    /// <returns>
    /// The number of matches, or -1 when the request itself failed. Distinguished
    /// because "the search ran and matched nothing" and "the search could not
    /// run" are different observations, and conflating them would let a transport
    /// error masquerade as the defect being hunted.
    /// </returns>
    public async Task<int> CountElementsAsync(string @using, string value)
    {
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(
                new Uri($"/session/{_sessionId}/elements", UriKind.Relative),
                new { @using, value }).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return -1;
            }

            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(false));

            return body.RootElement.GetProperty("value").GetArrayLength();
        }
        catch (HttpRequestException)
        {
            return -1;
        }
        catch (TaskCanceledException)
        {
            return -1;
        }
    }

    /// <summary>Clicks an element found by automation id, ignoring failure.</summary>
    /// <param name="automationId">The automation id.</param>
    /// <returns>A task that completes when the attempt is over.</returns>
    public async Task ClickAsync(string automationId)
    {
        using HttpResponseMessage found = await _http.PostAsJsonAsync(
            new Uri($"/session/{_sessionId}/element", UriKind.Relative),
            new { @using = "accessibility id", value = automationId }).ConfigureAwait(false);

        if (!found.IsSuccessStatusCode)
        {
            return;
        }

        using JsonDocument body = JsonDocument.Parse(
            await found.Content.ReadAsStringAsync().ConfigureAwait(false));

        string? elementId = body.RootElement.GetProperty("value").GetProperty("ELEMENT").GetString();
        if (elementId is null)
        {
            return;
        }

        using HttpResponseMessage clicked = await _http.PostAsJsonAsync(
            new Uri($"/session/{_sessionId}/element/{elementId}/click", UriKind.Relative),
            new { }).ConfigureAwait(false);

        clicked.Dispose();
    }

    /// <summary>Stops the session and the server.</summary>
    public void Dispose()
    {
        if (_sessionId is not null)
        {
            try
            {
                _http.DeleteAsync(new Uri($"/session/{_sessionId}", UriKind.Relative))
                    .GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
                // Server already gone; nothing to clean up.
            }
        }

        _http.Dispose();

        if (_server is not null)
        {
            try
            {
                _server.Kill(entireProcessTree: true);
                _server.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            _server.Dispose();
        }
    }
}
