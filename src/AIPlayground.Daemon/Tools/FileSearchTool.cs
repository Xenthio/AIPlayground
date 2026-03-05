using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;
using AIPlayground.Daemon.Transports;

namespace AIPlayground.Daemon.Tools;

/// <summary>
/// A tool that allows the AI to search Garry's Mod mounted files (models, materials, sounds, etc.)
/// </summary>
public sealed class FileSearchTool : ITool
{
    private readonly IGModTransport _transport;
    private readonly string _gmodPath;

    public FileSearchTool(IGModTransport transport, string gmodPath)
    {
        _transport = transport;
        _gmodPath = gmodPath;
    }

    public string Name => "search_assets";
    public string Description => "Search the live Garry's Mod engine for mounted files! Use this to find exact model (.mdl), sound (.wav/.mp3), or material (.vmt) paths before guessing them in your code. (e.g. pattern='models/weapons/*.mdl', path='GAME')";

    public object Parameters => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The search pattern (e.g., 'models/weapons/*_cbar.mdl', 'sound/weapons/*shotgun*.wav')"
            },
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The search path ID. Usually 'GAME' to search everything mounted."
            }
        },
        ["required"] = new JsonArray { "pattern", "path" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var node = JsonNode.Parse(arguments.RootElement.GetRawText());
            var pattern = node?["pattern"]?.ToString() ?? "";
            var pathId = node?["path"]?.ToString() ?? "GAME";

            // Extract base directory vs wildcard from pattern
            var lastSlash = pattern.LastIndexOf('/');
            var basePath = lastSlash >= 0 ? pattern.Substring(0, lastSlash + 1) : "";
            var searchStr = lastSlash >= 0 ? pattern.Substring(lastSlash + 1) : pattern;

            // Only recurse if searchStr is bare wildcard (no pattern targeting specific files)
            bool recursive = searchStr == "*";
            var luaRecursive = recursive ? "true" : "false";
            var uniqueId = Guid.NewGuid().ToString("N");

            var luaScript = $$"""
            if not file.Exists("aiplayground/asset_cache.json", "DATA") then
                file.CreateDir("aiplayground")
                file.Write("aiplayground/asset_cache.json", "{}")
            end
            local ai_asset_cache = util.JSONToTable(file.Read("aiplayground/asset_cache.json", "DATA") or "{}") or {}
            local cacheDirty = false

            local function RecursiveSearch(basePath, searchPattern)
                local results = ""
                
                -- Ensure basePath has a trailing slash if it's not empty
                if basePath ~= "" and string.sub(basePath, -1) ~= "/" then
                    basePath = basePath .. "/"
                end
                
                -- Always search the current exact directory using the pattern
                local files, _ = file.Find(basePath .. "{{searchStr}}", "{{pathId}}")
                
                if files then
                    for _, f in ipairs(files) do
                        local fullPath = basePath .. f
                        local boundsInfo = ""
                        
                        -- Cache lookup system
                            if ai_asset_cache[fullPath] then
                                boundsInfo = ai_asset_cache[fullPath]
                            else
                                -- If it's a model, calculate its bounding box size so the AI knows how big it is
                                if string.match(string.lower(f), "%.mdl$") then
                                    local ent = ents.Create("prop_physics")
                                    if IsValid(ent) then
                                        ent:SetModel(fullPath)
                                        ent:Spawn()
                                        local mins, maxs = ent:GetModelBounds()
                                        if mins and maxs then
                                            local size = maxs - mins
                                            boundsInfo = string.format(" [Size: W:%.1f, L:%.1f, H:%.1f]", size.y, size.x, size.z)
                                        end
                                        ent:Remove()
                                    end
                                elseif string.match(string.lower(f), "%.wav$") or string.match(string.lower(f), "%.mp3$") then
                                    local soundDuration = SoundDuration(fullPath)
                                    if soundDuration and soundDuration > 0 then
                                        boundsInfo = string.format(" [Length: %.1fs]", soundDuration)
                                    else
                                        boundsInfo = " [Audio File]"
                                    end
                                elseif string.match(string.lower(f), "%.vmt$") then
                                    local mat = Material(fullPath)
                                    if mat and not mat:IsError() then
                                        boundsInfo = string.format(" [Shader: %s]", mat:GetShader())
                                    else
                                        boundsInfo = " [Material]"
                                    end
                                elseif string.match(string.lower(f), "%.vtf$") then
                                    local mat = Material(string.gsub(fullPath, "%.vtf$", ""))
                                    if mat and not mat:IsError() then
                                        boundsInfo = string.format(" [Texture]")
                                    end
                                end
                                
                                -- Save to cache so we never evaluate this specific file again
                                if boundsInfo ~= "" then
                                    ai_asset_cache[fullPath] = boundsInfo
                                    cacheDirty = true
                                end
                            end
                            
                            results = results .. fullPath .. boundsInfo .. "\n"
                    end
                end
                
                local _, dirs = file.Find(basePath .. "*", "{{pathId}}")
                
                if dirs and {{luaRecursive}} then
                    for _, d in ipairs(dirs) do
                        results = results .. RecursiveSearch(basePath .. d .. "/", searchPattern)
                    end
                end
                
                return results
            end
            
            local searchStr = string.gsub("{{searchStr}}", "%.", "%%.")
            searchStr = string.gsub(searchStr, "%%*", ".*")
            
            local finalResults = RecursiveSearch("{{basePath}}", searchStr)
            
            if cacheDirty then
                file.Write("aiplayground/asset_cache.json", util.TableToJSON(ai_asset_cache))
            end
            
            if not file.Exists("aiplayground", "DATA") then
                file.CreateDir("aiplayground")
            end
            
            file.Write("aiplayground/search_{{uniqueId}}.txt", finalResults)
            """;
            
            await _transport.RunLuaAsync(luaScript);

            var resultPath = Path.Combine(_gmodPath, "garrysmod", "data", "aiplayground", $"search_{uniqueId}.txt");
            
            // Wait up to 5 seconds for the engine to write the file
            int waitMs = 0;
            while (!File.Exists(resultPath) && waitMs < 5000)
            {
                await Task.Delay(250, cancellationToken);
                waitMs += 250;
            }

            if (!File.Exists(resultPath))
            {
                return ToolResult.Fail("Engine search timed out or yielded no results.");
            }

            // Wait a tiny bit more to ensure file handle is flushed by Garry's Mod
            await Task.Delay(200, cancellationToken);

            var results = "";
            try
            {
                using var stream = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                results = await reader.ReadToEndAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"Failed to read engine search result: {ex.Message}");
            }
            
            try { File.Delete(resultPath); } catch {}

            if (string.IsNullOrWhiteSpace(results))
                return ToolResult.Ok($"No files found matching '{pattern}' in '{pathId}'");

            var lines = results.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            if (lines.Length > 100)
            {
                return ToolResult.Ok($"Found {lines.Length} files. Showing first 100:\n" + string.Join("\n", lines.Take(100)));
            }

            return ToolResult.Ok($"Found {lines.Length} files:\n" + string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to search assets: {ex.Message}");
        }
    }
}