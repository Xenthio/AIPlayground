if SERVER then
    AddCSLuaFile()
end

-- Automatically mounts and loads all AIPlayground sub-projects 
-- The AI creates its projects in lua/ai_projects/project_name/ inside the AIPlayground_Projects addon
local function LoadAIProjects()
    local path = "ai_projects/"
    
    -- Find all folders (projects) inside lua/ai_projects/
    local _, folders = file.Find(path .. "*", "LUA")
    
    if folders then
        for _, folder in ipairs(folders) do
            local projectPath = path .. folder .. "/"
            
            -- Inside each project, find all lua files
            local files, _ = file.Find(projectPath .. "*.lua", "LUA")
            
            if files then
                for _, f in ipairs(files) do
                    local fullPath = projectPath .. f
                    
                    -- Always send the file to the client
                    if SERVER then
                        AddCSLuaFile(fullPath)
                    end
                    
                    -- Include it on both client and server
                    include(fullPath)
                    print("[AIPlayground] Mounted AI project file: " .. fullPath)
                end
            end
        end
    end
end

-- Wait a frame or two on boot to ensure the AIPlayground_Projects addon has been indexed by the engine
timer.Simple(0.1, function()
    LoadAIProjects()
end)