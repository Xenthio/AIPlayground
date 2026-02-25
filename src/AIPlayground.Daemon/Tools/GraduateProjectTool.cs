using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSharp.Core.Interfaces;

namespace AIPlayground.Daemon.Tools;

/// <summary>
/// A tool that extracts an AIPlayground sub-project into its own standalone Garry's Mod Addon directory
/// </summary>
public sealed class GraduateProjectTool : ITool
{
    private readonly string _gmodAddonsPath;

    public GraduateProjectTool(string gmodAddonsPath)
    {
        _gmodAddonsPath = gmodAddonsPath;
    }

    public string Name => "graduate_project";
    public string Description => "Extract a fully working AI project (from AIPlayground/lua/ai_projects/<project_name>/) into its own standalone Garry's Mod Addon folder with an addon.json, ready for Steam Workshop publishing! This will move the project and prompt the user to restart the server.";

    public object Parameters => JsonNode.Parse("""
    {
        "type": "object",
        "properties": {
            "project_name": {
                "type": "string",
                "description": "The exact name of the folder inside AIPlayground/lua/ai_projects/ (e.g. laser_shotgun)"
            },
            "title": {
                "type": "string",
                "description": "The display title for the Addon (e.g. AI Laser Shotgun SWEP)"
            },
            "description": {
                "type": "string",
                "description": "A description of the addon for the Steam Workshop"
            },
            "type": {
                "type": "string",
                "description": "The primary category (e.g. weapon, vehicle, model, tool)"
            },
            "tags": {
                "type": "array",
                "items": { "type": "string" },
                "description": "Up to two Steam Workshop tags (e.g. [\"fun\", \"roleplay\"])"
            }
        },
        "required": [ "project_name", "title", "description", "type" ]
    }
    """)!;

    public Task<ToolResult> ExecuteAsync(JsonDocument arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = arguments.RootElement;
            var projectName = root.GetProperty("project_name").GetString() ?? "";
            var title = root.GetProperty("title").GetString() ?? "";
            var desc = root.GetProperty("description").GetString() ?? "";
            var type = root.GetProperty("type").GetString() ?? "weapon";

            var tags = new List<string>();
            if (root.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsElement.EnumerateArray())
                {
                    tags.Add(tag.GetString() ?? "");
                }
            }

            var sourceDir = Path.Combine(_gmodAddonsPath, "AIPlayground", "lua", "ai_projects", projectName);
            var destDir = Path.Combine(_gmodAddonsPath, projectName);

            if (!Directory.Exists(sourceDir))
            {
                return Task.FromResult(ToolResult.Fail($"Project {projectName} does not exist at {sourceDir}."));
            }

            if (Directory.Exists(destDir))
            {
                return Task.FromResult(ToolResult.Fail($"An addon named {projectName} already exists at {destDir}. Please pick a different project_name or manually delete it first."));
            }

            // Create standard Garry's Mod Addon structure
            Directory.CreateDirectory(destDir);
            
            // Re-structure the lua files back into the native layout
            // (Moving from lua/ai_projects/<name>/ to lua/weapons/<name>/ or lua/autorun/)
            // For now, we'll just put them in lua/autorun/<name>_init.lua so they execute automatically like the ai_loader did
            var destLuaDir = Path.Combine(destDir, "lua", "autorun");
            Directory.CreateDirectory(destLuaDir);

            foreach (var file in Directory.GetFiles(sourceDir, "*.lua", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                // Prefix it with the project name to avoid autorun collisions
                var safeFileName = $"{projectName}_{fileName}";
                File.Copy(file, Path.Combine(destLuaDir, safeFileName));
            }

            // Also copy the docs folder if it exists
            var sourceDocs = Path.Combine(_gmodAddonsPath, "AIPlayground", "docs", projectName);
            if (Directory.Exists(sourceDocs))
            {
                var destDocs = Path.Combine(destDir, "docs");
                Directory.CreateDirectory(destDocs);
                foreach (var file in Directory.GetFiles(sourceDocs, "*.*", SearchOption.AllDirectories))
                {
                    File.Copy(file, Path.Combine(destDocs, Path.GetFileName(file)));
                }
            }

            // Create the addon.json
            var addonJson = new
            {
                title = title,
                type = type,
                tags = tags.Take(2),
                ignore = new[] { "*.psd", "*.vcproj", "*.svn*" }
            };

            File.WriteAllText(Path.Combine(destDir, "addon.json"), JsonSerializer.Serialize(addonJson, new JsonSerializerOptions { WriteIndented = true }));
            
            // Delete the old AIPlayground folder since it's graduated
            Directory.Delete(sourceDir, true);
            if (Directory.Exists(sourceDocs)) Directory.Delete(sourceDocs, true);

            return Task.FromResult(ToolResult.Ok($"Successfully graduated project '{projectName}' to standalone addon '{title}'. The files have been restructured and addon.json generated. Tell the user to RESTART Garry's Mod to mount the new addon!"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Failed to graduate project: {ex.Message}"));
        }
    }
}