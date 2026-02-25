using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;

namespace AIPlayground.Daemon.Tools;

/// <summary>
/// A tool that allows the AI to read the contents of a file
/// </summary>
public sealed class ReadFileTool : ITool
{
    private readonly string _workspacePath;

    public ReadFileTool(string workspacePath)
    {
        _workspacePath = workspacePath;
    }

    public string Name => "read_file";
    public string Description => "Read the contents of a file so you can inspect its current code before editing it or diagnosing an error. Path is relative to the AIPlayground_Projects root.";

    public object Parameters => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "Relative path to read from (e.g., lua/ai_projects/my_project/shared.lua)"
            }
        },
        "required": [ "path" ]
    }
    """)!;

    public async Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = arguments.RootElement;
            var path = root.GetProperty("path").GetString() ?? "";

            // Security: Prevent directory traversal
            var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, path));
            if (!fullPath.StartsWith(_workspacePath))
                return ToolResult.Fail("Access denied: path escapes workspace.");

            if (!File.Exists(fullPath))
                return ToolResult.Fail($"File not found: {path} (Absolute checked: {fullPath})");

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            return ToolResult.Ok(content);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to read file: {ex.Message}");
        }
    }
}