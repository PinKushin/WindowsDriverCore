using System.Collections.Generic;
using System.Text.Json;
using WindowsDriverCore.Protocol.Errors;

namespace WindowsDriverCore.Protocol.Sessions;

/// <summary>
/// The outcome of reading a session-creation request body.
/// </summary>
/// <param name="Capabilities">The parsed capabilities, or null when rejected.</param>
/// <param name="Fault">The fault to report, or null when accepted.</param>
/// <param name="Message">
/// The message belonging to <paramref name="Fault"/>. Separate because two
/// different rejections share <see cref="WebDriverFault.InvalidArgument"/> but
/// carry different messages, and clients match on the message.
/// </param>
public sealed record CapabilityParseResult(
    SessionCapabilities? Capabilities,
    WebDriverFault? Fault,
    string? Message);

/// <summary>
/// Capabilities supplied on <c>POST /session</c>.
/// </summary>
/// <param name="App">
/// An application id, a full executable path, or <c>Root</c> for the desktop.
/// </param>
/// <param name="AppArguments">Launch arguments, if any.</param>
/// <param name="AppWorkingDirectory">Working directory, classic apps only.</param>
/// <param name="AppTopLevelWindow">
/// A hex window handle to attach to instead of launching anything.
/// </param>
/// <param name="Echo">
/// The capabilities to echo back on success — recognised ones only.
/// </param>
public sealed record SessionCapabilities(
    string? App,
    string? AppArguments,
    string? AppWorkingDirectory,
    string? AppTopLevelWindow,
    IReadOnlyDictionary<string, string> Echo)
{
    /// <summary>The capability that requests a desktop-wide session.</summary>
    public const string DesktopApp = "Root";

    private const string BadCapabilitiesMessage =
        "Bad capabilities. Specify either app or appTopLevelWindow to create a session";

    private const string EmptyAppMessage = "Capability: app cannot be empty";

    /// <summary>
    /// Capabilities WinAppDriver recognises. Anything else is dropped from the
    /// echo, which was measured: sending <c>deviceName</c> alongside <c>app</c>
    /// echoed back only <c>app</c>.
    /// </summary>
    private static readonly string[] Recognised =
    [
        "app", "appArguments", "appTopLevelWindow", "appWorkingDir",
        "platformName", "platformVersion",
    ];

    /// <summary>Whether this session drives the whole desktop rather than one app.</summary>
    public bool IsDesktopSession =>
        string.Equals(App, DesktopApp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a session-creation request body.
    /// </summary>
    /// <param name="body">The parsed request body.</param>
    /// <returns>The capabilities, or the fault to report.</returns>
    /// <remarks>
    /// Only <c>desiredCapabilities</c> is read. The W3C
    /// <c>capabilities.alwaysMatch</c> shape is deliberately not understood,
    /// because WinAppDriver does not understand it either — accepting it would
    /// create sessions the real server rejects, so code written against this
    /// driver would fail against WinAppDriver.
    /// </remarks>
    public static CapabilityParseResult Parse(JsonElement body)
    {
        if (!body.TryGetProperty("desiredCapabilities", out JsonElement desired) ||
            desired.ValueKind != JsonValueKind.Object)
        {
            return Rejected(BadCapabilitiesMessage);
        }

        Dictionary<string, string> echo = new(StringComparer.Ordinal);
        foreach (string name in Recognised)
        {
            if (desired.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
            {
                echo[name] = value.GetString() ?? string.Empty;
            }
        }

        string? app = echo.GetValueOrDefault("app");
        string? topLevelWindow = echo.GetValueOrDefault("appTopLevelWindow");

        // Exclusive, not preferential. Supplying both is rejected with the same
        // message as supplying neither — measured, and the opposite of what a
        // "prefer app" reading would produce.
        if ((app is null) == (topLevelWindow is null))
        {
            return Rejected(BadCapabilitiesMessage);
        }

        if (app is not null && app.Length == 0)
        {
            return Rejected(EmptyAppMessage);
        }

        return new CapabilityParseResult(
            new SessionCapabilities(
                App: app,
                AppArguments: echo.GetValueOrDefault("appArguments"),
                AppWorkingDirectory: echo.GetValueOrDefault("appWorkingDir"),
                AppTopLevelWindow: topLevelWindow,
                Echo: echo),
            Fault: null,
            Message: null);
    }

    private static CapabilityParseResult Rejected(string message) =>
        new(Capabilities: null, WebDriverFault.InvalidArgument, message);
}
