using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using WindowsDriverCore.Protocol.Responses;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Reshapes a response for a W3C client, and leaves a JSON Wire one untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>One translation point, not sixty.</b> Every handler builds a JSON Wire
/// envelope and this rewrites it if the client speaks W3C. Serving both dialects
/// from the handlers would put the decision in every route, and only the JSON
/// Wire half has a suite watching it — so the W3C half would rot silently.
/// </para>
/// <para>
/// <b>The JSON Wire path returns the SAME OBJECT, not an equal one.</b> Nothing
/// is rebuilt, reordered or re-serialised for a client that did not ask for W3C.
/// That is deliberate: the compatibility suite is the scoreboard, and a
/// translation layer that "shouldn't change anything" is exactly the shape of
/// change that moves a score by two tests and takes a day to find.
/// </para>
/// <para>
/// The three differences that actually break a Selenium 4 client:
/// </para>
/// <list type="bullet">
///   <item>the envelope is <c>{value: …}</c> — no <c>status</c>, no sibling <c>sessionId</c>;</item>
///   <item>an element is keyed by uuid rather than <c>ELEMENT</c>;</item>
///   <item>a failure names its error as a STRING, with a <c>stacktrace</c> member.</item>
/// </list>
/// <para>
/// <b>The HTTP status code is NOT translated, deliberately.</b> W3C answers a
/// stale element and a bad window with 404 where JSON Wire uses 400, and this
/// keeps the JSON Wire number for both. A Selenium 4 client dispatches on the
/// error string and only asks the status code whether the request failed at all
/// — so the two spellings are indistinguishable to it, and a second table of
/// codes would be a second thing to keep in step with no test able to tell it
/// had drifted. If a client ever turns up that reads the code, this is the place
/// and <see cref="WindowsDriverCore.Protocol.Errors.WebDriverFault"/> is where
/// the mapping would live.
/// </para>
/// </remarks>
public sealed class ProtocolDialectFilter : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        object? result = await next(context).ConfigureAwait(false);

        if (ProtocolDialectContext.Of(context.HttpContext) != ProtocolDialect.W3C)
        {
            return result;
        }

        if (result is not IValueHttpResult valued || valued.Value is not IJsonWireEnvelope envelope)
        {
            // GET /status has no envelope and is dialect-free by design: a client
            // asks it before it has a session, so there is nothing to translate
            // and no dialect to translate into.
            return result;
        }

        return Results.Json(
            Rewrite(envelope),
            statusCode: (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK);
    }

    private static W3CResponse Rewrite(IJsonWireEnvelope envelope) => envelope switch
    {
        // Session creation is the one command whose W3C body is not just the JWP
        // value moved: the id travels INSIDE value, beside the capabilities,
        // rather than beside the envelope.
        SessionCreatedResponse created => new W3CResponse(
            new W3CSessionCreated(created.SessionId, created.Value), created.Status),

        FaultResponse fault => new W3CResponse(
            new W3CFault(fault.Value.Error, fault.Value.Message, string.Empty), fault.Status),

        IValueEnvelope value => new W3CResponse(Translate(value.Payload), value.Status),

        // VoidSessionResponse and VoidServerResponse. JSON Wire omits `value`
        // entirely; W3C requires it and spells "nothing happened" as null.
        _ => new W3CResponse(null, envelope.Status),
    };

    private static object? Translate(object? payload) => payload switch
    {
        ElementReference element => new W3CElementReference(element.Element),

        // GET /elements. Enumerated once into a list - the source may be a
        // deferred LINQ projection over a UIA result set, and serialising it
        // twice would walk the tree twice.
        IEnumerable<ElementReference> many => Rekey(many),

        _ => payload,
    };

    private static List<W3CElementReference> Rekey(IEnumerable<ElementReference> many)
    {
        List<W3CElementReference> rekeyed = [];
        foreach (ElementReference element in many)
        {
            rekeyed.Add(new W3CElementReference(element.Element));
        }

        return rekeyed;
    }
}
