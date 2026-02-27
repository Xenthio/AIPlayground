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
    public string Description => "List all files and folders currently in your virtual addons root directory. Use this to see what projects you've already created before deciding to make a new one!";

    public object Parameters => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "Optional subdirectory to list (e.g. my_cool_addon/lua/weapons/). Defaults to root."
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

            // If the AI asks to list a specific addon, make sure we append the ~ prefix so it looks in the right real folder
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                var parts = relativePath.Split('/', '\\');
                if (parts.Length > 0 && !parts[0].StartsWith("~"))
                {
                    parts[0] = "~" + parts[0];
                    relativePath = string.Join(Path.DirectorySeparatorChar, parts);
                }
            }

            var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, relativePath));
            
            if (!fullPath.StartsWith(_workspacePath))
                return Task.FromResult(ToolResult.Fail("Access denied: path escapes workspace."));

            if (!Directory.Exists(fullPath))
                return Task.FromResult(ToolResult.Fail($"Directory not found: {relativePath}"));

            var entries = new List<string>();
            foreach (var d in Directory.GetDirectories(fullPath))
            {
                var relativeName = Path.GetRelativePath(_workspacePath, d).Replace('\\', '/');
                
                // If we are listing the root, ONLY return folders that start with ~, but hide the ~ from the AI
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    var folderName = Path.GetFileName(d);
                    if (!folderName.StartsWith("~"))
                        continue;
                    
                    relativeName = folderName.Substring(1) + "/";
                }
                // If we are deep in a folder, just return it but strip the root ~ so the AI doesn't get confused
                else
                {
                    var parts = relativeName.Split('/');
                    if (parts.Length > 0 && parts[0].StartsWith("~"))
                        parts[0] = parts[0].Substring(1);
                    relativeName = string.Join("/", parts) + "/";
                }
                
                entries.Add($"[DIR]  {relativeName}");
            }
            foreach (var f in Directory.GetFiles(fullPath))
            {
                var relativeName = Path.GetRelativePath(_workspacePath, f).Replace('\\', '/');
                
                // Strip the ~ from the root folder name in the output so the AI stays blissfully unaware
                var parts = relativeName.Split('/');
                if (parts.Length > 0 && parts[0].StartsWith("~"))
                    parts[0] = parts[0].Substring(1);
                relativeName = string.Join("/", parts);

                entries.Add($"[FILE] {relativeName}");
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