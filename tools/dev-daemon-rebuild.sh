#!/bin/bash
# Operator helper: shut down dev daemon → rebuild → respawn manually.
#
# Standalone tool because the daemon's bin/Debug/net9.0/*.dll files are
# locked by the running process. `dotnet build` from the daemon dir
# fails with MSB3027 ("file in use") until the daemon exits. We POST
# /api/admin/restart (which calls Environment.Exit(99)) then poll
# until the port file is stale.
#
# Usage:
#   tools/dev-daemon-rebuild.sh        # restart + build + respawn
#   tools/dev-daemon-rebuild.sh --no-restart   # skip restart, just build (assumes daemon already down)
#   tools/dev-daemon-rebuild.sh --no-respawn   # restart + build, leave daemon down

set -euo pipefail

PORT_FILE="/c/Users/LuRy/AppData/Local/Temp/nks-wdc-daemon.port"
DAEMON_DIR="/c/work/sources/nks-ws/src/daemon/NKS.WebDevConsole.Daemon"
DAEMON_BIN="$DAEMON_DIR/bin/Debug/net9.0/NKS.WebDevConsole.Daemon.dll"

GRN=$'\033[32m'
YEL=$'\033[33m'
RED=$'\033[31m'
END=$'\033[0m'

log() { echo "${YEL}==>${END} $*"; }
ok()  { echo "${GRN}✓${END} $*"; }
err() { echo "${RED}✗${END} $*" >&2; }

DO_RESTART=true
DO_RESPAWN=true
for arg in "$@"; do
    case "$arg" in
        --no-restart) DO_RESTART=false ;;
        --no-respawn) DO_RESPAWN=false ;;
        -h|--help)
            cat <<EOF
Usage: tools/dev-daemon-rebuild.sh [options]

Restart dev daemon → dotnet build → respawn. Standalone tool because
the daemon's bin DLLs are locked while the process runs.

Options:
  --no-restart   skip the restart step (assumes daemon already down)
  --no-respawn   restart + build only, leave daemon down
  -h, --help     show this message
EOF
            exit 0
            ;;
        *) err "unknown arg: $arg"; exit 2 ;;
    esac
done

REMOTECMD="$(dirname "$0")/remote-cmd.sh"

# Helper: kill any lingering dotnet daemon process via elevated remote-cmd
# instead of local taskkill (which triggers UAC every call).
elevated_kill_daemon() {
    if [ ! -x "$REMOTECMD" ]; then return 0; fi
    log "elevated kill via remote-cmd (no UAC popup)"
    "$REMOTECMD" exec "Get-Process dotnet -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*WebDevConsole.Daemon*' -or (\$_.MainModule.FileName -like '*WebDevConsole.Daemon*' 2>\$null) } | Stop-Process -Force -ErrorAction SilentlyContinue; 'ok'" \
        2>/dev/null | head -3 || true
}

# Step 1: shut down daemon via /api/admin/restart
if $DO_RESTART; then
    if [ ! -f "$PORT_FILE" ]; then
        log "no port file at $PORT_FILE — daemon already down, skipping restart"
        # Still elevated-kill any orphan from a crashed previous run that
        # might be holding bin DLL locks.
        elevated_kill_daemon
    else
        TOKEN=$(awk 'NR==2' "$PORT_FILE")
        PORT=$(awk 'NR==1' "$PORT_FILE")
        log "POST /api/admin/restart on port $PORT (Environment.Exit 99)"
        curl -s -o /dev/null -w "  HTTP %{http_code}\n" \
            -X POST -H "Authorization: Bearer $TOKEN" \
            "http://localhost:$PORT/api/admin/restart" || true

        # Poll for shutdown — daemon exits async; bin DLL stops being
        # locked once process gone. ~5s usually enough.
        log "waiting for shutdown (max 30s)"
        for i in $(seq 1 30); do
            if ! curl -s -m 1 "http://localhost:$PORT/api/health" >/dev/null 2>&1; then
                ok "daemon down after ${i}s"
                break
            fi
            sleep 1
            if [ $i -eq 30 ]; then
                err "daemon still responding after 30s — abort"
                exit 1
            fi
        done
        # Extra second for OS to release file lock
        sleep 1
    fi
fi

# Step 2: build (retry once via elevated kill if file lock blocks first attempt)
log "dotnet build $DAEMON_DIR"
cd "$DAEMON_DIR"
if ! dotnet build --nologo --verbosity quiet 2>&1 | tail -5; then
    log "build failed — likely lingering daemon process. Trying elevated kill via remote-cmd…"
    elevated_kill_daemon
    sleep 2
    if ! dotnet build --nologo --verbosity quiet 2>&1 | tail -5; then
        err "build still failing after elevated kill"
        exit 1
    fi
fi
ok "build clean"

# Step 3: respawn
if $DO_RESPAWN; then
    if [ ! -f "$DAEMON_BIN" ]; then
        err "daemon binary missing at $DAEMON_BIN — build must have failed"
        exit 1
    fi
    log "spawning daemon in background: dotnet $DAEMON_BIN"
    cd "$DAEMON_DIR"
    nohup dotnet "$DAEMON_BIN" >/c/Users/LuRy/AppData/Local/Temp/nks-wdc-daemon.log 2>&1 &
    DAEMON_PID=$!
    log "daemon PID $DAEMON_PID — waiting for port file to refresh (max 15s)"
    for i in $(seq 1 15); do
        if [ -f "$PORT_FILE" ]; then
            NEW_PORT=$(awk 'NR==1' "$PORT_FILE")
            if curl -s -m 1 "http://localhost:$NEW_PORT/api/health" >/dev/null 2>&1; then
                ok "daemon healthy on port $NEW_PORT after ${i}s"
                break
            fi
        fi
        sleep 1
        if [ $i -eq 15 ]; then
            err "daemon failed to come up — check /c/Users/LuRy/AppData/Local/Temp/nks-wdc-daemon.log"
            exit 1
        fi
    done
fi

ok "rebuild complete"
