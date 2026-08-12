using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Resolves the <c>{sessionId}</c> route value to a live session, or rejects the
/// request.
/// </summary>
/// <remarks>
/// Every session-scoped route needs this and the answer is always the same, so
/// it lives in one place. The implementation being replaced inlined the lookup
/// and its error response **23 times** in one file, which is how the same
/// condition ended up with three slightly different messages.
/// </remarks>
public static class RequireSession
{
    private const string SessionItemKey = "WindowsDriverCore.Session";

    /// <summary>
    /// Requires a live session, rejecting the request with
    /// <see cref="WebDriverFault.InvalidSessionId"/> when there is none.
    /// </summary>
    /// <param name="builder">The route being built.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static RouteHandlerBuilder RequiresSession(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(static async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            string sessionId = http.Request.RouteValues["sessionId"] as string ?? string.Empty;

            ISessionStore sessions = http.RequestServices.GetRequiredService<ISessionStore>();
            DriverSession? session = sessions.Find(sessionId);

            if (session is null)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.InvalidSessionId,
                        $"No active session with ID {sessionId}"),
                    statusCode: WebDriverFault.InvalidSessionId.HttpStatus);
            }

            http.Items[SessionItemKey] = session;

            // The dialect was fixed when the session was created and every
            // response it produces is shaped for it. Reading it off the REQUEST
            // instead would let one client's classic command and its /actions
            // call - which Selenium 3 sends in different dialects - come back in
            // two different envelope shapes.
            ProtocolDialectContext.Remember(http, session.Dialect);

            // ASP.NET Core has no synchronization context, so this is a formality
            // rather than a fix — but it is free, and suppressing CA2007 across
            // the protocol layer would hide the cases where it does matter.
            return await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The session resolved by <see cref="RequiresSession"/>.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>The session.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The route did not call <see cref="RequiresSession"/>. This is a wiring
    /// mistake rather than a client error, so it throws rather than producing a
    /// protocol fault — a route that forgot the filter would otherwise report
    /// "no such session" for a session that exists.
    /// </exception>
    public static DriverSession GetSession(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(SessionItemKey, out object? value) &&
            value is DriverSession session)
        {
            return session;
        }

        throw new InvalidOperationException(
            $"Route '{context.Request.Path}' reads the session but does not call RequiresSession().");
    }
}
