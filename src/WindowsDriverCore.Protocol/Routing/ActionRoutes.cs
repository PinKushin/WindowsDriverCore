using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// <c>POST /session/{id}/actions</c> — payload validation only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation is implemented and execution is not, and a valid payload is
/// REFUSED rather than silently accepted.</b> Accepting a well-formed action
/// sequence and performing nothing would report success for doing nothing, which
/// is the failure this driver exists to fix — the same reason
/// <c>page load</c> and <c>script</c> timeouts answer 501 instead of being
/// quietly stored.
/// </para>
/// <para>
/// Worth 20 tests on the compatibility suite regardless. Measured 2026-08-10:
/// every <c>ActionsError_*</c> test asserts a specific validation message and
/// got "Command not recognized" instead, so they fail on the absence of
/// validation rather than on the absence of Actions.
/// </para>
/// <para>
/// The messages are the suite's own constants from
/// <c>CommonTestSettings.cs</c>, which it asserts against real WinAppDriver and
/// passes — a stricter source than a recording, because it is what a real client
/// actually compares.
/// </para>
/// </remarks>
public static class ActionRoutes
{
    private const string UnsupportedPointerType =
        "Currently only pen and touch pointer input source types are supported";
    private const string MultiplePen =
        "Currently only a single (non-concurrent) pen input is supported";
    private const string BadButton =
        "\"button\" in a pointer action JSON payload is undefined or is not an Integer greater than or equal to 0";
    private const string BadDuration =
        "\"duration\" in a pointer action JSON payload is not an Integer greater than or equal to 0";
    private const string BadHeight =
        "\"height\" attribute is not a floating point value greater or equal to 1";
    private const string BadWidth =
        "\"width\" attribute is not a floating point value greater or equal to 1";
    private const string MissingWidthOrHeight =
        "\"width\" and \"height\" attributes need to be specified together";
    private const string BadPressure =
        "\"pressure\" attribute is not a floating point value between 0 and 1";
    private const string BadTiltX = "\"tiltX\" attribute is not an integer value between -90 and 90";
    private const string BadTiltY = "\"tiltY\" attribute is not an integer value between -90 and 90";
    private const string BadTwist = "\"twist\" attribute is not an integer value between 0 and 359";

    private const string NotImplemented =
        "Unimplemented Command: no pointer injector is registered on this server";

    private const string KeysRefused =
        "The key actions in this sequence could not be delivered to the keyboard";

    /// <summary>Maps the actions route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapActionRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // RELEASE ACTIONS, which W3C defines and this driver did not serve at
        // all. A client that ends a sequence by releasing its input state got
        // the unknown-command fallback.
        //
        // The compatibility suite never sends it - a full run has 25 POSTs to
        // /actions and ZERO DELETEs, which is measured - so nothing here could
        // ever have caught the omission. That is the whole reason for the
        // standing audit rather than a green score.
        //
        // It clears what /actions accumulates: the per-source pointer positions
        // and any contact still down. Modifiers are released too, because a
        // key left held by an interrupted sequence would otherwise apply to
        // every later command in the session.
        app.MapDelete("/session/{sessionId}/actions",
            (HttpContext context, PointerActionRunner? runner, IKeyboardInput? keyboard) =>
            {
                DriverSession session = context.GetSession();

                runner?.ForgetContacts();

                if (keyboard is not null && session.Modifiers.All.Count > 0)
                {
                    keyboard.ReleaseHeld(session.Modifiers);
                }

                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            })
            .RequiresSession();

        app.MapPost("/session/{sessionId}/actions",
            async (
                HttpContext context,
                PointerActionRunner? runner,
                KeyActionRunner? keys,
                WheelActionRunner? wheel,
                IElementRegistry registry,
                IWindowLocator windows) =>
        {
            DriverSession session = context.GetSession();

            using JsonDocument body = await JsonDocument
                .ParseAsync(context.Request.Body)
                .ConfigureAwait(false);

            string? rejection = Validate(body.RootElement);

            if (rejection is not null)
            {
                return Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.InvalidArgument, rejection),
                    statusCode: WebDriverFault.InvalidArgument.HttpStatus);
            }

