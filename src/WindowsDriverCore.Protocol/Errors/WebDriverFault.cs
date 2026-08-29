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

    /// <summary>
    /// The element was found once but has since been removed from the UIA tree.
    /// </summary>
    /// <remarks>
    /// Reported on the **first** touch after the element dies, and only that one.
    /// WinAppDriver then evicts the element, so every subsequent request for the
    /// same id is <see cref="NoSuchElement"/> instead. Measured by reordering the
    /// probe sequence: whichever endpoint is called first reports 10, and it is
    /// not a property of the endpoint.
    ///
    /// Stale detection is therefore destructive, and matching it means removing
    /// the element from the store when staleness is detected — which also fixes
    /// the RCW leak the previous implementation had by never evicting anything.
    /// </remarks>
    public static WebDriverFault StaleElementReference { get; } =
        new(10, "stale element reference", 400);

    /// <summary>The route is not one this server serves.</summary>
    public static WebDriverFault UnknownCommand { get; } =
        new(9, "unknown command", 404);

    /// <summary>
    /// The element exists but cannot receive pointer or keyboard input.
    /// </summary>
    /// <remarks>
    /// WinAppDriver uses 105. Selenium's enum has
    /// <c>ElementNotInteractable = 60</c>.
    ///
    /// Note what this is *not*: clicking a **disabled** element returns HTTP 200
    /// success, not this fault. That was verified against a disabled Calculator
    /// memory button, and contradicts the assumption the previous implementation
    /// carried that disabled elements produce an error.
    /// </remarks>
    public static WebDriverFault ElementNotInteractable { get; } =
        new(105, "element not interactable", 400);

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

    /// <summary>A command needed a modal dialog and there was none.</summary>
    /// <remarks>
    /// <para>
    /// JSON Wire status 27, W3C name <c>no such alert</c>. A distinct fault
    /// rather than "no such element" because clients map it to a distinct
    /// exception - Selenium raises <c>NoAlertPresentException</c> - and a test
    /// that catches that is asking a different question from one catching a
    /// missing element.
    /// </para>
    /// <para>
    /// WinAppDriver never sends this: it does not serve the alert commands at
    /// all, measured 2026-08-29, and answers 404 to every one of them.
    /// </para>
    /// </remarks>
    public static WebDriverFault NoAlertPresent { get; } =
        new(27, "no such alert", 400);

    /// <summary>No session exists with the requested id.</summary>
    /// <remarks>
    /// WinAppDriver uses 101. Selenium's enum has <c>NoSuchDriver = 6</c>.
    /// </remarks>
    public static WebDriverFault InvalidSessionId { get; } =
        new(101, "invalid session id", 404);
}
