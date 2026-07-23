using System.Collections.Concurrent;

namespace WindowsDriverCore.Sessions;

public class SessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();

    public SessionContext Create(Dictionary<string, object> capabilities)
    {
        var sessionId = Guid.NewGuid().ToString();
        var context = new SessionContext(sessionId, 0, IntPtr.Zero, capabilities);
        _sessions[sessionId] = context;
        return context;
    }

    public SessionContext? Get(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var context);
        return context;
    }

    public void Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }
}
