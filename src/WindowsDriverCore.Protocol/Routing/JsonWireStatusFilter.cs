using Microsoft.AspNetCore.Http;
using WindowsDriverCore.Protocol.Responses;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Records each response's JSON Wire status where the request log can read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A filter rather than parsing the body.</b> The status lives inside the
/// serialized envelope, which middleware cannot see without buffering and
/// re-reading the response — and a <c>GET /source</c> body is tens of kilobytes,
/// so that would mean paying for the whole payload twice to learn one integer.
/// An endpoint filter runs while the result is still an object.
/// </para>
/// <para>
/// It writes to <see cref="HttpContext.Items"/> and nothing else. The filter has
/// no opinion about logging and the middleware has no opinion about envelopes,
/// which is what keeps the log out of the routing layer's business.
/// </para>
/// <para>
/// <b>Absence is meaningful and is left absent.</b> <c>GET /status</c> has no
/// envelope, and a request that never reaches an endpoint at all — the base-path
/// gate's 404 — never runs this. Both cases leave the key unset, and the log
/// reports <c>IRequestLog.NoJsonWireStatus</c> rather than inventing a zero.
/// </para>
/// </remarks>
public sealed class JsonWireStatusFilter : IEndpointFilter
{
    /// <summary>Where the status is parked for the request log.</summary>
    internal const string ItemKey = "WindowsDriverCore.JsonWireStatus";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        object? result = await next(context).ConfigureAwait(false);

        // IValueHttpResult is what Results.Json produces, and it exposes the
        // object that is about to be serialized. Anything else - a bare status
        // code, a stream - carries no envelope and is correctly ignored.
        if (result is IValueHttpResult valued && valued.Value is IJsonWireEnvelope envelope)
        {
            context.HttpContext.Items[ItemKey] = envelope.Status;
        }

        return result;
    }
}
