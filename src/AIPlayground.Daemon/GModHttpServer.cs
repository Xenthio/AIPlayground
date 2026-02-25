using System.Text;
using System.Text.Json;
using System.Net;
using AgentSharp.Core.Interfaces;
using AgentSharp.Core.Tools;
using System.Text.Json.Nodes;

namespace AIPlayground.Daemon;

/// <summary>
/// A minimal HTTP Server that Garry's Mod `http.Fetch` can talk to
/// </summary>
public sealed class GModHttpServer
{
    private readonly HttpListener _listener;
    private readonly string _addonPath;
    private readonly IBackendProvider _backend;
    private readonly List<ITool> _tools;
    private string _currentModel = "google/gemini-3-flash-preview";
    
    // A thread-safe queue of Lua strings for GMod to execute
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLuaExecution = new();

    public GModHttpServer(int port, string addonPath, IBackendProvider backend)
    {
        _addonPath = addonPath;
        _backend = backend;
        
        _tools = new List<ITool>
        {
            new WriteFileTool(_addonPath),
            new AIPlayground.Daemon.Tools.HotReloadFileTool(_addonPath, code => _pendingLuaExecution.Enqueue(code))
        };
        
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        Console.WriteLine($"[AIPlayground Daemon] Listening on {_listener.Prefixes.First()}");
        Console.WriteLine($"[AIPlayground Daemon] Bound to GMod Addon Path: {_addonPath}");

        // Cancel the listener directly when Ctrl+C is pressed so GetContextAsync aborts instantly
        using var registration = cancellationToken.Register(() => _listener.Stop());

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
            }
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.ErrorCode == 1229) // 995 = I/O aborted, 1229 = network connection aborted
        {
            // Expected during shutdown when _listener.Stop() is called
        }
        catch (ObjectDisposedException)
        {
            // Expected during shutdown
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var req = context.Request;
            var res = context.Response;

            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/chat")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var jsonBody = await reader.ReadToEndAsync();
                
                var payload = JsonNode.Parse(jsonBody);
                var prompt = payload?["prompt"]?.ToString() ?? "";
                var player = payload?["player"]?.ToString() ?? "Unknown";
                
                // Intercept !model commands directly in the daemon
                if (prompt.StartsWith("!model "))
                {
                    _currentModel = prompt.Substring(7).Trim();
                    Console.WriteLine($"[Daemon] Model switched to {_currentModel}");
                    
                    var switchRes = new { status = "ok", response = $"Switched backend model to {_currentModel}", is_model_switch = true };
                    var switchBuffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(switchRes));
                    res.ContentType = "application/json";
                    res.ContentLength64 = switchBuffer.Length;
                    await res.OutputStream.WriteAsync(switchBuffer);
                    res.Close();
                    return;
                }

                Console.WriteLine($"[GMOD CHAT] {player}: {prompt}");

                // Basic Agent Loop execution
                var messages = new List<ChatMessage>
                {
                    ChatMessage.System("You are a Garry's Mod Addon developer AI. Write clean, efficient Lua code and deploy it using your tools. Since you are building addons, ensure you specify a subfolder in `path` for the addon name, e.g., `my_cool_addon/lua/weapons/weapon_cool.lua`. Do NOT write directly to the `lua/` folder, always nest it inside an addon folder name so each creation is isolated."),
                    ChatMessage.User($"[From {player}]: {prompt}")
                };

                var completionReq = new CompletionRequest
                {
                    Model = _currentModel,
                    Messages = messages,
                    Tools = _tools.Select(t => t.GetDefinition()).ToList(),
                    Temperature = 0.7
                };

                Console.WriteLine("[Daemon] Sending prompt to LLM...");
                var response = await _backend.GenerateCompletionAsync(completionReq);

                // Handle Tool Calls if the LLM wants to execute code
                if (response.ToolCalls != null && response.ToolCalls.Any())
                {
                    foreach (var tc in response.ToolCalls)
                    {
                        var tool = _tools.FirstOrDefault(t => t.Name == tc.Function.Name);
                        if (tool != null)
                        {
                            Console.WriteLine($"[Daemon] Executing Tool: {tool.Name}");
                            var args = JsonDocument.Parse(tc.Function.Arguments);
                            var result = await tool.ExecuteAsync(args);
                            Console.WriteLine($"[Daemon] Tool Result: {(result.Success ? "Success" : "Failed")}");
                        }
                    }
                }

                // Send the text response back to GMod chat (plus any pending lua scripts)
                var pendingScripts = new List<string>();
                while (_pendingLuaExecution.TryDequeue(out var script))
                {
                    pendingScripts.Add(script);
                }

                var responseObj = new { status = "ok", response = response.Content, scripts = pendingScripts };
                var responseString = JsonSerializer.Serialize(responseObj);
                
                var buffer = Encoding.UTF8.GetBytes(responseString);
                res.ContentType = "application/json";
                res.ContentLength64 = buffer.Length;
                await res.OutputStream.WriteAsync(buffer);
            }
            else
            {
                res.StatusCode = 404;
            }
            
            res.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Daemon Error] {ex}");
        }
    }
}