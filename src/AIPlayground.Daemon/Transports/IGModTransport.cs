namespace AIPlayground.Daemon.Transports;

public class IncomingPromptEventArgs : EventArgs
{
    public string Player { get; set; } = string.Empty;
    public int UserId { get; set; } = 1;
    public string Prompt { get; set; } = string.Empty;
    public string DynamicContext { get; set; } = string.Empty;
}

public interface IGModTransport
{
    /// <summary>
    /// Starts the transport (polling, listening on HTTP, connecting to WebSocket, etc.)
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Fired whenever a user types !c or !model in Garry's Mod chat.
    /// </summary>
    event EventHandler<IncomingPromptEventArgs>? OnPromptReceived;

    /// <summary>
    /// Send conversational text back to the Garry's Mod chat.
    /// </summary>
    Task SendChatAsync(string message);

    /// <summary>
    /// Execute a Lua string on the Garry's Mod server globally.
    /// </summary>
    Task RunLuaAsync(string luaScript);
}