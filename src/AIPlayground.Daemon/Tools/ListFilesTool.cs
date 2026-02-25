using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;

namespace AIPlayground.Daemon.Tools;

/// <summary>
/// A tool that allows the AI to list files in its projects folder
/// </summary>
public sealed class ListFilesTool : ITool
{
    private readonly string _workspacePath;

    public ListFilesTool(string workspacePath)
    {
        _workspacePath = workspacePath;
    }

    public string Name => "list_files";
    public string Description => "List all files and folders currently in the AIPlayground_Projects directory. Use this to see what projects you've already created before deciding to make a new one!";

    public object Parameters => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "Optional subdirectory to list (e.g. lua/ai_projects/). Defaults to root."
            }
        }
    }
    """)!;

    public Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var relativePath = "";
            if (arguments.RootElement.TryGetProperty("path", out var pathElement))
            {
                relativePath = pathElement.GetString() ?? "";
            }

            var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, relativePath));
            
            if (!fullPath.StartsWith(_workspacePath))
                return Task.FromResult(ToolResult.Fail("Access denied: path escapes workspace."));

            if (!Directory.Exists(fullPath))
                return Task.FromResult(ToolResult.Fail($"Directory not found: {relativePath}"));

            var entries = new List<string>();
            foreach (var d in Directory.GetDirectories(fullPath))
            {
                entries.Add($"[DIR]  {Path.GetRelativePath(_workspacePath, d).Replace('\\', '/')}/");
            }
            foreach (var f in Directory.GetFiles(fullPath))
            {
                entries.Add($"[FILE] {Path.GetRelativePath(_workspacePath, f).Replace('\\', '/')}");
            }

            if (entries.Count == 0)
                return Task.FromResult(ToolResult.Ok($"Directory '{relativePath}' is empty."));

            return Task.FromResult(ToolResult.Ok($"Contents of {relativePath}:\n{string.Join("\n", entries)}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to list files: {ex.Message}"));
        }
    }
}