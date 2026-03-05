using System.Text.Json;

namespace AIPlayground.Daemon;

/// <summary>
/// Logs each interaction to a JSONL file.
/// Each line is one self-contained entry — easy to grep and promote to refined examples.
/// Also writes a human-readable .log file alongside for easy tailing.
/// </summary>
public class SessionLogger
{
    private readonly string _jsonlPath;
    private readonly string _readablePath;
    private readonly object _lock = new();

    public record ToolCallEntry(string Name, string ArgsJson, string Result);

    public class Entry
    {
        public string Timestamp              { get; set; } = DateTime.Now.ToString("o");
        public string Prompt                 { get; set; } = "";
        public string Response               { get; set; } = "";
        public List<string> ExecutedLua      { get; set; } = new();
        public List<ToolCallEntry> ToolCalls { get; set; } = new();
        /// <summary>Full message list sent to the LLM for this turn (system prompt included).</summary>
        public List<MessageLogEntry> Messages { get; set; } = new();
    }

    public class MessageLogEntry
    {
        public string Role    { get; set; } = "";
        public string Content { get; set; } = "";
        /// <summary>Non-null for tool result messages.</summary>
        public string? ToolCallId { get; set; }
        /// <summary>Non-null for assistant tool-call messages.</summary>
        public List<string>? ToolCallNames { get; set; }
    }

    public SessionLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        var stem = $"session_{DateTime.Now:yyyy-MM-dd}";
        _jsonlPath   = Path.Combine(logDirectory, stem + ".jsonl");
        _readablePath = Path.Combine(logDirectory, stem + ".log");
        Console.WriteLine($"[SessionLogger] Logging to {_jsonlPath}");
    }

    public void Write(Entry entry)
    {
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
        var readable = BuildReadable(entry);

        lock (_lock)
        {
            File.AppendAllText(_jsonlPath, json + "\n");
            File.AppendAllText(_readablePath, readable + "\n");
        }
    }

    private static string BuildReadable(Entry entry)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== {entry.Timestamp} ===");

        foreach (var msg in entry.Messages)
        {
            var roleLabel = msg.Role.ToUpperInvariant();
            if (msg.Role == "system")
            {
                // Truncate system prompt to first 500 chars
                var preview = msg.Content.Length > 500 ? msg.Content[..500] + "..." : msg.Content;
                sb.AppendLine($"[{roleLabel}] {preview}");
            }
            else if (msg.ToolCallNames != null)
            {
                sb.AppendLine($"[{roleLabel}] <tool calls: {string.Join(", ", msg.ToolCallNames)}>");
            }
            else if (msg.ToolCallId != null)
            {
                var preview = msg.Content.Length > 300 ? msg.Content[..300] + "..." : msg.Content;
                sb.AppendLine($"[TOOL RESULT:{msg.ToolCallId}] {preview}");
            }
            else
            {
                sb.AppendLine($"[{roleLabel}] {msg.Content}");
            }
        }

        if (entry.ExecutedLua.Count > 0)
        {
            sb.AppendLine($"[EXECUTED LUA x{entry.ExecutedLua.Count}]");
            foreach (var lua in entry.ExecutedLua)
                sb.AppendLine(lua.Length > 400 ? lua[..400] + "..." : lua);
        }

        return sb.ToString();
    }
}
