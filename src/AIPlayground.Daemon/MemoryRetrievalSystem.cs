using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AgentSharp.Core.Interfaces;

namespace AIPlayground.Daemon;

public sealed class ToolCallExample
{
    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("args")]
    public object? Args { get; set; }

    [JsonPropertyName("result")]
    public object? Result { get; set; }
}

public sealed class LuaExample
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("description")]
    public required string Description { get; init; }
    
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }
    
    [JsonPropertyName("toolcalls")]
    public List<ToolCallExample>? ToolCalls { get; set; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("is_docs")]
    public bool IsDoc { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

[JsonSerializable(typeof(List<LuaExample>))]
internal partial class ExampleMemoryJsonContext : JsonSerializerContext
{
}

public class MemoryRetrievalSystem
{
    private readonly IBackendProvider _backend;
    private readonly string _gilbaiExamplesDir;
    private List<LuaExample> _examples = new();

    public MemoryRetrievalSystem(IBackendProvider backend, string gilbaiExamplesDir)
    {
        _backend = backend;
        _gilbaiExamplesDir = gilbaiExamplesDir;
    }

    public async Task InitializeAsync()
    {
        if (!Directory.Exists(_gilbaiExamplesDir))
        {
            Directory.CreateDirectory(_gilbaiExamplesDir);
        }

        foreach (var folder in Directory.GetDirectories(_gilbaiExamplesDir))
        {
            try
            {
                var metaPath = Path.Combine(folder, "meta.json");
                var codePath = Path.Combine(folder, "response.lua");

                if (File.Exists(metaPath) && File.Exists(codePath))
                {
                    var json = await File.ReadAllTextAsync(metaPath);
                    var metaObj = JsonSerializer.Deserialize(json, GilbAIJsonContext.Default.GilbAIMeta);
                    var code = await File.ReadAllTextAsync(codePath);

                    if (metaObj != null && !string.IsNullOrWhiteSpace(metaObj.Prompt) && !string.IsNullOrWhiteSpace(code))
                    {
                        string desc = metaObj.Prompt.Trim();
                        if (!string.IsNullOrWhiteSpace(metaObj.Tags))
                        {
                            desc += $" (Tags: {metaObj.Tags.Trim()})";
                        }

                        var embeddingPath = Path.Combine(folder, ".embedding");
                        float[]? embeddingData = null;

                        if (File.Exists(embeddingPath))
                        {
                            var embeddingJson = await File.ReadAllTextAsync(embeddingPath);
                            embeddingData = JsonSerializer.Deserialize<float[]>(embeddingJson, GilbAIJsonContext.Default.SingleArray);
                        }

                        // Generate missing embedding if not present in .embedding file
                        if (embeddingData == null || embeddingData.Length == 0)
                        {
                            Console.WriteLine($"[Memory] Generating missing embedding for: {desc}");
                            embeddingData = await _backend.GenerateEmbeddingAsync(desc);
                            
                            // Save back the new embedding to .embedding file
                            var embeddingJson = JsonSerializer.Serialize(embeddingData, GilbAIJsonContext.Default.SingleArray);
                            await File.WriteAllTextAsync(embeddingPath, embeddingJson);
                        }

                        _examples.Add(new LuaExample
                        {
                            Description = desc,
                            Tags = metaObj.Tags?.Trim(),
                            ToolCalls = metaObj.ToolCalls,
                            Code = code.Trim(),
                            Embedding = embeddingData,
                            IsDoc = metaObj.IsDocs
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Memory] Failed to parse GilbAI example in {Path.GetFileName(folder)}: {ex.Message}");
            }
        }
    }

    public async Task AddLiveExampleAsync(LuaExample example)
    {
        Console.WriteLine($"[Memory] Embedding new live example: {example.Description}");
        example.Embedding = await _backend.GenerateEmbeddingAsync(example.Description);
        
        _examples.Add(example);
        // The actual file writing is handled by the RecordExampleTool to keep folder structure
    }

    public async Task<List<(string Description, float Score, bool IsDoc)>> SearchAsync(string query, int topK = 5)
    {
        if (_examples.Count == 0) return new();

        var queryEmbedding = await _backend.GenerateEmbeddingAsync(query);

        return _examples
            .Select(ex => (
                Description: ex.Description,
                Score: TensorPrimitives.CosineSimilarity(queryEmbedding, ex.Embedding!),
                IsDoc: ex.IsDoc
            ))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<string> GetRelevantExamplesAsync(string query, int topK = 2)
    {
        if (_examples.Count == 0) return string.Empty;

        var queryEmbedding = await _backend.GenerateEmbeddingAsync(query);

        var allRanked = _examples
            .Select(ex => new {
                Example = ex,
                Score = TensorPrimitives.CosineSimilarity(queryEmbedding, ex.Embedding!)
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var docs = allRanked
            .Where(x => x.Example.IsDoc && x.Score >= 0.68f)
            .Take(topK)
            .ToList();

        var examples = allRanked
            .Where(x => !x.Example.IsDoc && x.Score >= 0.3f)
            .Take(topK)
            .ToList();

        if (docs.Count == 0 && (examples.Count == 0 || examples[0].Score < 0.3f))
            return string.Empty;

        var sb = new System.Text.StringBuilder();

        foreach (var match in docs)
        {
            sb.AppendLine($"## Documentation - {match.Example.Description}");
            sb.AppendLine(match.Example.Code);
            sb.AppendLine();
        }

        if (examples.Count > 0)
        {
            sb.AppendLine("## Examples");
            foreach (var match in examples)
            {
                sb.AppendLine($"Player Prompt: {match.Example.Description}");

                if (match.Example.ToolCalls != null && match.Example.ToolCalls.Count > 0)
                {
                    sb.AppendLine("Toolcalls:");
                    foreach (var tc in match.Example.ToolCalls)
                    {
                        var argsStr = tc.Args != null ? JsonSerializer.Serialize(tc.Args) : "{}";
                        var resStr = tc.Result != null ? JsonSerializer.Serialize(tc.Result) : "{}";
                        sb.AppendLine($"- {tc.Tool}({argsStr}) -> {resStr}");
                    }
                }

                sb.AppendLine("Response:");
                sb.AppendLine("```lua");
                sb.AppendLine(match.Example.Code);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
