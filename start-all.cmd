@echo off
REM NKS WebDev Console — full-stack launcher: catalog API + daemon + Electron.
REM Opens three windows so you can read logs independently.

setlocal
cd /d "%~dp0"

echo [start-all] Launching catalog API (http://127.0.0.1:8765)...
start "NKS WDC - Catalog API" cmd /k "cd /d %~dp0services\catalog-api && run.cmd"

echo [start-all] Waiting for catalog API to come up...
timeout /t 4 /nobreak >nul

echo [start-all] Launching frontend + daemon (Electron)...
start "NKS WDC - Electron" cmd /k "cd /d %~dp0src\frontend && npm run dev"

echo.
echo [start-all] All services launched.
echo    - Catalog API: http://127.0.0.1:8765 (admin/admin)
echo    - Electron:    opens automatically
echo.
endlocal
