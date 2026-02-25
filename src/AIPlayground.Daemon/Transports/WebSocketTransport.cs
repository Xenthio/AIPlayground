using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIPlayground.Daemon.Transports;

public class WebSocketTransport : IGModTransport
{
    private readonly int _port;
    private WebSocket? _activeClient;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    
    public event EventHandler<IncomingPromptEventArgs>? OnPromptReceived;

    public WebSocketTransport(int port = 27015)
    {
        _port = port;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_port}/");
        listener.Start();
        
        // Ensure the listener stops if cancellation is requested
        using var reg = cancellationToken.Register(() => listener.Stop());

        Console.WriteLine($"[WebSocketTransport] Listening on ws://localhost:{_port}/");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    
                    // If we already have an active connection, close the old one
                    if (_activeClient != null && _activeClient.State == WebSocketState.Open)
                    {
                        Console.WriteLine("[WebSocketTransport] New client connected, closing old connection...");
                        await _activeClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "New connection established", CancellationToken.None);
                    }

                    _activeClient = wsContext.WebSocket;
                    Console.WriteLine($"[WebSocketTransport] Client connected!");

                    // Handle the client connection in the background so we can accept new ones if needed
                    _ = Task.Run(() => HandleClientAsync(_activeClient, cancellationToken), cancellationToken);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (HttpListenerException)
        {
            // Expected during shutdown when the listener is stopped
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                    Console.WriteLine("[WebSocketTransport] Client disconnected gracefully.");
                    break;
                }
                
                if (receiveResult.MessageType == WebSocketMessageType.Text)
                {
                    var payloadStr = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    
                    try
                    {
                        var payload = JsonNode.Parse(payloadStr);
                        var prompt = payload?["prompt"]?.ToString() ?? "";
                        var player = payload?["player"]?.ToString() ?? "Unknown";
                        var dynamicContext = payload?["context"]?.ToString() ?? "";

                        // Fire the event on the main thread loop
                        OnPromptReceived?.Invoke(this, new IncomingPromptEventArgs 
                        { 
                            Player = player, 
                            Prompt = prompt,
                            DynamicContext = dynamicContext
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WebSocketTransport Error] Failed to parse message: {ex.Message}");
                    }
                }
            }
        }
        catch (WebSocketException)
        {
            Console.WriteLine("[WebSocketTransport] Client connection lost.");
        }
        finally
        {
            if (_activeClient == webSocket)
            {
                _activeClient = null;
            }
        }
    }

    public async Task SendChatAsync(string message)
    {
        await SendJsonAsync(new { status = "ok", response = message, scripts = Array.Empty<string>() });
    }

    public async Task RunLuaAsync(string luaScript)
    {
        await SendJsonAsync(new { status = "ok", response = "", scripts = new[] { luaScript } });
    }

    private async Task SendJsonAsync(object responseObj)
    {
        if (_activeClient == null || _activeClient.State != WebSocketState.Open)
        {
            Console.WriteLine("[WebSocketTransport] Warning: Attempted to send data but no client is connected!");
            return;
        }

        await _sendLock.WaitAsync();
        try
        {
            var jsonStr = JsonSerializer.Serialize(responseObj);
            var bytes = Encoding.UTF8.GetBytes(jsonStr);
            await _activeClient.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocketTransport] Failed to send message: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }
}