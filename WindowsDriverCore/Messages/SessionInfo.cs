namespace WindowsDriverCore.Messages;

public record SessionInfo(string SessionId, Dictionary<string, object> Capabilities);