            // THE KEYBOARD HALF, which this route used to drop on the floor.
            // The runner's own comment called key sources "someone else's job"
            // and no such job existed, so a Selenium 4 ActionChains keystroke
            // sequence was answered 200 having typed nothing.
            //
            // Performed BEFORE the pointer half, because that is the order the
            // client wrote them in for the common case - hold a modifier, then
            // click - and because a pointer refusal must not swallow keystrokes
            // that already went out.
            if (keys is not null && KeyActionRunner.HasKeySource(body.RootElement))
            {
                if (!keys.Perform(body.RootElement, session.Modifiers))
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(WebDriverFault.UnknownError, KeysRefused),
                        statusCode: WebDriverFault.UnknownError.HttpStatus);
                }

                session.InputPending = true;

                // A KEY-ONLY SEQUENCE IS COMPLETE HERE. Falling through to the
                // 501 below would refuse a payload that has just been performed
                // in full, purely because no pointer injector is registered.
                if (!PointerActionRunner.HasPointerSource(body.RootElement))
                {
                    return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
                }
            }

            // THE WHEEL HALF, the third of the three source types /actions
            // defines and the second one this route was dropping. Found while
            // adding a mouse wheel for windows: scroll rather than by the audit,
            // which is worth noting: the route answered 200 to a scroll sequence
            // that turned nothing.
            if (wheel is not null && WheelActionRunner.HasWheelSource(body.RootElement))
            {
                PointerRefusal? spun = wheel.Perform(body.RootElement, session.WindowHandle);

                if (spun is not null)
                {
                    return Results.Json(
                        JsonWireResponse.ForFault(WebDriverFault.UnknownError, spun.Message),
                        statusCode: WebDriverFault.UnknownError.HttpStatus);
                }

                session.InputPending = true;

                // A WHEEL-ONLY SEQUENCE IS COMPLETE HERE, and the early return
                // has to come AFTER the scroll rather than before it. Written the
                // other way round first, which answered 200 to a wheel-only
                // payload without turning the wheel - the precise "success for
                // work not done" this route exists to avoid, reintroduced while
                // fixing it.
                if (!PointerActionRunner.HasPointerSource(body.RootElement))
                {
                    return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
                }
            }

            if (runner is null)
            {
                // No injector registered. Refused rather than reported as done,
                // for the same reason a valid payload used to be refused:
                // accepting an action and performing nothing is the defect.
                return Results.Text(NotImplemented, statusCode: 501);
            }

            PointerRefusal? failure = runner.Perform(body.RootElement, session.WindowHandle);

            if (failure is null)
            {
                // A W3C action sequence is dispatched input like any other, and
                // this route never said so - only the keyboard, mouse and
                // element-action routes did. A read following a gesture
                // therefore never waited for the application at all.
                session.InputPending = true;

                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            }

            // A dead ORIGIN element is answered by the one place that knows what
            // this server handed out — the same rule the element routes use, and
            // the same rule the /touch commands now ask for. Formatting the
            // outcome into a sentence here is what failed the suite's
            // *Error_StaleElement tests character for character.
            return failure.ElementOutcome is { } outcome && failure.ElementId is { } elementId
                ? ElementFault.ForActionsOrigin(outcome, session, elementId, registry, windows)
                : Results.Json(
                    JsonWireResponse.ForFault(WebDriverFault.UnknownError, failure.Message),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
        }).RequiresSession();

        return app;
    }

    /// <summary>The first thing wrong with the payload, or null.</summary>
    /// <remarks>
    /// First rather than all: the suite asserts one message per malformed
    /// payload, and reporting a list would match none of them.
    /// </remarks>
    private static string? Validate(JsonElement payload)
    {
        if (!payload.TryGetProperty("actions", out JsonElement sources) ||
            sources.ValueKind != JsonValueKind.Array)
        {
            return ElementFault.BadOriginMessage;
        }

        int penSources = 0;

        foreach (JsonElement source in sources.EnumerateArray())
        {
            string? type = Text(source, "type");

            if (type == "pointer")
            {
                string? pointerType = source.TryGetProperty("parameters", out JsonElement parameters)
                    ? Text(parameters, "pointerType")
                    : null;

                if (pointerType is not null && pointerType is not ("pen" or "touch"))
                {
                    return UnsupportedPointerType;
                }

                if (pointerType == "pen" && ++penSources > 1)
                {
                    return MultiplePen;
                }
            }

            if (!source.TryGetProperty("actions", out JsonElement steps) ||
                steps.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement step in steps.EnumerateArray())
            {
                string? rejection = ValidateStep(step);
                if (rejection is not null)
                {
                    return rejection;
                }
            }
        }

        return null;
    }

    private static string? ValidateStep(JsonElement step)
    {
        string? action = Text(step, "type");

        if (action is "pointerDown" or "pointerUp" &&
            (!step.TryGetProperty("button", out JsonElement button) ||
             button.ValueKind != JsonValueKind.Number ||
             button.GetDouble() < 0))
        {
            return BadButton;
        }

        if (step.TryGetProperty("duration", out JsonElement duration) &&
            (duration.ValueKind != JsonValueKind.Number || duration.GetDouble() < 0))
        {
            return BadDuration;
        }

        if (step.TryGetProperty("origin", out JsonElement origin) &&
            origin.ValueKind == JsonValueKind.String &&
            origin.GetString() is not ("viewport" or "pointer"))
        {
            return ElementFault.BadOriginMessage;
        }

        // width and height are a pair: one without the other is its own error,
        // and the suite has a test for exactly that.
        bool hasWidth = step.TryGetProperty("width", out JsonElement width);
        bool hasHeight = step.TryGetProperty("height", out JsonElement height);

        // EACH VALUE IS JUDGED BEFORE THE PAIR IS, and that order is measured
        // rather than chosen. The suite pins both halves with two adjacent
        // tests: ActionsError_BadPointerTouch_Width sends width:-1 ALONE - so
        // the pair is incomplete too - and expects the bad-value message, while
        // ActionsError_BadPointerTouch_Width_MissingHeight sends a VALID
        // width:1 alone and expects the missing-pair message.
        //
        // Checking the pair first answers "specified together" for both, which
        // passes the second test and fails the first. Measured at a085cd6:
        // exactly those two tests failed, with the pairing message where the
        // reference sends the value message.
        if (hasWidth && !IsAtLeast(width, 1))
        {
            return BadWidth;
        }

        if (hasHeight && !IsAtLeast(height, 1))
        {
            return BadHeight;
        }

        if (hasWidth != hasHeight)
        {
            return MissingWidthOrHeight;
        }

        if (step.TryGetProperty("pressure", out JsonElement pressure) && !IsBetween(pressure, 0, 1))
        {
            return BadPressure;
        }

        if (step.TryGetProperty("tiltX", out JsonElement tiltX) && !IsWholeBetween(tiltX, -90, 90))
        {
            return BadTiltX;
        }

        if (step.TryGetProperty("tiltY", out JsonElement tiltY) && !IsWholeBetween(tiltY, -90, 90))
        {
            return BadTiltY;
        }

        if (step.TryGetProperty("twist", out JsonElement twist) && !IsWholeBetween(twist, 0, 359))
        {
            return BadTwist;
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsAtLeast(JsonElement value, double minimum) =>
        value.ValueKind == JsonValueKind.Number && value.GetDouble() >= minimum;

    private static bool IsBetween(JsonElement value, double minimum, double maximum) =>
        value.ValueKind == JsonValueKind.Number &&
        value.GetDouble() >= minimum &&
        value.GetDouble() <= maximum;

    /// <summary>A whole number in range.</summary>
    /// <remarks>
    /// The message says "integer", so 45.5 is rejected even though it is in
    /// range — a float where an integer is required is exactly what these tests
    /// send.
    /// </remarks>
    private static bool IsWholeBetween(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        double number = value.GetDouble();
        return number >= minimum &&
               number <= maximum &&
               Math.Abs(number - Math.Round(number)) < double.Epsilon;
    }
}
