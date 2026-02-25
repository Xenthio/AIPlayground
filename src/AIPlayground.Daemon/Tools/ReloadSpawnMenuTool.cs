using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;

namespace AIPlayground.Daemon.Tools;

/// <summary>
/// A tool that allows the AI to force the client to reload their Q-Menu (Spawn Menu)
/// </summary>
public sealed class ReloadSpawnMenuTool : ITool
{
    private readonly Action<string> _onQueueAction;

    public ReloadSpawnMenuTool(Action<string> onQueueAction)
    {
        _onQueueAction = onQueueAction;
    }

    public string Name => "reload_spawn_menu";
    public string Description => "Forces the Garry's Mod client to reload their Spawn Menu (Q-Menu). Call this after creating a brand new SWEP or Entity so that the new 'AI Creations' category appears instantly without the player needing to rejoin.";

    public object Parameters => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {}
    }
    """)!;

    public Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            // Send our special keyword to the client
            _onQueueAction("!SPAWNMENU");
            return Task.FromResult(ToolResult.Ok("Successfully forced the player's Garry's Mod client to reload their Spawn Menu."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to reload spawn menu: {ex.Message}"));
        }
    }
}