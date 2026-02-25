using System.Text.Json;
using System.Text.Json.Nodes;
using AIPlayground.Daemon;
using AIPlayground.Daemon.Transports;
using AgentSharp.Core.Interfaces;
using AgentSharp.Core.Backends;

namespace AIPlayground;

/// <summary>
/// Main entry point for the C# AIPlayground Daemon that talks to Garry's Mod
/// </summary>
public static class Program
{
    private static CancellationTokenSource _cts = new();

    public static async Task Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  AIPlayground GMod Agent Daemon");
        Console.WriteLine("========================================");

        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Shutting down daemon...");
            e.Cancel = true;
            _cts.Cancel();
        };

        // Resolve config to get real Addon path
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
        var gmodBasePath = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod";
        var addonWorkspace = @"E:\AIPlayground\src\AIPlayground.GMod"; // Fallback
        
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonNode.Parse(json);
                if (config?["GModAddonsPath"] != null)
                {
                    addonWorkspace = config["GModAddonsPath"]!.ToString();
                    gmodBasePath = Directory.GetParent(Directory.GetParent(addonWorkspace)!.FullName)!.FullName;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNING] Failed to load config.json: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[WARNING] No config.json found! Run setup_config.bat first to set your GMod path.");
            Console.WriteLine($"[WARNING] Defaulting to dummy workspace: {addonWorkspace}");
            Console.WriteLine();
        }

        // Load the OpenRouter Key from Environment Variable
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: OPENROUTER_API_KEY is not set.");
            return;
        }

        var openRouterBackend = new OpenRouterBackend(apiKey);

        var wsTransport = new WebSocketTransport(27016);
        var server = new GModFileBridge(gmodBasePath, addonWorkspace, openRouterBackend, wsTransport);

        try
        {
            await server.StartAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Exited gracefully.");
        }
    }
}