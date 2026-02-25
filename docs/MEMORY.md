# AgentSharp & AIPlayground: Phase 1 Completion

## Project Status
**AgentSharp.Core**: A universal C# AI library that executes Tool definitions dynamically and runs autonomous loops using the OpenRouter backend. Fully operational.
**AIPlayground (GMod)**: A daemon and client-side bridge that allows Garry's Mod to communicate with the C# AgentSharp backend to build, edit, and instantly hot-load SWEPs, Entities, and Lua scripts without ever restarting the game.

## Core Architecture
- The C# Daemon runs locally and monitors `garrysmod/data/aiplayground/inbox.json` for prompts triggered by typing `!c <prompt>` in the GMod chat.
- The AI runs in an autonomous `maxTurns = 5` loop.
- It operates completely independently inside the `garrysmod/addons/AIPlayground_Projects` addon folder.

## Tools Available to the AI
1. **list_files(path)**: Prevents hallucinations by forcing the AI to list the directory of `lua/ai_projects/` to see what projects exist before acting.
2. **read_file(path)**: Forces the AI to read its existing code before attempting to modify it.
3. **write_file(path, content)**: Writes raw code straight into the `AIPlayground_Projects` folder.
4. **hot_reload(path)**: Injects the script into the live server memory.
5. **reload_spawn_menu()**: Automatically triggers `RunConsoleCommand("spawnmenu_reload")` on the client so the new weapon appears in the Q-Menu without re-joining.
6. **graduate_project(name, title, desc, type, tags)**: Packages the completed project from `AIPlayground_Projects` into its own standalone, Workshop-ready Addon directory with a generated `addon.json`.

## Error Feedback Loop
- GMod's `GM:OnLuaError` hook was added to both the client (`ai_playground_client.lua`) and the server (`ai_playground_server.lua`).
- If an AI-generated script throws a Syntax or Runtime Error (e.g., during a SWEP's `PrimaryAttack`), the hook catches it.
- The client instantly flashes red in chat and automatically sends an invisible prompt to the Daemon: `"You got a Lua execution error:\n<Trace>\nPlease fix the file and run hot_reload again."`
- The AI automatically reads the error, calls `read_file`, identifies the bug, calls `write_file`, and instantly reloads the fixed code.

## Key Design Principles
- **No Mental Notes:** The AI relies entirely on `list_files` and `read_file`. It does not guess paths.
- **Isolate Projects:** All code MUST be written to `lua/ai_projects/<project_name>/`.
- **Global Evaluation:** `hot_reload` uses `CompileString` with `pcall` to safely execute scripts on the server without crashing the entire instance if the syntax is malformed.

## Next Steps (Phase 2 Ideas)
- We have established a flawless SWEP generation pipeline. Next we should try creating Entities, NPCs, or UI panels.
- Addon graduation currently defaults to `lua/autorun/`. We could enhance `GraduateProjectTool.cs` to map files specifically to `lua/weapons/` or `lua/entities/` during the graduation process to match standard GMod structure.