namespace WindowsDriverCore.Sessions;

public record SessionContext(
    string SessionId,
    int ProcessId,
    IntPtr MainWindowHandle,
    Dictionary<string, object> Capabilities);

public interface ISessionStore
{
    SessionContext Create(Dictionary<string, object> capabilities);
    SessionContext? Get(string sessionId);
    void Remove(string sessionId);
}
