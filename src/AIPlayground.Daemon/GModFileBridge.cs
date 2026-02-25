using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;
using AgentSharp.Core.Tools;
using AIPlayground.Daemon.Transports;

namespace AIPlayground.Daemon;

/// <summary>
/// A file-based communication bridge for Garry's Mod since HTTP blocks localhost
/// </summary>
public sealed class GModFileBridge
{
    private readonly IGModTransport _transport;
    private readonly string _addonPath;
    private readonly IBackendProvider _backend;
    private readonly List<ITool> _tools;
    private string _currentModel = "google/gemini-3-flash-preview";

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLuaExecution = new();

    private readonly string _dataPath;
    private readonly string _inboxFile;
    private readonly string _outboxFile;

    private readonly string _projectsAddonPath;

    private readonly List<ChatMessage> _chatHistory = new();

    public GModFileBridge(string gmodBasePath, string addonPath, IBackendProvider backend, IGModTransport transport)
    {
        _addonPath = addonPath;
        _backend = backend;
        _transport = transport;
        _transport.OnPromptReceived += OnPromptReceived;

        // The path to the standalone AIPlayground_Projects addon that Garry's Mod will actually mount
        _projectsAddonPath = Path.Combine(gmodBasePath, "garrysmod", "addons", "AIPlayground_Projects");

        // Ensure the standalone projects addon exists so the AI has somewhere to write
        if (!Directory.Exists(_projectsAddonPath))
        {
            Console.WriteLine("[Daemon] Creating standalone AIPlayground_Projects addon...");
            Directory.CreateDirectory(Path.Combine(_projectsAddonPath, "lua", "ai_projects"));
        }

        // Setup data directory inside Garry's Mod for communication
        _dataPath = Path.Combine(gmodBasePath, "garrysmod", "data", "aiplayground");
        Directory.CreateDirectory(_dataPath);

        _inboxFile = Path.Combine(_dataPath, "inbox.json");
        _outboxFile = Path.Combine(_dataPath, "outbox.json");

        // Clear old state
        if (File.Exists(_inboxFile)) File.Delete(_inboxFile);
        if (File.Exists(_outboxFile)) File.Delete(_outboxFile);

        _tools = new List<ITool>
        {
            new WriteFileTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.ReadFileTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.HotReloadFileTool(_projectsAddonPath, code => _pendingLuaExecution.Enqueue(code)),
            new AIPlayground.Daemon.Tools.GraduateProjectTool(Path.Combine(gmodBasePath, "garrysmod", "addons")),
            new AIPlayground.Daemon.Tools.ReloadSpawnMenuTool(code => _pendingLuaExecution.Enqueue(code)),
            new AIPlayground.Daemon.Tools.ListFilesTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.FileSearchTool(_transport, gmodBasePath),
            new AIPlayground.Daemon.Tools.RunLuaTool(_transport)
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"[AgentPlaygroundRunner] Connecting Transport to Garry's Mod...");
        await _transport.StartAsync(cancellationToken);
    }

    private bool _isProcessing = false;
    private readonly Queue<(string Player, string Prompt, string DynamicContext)> _promptQueue = new();

