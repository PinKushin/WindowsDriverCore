using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Errors;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>The body of <c>GET /session/{id}/location</c>.</summary>
/// <param name="Latitude">Degrees, -90 to 90.</param>
/// <param name="Longitude">Degrees, -180 to 180.</param>
/// <param name="Altitude">Metres above sea level.</param>
/// <remarks>
/// The three keys the suite's own <c>Location.cs</c> reads back, which asserts
/// each is non-null and that latitude and longitude are in range.
/// </remarks>
public sealed record GeoLocationBody(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("altitude")] double Altitude);

/// <summary>
/// <c>GET /session/{id}/location</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last of WinAppDriver's 59 documented endpoints this driver did not
/// serve.</b> Measured 2026-08-29 by probing the whole list against a running
/// server: three of the four apparent gaps were errors in its own table — it
/// writes GET for <c>/element/:id/element</c>, which is a POST, and truncates
/// <c>/equals/:other</c> — leaving this one.
/// </para>
/// <para>
/// <b>Refusing is the common answer and that is correct.</b> A machine with no
/// location provider or no consent does not know where it is; the reference has
/// the same failure on the same guest. What would be wrong is inventing a
/// plausible coordinate, which the client could not distinguish from a real one.
/// </para>
/// <para>
/// <b>There is no setter.</b> JSON Wire defines <c>POST /location</c> for mocking
/// a device's position, and there is nothing to mock — a desktop reports the
/// machine's real location through a service this driver does not own. Accepting
/// a position and ignoring it would be the defect; the unknown-command fallback
/// says so instead.
/// </para>
/// </remarks>
public static class LocationRoutes
{
    /// <summary>Maps the location route.</summary>
    /// <param name="app">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IEndpointRouteBuilder MapLocationRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/session/{sessionId}/location",
            (HttpContext context, IGeolocation? geolocation) =>
            {
                DriverSession session = context.GetSession();

                if (geolocation is not null && geolocation.TryRead(out GeoPosition? position) &&
                    position is not null)
                {
                    return Results.Json(JsonWireResponse.ForSession(
                        session.Id,
                        new GeoLocationBody(position.Latitude, position.Longitude, position.Altitude)));
                }

                return Results.Json(
                    JsonWireResponse.ForFault(
                        WebDriverFault.UnknownError,
                        "This machine reported no location: there is no location provider, " +
                        "consent has not been granted, or no fix was available"),
                    statusCode: WebDriverFault.UnknownError.HttpStatus);
            })
            .RequiresSession();

        return app;
    }
}
