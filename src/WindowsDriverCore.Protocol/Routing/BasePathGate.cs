using Microsoft.AspNetCore.Http;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Rejects requests that do not sit under the configured base path.
/// </summary>
/// <remarks>
/// <c>UsePathBase</c> alone is not enough. It strips the prefix when a request
/// carries it and otherwise lets the request through untouched, so a server
/// started with <c>4723/wd/hub</c> would answer both <c>/wd/hub/status</c> and
/// bare <c>/status</c>.
///
/// WinAppDriver does not. Measured against 1.2.2009.02003 started with
/// <c>127.0.0.1 4728/wd/hub</c>: <c>/wd/hub/status</c> returns 200 and bare
/// <c>/status</c> returns 404. Its 404 is an http.sys HTML page rather than a
/// JSON Wire envelope, because the request never reaches the driver at all —
/// which is why this gate runs before routing and answers with a bare status
/// code rather than the unknown-command fault.
/// </remarks>
public sealed class BasePathGate
{
    private readonly RequestDelegate _next;
    private readonly string _basePath;

    /// <summary>Creates the gate.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="basePath">The base path, for example <c>/wd/hub</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public BasePathGate(RequestDelegate next, string basePath)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(basePath);

        _next = next;
        _basePath = basePath;
    }

    /// <summary>Passes the request on, or rejects it with 404.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsUnderBasePath(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _next(context);
    }

    /// <summary>
    /// Whether a path sits under the base path.
    /// </summary>
    /// <remarks>
    /// The segment boundary matters: with a base path of <c>/wd/hub</c>, the
    /// path <c>/wd/hubbub</c> is not under it. A plain <c>StartsWith</c> would
    /// admit it.
    /// </remarks>
    private bool IsUnderBasePath(PathString path)
    {
        string value = path.Value ?? string.Empty;

        if (!value.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.Length == _basePath.Length || value[_basePath.Length] == '/';
    }
}
