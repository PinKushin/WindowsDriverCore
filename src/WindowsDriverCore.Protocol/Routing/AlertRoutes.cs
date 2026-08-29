using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// The alert commands, in both dialects.
/// </summary>
/// <remarks>
/// <para>
/// <b>WinAppDriver serves none of these.</b> Measured 2026-08-29 against
/// 1.2.2009: <c>alert_text</c>, <c>accept_alert</c> and <c>dismiss_alert</c> all
/// answer 404, and so do the W3C spellings. So this is the *plus more* half of
/// the goal rather than a gap against the reference — but the capability was
/// already here, since this driver finds a WinUI <c>ContentDialog</c> by
/// automation id today.
/// </para>
/// <para>
/// <b>Six routes, three commands.</b> JSON Wire spells them
/// <c>/alert_text</c>, <c>/accept_alert</c> and <c>/dismiss_alert</c>; W3C nests
/// them under <c>/alert/</c>. Same handlers, because two implementations of one
/// question is how WinAppDriver's own XPath singular and plural drifted apart.
/// </para>
/// <para>
/// <b><c>POST /alert_text</c> is NOT served.</b> Both dialects define it for
/// typing into a prompt's text field. A Windows dialog has no single canonical
/// input — a message box has none at all — so there is nothing to type into, and
/// picking one by guesswork would type into a field the caller never named. A
/// client that wants that has <c>/element</c> and can name it.
/// </para>
/// </remarks>
public static class AlertRoutes
{
    /// <summary>What the protocol calls the absence of an alert.</summary>
    private const string NoAlertMessage =
        "No modal dialog is open in this session's window";

    /// <summary>Maps the alert routes.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapAlertRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/session/{sessionId}/alert_text", ReadText).RequiresSession();
        app.MapGet("/session/{sessionId}/alert/text", ReadText).RequiresSession();

        app.MapPost("/session/{sessionId}/accept_alert", Accept).RequiresSession();
        app.MapPost("/session/{sessionId}/alert/accept", Accept).RequiresSession();

        app.MapPost("/session/{sessionId}/dismiss_alert", Dismiss).RequiresSession();
        app.MapPost("/session/{sessionId}/alert/dismiss", Dismiss).RequiresSession();

        return app;
    }

    private static IResult ReadText(HttpContext context, IAlertInspector? alerts)
    {
        DriverSession session = context.GetSession();

        if (alerts is null)
        {
            return NoInspector();
        }

        ElementRead<string> text = alerts.Text(session.WindowHandle);

        return text.Outcome switch
        {
            ElementReadOutcome.Read =>
                Results.Json(JsonWireResponse.ForSession(session.Id, text.Value)),

            // NOT "no such element". The protocol has a fault that means exactly
            // this, and a client catches NoAlertPresentException by name.
            ElementReadOutcome.NotFound => Fault(WebDriverFault.NoAlertPresent, NoAlertMessage),

            _ => Fault(WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage),
        };
    }

    private static IResult Accept(HttpContext context, IAlertInspector? alerts) =>
        Press(context, alerts, static (inspector, window) => inspector.Accept(window), "accepted");

    private static IResult Dismiss(HttpContext context, IAlertInspector? alerts) =>
        Press(context, alerts, static (inspector, window) => inspector.Dismiss(window), "dismissed");

    private static IResult Press(
        HttpContext context,
        IAlertInspector? alerts,
        Func<IAlertInspector, nint, ElementAction> press,
        string verb)
    {
        DriverSession session = context.GetSession();

        if (alerts is null)
        {
            return NoInspector();
        }

        ElementAction outcome = press(alerts, session.WindowHandle);

        switch (outcome.Outcome)
        {
            case ElementActionOutcome.Performed:
                // Pressing a dialog button is dispatched input, so a read that
                // follows waits for the application - the dialog is closing and
                // the window behind it is repainting.
                session.InputPending = true;
                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));

            case ElementActionOutcome.NotFound:
                return Fault(WebDriverFault.NoAlertPresent, NoAlertMessage);

            case ElementActionOutcome.NoSuchWindow:
                return Fault(WebDriverFault.NoSuchWindow, ElementFault.WindowClosedMessage);

            default:
                // THE DIALOG IS THERE AND NO BUTTON MATCHED. Named as its own
                // failure rather than folded into "no alert", because the two
                // need different fixes: one is a test that ran too early, the
                // other is a dialog whose buttons this driver does not recognise
                // - and reporting the second as the first sends the reader
                // looking for a race that is not there.
                return Fault(
                    WebDriverFault.UnknownError,
                    $"A modal dialog is open but no button could be {verb}: none of its " +
                    "buttons carry a recognised automation id or caption");
        }
    }

    private static IResult NoInspector() =>
        Fault(WebDriverFault.UnknownError, "No alert inspector is registered on this server");

    private static IResult Fault(WebDriverFault fault, string message) =>
        Results.Json(JsonWireResponse.ForFault(fault, message), statusCode: fault.HttpStatus);
}
