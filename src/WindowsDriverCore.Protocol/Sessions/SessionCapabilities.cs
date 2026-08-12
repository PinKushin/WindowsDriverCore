using System.Collections.Generic;
using System.Text.Json;
using WindowsDriverCore.Protocol.Errors;

namespace WindowsDriverCore.Protocol.Sessions;
/// <summary>Which protocol dialect a client is speaking.</summary>
/// <remarks>
/// <para>
/// <b>Decided once, by which key <c>POST /session</c> used, and never
/// renegotiated.</b> <c>desiredCapabilities</c> is JSON Wire; <c>capabilities</c>
/// with <c>alwaysMatch</c> is W3C. Nothing else about a request identifies the
/// client, and a session that changed dialect halfway would answer two clients
/// at once.
/// </para>
/// <para>
/// <b>Selenium 4 dropped JWP entirely</b>, so a current Selenium cannot drive
/// WinAppDriver at all — the largest cluster in its tracker, ~42 reactions
/// across #1610, #1839, #1997 and #1543. Serving both is a reason this driver
/// exists rather than a courtesy.
/// </para>
/// </remarks>
public enum ProtocolDialect
{
    /// <summary>JSON Wire Protocol, as WinAppDriver and Selenium 3 speak it.</summary>
    JsonWire,

    /// <summary>W3C WebDriver, as Selenium 4 speaks it.</summary>
    W3C,
}


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
/// <param name="Dialect">
/// Which protocol the client is speaking, decided by which key
/// <c>POST /session</c> used and fixed for the session's life.
/// </param>
public sealed record SessionCapabilities(
    string? App,
    string? AppArguments,
    string? AppWorkingDirectory,
    string? AppTopLevelWindow,
    IReadOnlyDictionary<string, string> Echo,
    ProtocolDialect Dialect = ProtocolDialect.JsonWire)
{
    /// <summary>The capability that requests a desktop-wide session.</summary>
    public const string DesktopApp = "Root";

    private const string BadCapabilitiesMessage =
        "Bad capabilities. Specify either app or appTopLevelWindow to create a session";

    private const string EmptyAppMessage = "Capability: app cannot be empty";

    private const string EmptyTopLevelWindowMessage =
        "Capability: appTopLevelWindow cannot be empty";

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
    /// <para>
    /// <b>BOTH SHAPES, and the one used decides the dialect.</b>
    /// <c>desiredCapabilities</c> is JSON Wire; <c>capabilities.alwaysMatch</c>
    /// is W3C. This previously read only the first, with the reasoning that
    /// WinAppDriver does not understand the second either — but matching the
    /// reference's LIMITATIONS is not the goal. Selenium 4 speaks only W3C and
    /// so cannot drive WinAppDriver at all, which is the most requested thing in
    /// its tracker, and serving it is a reason this driver exists.
    /// </para>
    /// <para>
    /// JWP is still the floor: a JWP client sees no change whatsoever, and the
    /// compatibility suite remains the scoreboard.
    /// </para>
    /// </remarks>
    public static CapabilityParseResult Parse(JsonElement body)
    {
        ProtocolDialect dialect = ProtocolDialect.JsonWire;

        if (!body.TryGetProperty("desiredCapabilities", out JsonElement desired) ||
            desired.ValueKind != JsonValueKind.Object)
        {
            // W3C nests them under capabilities.alwaysMatch. firstMatch is not
            // read: it offers ALTERNATIVES to negotiate between, and this driver
            // has exactly one platform to offer - so there is nothing to choose
            // and pretending to choose would be a lie the client acts on.
            if (body.TryGetProperty("capabilities", out JsonElement w3c) &&
                w3c.ValueKind == JsonValueKind.Object &&
                w3c.TryGetProperty("alwaysMatch", out JsonElement always) &&
                always.ValueKind == JsonValueKind.Object)
            {
                desired = always;
                dialect = ProtocolDialect.W3C;
            }
            else
            {
                return Rejected(BadCapabilitiesMessage);
            }
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

        if (topLevelWindow is not null && topLevelWindow.Length == 0)
        {
            return Rejected(EmptyTopLevelWindowMessage);
        }

        return new CapabilityParseResult(
            new SessionCapabilities(
                App: app,
                AppArguments: echo.GetValueOrDefault("appArguments"),
                AppWorkingDirectory: echo.GetValueOrDefault("appWorkingDir"),
                AppTopLevelWindow: topLevelWindow,
                Echo: echo,
                Dialect: dialect),
            Fault: null,
            Message: null);
    }

    private static CapabilityParseResult Rejected(string message) =>
        new(Capabilities: null, WebDriverFault.InvalidArgument, message);
}
