#!/bin/bash
# Phase D (#109-D2) — end-to-end validation that the cutover toggle
# actually flips authority for the 11 gated endpoints (see
# /api/admin/plugin-readiness gatedEndpoints[] for the live list).
#
# Strategy:
#   1. Capture current useLegacyHostHandlers value (will restore at end).
#   2. POST hooks/test in legacy=true → expect daemon shape (workingDir present).
#   3. PUT useLegacyHostHandlers=false + restart daemon → expect new boot picks up legacy=false.
#   4. POST hooks/test in legacy=false → expect plugin shape (no workingDir field).
#   5. Curl plugin-readiness — assert mode=plugin, restartPending=false (boot==current).
#   6. Restore original setting + restart daemon back.
#
# Self-isolating: always restores the original setting even on failure.
# Restart-required toggle (matches plugin DLL load semantics — no hot
# reload), so the script restarts the daemon TWICE — once after each
# flip. ~30s total runtime.
#
# Usage:
#   tools/validate-cutover.sh

set -euo pipefail

PORT_FILE="/c/Users/LuRy/AppData/Local/Temp/nks-wdc-daemon.port"
DAEMON_DIR="/c/work/sources/nks-ws/src/daemon/NKS.WebDevConsole.Daemon"
DAEMON_BIN="$DAEMON_DIR/bin/Debug/net9.0/NKS.WebDevConsole.Daemon.dll"
LOG="/c/Users/LuRy/AppData/Local/Temp/wdc-daemon.log"

GRN=$'\033[32m'
YEL=$'\033[33m'
RED=$'\033[31m'
END=$'\033[0m'

PASS=0
FAIL=0

log() { echo "${YEL}==>${END} $*"; }
ok()  { echo "  ${GRN}✓${END} $*"; PASS=$((PASS + 1)); }
err() { echo "  ${RED}✗${END} $*" >&2; FAIL=$((FAIL + 1)); }

read_port_token() {
    PORT=$(awk 'NR==1' "$PORT_FILE")
    TOKEN=$(awk 'NR==2' "$PORT_FILE")
}

restart_daemon() {
    log "POST /api/admin/restart on port $PORT"
    curl -s -o /dev/null -w "  HTTP %{http_code}\n" \
        -X POST -H "Authorization: Bearer $TOKEN" \
        "http://localhost:$PORT/api/admin/restart" || true

    log "waiting for shutdown (max 20s)"
    for i in $(seq 1 20); do
        if ! curl -s -m 1 "http://localhost:$PORT/api/health" >/dev/null 2>&1; then
            ok "daemon down after ${i}s"
            break
        fi
        sleep 1
    done
    sleep 1

    log "spawning fresh daemon"
    nohup dotnet "$DAEMON_BIN" >"$LOG" 2>&1 &
    disown

    log "waiting for new daemon (max 30s)"
    for i in $(seq 1 30); do
        if curl -s -m 1 "http://localhost:$PORT/api/health" >/dev/null 2>&1; then
            ok "daemon up after ${i}s"
            sleep 2
            read_port_token
            return 0
        fi
        sleep 1
    done
    err "daemon never came back up"
    return 1
}

api() {
    curl -s -m 5 -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" "$@"
}

# ────────────────────────────────────────────────────────────────────
# Phase 0: capture baseline
# ────────────────────────────────────────────────────────────────────
read_port_token
log "baseline daemon on port $PORT"

ORIGINAL=$(api "http://localhost:$PORT/api/settings" \
    | python3 -c "import sys,json; v=json.load(sys.stdin).get('deploy.useLegacyHostHandlers'); print('true' if v is None else str(v).lower())")
log "original deploy.useLegacyHostHandlers=$ORIGINAL"

cleanup() {
    set +e
    log "RESTORING original setting deploy.useLegacyHostHandlers=$ORIGINAL"
    read_port_token 2>/dev/null || true
    api -X PUT "http://localhost:$PORT/api/settings" \
        -d "{\"deploy.useLegacyHostHandlers\":\"$ORIGINAL\"}" >/dev/null 2>&1 || true
    restart_daemon || true
    echo
    echo "${YEL}=== summary ===${END}"
    echo "  PASS: ${GRN}$PASS${END}"
    if [ $FAIL -gt 0 ]; then
        echo "  FAIL: ${RED}$FAIL${END}"
        exit 1
    fi
    exit 0
}
trap cleanup EXIT INT TERM

# ────────────────────────────────────────────────────────────────────
# Phase 1: legacy=true baseline — daemon serves
# ────────────────────────────────────────────────────────────────────
log "[Phase 1] forcing legacy=true + restart"
api -X PUT "http://localhost:$PORT/api/settings" \
    -d '{"deploy.useLegacyHostHandlers":"true"}' >/dev/null
restart_daemon

R1=$(api -X POST -d '{"type":"shell","command":"echo legacy-true-OK","timeoutSeconds":3}' \
    "http://localhost:$PORT/api/nks.wdc.deploy/sites/blog.loc/hooks/test")
echo "  hooks/test response: $R1"
if echo "$R1" | grep -q '"workingDir"'; then
    ok "legacy=true: daemon handler authoritative (workingDir field present)"
else
    err "legacy=true: expected workingDir field — daemon handler not authoritative"
fi

R1B=$(api "http://localhost:$PORT/api/admin/plugin-readiness")
echo "  readiness mode: $(echo "$R1B" | python3 -c 'import sys,json; print(json.load(sys.stdin).get("mode"))')"
if echo "$R1B" | grep -q '"mode":"built-in"'; then
    ok "readiness reports mode=built-in"
else
    err "readiness mode mismatch"
fi

# ────────────────────────────────────────────────────────────────────
# Phase 2: legacy=false → plugin authority
# ────────────────────────────────────────────────────────────────────
log "[Phase 2] flipping legacy=false + restart"
api -X PUT "http://localhost:$PORT/api/settings" \
    -d '{"deploy.useLegacyHostHandlers":"false"}' >/dev/null
restart_daemon

R2=$(api -X POST -d '{"type":"shell","command":"echo legacy-false-OK","timeoutSeconds":3}' \
    "http://localhost:$PORT/api/nks.wdc.deploy/sites/blog.loc/hooks/test")
echo "  hooks/test response: $R2"
if echo "$R2" | grep -qv '"workingDir"'; then
    ok "legacy=false: plugin handler authoritative (no workingDir field)"
else
    err "legacy=false: workingDir leaked — plugin handler not winning"
fi

R2B=$(api "http://localhost:$PORT/api/admin/plugin-readiness")
if echo "$R2B" | grep -q '"mode":"plugin"'; then
    ok "readiness reports mode=plugin"
else
    err "readiness mode not plugin"
fi
if echo "$R2B" | grep -q '"restartPending":false'; then
    ok "restartPending=false (boot value matches current)"
else
    err "restartPending mismatch — boot/current drift not cleared"
fi

# Probe the test-host-connection plugin endpoint too — pure utility.
R2C=$(api -X POST -d '{"host":"127.0.0.1","port":80}' \
    "http://localhost:$PORT/api/nks.wdc.deploy/test-host-connection")
if echo "$R2C" | grep -q '"ok":true'; then
    ok "plugin test-host-connection responds"
else
    err "plugin test-host-connection failed: $R2C"
fi

# Cleanup runs via trap — restores ORIGINAL setting + restart.
