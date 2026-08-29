using WindowsDriverCore.Automation;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>One entry of <c>GET /sessions</c>.</summary>
/// <param name="Capabilities">The capabilities the session was created with.</param>
/// <param name="Id">The session id.</param>
/// <remarks>
/// Capabilities before id, which is the order WinAppDriver serialises. Harmless
/// to a parser, but the recordings are compared key by key and matching costs
/// nothing.
/// </remarks>
public sealed record SessionListEntry(
    [property: JsonPropertyName("capabilities")] IReadOnlyDictionary<string, string> Capabilities,
    [property: JsonPropertyName("id")] string Id);

/// <summary>
/// Session lifecycle routes.
/// </summary>
public static class SessionRoutes
{
    /// <summary>Maps the session lifecycle routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapSessionRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session", async (HttpContext context, ISessionStore sessions, SessionFactory factory) =>
        {
            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body)
                .ConfigureAwait(false);

            CapabilityParseResult parsed = SessionCapabilities.Parse(body.RootElement);

            // Recorded before the validation branch, so a W3C client that sends
            // bad capabilities is REFUSED in W3C too. A fault shaped for the
            // wrong dialect is the one a client cannot report usefully - it sees
            // an unrecognised body and raises "unknown error" for a message that
            // said exactly what was wrong.
            ProtocolDialectContext.Remember(context, parsed.Dialect);

            if (parsed.Capabilities is null)
            {
                // Validation before anything is started. Launching and then
                // rejecting would leave an application running that nothing will
                // ever close.
                return Results.Json(
                    JsonWireResponse.ForFault(parsed.Fault!, parsed.Message!),
                    statusCode: parsed.Fault!.HttpStatus);
            }

