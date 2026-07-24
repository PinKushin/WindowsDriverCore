using System.Text.Json.Serialization;

namespace WindowsDriverCore.Messages;

public record Capabilities(
    [property: JsonPropertyName("alwaysMatch")] Dictionary<string, object>? AlwaysMatch,
    [property: JsonPropertyName("firstMatch")] List<Dictionary<string, object>>? FirstMatch);

public record SessionRequest(
    [property: JsonPropertyName("capabilities")] Capabilities? Capabilities,
    [property: JsonPropertyName("desiredCapabilities")] Dictionary<string, object>? DesiredCapabilities);
