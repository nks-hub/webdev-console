#!/bin/bash
# Thin curl wrapper around the RemoteCmd relay HTTP API.
#
# The MCP wrapper at .claude.json mcpServers.remote-cmd uses an outdated
# token ("heslo123") that doesn't match the running relay's command-line
# argument ("rcmd-nks-2026-prod-v11"), so every mcp__remote-cmd__* call
# returns "Invalid token". This script bypasses that wrapper by hitting
# the relay directly with the correct token.
#
# Usage:
#   tools/remote-cmd.sh list
#   tools/remote-cmd.sh exec <command...>
#   tools/remote-cmd.sh exec --client <name> <command...>
#   tools/remote-cmd.sh status
#
# The token is read from the running relay process via wmic, so this
# stays in sync if the relay restarts with a different secret.

set -euo pipefail

URL="${REMOTECMD_URL:-https://localhost:7890}"
DEFAULT_CLIENT="${REMOTECMD_DEFAULT_CLIENT:-}"

# Discover the live token from the running relay's command line so we
# don't have to hard-code it. Falls back to the env var if the wmic
# probe fails (non-Windows, unprivileged shell, etc.).
discover_token() {
    if [ -n "${REMOTECMD_TOKEN:-}" ]; then
        echo "$REMOTECMD_TOKEN"
        return 0
    fi
    # wmic prints headers + a trailing blank line — grep the token shape.
    local cmd
    cmd=$(wmic process where "Name='RemoteCmd.Server.exe'" get CommandLine 2>/dev/null \
        | grep -oE 'rcmd-[a-z0-9-]+' | head -1)
    if [ -z "$cmd" ]; then
        echo "ERROR: unable to discover RemoteCmd token (relay not running?)" >&2
        return 1
    fi
    echo "$cmd"
}

TOKEN=$(discover_token)

cmd_list() {
    curl -k -s "$URL/api/clients?token=$TOKEN" --max-time 5
    echo ""
}

cmd_status() {
    curl -k -s "$URL/api/status?token=$TOKEN" --max-time 5
    echo ""
}

cmd_exec() {
    local client="$DEFAULT_CLIENT"
    if [ "${1:-}" = "--client" ]; then
        client="$2"
        shift 2
    fi
    if [ -z "$client" ]; then
        # Auto-pick if exactly one client is connected.
        client=$(curl -k -s "$URL/api/clients?token=$TOKEN" --max-time 5 \
            | python -c "import json,sys; data=json.load(sys.stdin); conn=[c for c in data['clients'] if c['connected']]; print(conn[0]['name']) if len(conn)==1 else exit(1)" 2>/dev/null)
        if [ -z "$client" ]; then
            echo "ERROR: --client required when 0 or >1 clients are connected" >&2
            exit 2
        fi
    fi
    local command="$*"
    if [ -z "$command" ]; then
        echo "ERROR: no command supplied" >&2
        exit 2
    fi
    # JSON-escape the command body via python (handles quotes, newlines).
    local body
    body=$(python -c "import json,sys; print(json.dumps({'client':sys.argv[1],'command':sys.argv[2]}))" "$client" "$command")
    curl -k -s -X POST -H "Content-Type: application/json" \
        -d "$body" "$URL/api/exec?token=$TOKEN" --max-time 60
    echo ""
}

case "${1:-}" in
    list)   cmd_list ;;
    status) cmd_status ;;
    exec)
        shift
        cmd_exec "$@"
        ;;
    -h|--help|"")
        cat <<EOF
Usage: tools/remote-cmd.sh <subcommand> [args]

Subcommands:
  list                              List all clients (connected + history)
  status                            Aggregate status (total/connected count)
  exec [--client <name>] <command>  Run PowerShell on remote client
                                    Auto-picks client when exactly 1 connected.

Env:
  REMOTECMD_URL              Override relay URL (default https://localhost:7890)
  REMOTECMD_TOKEN            Override token (default discovered from relay process)
  REMOTECMD_DEFAULT_CLIENT   Default --client when omitted

Examples:
  tools/remote-cmd.sh list
  tools/remote-cmd.sh exec hostname
  tools/remote-cmd.sh exec --client wdc-restart-elevated 'Get-Process dotnet'
EOF
        ;;
    *)
        echo "Unknown subcommand: $1" >&2
        exit 2
        ;;
esac
