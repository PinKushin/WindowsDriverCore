using Microsoft.AspNetCore.Http;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Drops empty segments from a request path before routing sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Because a client builds a URL with a missing session id and expects an
/// answer.</b> Selenium 3.8 clears its session id on <c>Quit()</c>, so a command
/// issued afterwards carries an empty segment:
/// </para>
/// <code>
/// GET /session//title
/// </code>
/// <para>
/// <c>MiscellaneousSessionError_StaleSessionId</c> requires the reply to begin
/// <c>No active session with ID title</c> — naming <b>title</b> as the session.
/// The reference is therefore not matching <c>/session/{sessionId}/title</c>
/// with an empty id; it drops the empty segment, leaving <c>/session/title</c>,
/// and answers for a session that does not exist. ASP.NET Core routing will not
/// match a required parameter against an empty segment, so without this the
/// request reached the unknown-command fallback and answered <c>404 jwp 9</c>.
/// </para>
/// <para>
/// <b>Normalisation, not a special case for that one word.</b> Selenium can send
/// any command after a quit — <c>/session//url</c>, <c>/session//window</c>,
/// <c>/session//element</c> — and a route added for <c>title</c> alone would
/// leave every one of those answering the wrong thing.
/// </para>
/// <para>
/// <b>Only empty segments.</b> <c>.</c> and <c>..</c> are left exactly as they
/// arrive: this collapses <c>//</c> and nothing else, so it cannot turn a path
/// into one that escapes anywhere. Nothing here reaches the file system in any
/// case — these paths select a route, not a file.
/// </para>
/// <para>
/// <b>Before <c>UseRouting</c>, after the transcript.</b> The log records the
/// path the client actually sent, which is the one worth reading when a client
/// is building URLs wrongly.
/// </para>
/// </remarks>
public sealed class EmptySegmentCollapse
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
    public EmptySegmentCollapse(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>Rewrites the path if it has an empty segment, then passes it on.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? path = context.Request.Path.Value;

        // The overwhelmingly common case, and it allocates nothing: no doubled
        // separator means nothing to collapse.
        if (path is not null && path.Contains("//", StringComparison.Ordinal))
        {
            context.Request.Path = new PathString(Collapse(path));
        }

        return _next(context);
    }

    /// <summary>Removes empty segments, keeping the leading separator.</summary>
    /// <remarks>
    /// A trailing separator is dropped with them, so <c>/session/abc/</c> and
    /// <c>/session/abc</c> select the same route — which is what dropping empty
    /// segments means, applied consistently. The root path stays <c>/</c>
    /// rather than becoming empty.
    /// </remarks>
    private static string Collapse(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }
}
