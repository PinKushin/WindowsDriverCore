using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Status;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Registers the JSON Wire Protocol surface.
/// </summary>
public static class JsonWireRoutes
{
    /// <summary>
    /// Maps every route this driver serves, plus the fallback that answers
    /// anything unrecognised.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapJsonWireProtocol(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // An empty group, purely so ONE filter attaches to every route below it.
        // Adding it per-route would mean remembering it at every future Map call,
        // and a route that forgot would go missing from the transcript with no
        // symptom other than a gap nobody notices.
        RouteGroupBuilder logged = app.MapGroup(string.Empty);
        logged.AddEndpointFilter<JsonWireStatusFilter>();

        logged.MapGet("/status", (IServerStatusProvider status) =>
            Results.Json(status.GetStatus()));

        logged.MapSessionRoutes();
        logged.MapElementRoutes();
        logged.MapTimeoutRoutes();
        logged.MapWindowRoutes();
        logged.MapPageSourceRoutes();
        logged.MapScreenshotRoutes();
        logged.MapElementEqualsRoutes();
        logged.MapActionRoutes();
        logged.MapKeyboardRoutes();
        logged.MapElementPropertyRoutes();
        logged.MapElementActionRoutes();
        logged.MapMouseRoutes();
        logged.MapTouchRoutes();

        // Anything not matched above. WinAppDriver answers an unrecognised route
        // with status 9 and a message naming the method and path — not with an
        // empty 404 — so a client sees what it asked for.
        logged.MapFallback(static (HttpContext context) =>
        {
            FaultResponse fault = JsonWireResponse.ForFault(
                WebDriverFault.UnknownCommand,
                $"Command not recognized: {context.Request.Method}: {context.Request.Path}");

            return Results.Json(fault, statusCode: WebDriverFault.UnknownCommand.HttpStatus);
        });

        return app;
    }
}
