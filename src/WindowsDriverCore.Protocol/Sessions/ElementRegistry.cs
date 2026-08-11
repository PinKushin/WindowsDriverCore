using System.Collections.Concurrent;

namespace WindowsDriverCore.Protocol.Sessions;

/// <summary>
/// Remembers which element ids this server has handed to a client.
/// </summary>
/// <remarks>
/// <para>
/// It exists to separate two failures that look identical to UI Automation. An
/// element id that no longer resolves is either <b>stale</b> — issued by this
/// server, and the element has since gone — or <b>unknown</b>, an id that was
/// never issued. WinAppDriver answers status 10 for the first and status 7 for
/// the second, and the compatibility suite asserts on both.
/// </para>
/// <para>
/// <b>Ids are stored, not elements.</b> Holding COM element references between
/// calls is the design that produces WinAppDriver's #857 and #1079: the held
/// view drifts from the live tree and searches start answering about a UI that
/// no longer exists. A set of strings has nothing to drift from, which is why
/// this distinction is available to this driver at all — WinAppDriver gets it as
/// a side effect of a cache, and pays for the cache.
/// </para>
/// </remarks>
public interface IElementRegistry
{
    /// <summary>Records an id handed to a client.</summary>
    /// <param name="sessionId">The session the id was issued under.</param>
    /// <param name="elementId">The element id.</param>
    void Record(string sessionId, string elementId);

    /// <summary>
    /// Asks whether an id was issued, and forgets it if it was.
    /// </summary>
    /// <param name="sessionId">The session asking.</param>
    /// <param name="elementId">The element id that failed to resolve.</param>
    /// <returns>
    /// <see langword="true"/> when this server issued the id, meaning the
    /// element is stale rather than unknown.
    /// </returns>
    /// <remarks>
    /// Destructive by design, and measured: through real WinAppDriver the first
    /// touch of a dead element answers 10 and every touch after it answers 7.
    /// Recorded as <c>error.element.stale.text</c> (400/10) followed by
    /// <c>error.element.stale.click</c> and friends (404/7) against the same id.
    /// </remarks>
    bool TryConsume(string sessionId, string elementId);

    /// <summary>Drops everything recorded for a session.</summary>
    /// <param name="sessionId">The session being deleted.</param>
    void Forget(string sessionId);

    /// <summary>How many ids are held for a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The count, zero for a session that has found nothing.</returns>
    /// <remarks>
    /// For diagnostics and for tests that measure growth. The recommended Appium
    /// arrangement is one session for a whole suite, so this number is the
    /// session's accumulated cost and is worth being able to state rather than
    /// guess at.
    /// </remarks>
    int CountFor(string sessionId);

    /// <summary>Drops everything recorded for every session, unconditionally.</summary>
    /// <remarks>
    /// A test seam, not a production operation — see
    /// <see cref="ISessionStore.Clear"/>, which this exists for the same reason
    /// as.
    /// </remarks>
    void Clear();
}

/// <inheritdoc />
public sealed class ElementRegistry : IElementRegistry
{
    // Keyed by session because runtime ids are process-scoped, and two sessions
    // can drive the same application. Reporting another session's id as stale
    // would claim "this used to exist" about something the client never saw.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _bySession =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void Record(string sessionId, string elementId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(elementId);

        _bySession
            .GetOrAdd(sessionId, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            .TryAdd(elementId, 0);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public bool TryConsume(string sessionId, string elementId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(elementId);

        // TryRemove is the test and the consumption in one atomic step. Reading
        // then removing would let two concurrent commands on the same dead
        // element both report status 10.
        return _bySession.TryGetValue(sessionId, out ConcurrentDictionary<string, byte>? issued) &&
            issued.TryRemove(elementId, out _);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    public int CountFor(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        return _bySession.TryGetValue(sessionId, out ConcurrentDictionary<string, byte>? issued)
            ? issued.Count
            : 0;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    public void Forget(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        // A session that was never seen is not an error: DELETE /session runs
        // for sessions that never found an element.
        _bySession.TryRemove(sessionId, out _);
    }

    /// <inheritdoc />
    public void Clear() => _bySession.Clear();
}
