#!/bin/bash
# Local pre-push gate — mirrors what CI runs on GitHub so failures
# get caught BEFORE pushing instead of red checkmarks on the PR.
#
# What CI runs (.github/workflows/{ci.yml,test.yml,api-type-check.yml}):
#   1. dotnet restore WebDevConsole.sln
#   2. dotnet build WebDevConsole.sln -c Release
#   3. dotnet test WebDevConsole.sln -c Release
#   4. (optional) Frontend build + Playwright contract specs
#
# Usage:
#   tools/pre-push-check.sh              # all checks
#   tools/pre-push-check.sh --quick      # skip Release build, use Debug
#   tools/pre-push-check.sh --no-fe      # skip frontend build
#   tools/pre-push-check.sh --skip-tests # build only

set -euo pipefail

QUICK=false
SKIP_FE=false
SKIP_TESTS=false
for arg in "$@"; do
    case "$arg" in
        --quick) QUICK=true ;;
        --no-fe) SKIP_FE=true ;;
        --skip-tests) SKIP_TESTS=true ;;
        -h|--help)
            cat <<EOF
Usage: tools/pre-push-check.sh [options]

Local pre-push gate that runs the same checks CI does, so failures
get caught BEFORE you push.

Options:
  --quick         Use Debug config (faster, skips Release publish step)
  --no-fe         Skip frontend npm build + Playwright
  --skip-tests    Build only — don't run tests
  -h, --help      This message
EOF
            exit 0
            ;;
        *) echo "ERR: unknown arg: $arg" >&2; exit 2 ;;
    esac
done

GRN=$'\033[32m'
RED=$'\033[31m'
YEL=$'\033[33m'
END=$'\033[0m'

step() { echo "${YEL}==> $*${END}"; }
ok()   { echo "${GRN}✓ $*${END}"; }
err()  { echo "${RED}✗ $*${END}" >&2; }

CONFIG="Release"
$QUICK && CONFIG="Debug"

cd "$(dirname "$0")/.."

# Kill any locally running daemon to avoid bin DLL locks.
DAEMON_TOOL="$(dirname "$0")/dev-daemon-rebuild.sh"

step "1/5  dotnet restore"
dotnet restore WebDevConsole.sln --verbosity quiet
ok "restore clean"

step "2/5  dotnet build ($CONFIG)"
# Stop daemon first if running — its bin DLLs are locked otherwise.
# Daemon API admin/restart triggers Environment.Exit(99) without UAC.
PORT_FILE="/c/Users/LuRy/AppData/Local/Temp/nks-wdc-daemon.port"
if [ -f "$PORT_FILE" ]; then
    PORT=$(awk 'NR==1' "$PORT_FILE")
    TOKEN=$(awk 'NR==2' "$PORT_FILE")
    if curl -s -m 1 "http://localhost:$PORT/api/health" >/dev/null 2>&1; then
        echo "  shutting down running daemon via /api/admin/restart…"
        curl -s -o /dev/null -X POST -H "Authorization: Bearer $TOKEN" \
            "http://localhost:$PORT/api/admin/restart" || true
        # Poll for shutdown — DLL stops being locked once process gone.
        for i in $(seq 1 20); do
            curl -s -m 1 "http://localhost:$PORT/api/health" >/dev/null 2>&1 || break
            sleep 1
        done
        sleep 1  # OS grace
    fi
fi
if ! dotnet build WebDevConsole.sln --no-restore -c "$CONFIG" --nologo --verbosity quiet 2>&1 | tail -10; then
    err "build failed"
    exit 1
fi
ok "build clean"

if ! $SKIP_TESTS; then
    step "3/5  dotnet test ($CONFIG)"
    if ! dotnet test WebDevConsole.sln -c "$CONFIG" --no-build --nologo --verbosity quiet 2>&1 | tail -5; then
        err "tests failed"
        exit 1
    fi
    ok "tests pass"
fi

if ! $SKIP_FE; then
    step "4/5  frontend npm build"
    (cd src/frontend && npm run build --silent 2>&1 | tail -3)
    ok "frontend build clean"

    step "5/5  vitest (mcp-server)"
    (cd services/mcp-server && npx vitest run --reporter=basic 2>&1 | tail -5)
    ok "vitest pass"
fi

echo ""
ok "All pre-push checks passed — safe to git push"
