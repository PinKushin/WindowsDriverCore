using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

            return Results.Json(JsonWireResponse.ForSession(
                created.Session.Id,
                created.Session.Capabilities));
        });

        app.MapGet("/sessions", (ISessionStore sessions) =>
            Results.Json(JsonWireResponse.ForServer(
                sessions.All()
                    .Select(session => new SessionListEntry(session.Capabilities, session.Id))
                    .ToList())));

        app.MapDelete("/session/{sessionId}",
            (string sessionId, ISessionStore sessions, IElementRegistry elements) =>
        {
            // Remove returns the session so teardown does not need a second
            // lookup, which could race another request deleting the same id.
            DriverSession? removed = sessions.Remove(sessionId);

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

            // Shutting the application down belongs here and is not implemented
            // yet — the launcher arrives with POST /session. Removing the session
            // without it leaks the process, which is why this is a stated gap
            // rather than a silent one.
            return Results.Json(JsonWireResponse.ForServerVoid());
        });

        // Orientation is fixed. WinAppDriver answers LANDSCAPE for every session
        // regardless of the window, which the recording confirms — there is no
        // rotation concept on the desktop.
        app.MapGet("/session/{sessionId}/orientation", (HttpContext context) =>
            Results.Json(JsonWireResponse.ForSession(
                context.GetSession().Id,
                "LANDSCAPE")))
            .RequiresSession();

        return app;
    }
}