            SessionCreateResult created = factory.Create(parsed.Capabilities);
            if (created.Session is null)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(created.Fault!, created.Message!),
                    statusCode: created.Fault!.HttpStatus);
            }

            // Stored only once it exists. A session reported but not stored fails
            // on the client's very next request.
            sessions.Add(created.Session);

            return Results.Json(JsonWireResponse.ForSessionCreated(
                created.Session.Id,
                created.Session.Capabilities));
        });

        app.MapGet("/sessions", (ISessionStore sessions) =>
            Results.Json(JsonWireResponse.ForServer(
                sessions.All()
                    .Select(session => new SessionListEntry(session.Capabilities, session.Id))
                    .ToList())));

        app.MapDelete("/session/{sessionId}",
            (HttpContext context,
             string sessionId,
             ISessionStore sessions,
             IElementRegistry elements,
             IElementHandleCache handles,
             IApplicationTerminator terminator,
             IKeyboardInput? keyboard) =>
        {
            // Remove returns the session so teardown does not need a second
            // lookup, which could race another request deleting the same id.
            DriverSession? removed = sessions.Remove(sessionId);

            // This route removes the session itself rather than going through
            // RequiresSession, so nothing else has recorded which dialect to
            // answer in. A W3C client would otherwise get a JSON Wire body for
            // the one command it always calls - teardown - and report a failed
            // quit for a session that shut down perfectly.
            if (removed is not null)
            {
                ProtocolDialectContext.Remember(context, removed.Dialect);
            }

            if (removed is null)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.InvalidSessionId,
                        $"No active session with ID {sessionId}"),
                    statusCode: WebDriverFault.InvalidSessionId.HttpStatus);
            }

            // The ids this session was handed can never resolve again, so
            // keeping them would grow for the server's lifetime and would let a
            // later session with a colliding runtime id read "stale" for an
            // element it never saw.
            elements.Forget(sessionId);
            handles.Forget(removed.WindowHandle);

            // ONLY an application this driver started. A desktop session
            // addresses explorer and an attached session addresses a window
            // somebody else opened — both have real process ids, so the flag is
            // recorded at creation rather than inferred from the id.
            //
            // The result is deliberately not checked: an application that will
            // not die is not a reason to keep the session. The client asked for
            // it to be gone, and a second delete must report it unknown rather
            // than hand back a session addressing a half-dead application.
            // ONLY if no other live session is still addressing the same
            // process. Windows 10's Calculator is single-instance, so two
            // sessions for the same application share one process — closing it
            // when the first ends takes the application out from under the
            // second. Measured: session shutdown cost 4 tests on the
            // compatibility suite until this check existed, because that is
            // exactly what the suite does.
            //
            // The last session out closes it, so nothing leaks either.
            // LIFT ANYTHING STILL HELD, and do it before the application goes.
            //
            // /keys persists modifiers between calls by design, so a session can
            // legitimately end with shift down. Nobody sends the key-up after
            // that: the client has gone, and the key stays down for the DESKTOP -
            // the driver's state outliving the driver, which is the exact failure
            // the element route's implicit release exists to prevent.
            keyboard?.ReleaseHeld(removed.Modifiers);

            bool anotherSessionIsUsingIt = sessions.All()
                .Any(other => other.ProcessId == removed.ProcessId);

            if (removed.OwnsApplication && !anotherSessionIsUsingIt)
            {
                terminator.Terminate(removed.ProcessId, removed.WindowHandle);
            }

            return Results.Json(JsonWireResponse.ForServerVoid());
        });

        // Orientation is fixed. WinAppDriver answers LANDSCAPE for every LIVE
        // session regardless of the window, which the recording confirms — there
        // is no rotation concept on the desktop.
        //
        // BUT THE WINDOW HAS TO BE THERE. GetOrientationError_NoSuchWindow reads
        // the orientation of a session whose window has been closed and expects
        // the no-such-window fault; this answered 200 LANDSCAPE and the suite
        // reported "Exception should have been thrown".
        //
        // The session filter cannot catch it: the SESSION still exists and only
        // the window is gone, so the check belongs here. Same shape as
        // NavigationRoutes, for a milder version of the same reason — navigation
        // refuses because a keystroke would otherwise land in somebody else's
        // application, and this refuses because reporting the orientation of a
        // window that does not exist is a statement about a window that does not
        // exist.
        // SETTING IT IS REFUSED RATHER THAN ACCEPTED AND IGNORED. Both dialects
        // define a POST, and a desktop has exactly one orientation - so the
        // honest answer to "make it PORTRAIT" is no, not a 200 that changes
        // nothing. Same rule as page-load timeouts and an unperformable action
        // sequence: reporting success for work not done is the defect this
        // driver exists to fix.
        //
        // LANDSCAPE is accepted, because a client asking for the state it is
        // already in has not been refused anything.
        app.MapPost("/session/{sessionId}/orientation",
            async (HttpContext context, IWindowLocator windows) =>
            {
                DriverSession session = context.GetSession();

                if (!windows.Exists(session.WindowHandle))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                        statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
                }

                using JsonDocument body = await JsonDocument
                    .ParseAsync(context.Request.Body)
                    .ConfigureAwait(false);

                string? wanted = body.RootElement.TryGetProperty("orientation", out JsonElement value)
                    ? value.GetString()
                    : null;

                return string.Equals(wanted, "LANDSCAPE", StringComparison.OrdinalIgnoreCase)
                    ? Results.Json(JsonWireResponse.ForSessionVoid(session.Id))
                    : Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.InvalidArgument,
                            "A desktop session is always LANDSCAPE and cannot be rotated"),
                        statusCode: WebDriverFault.InvalidArgument.HttpStatus);
            })
            .RequiresSession();

        app.MapGet("/session/{sessionId}/orientation",
            (HttpContext context, IWindowLocator windows) =>
            {
                DriverSession session = context.GetSession();

                if (!windows.Exists(session.WindowHandle))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(
                            WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                        statusCode: WebDriverFault.NoSuchWindow.HttpStatus);
                }

                return Results.Json(JsonWireResponse.ForSession(session.Id, "LANDSCAPE"));
            })
            .RequiresSession();

        return app;
    }
}
