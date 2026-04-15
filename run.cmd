@echo off
REM NKS WebDev Console — unified one-click launcher.
REM
REM Starts the C# daemon and the Electron frontend in their own console
REM windows so both log streams are visible. Kills any leftover instances
REM from a previous run before bringing fresh ones up, so restarts are
REM always clean.
REM
REM Usage:
REM   run.cmd                    - build (if needed) + start both
REM   run.cmd --no-build          - skip build, just start
REM   run.cmd --daemon-only       - only daemon, no Electron
REM   run.cmd --frontend-only     - only frontend, no daemon (assumes daemon runs)

setlocal EnableDelayedExpansion
cd /d "%~dp0"

set SKIP_BUILD=0
set DAEMON_ONLY=0
set FRONTEND_ONLY=0

for %%A in (%*) do (
    if /I "%%A"=="--no-build" set SKIP_BUILD=1
    if /I "%%A"=="--daemon-only" set DAEMON_ONLY=1
    if /I "%%A"=="--frontend-only" set FRONTEND_ONLY=1
)

echo [run] ===== NKS WebDev Console =====
echo.

REM ---- 1. Kill existing instances ------------------------------------------
echo [run] Stopping any previous daemon/Electron instances...
taskkill /F /IM NKS.WebDevConsole.Daemon.exe >nul 2>&1
taskkill /F /IM electron.exe >nul 2>&1
REM Clear the stale port file so the new daemon picks a fresh entry.
if exist "%TEMP%\nks-wdc-daemon.port" del /Q "%TEMP%\nks-wdc-daemon.port" >nul 2>&1
timeout /t 1 /nobreak >nul

REM ---- 2. Build daemon if requested ----------------------------------------
if "%SKIP_BUILD%"=="0" (
    if "%FRONTEND_ONLY%"=="0" (
        echo [run] Building daemon ^(Release^)...
        dotnet build src\daemon\NKS.WebDevConsole.Daemon\NKS.WebDevConsole.Daemon.csproj -c Release --nologo >"%TEMP%\wdc-build.log" 2>&1
        if errorlevel 1 (
            echo [run] Daemon build FAILED. Log:
            type "%TEMP%\wdc-build.log"
            exit /b 1
        )
        echo [run] Daemon build OK.
    )
)

REM ---- 3. Start daemon -----------------------------------------------------
if "%FRONTEND_ONLY%"=="0" (
    set DAEMON_EXE=src\daemon\NKS.WebDevConsole.Daemon\bin\Release\net9.0\NKS.WebDevConsole.Daemon.exe
    if not exist "!DAEMON_EXE!" (
        echo [run] Daemon binary not found at !DAEMON_EXE!
        echo [run] Run without --no-build to build it first.
        exit /b 1
    )
    echo [run] Launching daemon in its own window...
    start "NKS WDC - Daemon" cmd /k "echo === NKS WDC Daemon === && %~dp0!DAEMON_EXE!"

    echo [run] Waiting for daemon to open its port file...
    set /a WAITS=0
    :WAIT_PORT
    if exist "%TEMP%\nks-wdc-daemon.port" goto DAEMON_READY
    set /a WAITS+=1
    if !WAITS! GEQ 30 (
        echo [run] Daemon didn't write the port file after 30s. Check its window for errors.
        exit /b 1
    )
    timeout /t 1 /nobreak >nul
    goto WAIT_PORT
    :DAEMON_READY
    echo [run] Daemon is up.
)

if "%DAEMON_ONLY%"=="1" (
    echo [run] --daemon-only requested; skipping frontend.
    endlocal
    exit /b 0
)

REM ---- 4. Start frontend (Electron + Vite) ---------------------------------
if not exist "src\frontend\node_modules\" (
    echo [run] Frontend dependencies missing — running npm install...
    pushd src\frontend
    call npm install
    if errorlevel 1 (
        echo [run] npm install failed.
        popd
        exit /b 1
    )
    popd
)

echo [run] Launching Electron frontend in its own window...
start "NKS WDC - Electron" cmd /k "cd /d %~dp0src\frontend && npm run dev"

echo.
echo [run] All components launched:
echo     Daemon window:    "NKS WDC - Daemon"
echo     Frontend window:  "NKS WDC - Electron"
echo     Port file:        %TEMP%\nks-wdc-daemon.port
echo.
echo [run] Close this window freely; the daemon + Electron keep running.
endlocal
