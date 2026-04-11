@echo off
REM NKS WebDev Console - daemon only (for debugging without Electron)
setlocal
cd /d "%~dp0src\daemon\NKS.WebDevConsole.Daemon"
echo [daemon] Running C# daemon standalone (Ctrl+C to stop)...
dotnet run
endlocal
