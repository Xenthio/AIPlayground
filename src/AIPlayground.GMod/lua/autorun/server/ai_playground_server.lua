if CLIENT then return end

require("gwsockets")

util.AddNetworkString("AIPlayground_RunLua")
util.AddNetworkString("AIPlayground_RunLuaClient")
util.AddNetworkString("AIPlayground_ErrorReport")
util.AddNetworkString("AIPlayground_ClientChat")
util.AddNetworkString("AIPlayground_AskDaemon")

local socketUrl = "ws://localhost:27016/"
local websocket = nil
local messageQueue = {}
local reconnectTimerName = "AIPlayground_Daemon_Reconnect"
local isShuttingDown = false

-- Prevent creating multiple connections on Lua Refresh
if _G.AIPlayground_GlobalSocket then
    _G.AIPlayground_GlobalSocket:closeNow()
    _G.AIPlayground_GlobalSocket = nil
end

local function ConnectToDaemon()
    if isShuttingDown then return end
    
    -- If we somehow already have a socket, don't create another one
    if websocket and websocket:isConnected() then return end
    
    print("[AIPlayground] Connecting to local Daemon at " .. socketUrl)
    websocket = GWSockets.createWebSocket(socketUrl)
    _G.AIPlayground_GlobalSocket = websocket

    function websocket:onConnected()
        print("[AIPlayground] Connected to C# Daemon via GWSockets!")
        timer.Remove(reconnectTimerName)
        
        -- Broadcast connection success to all clients
        net.Start("AIPlayground_ClientChat")
        net.WriteTable({Color(100, 255, 100), "[AI System] ", Color(255, 255, 255), "Connected to AIPlayground Daemon!"})
        net.Broadcast()
        
        -- Flush queued messages
        for _, msg in ipairs(messageQueue) do
            websocket:write(msg)
        end
        messageQueue = {}
    end

    function websocket:onDisconnected()
        if isShuttingDown then return end
        
        print("[AIPlayground] Disconnected from Daemon. Reconnecting in 5 seconds...")
        
        net.Start("AIPlayground_ClientChat")
        net.WriteTable({Color(255, 100, 100), "[AI System] ", Color(255, 255, 255), "Lost connection to Daemon. Auto-reconnecting..."})
        net.Broadcast()
        
        websocket = nil
        timer.Create(reconnectTimerName, 5, 1, ConnectToDaemon)
    end

    function websocket:onError(err)
        print("[AIPlayground] WebSocket Error: " .. tostring(err))
    end

    function websocket:onMessage(msg)
        if not msg or msg == "" then return end
        
        local res = util.JSONToTable(msg)
        if not res then return end

        if res.response and res.response ~= "" then
            local isThought = string.StartWith(res.response, "<thought>")
            local text = res.response
            
            if isThought then
                text = string.sub(text, 11) -- Remove "<thought> "
                -- Thoughts print to server console only, not chat
                local lines = string.Explode("\n", text)
                for _, line in ipairs(lines) do
                    if line and string.Trim(line) ~= "" then
                        print("[AI Thought] " .. string.Trim(line))
                    end
                end
            elseif res.is_model_switch then
                net.Start("AIPlayground_ClientChat")
                net.WriteTable({Color(255, 150, 0), "[AI System] ", Color(255, 255, 255), text})
                net.Broadcast()
            else
                -- Extract and run any ```lua blocks; print remaining text to chat
                local luaBlocks = {}
                local stripped = string.gsub(text, "```[lL][uU][aA]%s*\n?(.-)\n?```", function(code)
                    table.insert(luaBlocks, code)
                    return ""   -- remove block from chat text
                end)

                -- Print non-code text lines
                local printedAny = false
                local lines = string.Explode("\n", stripped)
                for _, line in ipairs(lines) do
                    line = string.Trim(line)
                    if line ~= "" then
                        net.Start("AIPlayground_ClientChat")
                        net.WriteTable({Color(100, 255, 100), "[AI] ", Color(255, 255, 255), line})
                        net.Broadcast()
                        printedAny = true
                    end
                end

                -- Execute extracted Lua blocks
                for _, code in ipairs(luaBlocks) do
                    print("[AIPlayground] Running inline Lua from response...")
                    local scriptId = "AI_" .. tostring(__AI_CONV_ID or "UNKNOWN") .. "_" .. tostring(os.time())
                    local safeCode = string.gsub(code, "AddCSLuaFile%(.-%)", "-- AddCSLuaFile omitted for hotreload")

                    local env = setmetatable({
                        RequestingPlayer = IsValid(_lastRequestingPly) and _lastRequestingPly or nil,
                        RunClientLua = function(c)
                            net.Start("AIPlayground_RunLuaClient")
                            net.WriteString(c)
                            net.Broadcast()
                        end,
                        RunSharedLua = function(c)
                            local sf = CompileString(c, scriptId .. "_Shared", false)
                            if isstring(sf) then
                                print("[AIPlayground] Shared Lua Syntax Error: " .. sf)
                                AskDaemonServer("You got a Server Lua Syntax Error in RunSharedLua:\n" .. sf .. "\n\nFailing code:\n```lua\n" .. c .. "\n```\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                            else
                                SWEP = SWEP or {Primary = {}, Secondary = {}}
                                ENT = ENT or {}
                                local s, e = pcall(sf)
                                SWEP = nil
                                ENT = nil
                                if not s then
                                    print("[AIPlayground] Shared Lua Runtime Error: " .. tostring(e))
                                    AskDaemonServer("You got a Server Lua Runtime Error in RunSharedLua:\n" .. tostring(e) .. "\n\nFailing code:\n```lua\n" .. c .. "\n```\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                                end
                            end
                            net.Start("AIPlayground_RunLuaClient")
                            net.WriteString(c)
                            net.Broadcast()
                        end,
                    }, { __index = _G })

                    local func = CompileString(safeCode, scriptId, false)
                    if isstring(func) then
                        print("[AIPlayground] Inline Lua Syntax Error: " .. func)
                        AskDaemonServer("You got a Server Lua Syntax Error:\n" .. func .. "\n\nFailing code:\n```lua\n" .. safeCode .. "\n```\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                    else
                        setfenv(func, env)
                        local ok, err = pcall(func)
                        if not ok then
                            print("[AIPlayground] Inline Lua Runtime Error: " .. tostring(err))
                            AskDaemonServer("You got a Server Lua Runtime Error:\n" .. tostring(err) .. "\n\nFailing code:\n```lua\n" .. safeCode .. "\n```\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                        else
                            print("[AIPlayground] Inline Lua executed successfully.")
                            -- Missing path check (strip line comments first to avoid false positives)
                            local codeNoComments = string.gsub(code, "%-%-[^\n]*", "")
                            local missingPaths = {}
                            for path in string.gmatch(codeNoComments, "[\"']([^\"']+)[\"']") do
                                if not string.find(path, "%*") and (string.StartWith(path, "models/") or string.StartWith(path, "sound/") or string.StartWith(path, "materials/")) then
                                    if string.find(path, "%.mdl$") or string.find(path, "%.wav$") or string.find(path, "%.vmt$") or string.find(path, "%.mp3$") then
                                        if not file.Exists(path, "GAME") then
                                            table.insert(missingPaths, path)
                                        end
                                    end
                                end
                            end
                            if #missingPaths > 0 then
                                local seen, unique = {}, {}
                                for _, p in ipairs(missingPaths) do
                                    if not seen[p] then seen[p] = true table.insert(unique, p) end
                                end
                                AskDaemonServer("Your script executed but referenced missing paths: " .. table.concat(unique, ", ") .. "\n\nPlease search_assets for valid replacements and clean up any broken props.")
                            end
                            net.Start("AIPlayground_RunLuaClient")
                            net.WriteString(safeCode)
                            net.WriteString(scriptId)
                            net.Broadcast()
                        end
                    end
                end
            end
        end
        
        -- Run Lua code scheduled by AI
        if res.scripts and #res.scripts > 0 then
            for _, script in ipairs(res.scripts) do
                if script == "!SPAWNMENU" then
                    -- Special broadcast command to force client menu reloads
                    net.Start("AIPlayground_ClientChat")
                    net.WriteTable({"!SPAWNMENU"})
                    net.Broadcast()
                else
                    print("[AIPlayground] Executing AI Lua script...")
                    -- Generate a unique identifier for this code execution
                    local scriptId = "AI_" .. tostring(__AI_CONV_ID or "UNKNOWN") .. "_" .. tostring(os.time())
                    
                    local safeCode = string.gsub(script, "AddCSLuaFile%(.-%)", "-- AddCSLuaFile omitted for hotreload")
                    
                    -- Provide GilbAI compatible environment functions
                    local env = setmetatable({
                        RequestingPlayer = IsValid(_lastRequestingPly) and _lastRequestingPly or nil,
                        RunClientLua = function(code)
                            print("[AIPlayground] RunClientLua called, sending " .. #code .. " bytes to clients...")
                            net.Start("AIPlayground_RunLuaClient")
                            net.WriteString(code)
                            net.WriteString(scriptId .. "_Client")
                            net.Broadcast()
                        end,
                        RunSharedLua = function(code)
                            -- Run on Server
                            local sharedFunc = CompileString(code, scriptId .. "_Shared", false)
                            if isstring(sharedFunc) then
                                print("[AIPlayground] Shared Lua Syntax Error: " .. sharedFunc)
                                AskDaemonServer("You got a Server Lua Syntax Error in RunSharedLua:\n" .. sharedFunc .. "\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                            else
                                SWEP = SWEP or {Primary = {}, Secondary = {}}
                                ENT = ENT or {}
                                local s, e = pcall(sharedFunc)
                                SWEP = nil
                                ENT = nil
                                if not s then
                                    print("[AIPlayground] Shared Lua Runtime Error: " .. tostring(e))
                                    AskDaemonServer("You got a Server Lua Runtime Error in RunSharedLua:\n" .. tostring(e) .. "\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                                end
                            end

                            -- Broadcast to Clients
                            net.Start("AIPlayground_RunLuaClient")
                            net.WriteString(code)
                            net.Broadcast()
                        end
                    }, { __index = _G })
                    
                    local func = CompileString(safeCode, scriptId, false)
                    if isstring(func) then
                        print("[AIPlayground] Lua Syntax Error: " .. func)
                        AskDaemonServer("You got a Server Lua Syntax Error:\n" .. func .. "\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                    else
                        setfenv(func, env)
                        local success, err = pcall(func)
                        if not success then
                            print("[AIPlayground] Lua Runtime Error: " .. tostring(err))
                            AskDaemonServer("You got a Server Lua Runtime Error:\n" .. tostring(err) .. "\n\nFix ONLY the error above. Output the corrected version of that exact script in full — do NOT write a new generic replacement.")
                        else
                            print("[AIPlayground] Lua executed successfully without errors.")
                            
                            -- Extract string paths to test (strip line comments first to avoid false positives)
                            local scriptNoComments = string.gsub(script, "%-%-[^\n]*", "")
                            local missingPaths = {}
                            for path in string.gmatch(scriptNoComments, "[\"']([^\"']+)[\"']") do
                                -- Ignore paths that contain asterisks (search patterns) and ignore partial string fragments
                                if not string.find(path, "%*") and (string.StartWith(path, "models/") or string.StartWith(path, "sound/") or string.StartWith(path, "materials/")) then
                                    if string.find(path, "%.mdl$") or string.find(path, "%.wav$") or string.find(path, "%.vmt$") or string.find(path, "%.mp3$") then
                                        if not file.Exists(path, "GAME") then
                                            table.insert(missingPaths, path)
                                        end
                                    end
                                end
                            end
                            
                            if #missingPaths > 0 then
                                -- Deduplicate missing paths list so we don't spam the same error string
                                local hash = {}
                                local uniquePaths = {}
                                for _, p in ipairs(missingPaths) do
                                    if not hash[p] then
                                        hash[p] = true
                                        table.insert(uniquePaths, p)
                                    end
                                end
                                
                                local missingList = table.concat(uniquePaths, ", ")
                                AskDaemonServer("Your script executed successfully, but you referenced paths that DO NOT EXIST on the server! You MUST use the `search_assets` tool to find valid replacements for: " .. missingList .. "\n\nCRITICAL: Since your script partially executed, there are probably broken/invisible ERROR props sitting in the world right now! You MUST write a cleanup script to delete the broken props you just spawned before trying again!")
                            end

                            net.Start("AIPlayground_RunLuaClient")
                            net.WriteString(safeCode)
                            net.WriteString(scriptId)
                            net.Broadcast()
                        end
                    end
                end
            end
        end
    end

    websocket:open()
end

ConnectToDaemon()

function AskDaemonServer(promptText, playerName)
    playerName = playerName or _lastRequestingPlayer or "Server"
    
    -- Build dynamic context string
    local plys = player.GetAll()
    local plyNames = {}
    local requestingUserId = 1
    for _, p in ipairs(plys) do
        local tr = p:GetEyeTrace()
        local lookPos = string.format("Looking at: Vector(%d, %d, %d)", math.Round(tr.HitPos.x), math.Round(tr.HitPos.y), math.Round(tr.HitPos.z))
        local plyPos = string.format("Position: Vector(%d, %d, %d)", math.Round(p:GetPos().x), math.Round(p:GetPos().y), math.Round(p:GetPos().z))
        
        table.insert(plyNames, string.format("%s (UserID: %d, %s, %s)", p:Nick(), p:UserID(), plyPos, lookPos))
        if p:Nick() == playerName then
            requestingUserId = p:UserID()
        end
    end
    
    local dynCtx = string.format("Map: %s\nPlayers Online: %d\nPlayer Data:\n- %s", game.GetMap(), #plys, table.concat(plyNames, "\n- "))
    
    local payload = util.TableToJSON({
        prompt = promptText,
        player = playerName,
        userId = requestingUserId,
        context = dynCtx
    })

    if websocket and websocket:isConnected() then
        websocket:write(payload)
    else
        table.insert(messageQueue, payload)
        
        net.Start("AIPlayground_ClientChat")
        net.WriteTable({Color(255, 150, 0), "[AI System] ", Color(255, 255, 255), "Daemon offline. Queuing message..."})
        net.Broadcast()
    end
end

-- Track the last requesting player so error fixup calls use the right UserID
local _lastRequestingPlayer = nil
local _lastRequestingPly = nil

-- Client asking Daemon
net.Receive("AIPlayground_AskDaemon", function(len, ply)
    if not ply:IsSuperAdmin() then return end
    local prompt = net.ReadString()
    _lastRequestingPlayer = ply:Nick()
    _lastRequestingPly = ply
    AskDaemonServer(prompt, ply:Nick())
end)

-- Server catching its own errors
hook.Add("OnLuaError", "AIPlayground_GlobalErrorCatcher", function(errorString, realm, stack, name, id)
    if realm == 1 or realm == "client" then return end 

    local isAIError = false
    if string.find(errorString, "ai_projects") or string.find(errorString, "AI_UNKNOWN") or string.find(errorString, "^AI_[A-Z0-9]+_") then
        isAIError = true
    end

    if not isAIError and stack then
        for _, level in ipairs(stack) do
            if string.find(level.File, "ai_projects") or string.find(level.File, "AI_UNKNOWN") or string.find(level.File, "^AI_[A-Z0-9]+_") then
                isAIError = true
                break
            end
        end
    end

    if isAIError then
        print("[AIPlayground] Caught global AI-related error! Sending to Daemon...")
        AskDaemonServer("You got a Server Lua Engine Error:\n" .. tostring(errorString) .. "\n\nPlease fix the file and run hot_reload again.")
    end
end)

hook.Add("ShutDown", "AIPlayground_CloseSocket", function()
    isShuttingDown = true
    timer.Remove(reconnectTimerName)
    if websocket then
        websocket:closeNow()
        websocket = nil
    end
end)