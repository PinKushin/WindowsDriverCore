using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using WindowsDriverCore.Diagnostics;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Records every request that reaches the server.
/// </summary>
/// <remarks>
/// <para>
/// <b>The diagnostic that was missing.</b> Every question asked of this driver
/// during the 2026-08-11 compatibility work — which request failed, with what
/// status, at which point in a flow — needed a bespoke probe, because the server
/// said nothing about what it had answered. WinAppDriver prints its own
/// transcript, which is the only reason its failures are readable.
/// </para>
/// <para>
/// <b>Outermost in the pipeline, deliberately.</b> Registered before the base
/// path gate so a request rejected for the wrong prefix is recorded too. A
/// transcript with a silent hole in it is worse than no transcript, because the
/// hole is invisible.
/// </para>
/// <para>
/// <b>It records, it does not interfere.</b> No buffering, no wrapping of the
/// response stream, and nothing on the response itself. The JSON Wire status
/// comes from <see cref="JsonWireStatusFilter"/> by way of
/// <see cref="HttpContext.Items"/>, so this type never touches a payload — which
/// is also why there is no path here that could write a caller's data anywhere.
/// </para>
/// </remarks>
public sealed class RequestLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRequestLog _log;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="log">Where finished requests are recorded.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public RequestLogMiddleware(RequestDelegate next, IRequestLog log)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(log);

        _next = next;
        _log = log;
    }

    /// <summary>Runs the request and records how it ended.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        long began = Stopwatch.GetTimestamp();

        // The path is read BEFORE the request runs. UsePathBase rewrites it, so
        // reading afterwards would log the stripped form and a server started
        // with a base path would produce a transcript that no client's requests
        // match.
        string route = context.Request.Path.Value ?? string.Empty;
        string method = context.Request.Method;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // In a finally, so an exception escaping the pipeline still leaves a
            // record. An unhandled fault is exactly the request most worth having
            // in the transcript, and it is the one a happy-path log would drop.
            _log.RequestCompleted(
                method,
                route,
                context.Response.StatusCode,
                JsonWireStatusOf(context),
                Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        }
    }

    private static int JsonWireStatusOf(HttpContext context) =>
        context.Items.TryGetValue(JsonWireStatusFilter.ItemKey, out object? status) &&
        status is int value
            ? value
            : IRequestLog.NoJsonWireStatus;
}
