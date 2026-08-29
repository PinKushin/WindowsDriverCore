using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Commands the protocol defines that a desktop has no equivalent for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Served in order to REFUSE, which is not the same as not serving them.</b>
/// The unknown-command fallback says "I do not recognise this"; these say "I know
/// exactly what you asked for, it does not exist here, and here is what to use
/// instead". A client author can act on the second and can only guess at the
/// first — it reads as a wrong driver, a wrong version, or a typo.
/// </para>
/// <para>
/// <b>WinAppDriver makes the same distinction and this matches it.</b> Measured
/// 2026-08-29: it answers <b>501</b> to <c>GET /url</c> and <c>POST /refresh</c>
/// while answering 404 to anything it does not route. What it does not do is say
/// why — the 501 carries no body at all.
/// </para>
/// <para>
/// <b>Refusing rather than inventing an equivalent is the whole point.</b> Each
/// of these has a plausible-looking desktop analogue, and every one of them is a
/// guess about the caller's intent:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>submit</c> as "press Enter" — a form's submit button is often not Enter,
/// and a dialog's default button may do something else entirely.
/// </description></item>
/// <item><description>
/// <c>refresh</c> as "send F5" — meaningful in a browser and in almost nothing
/// else, and destructive in an editor that binds it.
/// </description></item>
/// <item><description>
/// <c>url</c> as "the app id this session was created with" — a plausible
/// string that is not an address, which a client would then try to navigate to.
/// </description></item>
/// </list>
/// <para>
/// Same reasoning as <c>/window/fullscreen</c>, which is refused for the same
/// reason: a capability with no honest expression on this platform is a refusal,
/// and a vendor extension is where an explicit one belongs.
/// </para>
/// </remarks>
public static class InapplicableCommandRoutes
{
    /// <summary>Maps the commands this platform cannot honour.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapInapplicableCommandRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Refuse(app, "GET", "url",
            "A desktop application has no address. This session is identified by the " +
            "capabilities it was created with, which GET /session/{id} reports.");

        Refuse(app, "POST", "url",
            "There is nothing to navigate to: a desktop application has no address. " +
            "Start a different application with a new session, or use " +
            "POST /session/{id}/appium/app/launch.");

        Refuse(app, "POST", "refresh",
            "A desktop application has no reload. Sending F5 would be a guess about " +
            "the application - it is destructive in some editors and meaningless in " +
            "most - so send it deliberately with POST /session/{id}/keys if that is " +
            "what the application does.");

        Refuse(app, "POST", "element/{elementId}/submit",
            "There are no forms to submit. A dialog's default button is not always " +
            "Enter and may do something else entirely, so press the button you mean " +
            "with POST /session/{id}/element/{id}/click, or send Enter explicitly " +
            "with POST /session/{id}/element/{id}/value.");

        return app;
    }

    /// <summary>Registers one route that answers with why it cannot be served.</summary>
    /// <remarks>
    /// <see cref="WebDriverFault.UnknownCommand"/> rather than a bespoke fault:
    /// the protocol has no "known but inapplicable" status, and inventing one
    /// would give clients a number they cannot map. The MESSAGE is what carries
    /// the difference, which is also why every one of these names an alternative
    /// rather than just saying no.
    /// </remarks>
    private static void Refuse(
        IEndpointRouteBuilder app, string method, string suffix, string because)
    {
        RouteHandlerBuilder route = app.MapMethods(
            $"/session/{{sessionId}}/{suffix}",
            [method],
            (HttpContext context) =>
            {
                // The session is still resolved first, so a request against a
                // dead session reports THAT rather than this - a client chasing
                // an expired session should not be told about platform support.
                DriverSession session = context.GetSession();

                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownCommand,
                        $"{method} /session/{session.Id}/{suffix} is not supported: {because}"),
                    statusCode: WebDriverFault.UnknownCommand.HttpStatus);
            });

        route.RequiresSession();
    }
}
