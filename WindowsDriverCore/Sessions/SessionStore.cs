using System.Collections.Concurrent;

namespace WindowsDriverCore.Sessions;

public class SessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionContext> _sessions = new();

    public SessionContext Create(int processId, IntPtr mainWindowHandle, Dictionary<string, object> capabilities)
    {
        var sessionId = Guid.NewGuid().ToString();
        var context = new SessionContext(sessionId, processId, mainWindowHandle, capabilities);
        _sessions[sessionId] = context;
        return context;
    }

    public SessionContext? Get(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var context);
        return context;
    }

    public IReadOnlyList<SessionContext> GetAll()
    {
        return _sessions.Values.ToList().AsReadOnly();
    }

    public void Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }
}
