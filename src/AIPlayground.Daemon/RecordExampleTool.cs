using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentSharp.Core.Interfaces;
using AgentSharp.Core.Tools;

namespace AIPlayground.Daemon.Tools;

public sealed class RecordExampleTool : ITool
{
    public string Name => "record_example";
    public string Description => "Saves the current multi-step agent workflow and its resulting Lua code as an embedded example so you can retrieve it later. Use this when you have successfully completed a complex user request and want to remember the steps and code for future use.";

    public object Parameters => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["prompt"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The original user prompt that started this workflow."
            },
            ["tags"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A space-separated list of keywords describing the example (e.g., 'swep weapon melon launcher')."
            },
            ["final_lua_code"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The final, working Lua code that fulfilled the prompt."
            }
        },
        ["required"] = new JsonArray { "prompt", "tags", "final_lua_code" }
    };

    private readonly string _gilbaiExamplesDir;
    private readonly MemoryRetrievalSystem _memorySystem;
    private readonly AgentOrchestrator _bridge; // We need this to get the chat history to extract tool calls

    public RecordExampleTool(string gilbaiExamplesDir, MemoryRetrievalSystem memorySystem, AgentOrchestrator bridge)
    {
        _gilbaiExamplesDir = gilbaiExamplesDir;
        _memorySystem = memorySystem;
        _bridge = bridge;
    }

    public async Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = arguments.RootElement;
            var prompt = root.GetProperty("prompt").GetString() ?? "";
            var tags = root.GetProperty("tags").GetString() ?? "";
            var finalCode = root.GetProperty("final_lua_code").GetString() ?? "";

            var history = _bridge.GetChatHistory();
            var toolCalls = new List<ToolCallExample>();

            // Walk backwards through history to find the most recent tool calls related to this request
            // This is a simplified extraction; it grabs all tool calls since the last user message
            bool foundUserMessage = false;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var msg = history[i];
                if (msg.Role == "user")
                {
                    foundUserMessage = true;
                    break;
                }

                if (msg.Role == "assistant" && msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        if (tc.Function.Name == "record_example") continue; // Ignore self

                        // Try to find the corresponding result
                        string? resultStr = null;
                        for (int j = i + 1; j < history.Count; j++)
                        {
                            var rMsg = history[j];
                            if (rMsg.Role == "tool" && rMsg.ToolCallId == tc.Id)
                            {
                                resultStr = rMsg.Content;
                                break;
                            }
                        }

                        object? parsedArgs = null;
                        try { parsedArgs = JsonSerializer.Deserialize<object>(tc.Function.Arguments); } catch { }
                        
                        object? parsedResult = resultStr; // Keep as string for simplicity, or parse if JSON

                        toolCalls.Insert(0, new ToolCallExample
                        {
                            Tool = tc.Function.Name,
                            Args = parsedArgs ?? tc.Function.Arguments,
                            Result = parsedResult
                        });
                    }
                }
            }

            var meta = new GilbAIMeta
            {
                Prompt = prompt,
                Tags = tags,
                ToolCalls = toolCalls
            };

            string folderName = Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + prompt.Replace(" ", "_").ToLower().Substring(0, Math.Min(prompt.Length, 20));
            folderName = string.Join("_", folderName.Split(Path.GetInvalidFileNameChars()));
            
            string newFolderPath = Path.Combine(_gilbaiExamplesDir, folderName);
            Directory.CreateDirectory(newFolderPath);

            var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(newFolderPath, "meta.json"), metaJson);
            await File.WriteAllTextAsync(Path.Combine(newFolderPath, "response.lua"), finalCode);

            var newExample = new LuaExample
            {
                Description = string.IsNullOrWhiteSpace(tags) ? prompt : $"{prompt} (Tags: {tags})",
                Tags = tags,
                ToolCalls = toolCalls,
                Code = finalCode
            };

            await _memorySystem.AddLiveExampleAsync(newExample);

            return ToolResult.Ok("Successfully recorded the workflow as a reusable embedded example!");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
