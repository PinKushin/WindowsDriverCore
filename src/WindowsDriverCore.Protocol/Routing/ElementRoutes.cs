using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>The body of an element search.</summary>
/// <param name="Using">The locator strategy.</param>
/// <param name="Value">The value to match.</param>
public sealed record FindElementRequest(
    [property: JsonPropertyName("using")] string? Using,
    [property: JsonPropertyName("value")] string? Value);

/// <summary>
/// Element search routes.
/// </summary>
public static class ElementRoutes
{
    /// <summary>How often a find is retried while an implicit wait is set.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

    private const string NoSuchElementMessage =
        "An element could not be located on the page using the given search parameters.";

    private const string WindowClosedMessage = "Currently selected window has been closed";

    /// <summary>Maps the element search routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapElementRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/element",
            async (HttpContext context, IElementFinder finder, IElementRegistry registry,
                   IWindowLocator windows) =>
            {
                (LocatorParseResult locator, IResult? rejection) = await ReadLocator(context)
                    .ConfigureAwait(false);
                if (rejection is not null)
                {
                    return rejection;
                }

                DriverSession session = context.GetSession();

                // FindFirst, not FindAll: this route uses one match and UIA can
                // stop as soon as it has one. Measured at 9.8 ms against 12.0 ms
                // for the exhaustive walk on Calculator.
                FindResult found = await RetryWhileEmpty(
                    session,
                    () => finder.FindFirst(session.WindowHandle, locator.Kind, locator.Value))
                    .ConfigureAwait(false);

                if (found.Failure != FindFailure.None)
                {
                    return FailureResponse(found.Failure, locator.Value);
                }

                // A single find with no match is an error; the plural form is
                // not. Measured: POST /element answers 404 status 7, while
                // POST /elements answers 200 with an empty array.
                if (found.ElementIds.Count == 0)
                {
                    // An empty result and a dead window look identical to the
                    // search, so the window has to be asked about separately.
                    // Answering "no such element" when the window is gone sends
                    // a client looking for a better locator for something that
                    // cannot be found by any locator — and a retry loop will keep
                    // searching a window that no longer exists until it times
                    // out. Measured: 24 tests on the compatibility suite expect
                    // "Currently selected window has been closed" here.
                    if (!windows.Exists(session.WindowHandle))
                    {
                        return Fault(WebDriverFault.NoSuchWindow, WindowClosedMessage);
                    }

                    return Fault(WebDriverFault.NoSuchElement, NoSuchElementMessage);
                }

                // Recording what was handed out is what lets a later command
                // tell a stale element from an id this server never issued.
                // Only the id that is actually returned: recording all the
                // matches would report "stale" for elements the client was
                // never told about.
                registry.Record(session.Id, found.ElementIds[0]);

                return Results.Json(JsonWireResponse.ForSession(
                    session.Id,
                    new ElementReference(found.ElementIds[0])));
            })
            .RequiresSession();

        app.MapPost("/session/{sessionId}/elements",
            async (HttpContext context, IElementFinder finder, IElementRegistry registry) =>
            {
                (LocatorParseResult locator, IResult? rejection) = await ReadLocator(context)
                    .ConfigureAwait(false);
                if (rejection is not null)
                {
                    return rejection;
                }

                DriverSession session = context.GetSession();
                FindResult found = finder.FindAll(session.WindowHandle, locator.Kind, locator.Value);

                if (found.Failure != FindFailure.None)
                {
                    return FailureResponse(found.Failure, locator.Value);
                }

                foreach (string id in found.ElementIds)
                {
                    registry.Record(session.Id, id);
                }

                return Results.Json(JsonWireResponse.ForSession(
                    session.Id,
                    found.ElementIds.Select(id => new ElementReference(id)).ToList()));
            })
            .RequiresSession();

        // Nested find: the same search, rooted at an element instead of the
        // window.
        //
        // This is the protocol's answer to duplicate and unnamed elements. Rows
        // that are indistinguishable across a window are usually unique inside
        // their own container, and measured against WinAppDriver the scoping is
        // real: CalculatorResults searched inside the keypad answers "no such
        // element" while the same locator at window scope finds it.
        app.MapPost("/session/{sessionId}/element/{elementId}/element",
            async (HttpContext context, IElementFinder finder, IElementRegistry registry,
                   string elementId) =>
            {
                (LocatorParseResult locator, IResult? rejection) = await ReadLocator(context)
                    .ConfigureAwait(false);
                if (rejection is not null)
                {
                    return rejection;
                }

                DriverSession session = context.GetSession();
                FindResult found = finder.FindFirst(
                    new SearchScope(session.WindowHandle, elementId), locator.Kind, locator.Value);

                if (found.Failure != FindFailure.None)
                {
                    return FailureResponse(found.Failure, locator.Value);
                }

                if (found.ElementIds.Count == 0)
                {
                    return Fault(WebDriverFault.NoSuchElement, NoSuchElementMessage);
                }

                registry.Record(session.Id, found.ElementIds[0]);

                return Results.Json(JsonWireResponse.ForSession(
                    session.Id,
                    new ElementReference(found.ElementIds[0])));
            })
            .RequiresSession();

        app.MapPost("/session/{sessionId}/element/{elementId}/elements",
            async (HttpContext context, IElementFinder finder, IElementRegistry registry,
                   string elementId) =>
            {
                (LocatorParseResult locator, IResult? rejection) = await ReadLocator(context)
                    .ConfigureAwait(false);
                if (rejection is not null)
                {
                    return rejection;
                }

                DriverSession session = context.GetSession();
                FindResult found = finder.FindAll(
                    new SearchScope(session.WindowHandle, elementId), locator.Kind, locator.Value);

                if (found.Failure != FindFailure.None)
                {
                    return FailureResponse(found.Failure, locator.Value);
                }

                foreach (string id in found.ElementIds)
                {
                    registry.Record(session.Id, id);
                }

                return Results.Json(JsonWireResponse.ForSession(
                    session.Id,
                    found.ElementIds.Select(id => new ElementReference(id)).ToList()));
            })
            .RequiresSession();

        return app;
    }

    private static async Task<(LocatorParseResult Locator, IResult? Rejection)> ReadLocator(
        HttpContext context)
    {
        FindElementRequest? request = await context.Request
            .ReadFromJsonAsync<FindElementRequest>(context.RequestAborted)
            .ConfigureAwait(false);

        LocatorParseResult locator = Locator.Parse(request?.Using ?? string.Empty, request?.Value ?? string.Empty);

        if (locator.Rejection == LocatorRejection.Unsupported)
        {
            return (locator, UnimplementedCommand(
                $"{locator.UnsupportedStrategy} locator strategy is not supported"));
        }

        return (locator, null);
    }

    private static IResult FailureResponse(FindFailure failure, string locatorValue) => failure switch
    {
        FindFailure.NoSuchWindow => Fault(WebDriverFault.NoSuchWindow, WindowClosedMessage),

        // The server sends only the expression. The client appends
        // " (XPathLookupError)" from the error string with spaces removed, so
        // sending that suffix here would double it.
        FindFailure.XPathLookupError => Fault(
            WebDriverFault.XPathLookupError,
            $"Invalid XPath expression: {locatorValue}"),

        _ => Fault(WebDriverFault.UnknownError, "An unknown error occurred"),
    };

    private static IResult Fault(WebDriverFault fault, string message) =>
        Results.Json(JsonWireResponse.ForFault(fault, message), statusCode: fault.HttpStatus);

    /// <summary>
    /// An unimplemented command, as WinAppDriver reports it.
    /// </summary>
    /// <remarks>
    /// HTTP 501 with a <b>plain-text</b> body and no JSON envelope. Measured, and
    /// load-bearing: the client cannot parse the body, so it prefixes its own
    /// "Unexpected error. " — which is why the compatibility suite asserts on
    /// "Unexpected error. Unimplemented Command: ...". Returning JSON here would
    /// produce a different client-side message and fail those tests.
    /// </remarks>
    private static IResult UnimplementedCommand(string what) =>
        Results.Text($"Unimplemented Command: {what}", statusCode: StatusCodes.Status501NotImplemented);

    /// <summary>
    /// Retries a find until the session's implicit wait elapses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the singular route.</b> Measured against WinAppDriver: a find for
    /// many elements answers 200 with an empty array rather than an error, so
    /// there is nothing to wait for — waiting there would turn a legitimate
    /// "none present" into a delay on every call.
    /// </para>
    /// <para>
    /// Retries on NOTHING FOUND only. A failure — no such window, a bad
    /// locator — is answered immediately, because repeating it cannot change the
    /// outcome and would turn a clear error into a slow one.
    /// </para>
    /// <para>
    /// The default is zero, so a session that never sets a timeout does exactly
    /// one pass and pays nothing. This is the compatibility shim, not the
    /// mechanism a reliable find should need.
    /// </para>
    /// </remarks>
    private static async Task<FindResult> RetryWhileEmpty(
        DriverSession session, Func<FindResult> find)
    {
        FindResult found = find();

        if (session.ImplicitWait <= TimeSpan.Zero ||
            found.Failure != FindFailure.None ||
            found.ElementIds.Count > 0)
        {
            return found;
        }

        long deadline = Stopwatch.GetTimestamp() +
            (long)(session.ImplicitWait.TotalSeconds * Stopwatch.Frequency);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            // Polling, not sleeping on a guess: the loop returns the moment the
            // element appears and the interval only bounds how long it may wait
            // past that.
            await Task.Delay(RetryInterval).ConfigureAwait(false);

            found = find();
            if (found.Failure != FindFailure.None || found.ElementIds.Count > 0)
            {
                return found;
            }
        }

        return found;
    }
}
