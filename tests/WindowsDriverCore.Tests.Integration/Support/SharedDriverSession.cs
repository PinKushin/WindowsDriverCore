using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// One application, launched through the driver, shared by the whole run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaces SharedCalculator, which launched the application directly.</b>
/// Doing it through the driver means the lifecycle is the driver's: the session
/// starts the application and <c>DELETE /session</c> closes it, so no fixture
/// needs to know a process name or own a teardown. Killing by name was wrong on
/// Windows 11 (it destroyed the instance other fixtures were using) and silently
/// did nothing on Windows 10 (where the process is <c>Calculator</c>, not
/// <c>CalculatorApp</c>).
/// </para>
/// <para>
/// <b>Fixtures still drive the automation layer directly.</b> This supplies a
/// window handle and nothing else — what a test measures is unchanged, only who
/// owns the application. The handle comes back from
/// <c>GET /session/{id}/window_handle</c>, which is the driver reporting what it
/// is addressing rather than the test guessing.
/// </para>
/// </remarks>
internal static class SharedDriverSession
{
    public const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private static readonly object Gate = new();
    private static WebApplicationFactory<WindowsDriverCore.Host.Program>? _factory;
    private static HttpClient? _client;
    private static string? _sessionId;
    private static nint _window;

    /// <summary>The shared window, opening the application if needed.</summary>
    /// <returns>The window handle, or zero if the application is unavailable.</returns>
    public static nint Window()
    {
        lock (Gate)
        {
            if (_window != 0 && AppLifetime.WindowExists(_window))
            {
                return _window;
            }

            _factory ??= new WebApplicationFactory<WindowsDriverCore.Host.Program>();
            _client ??= _factory.CreateClient();

            HttpResponseMessage created = _client.PostAsJsonAsync(
                new Uri("/session", UriKind.Relative),
                new { desiredCapabilities = new { app = CalculatorAumid } })
                .GetAwaiter().GetResult();

            if (!created.IsSuccessStatusCode)
            {
                return 0;
            }

            using JsonDocument body = JsonDocument.Parse(
                created.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            _sessionId = body.RootElement.GetProperty("sessionId").GetString();

            _window = ReadWindowHandle();
            return _window;
        }
    }

    /// <summary>Ends the session, which closes the application.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            if (_client is not null && _sessionId is not null)
            {
                // No result check: a session that will not delete is not
                // something a test teardown can do anything about, and throwing
                // here would replace a real failure with this one.
                _client.DeleteAsync(new Uri($"/session/{_sessionId}", UriKind.Relative))
                    .GetAwaiter().GetResult().Dispose();
            }

            _sessionId = null;
            _window = 0;
            _client?.Dispose();
            _client = null;
            _factory?.Dispose();
            _factory = null;
        }
    }

    private static nint ReadWindowHandle()
    {
        HttpResponseMessage response = _client!
            .GetAsync(new Uri($"/session/{_sessionId}/window_handle", UriKind.Relative))
            .GetAwaiter().GetResult();

        using JsonDocument body = JsonDocument.Parse(
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        string? handle = body.RootElement.GetProperty("value").GetString();

        // "0x00551120" — the 0x prefix has to come off before parsing as hex.
        return handle is not null && handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? nint.Parse(handle[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0;
    }
}
