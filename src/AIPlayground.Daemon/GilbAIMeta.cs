using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AIPlayground.Daemon;

public sealed class GilbAIMeta
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
    
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("toolcalls")]
    public List<ToolCallExample>? ToolCalls { get; set; }
    
    [JsonPropertyName("is_docs")]
    public bool IsDocs { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

[JsonSerializable(typeof(GilbAIMeta))]
[JsonSerializable(typeof(float[]))]
internal partial class GilbAIJsonContext : JsonSerializerContext
{
}
