using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;

namespace AIPlayground.Daemon.Tools;

/// <summary>
/// A tool that allows the AI to execute a newly written Lua file directly on the GMod server
/// </summary>
public sealed class HotReloadFileTool : ITool
{
    private readonly string _workspacePath;
    private readonly Action<string> _onExecuteLua;

    public HotReloadFileTool(string workspacePath, Action<string> onExecuteLua)
    {
        _workspacePath = workspacePath;
        _onExecuteLua = onExecuteLua;
    }

    public string Name => "hot_reload";
    public string Description => "Load a Lua file you just created into the live server so the weapon/code works instantly. Example: `hot_reload(path = 'my_cool_addon/lua/weapons/weapon_cool.lua')`. This literally reads the file off the disk and runs it instantly on the server.";

    public object Parameters => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "The exact same path you used in write_file (e.g. my_cool_addon/lua/weapons/weapon_cool.lua)"
            }
        },
        "required": [ "path" ]
    }
    """)!;

    public async Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = arguments.RootElement.GetProperty("path").GetString() ?? "";
            
            if (string.IsNullOrWhiteSpace(path))
                return ToolResult.Fail("Path cannot be empty.");

            var fullPath = Path.GetFullPath(Path.Combine(_workspacePath, path));
            if (!fullPath.StartsWith(_workspacePath))
                return ToolResult.Fail("Access denied: path escapes workspace.");

            if (!File.Exists(fullPath))
                return ToolResult.Fail($"File not found: {path}");

            // Read the raw Lua code from the disk
            var code = await File.ReadAllTextAsync(fullPath, cancellationToken);

            // Give it to the GMod server queue to run globally via RunString()
            _onExecuteLua(code);

            return ToolResult.Ok($"Successfully hot reloaded {path} ({code.Length} bytes injected to live server).");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to hot reload: {ex.Message}");
        }
    }
}