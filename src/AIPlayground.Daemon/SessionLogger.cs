using System.Text.Json;

namespace AIPlayground.Daemon;

/// <summary>
/// Logs each interaction (prompt, tool calls, executed Lua, final response) to a JSONL file.
/// Each line is one self-contained entry — easy to grep and promote to refined examples.
/// </summary>
public class SessionLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public record ToolCallEntry(string Name, string ArgsJson, string Result);

    public class Entry
    {
        public string Timestamp             { get; set; } = DateTime.Now.ToString("o");
        public string Prompt                { get; set; } = "";
        public string Response              { get; set; } = "";
        public List<string> ExecutedLua     { get; set; } = new();
        public List<ToolCallEntry> ToolCalls { get; set; } = new();
    }

    public SessionLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"session_{DateTime.Now:yyyy-MM-dd}.jsonl");
        Console.WriteLine($"[SessionLogger] Logging to {_logPath}");
    }

    public void Write(Entry entry)
    {
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = false });
        lock (_lock)
            File.AppendAllText(_logPath, json + "\n");
    }
}
