using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;
using AgentSharp.Core.Tools;
using AIPlayground.Daemon.Transports;

namespace AIPlayground.Daemon;

/// <summary>
/// Per-player conversation state: history, conversation ID, and script ID counter.
/// </summary>
public sealed class ConversationState
{
    public string PlayerName { get; }
    public string ConversationId { get; private set; }
    public List<ChatMessage> History { get; } = new();
    private int _scriptCounter = 0;

    public ConversationState(string playerName)
    {
        PlayerName = playerName;
        ConversationId = NewId();
    }

    /// <summary>Start a fresh conversation (new prompt, not an error fix).</summary>
    public void StartNewConversation()
    {
        ConversationId = NewId();
        History.Clear();
        _scriptCounter = 0;
    }

    /// <summary>Unique script name for this turn, embeds conversation ID for error traceability.</summary>
    public string NextScriptId() => $"AI_{ConversationId}_{++_scriptCounter}";

    private static string NewId() =>
        Guid.NewGuid().ToString("N")[..8].ToUpper();
}

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

    // !history on = inject cross-conversation context (globals/SWEPs created recently) into system prompt
    private bool _globalHistoryEnabled = false;
    private readonly List<string> _globalContextLog = new(); // compact summaries of what was created

    // Prompt router: if set, use this model to classify prompt complexity before sending to main model
    private string? _routerModel = null;

    // Project tools (write_file, read_file, etc.) — disabled by default, toggle with !projects on/off
    private bool _projectsEnabled = false;

    private readonly SessionLogger _logger;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLuaExecution = new();

    private readonly string _dataPath;
    private readonly string _projectsAddonPath;
    
    private readonly MemoryRetrievalSystem _memorySystem;

    // Per-player conversation state
    private readonly Dictionary<string, ConversationState> _conversations = new();

    private ConversationState GetConversation(string playerName)
    {
        if (!_conversations.TryGetValue(playerName, out var conv))
        {
            conv = new ConversationState(playerName);
            _conversations[playerName] = conv;
        }
        return conv;
    }

    // Expose most recent conversation history for RecordExampleTool
    public IReadOnlyList<ChatMessage> GetChatHistory() =>
        _conversations.Values.LastOrDefault()?.History ?? Array.Empty<ChatMessage>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _transport.StartAsync(cancellationToken);
    }

        _addonPath = addonPath;
        _backend = backend;
        _transport = transport;
        _memorySystem = memorySystem;
        _transport.OnPromptReceived += OnPromptReceived;

        // We'll write directly into garrysmod/addons, but the tools will prepend the ~ internally
        _projectsAddonPath = Path.Combine(gmodBasePath, "garrysmod", "addons");
        _dataPath = Path.Combine(gmodBasePath, "garrysmod", "data");

        var logPath = Path.Combine(_dataPath, "aiplayground", "logs");
        _logger = new SessionLogger(logPath);

        _tools = new List<ITool>
        {
            new AIPlayground.Daemon.Tools.ReadFileTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.HotReloadFileTool(_projectsAddonPath, code => _pendingLuaExecution.Enqueue(code)),
            new AIPlayground.Daemon.Tools.ReloadSpawnMenuTool(code => _pendingLuaExecution.Enqueue(code)),
            new AIPlayground.Daemon.Tools.ListFilesTool(_projectsAddonPath),
            new AIPlayground.Daemon.Tools.FileSearchTool(_transport, gmodBasePath),
            new AIPlayground.Daemon.Tools.RecordExampleTool(@"D:\gilbai\examples", memorySystem, this)
        };

        var projectToolNames = new HashSet<string> { "read_file", "hot_reload_file", "reload_spawnmenu", "list_files", "write_file" };
        _projectToolNames = projectToolNames;
    }

    private readonly HashSet<string> _projectToolNames;
    private IEnumerable<ITool> GetActiveTools() =>
        _projectsEnabled ? _tools : _tools.Where(t => !_projectToolNames.Contains(t.Name));

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
                var modelName = prompt.Substring(7).Trim();
                _currentModel = modelName;
                Console.WriteLine($"[Daemon] Model switched to: {modelName}");
                await _transport.SendChatAsync($"Model switched to: {modelName}");
                return;
            }

            // Intercept !router commands
            if (prompt.StartsWith("!router "))
            {
                var arg = prompt.Substring(8).Trim();
                if (arg == "off")
                {
                    _routerModel = null;
                    await _transport.SendChatAsync("Prompt router disabled.");
                }
                else
                {
                    _routerModel = arg;
                    await _transport.SendChatAsync($"Prompt router enabled: {arg}");
                }
                return;
            }

            // Intercept !history toggle command
            if (prompt.StartsWith("!history "))
            {
                var arg = prompt.Substring(9).Trim();
                if (arg == "off")
                {
                    _globalHistoryEnabled = false;
                    _globalContextLog.Clear();
                    await _transport.SendChatAsync("Global history disabled and cleared.");
                }
                else if (arg == "on")
                {
                    _globalHistoryEnabled = true;
                    await _transport.SendChatAsync("Global history enabled. Recent globals/hooks/SWEPs will be injected into future prompts.");
                }
                return;
            }

            // Intercept !projects toggle
            if (prompt.StartsWith("!projects "))
            {
                var arg = prompt.Substring(10).Trim();
                _projectsEnabled = arg == "on";
                Console.WriteLine($"[Daemon] Project tools {(_projectsEnabled ? "enabled" : "disabled")}.");
                await _transport.SendChatAsync($"Project tools {(_projectsEnabled ? "enabled" : "disabled")}.");
                return;
            }

            // Intercept !run command: run a named example directly
            if (prompt.StartsWith("!run "))
            {
                var exampleName = prompt.Substring(5).Trim();
                var examplePath = Path.Combine(@"D:\gilbai\examples", exampleName, "response.lua");
                if (File.Exists(examplePath))
                {
                    var code = await File.ReadAllTextAsync(examplePath);
                    _pendingLuaExecution.Enqueue(code);
                    await _transport.SendChatAsync($"Running example: {exampleName}");
                }
                else
                {
                    await _transport.SendChatAsync($"Example not found: {exampleName}");
                }
                return;
            }

            // Intercept !search command for testing RAG embeddings directly in game
            if (prompt.StartsWith("!search "))
            {
                var query = prompt.Substring(8).Trim();
                Console.WriteLine($"[Daemon] Semantic search for: {query}");
                var results = await _memorySystem.SearchAsync(query, topK: 5);

                if (results.Count == 0)
                {
                    await _transport.SendChatAsync("!search: No examples in pool.");
                    return;
                }

                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"Search: \"{query}\"");
                foreach (var r in results)
                {
                    var tag = r.IsDoc ? "[doc]" : "[ex]";
                    var bar = r.Score >= 0.68f ? "███" : r.Score >= 0.3f ? "██░" : "█░░";
                    lines.AppendLine($"{bar} {r.Score:F2} {tag} {r.Description}");
                }
                await _transport.SendChatAsync(lines.ToString().TrimEnd());
                return;
            }

            Console.WriteLine($"[GMOD CHAT] {player}: {prompt}");

            // Route to cheaper model for simple prompts if router is enabled
            string modelForThisTurn = _currentModel;
            if (_routerModel != null)
            {
                var classification = await ClassifyPromptAsync(prompt, _routerModel);
                Console.WriteLine($"[Router] {classification}");
                if (classification == "SIMPLE")
                    modelForThisTurn = "openai/gpt-oss-120b";
            }

            // Determine if this is an error fix (sent by "Server") or a real user request
            bool isErrorFix = player == "Server";

            // Get or create per-player conversation state
            // Error fixes route to the player whose conversation we're fixing
            // (the Lua side embeds the conversationId in the error message)
            ConversationState conv;
            if (isErrorFix)
            {
                // Try to extract conversation ID from error message: look for "AI_XXXXXXXX_"
                var convIdMatch = System.Text.RegularExpressions.Regex.Match(prompt, @"AI_([A-Z0-9]{8})_");
                if (convIdMatch.Success)
                {
                    var convId = convIdMatch.Groups[1].Value;
                    conv = _conversations.Values.FirstOrDefault(c => c.ConversationId == convId)
                           ?? GetConversation("Server");
                }
                else
                {
                    // Fallback: use most recent non-Server conversation
                    conv = _conversations.Values.LastOrDefault(c => c.PlayerName != "Server")
                           ?? GetConversation("Server");
                }
            }
            else
            {
                conv = GetConversation(player);
                conv.StartNewConversation(); // fresh conversation for each real user request
            }

            Console.WriteLine($"[Daemon] Conv [{conv.ConversationId}] {player}: streaming...");

            // Get relevant examples from memory
            string relevantExamples = await _memorySystem.GetRelevantExamplesAsync(prompt);

            // Build global context injection if !history on
            string globalContextSection = "";
            if (_globalHistoryEnabled && _globalContextLog.Count > 0)
            {
                var recent = _globalContextLog.TakeLast(10).ToList();
                globalContextSection = $"## Recent Session Context\nThe following things were created in this session and can be referenced or built upon:\n" +
                                       string.Join("\n", recent) + "\n\n";
            }

            var messages = new List<ChatMessage>
            {
                ChatMessage.System(
                                   $"You are an AI assistant embedded inside Garry's Mod (GMod), a Lua-powered sandbox game.\n" +
                                   $"You help players by writing Lua scripts that run directly on the GMod server.\n\n" +
                                   $"## Lua Gotchas\n" +
                                   $"- Net messages: `util.AddNetworkString` on server first\n" +
                                   $"- No `continue` keyword — use `if not cond then`\n" +
                                   $"- `true`/`false` lowercase; `NULL` = entity check, `nil` = Lua null\n" +
                                   $"- No `SetDrawBackground`\n" +
                                   $"- `CLuaEmitter`: check `:IsValid()` before use; create with `ParticleEmitter(pos, use3D)`\n" +
                                   $"- `npc_grenade_frag` needs `ent:Fire(\"SetTimer\", seconds)` or it won't explode\n" +
                                   $"- Hard 8192 entity limit — batch spawns, use `SafeRemoveEntityDelayed(ent, seconds)`\n" +
                                   $"- SWEP projectiles: offset spawn pos so they don't clip the player\n" +
                                   $"- `DynamicLight` is CLIENT only\n" +
                                   $"- SWEP table MUST pre-declare sub-tables: `local SWEP = {{}}; SWEP.Primary = {{}}; SWEP.Secondary = {{}}` before setting `.Primary.ClipSize` etc. — indexing nil = crash\n" +
                                   $"- `RequestingPlayer:ChatPrint()` — ALWAYS guard with `if SERVER then`. Never call at top-level unfenced code — the realm may be CLIENT.\n\n" +
                                   $"## Client-Side Hooks (CLIENT realm only)\n" +
                                   $"- `EntityTakeDamage` does NOT fire on the client. For client-side damage detection, check `LocalPlayer():Health()` deltas in `HUDPaint` or a `Think` hook.\n" +
                                   $"- `OnEntityCreated` fires on client, but entities may not have all properties set yet.\n" +
                                   $"- Use `render.SetScissorRect()` for clipped UI regions; remember to call with all zeros to disable.\n" +
                                   $"\n" +
                                   $"## HUD System\n" +
                                   $"- `HL2Hud` (global table) is ALWAYS available on clients — GilbUtils auto-loads the HL2 HUD replacement (`cl_hl2_hud.lua`).\n" +
                                   $"- The HL2 HUD is drawn via **EHUD** (ExtensibleHUD). Health/suit/ammo panels are registered as `base_element` on EHUD columns.\n" +
                                   $"- **To replace a HUD panel**: do NOT add a `HUDShouldDraw` hook (native panels are already hidden). Instead, replace `EHUD.GetColumn(\"health\").base_element`, `EHUD.GetColumn(\"suit\").base_element`, or `EHUD.GetColumn(\"ammo\").base_element` with your own element object.\n" +
                                   $"- **To restore defaults**: set `base_element` back to `HL2Hud.healthElem`, `HL2Hud.suitElem`, or `HL2Hud.ammoElem`.\n" +
                                   $"- **To add extra elements** above a column: `EHUD.AddToColumn(\"health\", \"my_id\", myElem, priority)`.\n" +
                                   $"- `HL2Hud.Colors` fields: FgColor, BrightFg, DamagedFg, BrightDamagedFg, BgColor, DamagedBg, AuxHigh, AuxLow, AuxDisabled (alpha number).\n" +
                                   $"- To recolor: update `HL2Hud.Colors.*` fields directly inside `RunClientLua`.\n" +
                                   $"- **IMPORTANT**: After updating Colors, call `HL2Hud.ApplyColors()` to propagate changes to live animation state. Without this, colors only update when animations fire (e.g. damage).\n" +
                                   $"- For continuous effects (rainbow, pulsing), use a `Think` hook that updates `HL2Hud.Colors.*` AND calls `HL2Hud.ApplyColors()` every frame.\n" +
                                   $"- `HL2Hud.healthEvent(name)` / `HL2Hud.suitEvent(name)` / `HL2Hud.auxEvent(name)` — fire animation events manually if needed.\n" +
                                   $"\n" +
                                   $"## Positioning\n" +
                                   $"Use `RequestingPlayer` (pre-injected into the sandbox) to get the player who sent the request — always valid, no UserID guessing needed. `Player({userId})` also works as a fallback.\n\n" +
                                   (_projectsEnabled ? $"## Multi-File Addon Projects\n" +
                                   $"If the user explicitly asks you to create a permanent addon, swep, or project, use `write_file` to save it to your virtual root (e.g. `my_cool_addon/lua/weapons/weapon_cool.lua`). Do NOT use files for simple temporary requests.\n\n" : "") +
                                   $"## Response Format (One-Shot Lua)\n" +
                                   $"If you need to find valid model/sound/material paths, call `search_assets` first. Always verify paths before using them in code.\n" +
                                   $"For everything else, write your raw Lua code inside a Markdown ```lua code block — do NOT use file/project tools for simple requests. The system will automatically extract and execute it on the server immediately!\n" +
                                   $"Exactly ONE fenced ```lua block. No text outside it. Begin with:\n" +
                                   $"-- PLAN: realm, approach, cleanup\n" +
                                   $"End with (server-guarded):\n" +
                                   $"if SERVER then if IsValid(RequestingPlayer) then RequestingPlayer:ChatPrint(\"feedback message\") end end\n\n" +
                                   $"## Game State\n" +
                                   $"{dynamicContext}\n\n" +
                                   globalContextSection +
                                   (!string.IsNullOrWhiteSpace(relevantExamples) ? $"{relevantExamples}\n\n" : ""))
            };

            // Always inject this conversation's history (error fixes see the original request + prior turns)
            messages.AddRange(conv.History);

            // Add the new user prompt
            var userMsg = ChatMessage.User($"[From {player}]: {prompt}");
            messages.Add(userMsg);
            conv.History.Add(userMsg);

            // Cap per-conversation history at 30 messages
            if (conv.History.Count > 30) conv.History.RemoveAt(0);

            int turnCount = 0;
            const int maxTurns = 5;

            while (turnCount < maxTurns)
            {
                var completionReq = new CompletionRequest
                {
                    Model = modelForThisTurn,
                    Messages = messages,
                    Tools = GetActiveTools().Select(t => t.GetDefinition()).ToList(),
                                };

                Console.WriteLine($"[Daemon] Streaming prompt to LLM (Turn {turnCount + 1})...");

                var logEntry = new SessionLogger.Entry
                {
                    Prompt = $"[{conv.ConversationId}] [{player}] {prompt}",
                };

                string finalResponseText = "";
                List<ToolCall>? toolCalls = null;

                await foreach (var chunk in _backend.GenerateCompletionStreamAsync(completionReq))
                {
                    if (chunk.ToolCalls != null && chunk.ToolCalls.Count > 0)
                    {
                        toolCalls = chunk.ToolCalls;
                    }
                    if (!string.IsNullOrEmpty(chunk.ContentDelta))
                    {
                        finalResponseText += chunk.ContentDelta;
                        var isThought = chunk.ContentDelta.TrimStart().StartsWith("<thought>");
                        if (!isThought)
                            Console.Write(chunk.ContentDelta);
                    }
                }

                Console.WriteLine($"\n[Daemon] Stream Finished (Tool Calls: {toolCalls?.Count ?? 0})");

                if (toolCalls != null && toolCalls.Count > 0)
                {
                    var msg = ChatMessage.AssistantTools(toolCalls);
                    messages.Add(msg);
                    conv.History.Add(msg);

                    foreach (var tc in toolCalls)
                    {
                        Console.WriteLine($"\n[TOOL CALL] {tc.Function.Name}({tc.Function.Arguments})");
                        await _transport.SendChatAsync($"[TOOL CALL] {tc.Function.Name}({tc.Function.Arguments})");

                        using var argsDoc = JsonDocument.Parse(tc.Function.Arguments ?? "{}");

                        var tool = GetActiveTools().FirstOrDefault(t => t.Name == tc.Function.Name);
                        if (tool != null)
                        {
                            var result = await tool.ExecuteAsync(argsDoc);
                            var resultText = result.Success ? result.Content : $"Error: {result.Error}";
                            Console.WriteLine($"[TOOL RESULT] {(result.Success ? "Success" : "Failed")}\n{resultText}");
                            await _transport.SendChatAsync($"[TOOL RESULT] {(result.Success ? "Success" : "Failed")}\n{resultText}");

                            var resMsg = result.Success
                                ? ChatMessage.ToolResult(tc.Id, resultText)
                                : ChatMessage.ToolResult(tc.Id, $"Tool failed: {result.Error}");
                            messages.Add(resMsg);
                            conv.History.Add(resMsg);
                        }
                        else
                        {
                            var nfMsg = ChatMessage.ToolResult(tc.Id, $"Tool '{tc.Function.Name}' not found.");
                            messages.Add(nfMsg);
                            conv.History.Add(nfMsg);
                        }
                    }

                    turnCount++;
                    continue;
                }

                // No tool calls — process response text
                if (!string.IsNullOrWhiteSpace(finalResponseText))
                {
                    var isThought = finalResponseText.TrimStart().StartsWith("<thought>");
                    if (!isThought)
                    {
                        var msg = ChatMessage.Assistant(finalResponseText);
                        conv.History.Add(msg);

                        // Log to session
                        logEntry.Response = finalResponseText;
                        _logger.Write(logEntry);
                    }
                }

                // Dispatch pending Lua to GMod
                while (_pendingLuaExecution.TryDequeue(out var luaCode))
                {
                    await _transport.RunLuaAsync($"__AI_CONV_ID = \"{conv.ConversationId}\"\n" + luaCode);
                }
                if (!string.IsNullOrWhiteSpace(finalResponseText))
                {
                    var isThought = finalResponseText.TrimStart().StartsWith("<thought>");
                    if (!isThought)
                    {
                        // Extract and broadcast the response
                        var isModelSwitch = finalResponseText.Contains("[Model Switch]");
                        if (!isModelSwitch)
                        {
                            var luaBlocks = new List<string>();
                            var stripped = System.Text.RegularExpressions.Regex.Replace(
                                finalResponseText,
                                @"```(?:lua|LUA)\s*\n?(.*?)\n?```",
                                m => { luaBlocks.Add(m.Groups[1].Value); return ""; },
                                System.Text.RegularExpressions.RegexOptions.Singleline
                            );

                            // Broadcast non-code text
                            foreach (var line in stripped.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                            {
                                await _transport.SendChatAsync($"[AI] {line}");
                            }

                            // Queue Lua blocks
                            foreach (var block in luaBlocks)
                            {
                                Console.WriteLine($"[Daemon] Queuing fenced Lua block for execution.");
                                _pendingLuaExecution.Enqueue(block);
                            }

                            // Also check for unfenced Lua
                            if (luaBlocks.Count == 0 && LooksLikeLua(finalResponseText))
                            {
                                Console.WriteLine("[Daemon] Detected unfenced Lua response. Queuing for execution!");
                                _pendingLuaExecution.Enqueue(finalResponseText);
                            }
                        }
                    }
                }

                while (_pendingLuaExecution.TryDequeue(out var luaCode))
                {
                    await _transport.RunLuaAsync($"__AI_CONV_ID = \"{conv.ConversationId}\"\n" + luaCode);
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Daemon] Error processing request: {ex.Message}");
            await _transport.SendChatAsync($"[AI Error] {ex.Message}");
        }
    }

    private static bool LooksLikeLua(string text)
    {
        var luaKeywords = new[] { "local ", "function ", "if ", "end", "RunSharedLua", "RunClientLua", "ents.", "player.", "hook.", "timer." };
        int hits = luaKeywords.Count(kw => text.Contains(kw));
        return hits >= 3;
    }

    private async Task<string> ClassifyPromptAsync(string prompt, string routerModel)
    {
        var req = new CompletionRequest
        {
            Model = routerModel,
            Messages = new List<ChatMessage>
            {
                ChatMessage.System("Classify the following GMod Lua request as SIMPLE or COMPLEX. SIMPLE = basic spawn, give item, one-liner effects. COMPLEX = custom SWEP, multi-file addon, advanced physics, HUD system. Reply with only SIMPLE or COMPLEX."),
                ChatMessage.User(prompt)
            },
                };

        string result = "COMPLEX";
        await foreach (var chunk in _backend.GenerateCompletionStreamAsync(req))
        {
            if (!string.IsNullOrEmpty(chunk.ContentDelta))
                result = chunk.ContentDelta.Trim().ToUpperInvariant();
        }
        return result.Contains("SIMPLE") ? "SIMPLE" : "COMPLEX";
    }
}
