using System.Collections.Generic;

namespace WindowsDriverCore.Protocol.Sessions;

/// <summary>
/// Holds the live sessions.
/// </summary>
public interface ISessionStore
{
    /// <summary>Adds a session.</summary>
    /// <param name="session">The session to add.</param>
    void Add(DriverSession session);

    /// <summary>Looks up a session.</summary>
    /// <param name="sessionId">The session id from the request path.</param>
    /// <returns>The session, or null if no session has that id.</returns>
    DriverSession? Find(string sessionId);

    /// <summary>Removes a session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns>
    /// The removed session, or null if no session had that id. Returning it lets
    /// the caller shut the application down without a second lookup that could
    /// race another request.
    /// </returns>
    DriverSession? Remove(string sessionId);

    /// <summary>Every live session, in creation order.</summary>
    /// <returns>The sessions.</returns>
    IReadOnlyList<DriverSession> All();

    /// <summary>Removes every session, unconditionally.</summary>
    /// <remarks>
    /// <b>Not a production operation — a test seam.</b> Nothing in the running
    /// server has a reason to drop every session at once; a real client deletes
    /// its own. This exists so a test fixture sharing one
    /// <c>WebApplicationFactory</c> across many <c>[Test]</c> methods can start
    /// each one from a known-empty store, including after a PREVIOUS test threw
    /// before reaching its own cleanup — the case self-cleanup through
    /// <c>DELETE /session</c> cannot cover, because a test that never finishes
    /// never calls it.
    /// </remarks>
    void Clear();
}
