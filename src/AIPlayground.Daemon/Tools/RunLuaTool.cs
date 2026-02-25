using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;
using AIPlayground.Daemon.Transports;

namespace AIPlayground.Daemon.Tools;

public sealed class RunLuaTool : ITool
{
    private readonly IGModTransport _transport;

    public RunLuaTool(IGModTransport transport)
    {
        _transport = transport;
    }

    public string Name => "run_lua";
    public string Description => "Execute a block of raw Lua code immediately in the Garry's Mod server environment. Use this to quickly test concepts, spawn props, or run one-off scripts without needing to create a permanent file.";
    public object Parameters => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["code"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The raw Lua code string to execute globally on the server."
            }
        },
        ["required"] = new JsonArray { "code" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = JsonNode.Parse(arguments.RootElement.GetRawText());
            var code = node?["code"]?.ToString();
            
            if (string.IsNullOrWhiteSpace(code))
            {
                return ToolResult.Fail("No Lua code provided.");
            }

            await _transport.RunLuaAsync(code);

            return ToolResult.Ok("Successfully sent Lua code to the server for execution.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Exception occurred: {ex.Message}");
        }
    }
}