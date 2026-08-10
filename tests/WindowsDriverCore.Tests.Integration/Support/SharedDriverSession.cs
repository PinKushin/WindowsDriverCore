using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// One application, opened through the real server, shared by the whole run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real server process, not in-process hosting.</b>
/// <c>WebApplicationFactory</c> runs the pipeline inside the test process: no
/// executable, no socket, no argument parsing, no console. Every fixture that
/// shares this now exercises the thing that actually ships — and the run is
/// visible while it happens, which it never was before.
/// </para>
/// <para>
/// <b>No fixture knows a process name.</b> The session starts the application
/// and <c>DELETE /session</c> closes it. Killing by name was wrong in both
/// directions at once: on Windows 11 it destroyed the instance other fixtures
/// were using, and on Windows 10 it matched nothing at all, because the process
/// there is called <c>Calculator</c> rather than <c>CalculatorApp</c>.
/// </para>
/// <para>
/// <b>Fixtures still drive the automation layer directly.</b> This supplies a
/// window handle and nothing else, so what a test measures is unchanged — only
/// who owns the application. The handle comes from
/// <c>GET /session/{id}/window_handle</c>, which is the driver reporting what it
/// is addressing rather than the test guessing.
/// </para>
/// </remarks>
internal static class SharedDriverSession
{
    public const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private static readonly object Gate = new();
    private static DriverServer? _server;
    private static string? _sessionId;
    private static nint _window;

    /// <summary>The shared window, opening the application if needed.</summary>
    /// <returns>The window handle, or zero if the application is unavailable.</returns>
    public static nint Window()
    {
        lock (Gate)
        {
            // Liveness every time rather than a cached handle: a fixture that
            // destroys windows deliberately would otherwise leave a dead one for
            // whichever fixture ran next, and that failure moves when tests are
            // reordered.
            if (_window != 0 && AppLifetime.WindowExists(_window))
            {
                return _window;
            }

            _server ??= DriverServer.Start();
            if (_server is null)
            {
                return 0;
            }

            HttpResponseMessage created = _server.Client.PostAsJsonAsync(
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

    /// <summary>Ends the session, which closes the application, and stops the server.</summary>
    public static void Close()
    {
        lock (Gate)
        {
            if (_server is not null && _sessionId is not null)
            {
                // No result check: a session that will not delete is not
                // something a teardown can act on, and throwing here would
                // replace a real failure with this one.
                _server.Client
                    .DeleteAsync(new Uri($"/session/{_sessionId}", UriKind.Relative))
                    .GetAwaiter().GetResult().Dispose();
            }

            _sessionId = null;
            _window = 0;
            _server?.Dispose();
            _server = null;
        }
    }

    private static nint ReadWindowHandle()
    {
        HttpResponseMessage response = _server!.Client
            .GetAsync(new Uri($"/session/{_sessionId}/window_handle", UriKind.Relative))
            .GetAwaiter().GetResult();

        using JsonDocument body = JsonDocument.Parse(
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        string? handle = body.RootElement.GetProperty("value").GetString();

        // "0x00551120" — the prefix comes off before parsing as hex.
        return handle is not null && handle.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? nint.Parse(handle[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : 0;
    }
}