    private void OnPromptReceived(object? sender, IncomingPromptEventArgs e)
    {
        lock (_promptQueue)
        {
            _promptQueue.Enqueue((e.Player, e.Prompt, e.DynamicContext));
            if (!_isProcessing)
            {
                _isProcessing = true;
                _ = ProcessQueueAsync();
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            (string Player, string Prompt, string DynamicContext) item;
            lock (_promptQueue)
            {
                if (_promptQueue.Count == 0)
                {
                    _isProcessing = false;
                    return;
                }
                item = _promptQueue.Dequeue();
            }

            await ProcessRequestAsync(item.Player, item.Prompt, item.DynamicContext);
        }
    }

    private async Task ProcessRequestAsync(string player, string prompt, string dynamicContext)
    {
        try
        {
            // Intercept !model commands directly in the daemon
            if (prompt.StartsWith("!model "))
            {
                _currentModel = prompt.Substring(7).Trim();
                Console.WriteLine($"[Daemon] Model switched to {_currentModel}");
                await _transport.SendChatAsync($"Switched backend model to {_currentModel}");
                return;
            }

            Console.WriteLine($"[GMOD CHAT] {player}: {prompt}");

            // Basic Agent Loop execution
            var messages = new List<ChatMessage>
            {
                ChatMessage.System("You are an agentic Garry's Mod Addon developer AI. Write clean, efficient Lua code and deploy it using your tools.\n\n" +
                                   "CRITICAL CONVERSATION RULES:\n" +
                                   "Keep your conversational replies extremely short and concise (1-2 sentences). Garry's Mod chat space is very limited. Do NOT format your text with markdown headers, bolding, or lists. Just state what you are doing in a single brief sentence.\n\n" +
                                   "CRITICAL DIRECTORY RULES:\n" +
                                   "The `write_file`, `read_file`, and `hot_reload` tools automatically start inside your designated addon folder (`garrysmod/addons/AIPlayground_Projects/`).\n" +
                                   "DO NOT prefix your paths with `AIPlayground_Projects/` or `addons/`. Your paths must start directly with `lua/` or `docs/`.\n\n" +
                                   "1. You must explicitly talk back to the user to explain what you are doing.\n" +
                                   "2. When you create or update a Lua file, you must ALWAYS use the `hot_reload` tool to load it into the live server immediately.\n" +
                                   "3. ISOLATE YOUR PROJECTS: Write ALL your Lua code strictly inside `lua/ai_projects/<project_name>/`. (e.g. YES: `lua/ai_projects/cool_gun/shared.lua` | NO: `AIPlayground_Projects/lua/...` | NO: `addons/lua/...`). Just put all the lua for a single project flat inside its specific `ai_projects/` directory.\n" +
                                   "4. VISION DOCUMENTS: When you start a new project, you MUST first create a `docs/<project_name>/VISION.md` file using `write_file`.\n" +
                                   "5. CHECK BEFORE CREATING/EDITING: If the user asks about an existing project or asks you to modify something, YOU MUST USE `list_files(path = \"lua/ai_projects/\")` to find what you've already created, and then YOU MUST USE `read_file` to read the existing code before using `write_file` to edit it! Do NOT guess the code, read it first! If `read_file` fails, you MUST STOP and tell the user, DO NOT try to write a new file from memory.\n" +
                                   "6. ASSET SEARCH: If you need to use a specific model (`.mdl`), sound (`.wav`), or material (`.vmt`), do NOT guess its path! Use the `search_assets` tool to query the Garry's Mod engine (e.g. `pattern = \"models/weapons/w_*.mdl\", path = \"GAME\"`) to find real file names before you write the Lua code!\n" +
                                   "7. SWEP REGISTRATION: You MUST use `weapons.Register(SWEP, \"weapon_classname\")` manually at the bottom of your file since you aren't using the standard `/weapons/` auto-load folder! ALWAYS include `SWEP.Category = \"AI Creations\"` and `SWEP.Spawnable = true`!\n" +
                                   "8. SWEP INITIALIZATION: Because you are defining SWEPs manually, `SWEP.Primary` and `SWEP.Secondary` do not exist by default! You MUST initialize them first (e.g. `SWEP.Primary = {} SWEP.Secondary = {}`) before setting things like `SWEP.Primary.ClipSize` or you will get a nil value error!\n" +
                                   "9. MENU RELOADS: ALWAYS call the `reload_spawn_menu` tool after you finish writing a completely new weapon, entity, or NPC, so the user doesn't have to restart their game to see the new category in their Q-Menu!\n" +
                                   "10. PATH FIXING: If an error mentions a bad path, or if `hot_reload` fails, use `list_files` to verify where you accidentally put the file, then use `write_file` to put it in the correct `lua/ai_projects/` folder.\n" +
                                   "11. MULTI-STEP: Do NOT say what you are going to do and then wait! Execute your plan immediately! You can execute MULTIPLE tools simultaneously in the exact same response! If you need to search multiple paths, fire 3 `search_assets` calls simultaneously in the same response! Never just talk without acting! Do NOT ask for permission to use tools or show the user options! Just pick one and execute it immediately!\n" +
                                   "12. DO NOT ABUSE FILES AND SWEPS: If the user asks you to simply \"spawn\" something, \"build\" something, or \"make\" something in the world, DO NOT create a permanent file, a SWEP, or a console command! Just immediately use the `run_lua` tool to execute a script that uses `player.GetAll()[1]:GetEyeTrace().HitPos` and `ents.Create()` to spawn the items directly at their crosshair! Do NOT ask what props they want, just spawn a creative combination of them immediately using the assets you found!\n" +
                                   "13. CLEANUP MISTAKES: If your `run_lua` script creates physical props in the world, but the script throws a missing path error or a Lua error, YOU MUST use `run_lua` to delete the props you just spawned before trying to spawn the corrected ones! Otherwise, the broken props will stay in the world forever! You can use `ents.FindByClass(\"prop_physics\")` and check their model/creation time, or keep track of what you spawned to clean it up!\n\n" +
                                   "CURRENT GAME STATE:\n" +
                                   $"{dynamicContext}")
            };

            // Inject previous conversation history BEFORE adding the new message
            messages.AddRange(_chatHistory);

            // Add the new user prompt
            var userMsg = ChatMessage.User($"[From {player}]: {prompt}");
            messages.Add(userMsg);

            // Save ONLY the new user prompt to history (so we don't accidentally save history recursively inside itself)
            _chatHistory.Add(userMsg);

            int turnCount = 0;
            const int maxTurns = 5; // Prevent infinite loops

            while (turnCount < maxTurns)
            {
                var completionReq = new CompletionRequest
                {
                    Model = _currentModel,
                    Messages = messages,
                    Tools = _tools.Select(t => t.GetDefinition()).ToList(),
                    Temperature = 0.7
                };

                Console.WriteLine($"[Daemon] Streaming prompt to LLM (Turn {turnCount + 1})...");
                
                string finalResponseText = "";
                var toolCalls = new List<AgentSharp.Core.Interfaces.ToolCall>();
                
                string contentBuffer = "";
                string thoughtBuffer = "";

                // Stream the response directly to the Garry's Mod WebSocket
                await foreach (var chunk in _backend.GenerateCompletionStreamAsync(completionReq))
                {
                    if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write(chunk.ReasoningDelta);
                        Console.ResetColor();
                        
                        finalResponseText += chunk.ReasoningDelta;
                        thoughtBuffer += chunk.ReasoningDelta;
                        
                        // Only send the thought over WebSocket when it hits a newline, punctuation, or gets too long
                        if (thoughtBuffer.Contains("\n") || thoughtBuffer.Contains(".") || thoughtBuffer.Contains("!") || thoughtBuffer.Length > 80)
                        {
                            await _transport.SendChatAsync($"<thought> {thoughtBuffer}");
                            thoughtBuffer = "";
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(chunk.ContentDelta))
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write(chunk.ContentDelta);
                        Console.ResetColor();

                        finalResponseText += chunk.ContentDelta;
                        contentBuffer += chunk.ContentDelta;
                        
                        // We don't stream normal conversational text into GMod since it clogs the chat box
                        // We will just accumulate it and send it in one block when the stream finishes!
                    }

                    if (chunk.ToolCalls != null && chunk.ToolCalls.Any())
                    {
                        toolCalls = chunk.ToolCalls;
                    }
                }
                
                // Flush remaining buffers at the end of the stream
                if (thoughtBuffer.Length > 0)
                {
                    await _transport.SendChatAsync($"<thought> {thoughtBuffer}");
                }
                // Normal chat is buffered completely until the stream is done
                if (contentBuffer.Length > 0)
                {
                    await _transport.SendChatAsync(contentBuffer);
                }

                Console.WriteLine(); // Newline after stream finishes

                Console.WriteLine($"\n[Daemon] Stream Finished (Tool Calls: {toolCalls.Count})");

                // If no tool calls, the AI is done thinking! Break the loop.
                if (!toolCalls.Any())
                {
                    var msg = ChatMessage.Assistant(finalResponseText);
                    messages.Add(msg);

                    // Prevent saving blank conversational filler
                    if (!string.IsNullOrWhiteSpace(finalResponseText))
                    {
                        _chatHistory.Add(msg);
                    }
                    break;
                }

                // AI returned tool calls. Add the assistant's message to history.
                var toolMsg = ChatMessage.AssistantTools(toolCalls);
                if (!string.IsNullOrWhiteSpace(finalResponseText)) 
                {
                    toolMsg.Content = finalResponseText;
                }
                messages.Add(toolMsg);
                _chatHistory.Add(toolMsg);

                // Execute each tool and append the results back to the chat history
                foreach (var tc in toolCalls)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[TOOL CALL] {tc.Function.Name}({tc.Function.Arguments})");
                    Console.ResetColor();

                    var tool = _tools.FirstOrDefault(t => t.Name == tc.Function.Name);
                    if (tool != null)
                    {
                        try
                        {
                            var args = JsonDocument.Parse(tc.Function.Arguments);

                            var resultTask = tool.ExecuteAsync(args);

                            // If the tool injected Lua into the queue synchronously before going async, flush it.
                            if (_pendingLuaExecution.Count > 0)
                            {
                                var immediateScripts = new List<string>();
                                while (_pendingLuaExecution.TryDequeue(out var s)) immediateScripts.Add(s);

                                foreach (var s in immediateScripts)
                                {
                                    await _transport.RunLuaAsync(s);
                                }
                            }

                            var result = await resultTask;

                            if (result.Success)
                            {
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine($"[TOOL RESULT] Success");
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.WriteLine(result.Content);
                                Console.ResetColor();
                            }
                            else
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"[TOOL RESULT] Failed: {result.Error}");
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.WriteLine(result.Content);
                                Console.ResetColor();
                            }

                            // Feed the result back to the LLM so it knows it worked
                            var resMsg = ChatMessage.ToolResult(tc.Id, result.Content + (result.Error != null ? $"\nError: {result.Error}" : ""));
                            messages.Add(resMsg);
                            _chatHistory.Add(resMsg);
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"[TOOL CRASH] {tool.Name}: {ex.Message}");
                            Console.ResetColor();

                            var failMsg = ChatMessage.ToolResult(tc.Id, $"Crashed: {ex.Message}");
                            messages.Add(failMsg);
                            _chatHistory.Add(failMsg);
                        }
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[TOOL ERROR] Tool '{tc.Function.Name}' not found.");
                        Console.ResetColor();

                        var nfMsg = ChatMessage.ToolResult(tc.Id, "Error: Tool not found.");
                        messages.Add(nfMsg);
                        _chatHistory.Add(nfMsg);
                    }
                }

                turnCount++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Daemon Error] {ex}");
            await _transport.SendChatAsync($"Daemon Error: {ex.Message}");
        }
    }
}