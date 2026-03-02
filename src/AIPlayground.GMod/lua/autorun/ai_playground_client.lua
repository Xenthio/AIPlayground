if SERVER then return end

-- Receive chat messages or spawnmenu reload commands from the Server
net.Receive("AIPlayground_ClientChat", function()
    local data = net.ReadTable()
    
    if data[1] == "!SPAWNMENU" then
        print("[AIPlayground] Reloading Spawnmenu...")
        RunConsoleCommand("spawnmenu_reload")
        return
    end

    chat.AddText(unpack(data))
end)

-- Receive successfully compiled hot-reload code from the server
net.Receive("AIPlayground_RunLuaClient", function()
    local code = net.ReadString()
    local scriptId = net.ReadString() or "AI_Generated_Script_Client"
    if code and code ~= "" then
        print("[AIPlayground] Executing Server-Broadcasted HotReload on Client (" .. scriptId .. ")...")
        
        local func = CompileString(code, scriptId, false)
        if isfunction(func) then
            pcall(func)
        end
    end
end)

local function SendToDaemon(message)
    net.Start("AIPlayground_AskDaemon")
    net.WriteString(message)
    net.SendToServer()
end

-- Catch Client-side global errors and send them to the server to forward to the daemon!
hook.Add("OnLuaError", "AIPlayground_GlobalClientErrorCatcher", function(errorString, realm, stack, name, id)
    local isAIError = false
    
    if string.find(errorString, "ai_projects") or string.find(errorString, "AI_Generated_Script") then
        isAIError = true
    end

    if not isAIError and stack then
        for _, level in ipairs(stack) do
            if string.find(level.File, "ai_projects") or string.find(level.File, "AI_Generated_Script") then
                isAIError = true
                break
            end
        end
    end

    if isAIError then
        print("[AIPlayground] Caught global client AI-related error! Feeding to daemon...")
        chat.AddText(Color(255, 50, 50), "[AI Code Error (Client)] ", Color(255, 255, 255), errorString)
        SendToDaemon("You got a Client-side Lua execution error:\n" .. errorString .. "\n\nPlease fix the file and run hot_reload again.")
    end
end)

-- Catch local chat messages starting with !c or !model
hook.Add("OnPlayerChat", "AIPlayground_Command", function(ply, text)
    if ply == LocalPlayer() then
        local lowerText = string.lower(text)
        
        -- Normal chat command
        if string.StartWith(lowerText, "!c ") then
            local prompt = string.sub(text, 4)
            chat.AddText(Color(100, 200, 255), "[You] ", Color(255, 255, 255), prompt)
            SendToDaemon(prompt)
            return true
        end

        -- Model switch command
        if string.StartWith(lowerText, "!model ") then
            local prompt = "!model " .. string.Trim(string.sub(text, 8))
            chat.AddText(Color(255, 150, 0), "[AI System] ", Color(255, 255, 255), "Switching model to " .. string.sub(text, 8) .. "...")
            SendToDaemon(prompt)
            return true
        end

        -- Embedding search command
        if string.StartWith(lowerText, "!search ") then
            local prompt = "!search " .. string.Trim(string.sub(text, 9))
            chat.AddText(Color(200, 100, 255), "[AI System] ", Color(255, 255, 255), "Testing semantic embedding search for: " .. string.sub(text, 9))
            SendToDaemon(prompt)
            return true
        end

        -- History toggle command
        if string.StartWith(lowerText, "!history ") then
            local prompt = "!history " .. string.Trim(string.sub(text, 10))
            chat.AddText(Color(100, 255, 150), "[AI System] ", Color(255, 255, 255), "Toggling history to " .. string.sub(text, 10) .. "...")
            SendToDaemon(prompt)
            return true
        end

        -- Router command
        if string.StartWith(lowerText, "!router ") then
            local arg = string.Trim(string.sub(text, 9))
            local prompt = "!router " .. arg
            if string.lower(arg) == "off" then
                chat.AddText(Color(255, 150, 0), "[AI System] ", Color(255, 255, 255), "Disabling prompt router...")
            else
                chat.AddText(Color(255, 150, 0), "[AI System] ", Color(255, 255, 255), "Enabling prompt router with model: " .. arg)
            end
            SendToDaemon(prompt)
            return true
        end
    end
end)