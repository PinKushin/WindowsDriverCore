namespace WindowsDriverCore.Sessions;

public class SessionContext
{
    public string SessionId { get; }
    public int ProcessId { get; set; }
    public IntPtr MainWindowHandle { get; set; }
    public Dictionary<string, object> Capabilities { get; }

    public SessionContext(string sessionId, int processId, IntPtr mainWindowHandle, Dictionary<string, object> capabilities)
    {
        SessionId = sessionId;
        ProcessId = processId;
        MainWindowHandle = mainWindowHandle;
        Capabilities = capabilities;
    }
}

public interface ISessionStore
{
    SessionContext Create(int processId, IntPtr mainWindowHandle, Dictionary<string, object> capabilities);
    SessionContext? Get(string sessionId);
    IReadOnlyList<SessionContext> GetAll();
    void Remove(string sessionId);
}
