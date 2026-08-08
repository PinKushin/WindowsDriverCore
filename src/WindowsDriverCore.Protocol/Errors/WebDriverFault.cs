namespace WindowsDriverCore.Protocol.Errors;

/// <summary>
/// One failure condition in the JSON Wire Protocol, as WinAppDriver reports it.
/// </summary>
/// <param name="Status">
/// The numeric <c>status</c> field. Note this is WinAppDriver's numbering, which
/// differs from Selenium's <c>WebDriverResult</c> enum for two values — see
/// <see cref="InvalidArgument"/> and <see cref="InvalidSessionId"/>.
/// </param>
/// <param name="Error">
/// The <c>value.error</c> string. Spacing and casing are load-bearing: the client
/// derives the suffix it appends to some messages by removing spaces from this.
/// </param>
/// <param name="HttpStatus">The HTTP status code sent alongside the body.</param>
/// <remarks>
/// Every value here is anchored to a recording of a real WinAppDriver response in
/// <c>tests/WindowsDriverCore.Tests.Protocol/Recordings</c>. Nothing in this table
/// is derived from the specification, because doing that produced two confident
/// and wrong conclusions before the recordings existed.
/// </remarks>
public sealed record WebDriverFault(int Status, string Error, int HttpStatus)
{
    /// <summary>A find returned nothing, or an element id is not known.</summary>
    /// <remarks>
    /// Also what WinAppDriver returns for a malformed tag name, rather than an
    /// invalid-selector fault. Measured, not assumed.
    /// </remarks>
    public static WebDriverFault NoSuchElement { get; } =
        new(7, "no such element", 404);

    /// <summary>The route is not one this server serves.</summary>
    public static WebDriverFault UnknownCommand { get; } =
        new(9, "unknown command", 404);

    /// <summary>A failure with no more specific mapping.</summary>
    public static WebDriverFault UnknownError { get; } =
        new(13, "unknown error", 500);

    /// <summary>An XPath expression could not be evaluated.</summary>
    /// <remarks>
    /// The error string really is spaced and title-cased like this. The client
    /// appends it to the message with spaces stripped, producing the
    /// <c>(XPathLookupError)</c> suffix the compatibility suite asserts on — so
    /// the server must not send that suffix itself.
    /// </remarks>
    public static WebDriverFault XPathLookupError { get; } =
        new(19, "XPath Lookup Error", 500);

    /// <summary>The requested window does not exist or is no longer valid.</summary>
    /// <remarks>HTTP 400, not 404, despite being a "not found" condition.</remarks>
    public static WebDriverFault NoSuchWindow { get; } =
        new(23, "no such window", 400);

    /// <summary>A parameter or capability was present but unusable.</summary>
    /// <remarks>
    /// WinAppDriver uses 100. Selenium's <c>WebDriverResult</c> enum has
    /// <c>InvalidArgument = 61</c>, which is what a reading of the client would
    /// suggest and is wrong on the wire.
    /// </remarks>
    public static WebDriverFault InvalidArgument { get; } =
        new(100, "invalid argument", 400);

    /// <summary>No session exists with the requested id.</summary>
    /// <remarks>
    /// WinAppDriver uses 101. Selenium's enum has <c>NoSuchDriver = 6</c>.
    /// </remarks>
    public static WebDriverFault InvalidSessionId { get; } =
        new(101, "invalid session id", 404);
}
