using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>GET /session/{sessionId}/source</c>.
/// </summary>
/// <remarks>
/// Its own file rather than another route on <see cref="WindowRoutes"/>: page
/// source is a question about the automation tree, and the window routes ask
/// Win32 about a window. Nothing is shared but the session.
/// </remarks>
public static class PageSourceRoutes
{
    /// <summary>Maps the page source route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapPageSourceRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/session/{sessionId}/source",
            (HttpContext context,
             IPageSourceReader source,
             IWindowLocator windows,
             ITerminationLog? log) =>
        {
            DriverSession session = context.GetSession();

            // ANY interaction changes the tree, and the source is a snapshot of
            // it. See PendingInput: a read that races the application diverges
            // from the reference, which wins the same race by being slow.
            PendingInput.Drain(session, windows, log);

            string? document = source.Source(session.WindowHandle);

            // A window that has gone reports itself, rather than answering with
            // the empty document its absent tree would produce. The suite's
            // GetSourceError_NoSuchWindow is exactly that distinction, and a
            // driver that renders nothing looks successful.
            return document is null
                ? Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
                    statusCode: WebDriverFault.NoSuchWindow.HttpStatus)
                : Results.Json(JsonWireResponse.ForSession(session.Id, document));
        }).RequiresSession();

        return app;
    }
}
