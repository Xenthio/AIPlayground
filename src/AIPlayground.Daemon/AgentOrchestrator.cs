using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;
using AgentSharp.Core.Tools;
using AIPlayground.Daemon.Transports;

namespace AIPlayground.Daemon;

/// <summary>
/// Orchestrates the Agent workflow between Garry's Mod and the LLM
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IGModTransport _transport;
    private readonly string _addonPath;
    private readonly IBackendProvider _backend;
    private readonly List<ITool> _tools;
    private string _currentModel = "google/gemini-3-flash-preview";
    private bool _useHistory = false;

    // Prompt router: if set, use this model to classify prompt complexity before sending to main model
    private string? _routerModel = null;

    // Project tools (write_file, read_file, etc.) — disabled by default, toggle with !projects on/off
    private bool _projectsEnabled = false;

    private readonly SessionLogger _logger;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLuaExecution = new();

    private readonly string _dataPath;

    private readonly string _projectsAddonPath;
    
    private readonly MemoryRetrievalSystem _memorySystem;
    private readonly List<ChatMessage> _chatHistory = new();

    public IReadOnlyList<ChatMessage> GetChatHistory() => _chatHistory;

    public AgentOrchestrator(string gmodBasePath, string addonPath, IBackendProvider backend, IGModTransport transport, MemoryRetrievalSystem memorySystem)
    {
        _addonPath = addonPath;
        _backend = backend;
        _transport = transport;
        _memorySystem = memorySystem;
        _transport.OnPromptReceived += OnPromptReceived;

        // We'll write directly into garrysmod/addons, but the tools will prepend the ~ internally
        _projectsAddonPath = Path.Combine(gmodBasePath, "garrysmod", "addons");

        // Setup data directory inside Garry's Mod for communication
        _dataPath = Path.Combine(gmodBasePath, "garrysmod", "data", "aiplayground");
        Directory.CreateDirectory(_dataPath);
        _logger = new SessionLogger(Path.Combine(_dataPath, "logs"));

        // Delete legacy file bridge artifacts
        var legacyInbox = Path.Combine(_dataPath, "inbox.json");
        var legacyOutbox = Path.Combine(_dataPath, "outbox.json");
        if (File.Exists(legacyInbox)) File.Delete(legacyInbox);
        if (File.Exists(legacyOutbox)) File.Delete(legacyOutbox);

        _tools = new List<ITool>
        {
            new WriteFileTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.ReadFileTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.HotReloadFileTool(_projectsAddonPath, code => _pendingLuaExecution.Enqueue(code)),
            new AIPlayground.Daemon.Tools.ReloadSpawnMenuTool(code => _pendingLuaExecution.Enqueue(code)),
            new AIPlayground.Daemon.Tools.ListFilesTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.FileSearchTool(_transport, gmodBasePath),
            new AIPlayground.Daemon.Tools.RecordExampleTool(@"D:\gilbai\examples", memorySystem, this)
        };
    }

    private static readonly HashSet<string> _projectToolNames = new()
    {
        "write_file", "read_file", "hot_reload_file", "list_files", "file_search", "reload_spawn_menu", "record_example"
    };

    private IEnumerable<ITool> GetActiveTools() =>
        _projectsEnabled ? _tools : _tools.Where(t => !_projectToolNames.Contains(t.Name));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"[AgentPlaygroundRunner] Connecting Transport to Garry's Mod...");
        await _transport.StartAsync(cancellationToken);
    }

    private bool _isProcessing = false;
    private readonly Queue<(string Player, int UserId, string Prompt, string DynamicContext)> _promptQueue = new();

    private void OnPromptReceived(object? sender, IncomingPromptEventArgs e)
    {
        lock (_promptQueue)
        {
            _promptQueue.Enqueue((e.Player, e.UserId, e.Prompt, e.DynamicContext));
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
            (string Player, int UserId, string Prompt, string DynamicContext) item;
            lock (_promptQueue)
            {
                if (_promptQueue.Count == 0)
                {
                    _isProcessing = false;
                    return;
                }
                item = _promptQueue.Dequeue();
            }

            await ProcessRequestAsync(item.Player, item.UserId, item.Prompt, item.DynamicContext);
        }
    }

    private async Task ProcessRequestAsync(string player, int userId, string prompt, string dynamicContext)
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

            // Intercept !router commands
            if (prompt.StartsWith("!router "))
            {
                var arg = prompt.Substring(8).Trim();
                if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    _routerModel = null;
                    Console.WriteLine("[Daemon] Prompt router disabled.");
                    await _transport.SendChatAsync("Prompt router disabled.");
                }
                else
                {
                    _routerModel = arg;
                    Console.WriteLine($"[Daemon] Prompt router enabled with model: {_routerModel}");
                    await _transport.SendChatAsync($"Prompt router enabled: {_routerModel}");
                }
                return;
            }

            // Intercept !history toggle command
            if (prompt.StartsWith("!history "))
            {
                var toggle = prompt.Substring(9).Trim().ToLower();
                if (toggle == "off" || toggle == "false")
                {
                    _useHistory = false;
                    _chatHistory.Clear();
                    Console.WriteLine($"[Daemon] Chat history tracking disabled and cleared.");
                    await _transport.SendChatAsync("Chat history disabled. Operating in stateless one-shot mode.");
                }
                else if (toggle == "on" || toggle == "true")
                {
                    _useHistory = true;
                    Console.WriteLine($"[Daemon] Chat history tracking enabled.");
                    await _transport.SendChatAsync("Chat history enabled.");
                }
                return;
            }

            // Intercept !projects toggle
            if (prompt.StartsWith("!projects "))
            {
                var arg = prompt.Substring(10).Trim().ToLower();
                _projectsEnabled = arg == "on" || arg == "true";
                Console.WriteLine($"[Daemon] Project tools {(_projectsEnabled ? "enabled" : "disabled")}.");
                await _transport.SendChatAsync($"Project tools {(_projectsEnabled ? "enabled" : "disabled")}.");
                return;
            }

            // Intercept !search command for testing RAG embeddings directly in game
            if (prompt.StartsWith("!search "))
            {
                var query = prompt.Substring(8).Trim();
                Console.WriteLine($"[Daemon] Testing semantic search for: {query}");
                var relevant = await _memorySystem.GetRelevantExamplesAsync(query, topK: 3);
                
                if (string.IsNullOrWhiteSpace(relevant))
                {
                    await _transport.SendChatAsync("Semantic Search: No highly relevant examples found (Score < 0.3)");
                }
                else
                {
                    // Print the raw context output to the Daemon console so you can inspect the exact code blocks
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("\n=== SEMANTIC SEARCH RESULTS ===");
                    Console.WriteLine(relevant);
                    Console.WriteLine("===============================\n");
                    Console.ResetColor();

                    // Send a small summary back to Garry's Mod chat so it doesn't spam the chatbox
                    var matchCount = relevant.Split("Player Prompt:").Length - 1;
                    await _transport.SendChatAsync($"Semantic Search: Found {matchCount} highly relevant examples! Check Daemon Console for raw code.");
                }
                return;
            }

            Console.WriteLine($"[GMOD CHAT] {player}: {prompt}");

            // Route to cheaper model for simple prompts if router is enabled
            string modelForThisTurn = _currentModel;
            if (_routerModel != null)
            {
                bool isComplex = await ClassifyPromptAsync(prompt);
                modelForThisTurn = isComplex ? _currentModel : "openai/gpt-oss-120b";
                Console.WriteLine($"[Router] {(isComplex ? "COMPLEX" : "SIMPLE")} → {modelForThisTurn}");
            }

            // Fetch embedded memory dynamically, substitute {{ID}} with requesting player's UserID
            var relevantExamples = await _memorySystem.GetRelevantExamplesAsync(prompt);
            if (!string.IsNullOrWhiteSpace(relevantExamples))
                relevantExamples = relevantExamples.Replace("{{ID}}", userId.ToString());

            // Session log entry — accumulates data across all turns for this prompt
            var logEntry = new SessionLogger.Entry { Prompt = prompt };

            // Basic Agent Loop execution
            var messages = new List<ChatMessage>
            {
                ChatMessage.System($"You are an agentic Garry's Mod Addon developer AI. Produce self-contained GMod Lua code for player tasks. Default GMod API only.\n\n" +
                                   $"## Available Shared Addons (always loaded — use these, do not reimplement)\n" +
                                   $"### GilbUtils\n" +
                                   $"- `GilbUtils.Gibs.Explode(ent, dmg)` — explode any entity into HL1-accurate gibs. Handles overkill scaling, head gib, blood decals, bodysplat sound. Optional 3rd arg: `{{ model=..., count=4, headGib=true }}`\n" +
                                   $"- `GilbUtils.Gibs.SpawnGib(model, bodygroup, pos, vel, bloodColor)` — spawn a single `hl1_hgib` at pos with the given velocity. Returns the gib; set `.GibVelocity` and `.AngVelocity` after spawn.\n" +
                                   $"- `hl1_hgib` and `hl1_debris_gib` scripted entities are registered and available — do NOT redefine them.\n" +
                                   $"- All gib spawning is SERVER only.\n\n" +
                                   $"## Realms\n" +
                                   $"- **Default: SERVER** (entities, hooks, physics, health, gravity)\n" +
                                   $"- `RunClientLua(code)` — client only (UI, effects, sounds, dynamic lights)\n" +
                                   $"- `RunSharedLua(code)` — both realms (use `if SERVER then` / `if CLIENT then` inside)\n\n" +
                                   $"## Lua Pitfalls\n" +
                                   $"- Delay: `timer.Simple(n, fn)` (NOT setTimeout)\n" +
                                   $"- Entity creation: SERVER only\n" +
                                   $"- Net messages: `util.AddNetworkString` on server first\n" +
                                   $"- No `continue` keyword — use `if not cond then`\n" +
                                   $"- `true`/`false` lowercase; `NULL` = entity check, `nil` = Lua null\n" +
                                   $"- No `SetDrawBackground`\n" +
                                   $"- `CLuaEmitter`: check `:IsValid()` before use; create with `ParticleEmitter(pos, use3D)`\n" +
                                   $"- `npc_grenade_frag` needs `ent:Fire(\"SetTimer\", seconds)` or it won't explode\n" +
                                   $"- Hard 8192 entity limit — batch spawns, use `SafeRemoveEntityDelayed(ent, seconds)`\n" +
                                   $"- SWEP projectiles: offset spawn pos so they don't clip the player\n" +
                                   $"- `DynamicLight` is CLIENT only\n\n" +
                                   $"## Client-Side Hooks (CLIENT realm only)\n" +
                                   $"- `EntityTakeDamage` does NOT fire on the client. For client-side damage detection, check `LocalPlayer():Health()` deltas in `HUDPaint` or a `Think` hook.\n" +
                                   $"- `OnEntityCreated` fires on client, but entities may not have all properties set yet.\n" +
                                   $"- Use `render.SetScissorRect()` for clipped UI regions; remember to call with all zeros to disable.\n\n" +
                                   $"## Positioning\n" +
                                   $"Always position relative to the requesting player. Use `Player({userId})` to get the requesting player (their UserID is already substituted). `ply:ChatPrint(text)` is valid for sending chat messages to a player.\n\n" +
                                   (_projectsEnabled ? $"## Multi-File Addon Projects\n" +
                                   $"If the user explicitly asks you to create a permanent addon, swep, or project, use `write_file` to save it to your virtual root (e.g. `my_cool_addon/lua/weapons/weapon_cool.lua`). Do NOT use files for simple temporary requests.\n\n" : "") +
                                   $"## Response Format (One-Shot Lua)\n" +
                                   $"If you just need to spawn something or execute a command, simply write your raw Lua code inside a Markdown ```lua code block. Do NOT use any tools. The system will automatically extract and execute it on the server immediately!\n" +
                                   $"Exactly ONE fenced ```lua block. No text outside it. Begin with:\n" +
                                   $"-- PLAN: realm, approach, cleanup\n" +
                                   $"End with:\n" +
                                   $"Player({userId}):ChatPrint(\"feedback message\")\n\n" +
                                   $"## Game State\n" +
                                   $"{dynamicContext}\n\n" +
                                   (!string.IsNullOrWhiteSpace(relevantExamples) ? $"{relevantExamples}\n\n" : ""))
            };

            // Inject previous conversation history BEFORE adding the new message (if enabled)
            if (_useHistory)
            {
                messages.AddRange(_chatHistory);
            }

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
                    Model = modelForThisTurn,
                    Messages = messages,
                    Tools = GetActiveTools().Select(t => t.GetDefinition()).ToList(),
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
                    // Check if the AI wrote a raw Lua code block instead of using a tool
                    var luaMatch = System.Text.RegularExpressions.Regex.Match(contentBuffer, @"```lua\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (luaMatch.Success)
                    {
                        string codeToRun = luaMatch.Groups[1].Value.Trim();
                        Console.WriteLine($"[Daemon] Detected fenced Lua block. Queuing for execution!");
                        logEntry.ExecutedLua.Add(codeToRun);
                        _pendingLuaExecution.Enqueue(codeToRun);
                        
                        // Send just the conversational text to chat without the giant code block
                        string chatOnly = System.Text.RegularExpressions.Regex.Replace(contentBuffer, @"```lua\s*(.*?)\s*```", "[Executing Lua...]", System.Text.RegularExpressions.RegexOptions.Singleline);
                        await _transport.SendChatAsync(chatOnly);
                    }
                    else if (LooksLikeLua(contentBuffer))
                    {
                        // AI returned raw Lua without a code fence — detect and run it
                        Console.WriteLine($"[Daemon] Detected unfenced Lua response. Queuing for execution!");
                        logEntry.ExecutedLua.Add(contentBuffer);
                        _pendingLuaExecution.Enqueue(contentBuffer);
                        await _transport.SendChatAsync("[Executing Lua...]");
                    }
                    else
                    {
                        await _transport.SendChatAsync(contentBuffer);
                    }
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

                    // Write session log entry
                    logEntry.Response = finalResponseText;
                    _logger.Write(logEntry);

                    // Flush any pending Lua execution that was queued from regex parsing of the final conversational stream block
                    if (_pendingLuaExecution.Count > 0)
                    {
                        var immediateScripts = new List<string>();
                        while (_pendingLuaExecution.TryDequeue(out var s)) immediateScripts.Add(s);

                        foreach (var s in immediateScripts)
                        {
                            await _transport.RunLuaAsync(s);
                        }
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

                    var tool = GetActiveTools().FirstOrDefault(t => t.Name == tc.Function.Name);
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

                            logEntry.ToolCalls.Add(new SessionLogger.ToolCallEntry(
                                tc.Function.Name,
                                tc.Function.Arguments,
                                result.Content + (result.Error != null ? $"\nError: {result.Error}" : "")
                            ));

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

    /// <summary>
    /// Classify whether a prompt is "complex" (true) or "simple" (false).
    /// Complex = needs the main model. Simple = a cheap fast model is fine.
    /// </summary>
    /// <summary>
    /// Heuristic: does this response look like raw Lua code rather than a prose reply?
    /// Triggered when the AI forgets the code fence but still emits valid Lua.
    /// </summary>
    private static bool LooksLikeLua(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        // Strong signals: starts with a Lua comment or known Lua call patterns
        if (t.StartsWith("--"))                   return true;
        if (t.StartsWith("local "))               return true;
        if (t.StartsWith("function "))            return true;
        if (t.StartsWith("hook."))                return true;
        if (t.StartsWith("RunClientLua("))        return true;
        if (t.StartsWith("RunSharedLua("))        return true;
        if (t.StartsWith("timer."))               return true;
        if (t.StartsWith("net."))                 return true;
        if (t.StartsWith("if CLIENT"))            return true;
        if (t.StartsWith("if SERVER"))            return true;
        // Weaker: count Lua-isms vs English words; Lua wins if density is high
        int luaHits = 0;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bhook\.Add\b"))    luaHits += 3;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\blocal\s+\w+\s*=")) luaHits += 2;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bend\b"))            luaHits += 2;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bfunction\b"))       luaHits += 2;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"RunClientLua|RunSharedLua")) luaHits += 5;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bnet\.Start\b"))   luaHits += 3;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\btimer\.\w+\b"))  luaHits += 3;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\bCompileString\b")) luaHits += 3;
        // Penalise if it has sentence-like prose (multiple capital letters starting lines)
        int proseLines = System.Text.RegularExpressions.Regex.Matches(text, @"^[A-Z][a-z]", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        return luaHits >= 5 && proseLines <= 2;
    }

    private async Task<bool> ClassifyPromptAsync(string prompt)
    {
        try
        {
            var req = new CompletionRequest
            {
                Model = _routerModel!,
                Messages = new List<ChatMessage>
                {
                    ChatMessage.System(
                        "Classify the complexity of a Garry's Mod AI request. Reply with ONE word only: SIMPLE or COMPLEX. No explanation.\n\n" +
                        "SIMPLE (cheap model fine):\n" +
                        "- Spawn a single prop or effect\n" +
                        "- Set a player stat (health, speed, gravity)\n" +
                        "- Play a sound or basic particle\n" +
                        "- Single short hook or timer\n\n" +
                        "COMPLEX (needs capable model):\n" +
                        "- Any SWEP or weapon\n" +
                        "- Any scripted entity (SENT)\n" +
                        "- Any HUD or UI element\n" +
                        "- Physics systems or movement mechanics\n" +
                        "- Multi-step or multi-file tasks\n" +
                        "- Error debugging or fixing\n" +
                        "- Anything with hooks, net messages, or custom logic\n" +
                        "- When in doubt: COMPLEX"),
                    ChatMessage.User(prompt)
                },
                Temperature = 0,
                MaxTokens = 4
            };

            var result = await _backend.GenerateCompletionAsync(req);
            var answer = result.Content?.Trim().ToUpperInvariant() ?? "";

            // Only route cheap if explicitly SIMPLE with no COMPLEX anywhere in the response
            bool isSimple = answer.Contains("SIMPLE") && !answer.Contains("COMPLEX");

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"[Router] '{prompt.Substring(0, Math.Min(50, prompt.Length))}' => \"{result.Content?.Trim()}\" => {(isSimple ? "SIMPLE" : "COMPLEX")}");
            Console.ResetColor();

            return !isSimple; // true = COMPLEX = use main model
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Router] Classification failed ({ex.Message}), defaulting to main model.");
            return true; // Fail safe: always use main model on error
        }
    }
}