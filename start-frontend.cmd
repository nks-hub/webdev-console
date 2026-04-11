@echo off
REM NKS WebDev Console - frontend + Electron (auto-starts daemon unless already running)
setlocal
cd /d "%~dp0src\frontend"

if not exist "node_modules\" (
    echo [frontend] Installing dependencies...
    call npm install
    if errorlevel 1 exit /b 1
)

echo [frontend] Starting electron-vite dev...
call npm run dev
endlocal
