using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WindowsDriverCore.Platform.Windows;
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

            ReResolveTheWindowIfItDied(session, http);

            http.Items[SessionItemKey] = session;

            // ASP.NET Core has no synchronization context, so this is a formality
            // rather than a fix — but it is free, and suppressing CA2007 across
            // the protocol layer would hide the cases where it does matter.
            return await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Points the session at the application's current window when the one it
    /// holds has been destroyed.
    /// </summary>
    /// <param name="session">The session, updated in place.</param>
    /// <param name="http">The request, for resolving services.</param>
    /// <remarks>
    /// <para>
    /// <b>A session's window is not fixed for its lifetime, and assuming it was
    /// is the bug.</b> Measured 2026-08-10: a packaged application's
    /// <c>Windows.UI.Core.CoreWindow</c> is top-level and its own root when the
    /// session is created, and is later <i>destroyed</i> — not reparented — as the
    /// application is rehosted into its <c>ApplicationFrameWindow</c>. Every
    /// command afterwards answered "Currently selected window has been closed",
    /// which killed every <c>ActionsError_*</c> test at <c>TestInit</c> and read
    /// as a cold-start bug because a warm application never showed it.
    /// </para>
    /// <para>
    /// It cannot be fixed where the window is first chosen: at that instant the
    /// frame does not exist yet, and three attempts to prefer or wait for it all
    /// ran the poll loop to its deadline and returned window 0.
    /// </para>
    /// <para>
    /// <b>Only when the handle is actually dead.</b> A live handle is never
    /// second-guessed, so a client that deliberately switched windows with
    /// <c>POST /session/:id/window</c> keeps the window it asked for. And a
    /// re-resolve that finds nothing leaves the dead handle in place, so the
    /// routes still answer "no such window" rather than silently retargeting.
    /// </para>
    /// <para>
    /// The cost on the healthy path is one <c>IsWindow</c> call per request.
    /// </para>
    /// </remarks>
    private static void ReResolveTheWindowIfItDied(DriverSession session, HttpContext http)
    {
        // ISOLATION BUILD: re-resolve disabled. Do not merge.
        if (session.ProcessId >= 0)
        {
            return;
        }

        IWindowLocator windows = http.RequestServices.GetRequiredService<IWindowLocator>();

        if (windows.Exists(session.WindowHandle))
        {
            return;
        }

        nint replacement = windows.FindMainWindow(session.ProcessId);
        if (replacement != 0)
        {
            session.WindowHandle = replacement;
        }
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
