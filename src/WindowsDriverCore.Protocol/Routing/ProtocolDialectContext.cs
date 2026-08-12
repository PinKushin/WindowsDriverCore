using Microsoft.AspNetCore.Http;
using WindowsDriverCore.Protocol.Sessions;

namespace WindowsDriverCore.Protocol.Routing;

/// <summary>
/// Which dialect the current request is being answered in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The routes never read this.</b> A handler builds one envelope and knows
/// nothing about who asked; <see cref="ProtocolDialectFilter"/> reshapes it on
/// the way out. That is the whole reason the dialect lives in
/// <see cref="HttpContext.Items"/> rather than being a parameter — sixty route
/// handlers each choosing a shape is sixty places to get it wrong, and the JSON
/// Wire suite would only catch the half of them it exercises.
/// </para>
/// <para>
/// Two writers, and they cover different requests.
/// <see cref="RequireSession.RequiresSession"/> writes the session's own dialect
/// for every session-scoped route; <c>POST /session</c> writes what it just
/// parsed, because at that point there is no session yet — and on a rejection
/// there never will be.
/// </para>
/// <para>
/// <b>Absent means JSON Wire.</b> <c>GET /status</c>, the unknown-command
/// fallback and anything that fails before a session is resolved all leave it
/// unset, and JSON Wire is what WinAppDriver answers there. Defaulting the other
/// way would change the reply to every client that never mentioned a dialect.
/// </para>
/// </remarks>
public static class ProtocolDialectContext
{
    private const string ItemKey = "WindowsDriverCore.Dialect";

    /// <summary>Records the dialect this request will be answered in.</summary>
    /// <param name="context">The current request.</param>
    /// <param name="dialect">The dialect.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static void Remember(HttpContext context, ProtocolDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemKey] = dialect;
    }

    /// <summary>The dialect recorded for this request.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>
    /// The recorded dialect, or <see cref="ProtocolDialect.JsonWire"/> when
    /// nothing recorded one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public static ProtocolDialect Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ItemKey, out object? value) && value is ProtocolDialect dialect
            ? dialect
            : ProtocolDialect.JsonWire;
    }
}
