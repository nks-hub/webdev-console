@echo off
REM NKS WebDev Console - one-click dev launcher
REM Starts the frontend (electron-vite) which auto-spawns the C# daemon.

setlocal
cd /d "%~dp0src\frontend"

if not exist "node_modules\" (
    echo [start] Installing frontend dependencies...
    call npm install
    if errorlevel 1 (
        echo [start] npm install failed.
        exit /b 1
    )
)

echo [start] Launching NKS WebDev Console (frontend + daemon)...
call npm run dev
endlocal
