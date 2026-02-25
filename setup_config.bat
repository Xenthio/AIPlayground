@echo off
echo ========================================================
echo   AIPlayground GMod Configuration Setup
echo ========================================================
echo.
echo Enter the full path to your Garry's Mod install directory
echo Example: C:\Program Files (x86)\Steam\steamapps\common\GarrysMod
echo (Do not include a trailing backslash)
echo.
set /p gmodpath="Game Path: "

if not exist "%gmodpath%\hl2.exe" (
    echo.
    echo ERROR: Could not find hl2.exe in that directory. Is it the right folder?
    pause
    exit /b
)

:: Create the symlink for the base AIPlayground addon so the client works
if exist "%gmodpath%\garrysmod\addons\AIPlayground" rmdir /s /q "%gmodpath%\garrysmod\addons\AIPlayground"
mklink /j "%gmodpath%\garrysmod\addons\AIPlayground" "%cd%\src\AIPlayground.GMod"

:: Write the configuration to a JSON file so the C# Daemon can read it
echo { > config.json
echo   "GModAddonsPath": "%gmodpath:\=\\%\\garrysmod\\addons" >> config.json
echo } >> config.json

echo.
echo SUCCESS: Configuration saved to config.json!
echo The AI will now write its addons directly to:
echo %gmodpath%\garrysmod\addons\
echo.
pause