namespace WindowsDriverCore.Messages;

public record Capabilities(Dictionary<string, object> AlwaysMatch);

public record SessionRequest(Capabilities? Capabilities);
