using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace WindowsDriverCore.Protocol.Sessions;

/// <summary>
/// In-memory session store, safe for concurrent use.
/// </summary>
/// <remarks>
/// Concurrency here is correctness, not performance: one server handles every
/// client, and a suite running tests in parallel adds and removes sessions from
/// several threads at once.
///
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> does not preserve insertion
/// order, and <c>GET /sessions</c> lists sessions in the order they were
/// created, so each entry carries a sequence number and <see cref="All"/> sorts
/// by it. Ordinal comparison because session ids are opaque GUIDs the server
/// generated; matching them loosely would let a client reach a session it did
/// not create.
/// </remarks>
public sealed class SessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _sessions =
        new(StringComparer.Ordinal);

    private long _sequence;

    /// <inheritdoc />
    public void Add(DriverSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        long order = Interlocked.Increment(ref _sequence);
        _sessions[session.Id] = new Entry(session, order);
    }

    /// <inheritdoc />
    public DriverSession? Find(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        return _sessions.TryGetValue(sessionId, out Entry? entry) ? entry.Session : null;
    }

    /// <inheritdoc />
    public DriverSession? Remove(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        return _sessions.TryRemove(sessionId, out Entry? entry) ? entry.Session : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<DriverSession> All() =>
        _sessions.Values
            .OrderBy(entry => entry.Order)
            .Select(entry => entry.Session)
            .ToList();

    private sealed record Entry(DriverSession Session, long Order);
}
