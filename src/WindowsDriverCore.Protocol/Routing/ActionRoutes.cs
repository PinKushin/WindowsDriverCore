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
    private const string BadOrigin =
        "\"origin\" in a action JSON payload is not equal to \"viewport\" or \"pointer\" and element is not an Object that represents a web element";
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

    /// <summary>Maps the actions route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapActionRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/session/{sessionId}/actions",
            async (
                HttpContext context,
                PointerActionRunner? runner,
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
                return Results.Json(JsonWireResponse.ForSessionVoid(session.Id));
            }

            // A dead ORIGIN element is answered by the one place that knows what
            // this server handed out — the same rule the element routes use, and
            // the same rule the /touch commands now ask for. Formatting the
            // outcome into a sentence here is what failed the suite's
            // *Error_StaleElement tests character for character.
            return failure.ElementOutcome is { } outcome && failure.ElementId is { } elementId
                ? ElementFault.For(outcome, session, elementId, registry, windows)
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
            return BadOrigin;
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
            return BadOrigin;
        }

        // width and height are a pair: one without the other is its own error,
        // and the suite has a test for exactly that.
        bool hasWidth = step.TryGetProperty("width", out JsonElement width);
        bool hasHeight = step.TryGetProperty("height", out JsonElement height);

        if (hasWidth != hasHeight)
        {
            return MissingWidthOrHeight;
        }



        if (hasWidth && !IsAtLeast(width, 1))
        {
            return BadWidth;
        }

        if (hasHeight && !IsAtLeast(height, 1))
        {
            return BadHeight;
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
