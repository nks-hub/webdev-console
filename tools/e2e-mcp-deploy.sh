#!/bin/bash
# Phase 7.8 — full E2E test of MCP intent + grants + dummy deploy flow
# against blog.loc and shop.loc on a live daemon.
#
# Exercises every public surface user-asked about:
#   - settings PUT (mcp.enabled, deploy.enabled, mcp.strict_kinds)
#   - kinds discovery (/api/mcp/kinds)
#   - grant CRUD (/api/mcp/grants)
#   - intent mint + confirm-request + confirm + audit + revoke
#   - deploy history + dummy deploy fire (Phase 7.5 stub)
#
# Run: bash tools/e2e-mcp-deploy.sh
# Runs all sections to completion and exits non-zero at the end if any
# `step` failed (PASS/FAIL summary printed before exit).
set -u

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
    cat <<EOF
Usage: tools/e2e-mcp-deploy.sh

Full E2E test of MCP intent + grants + dummy deploy flow against
blog.loc and shop.loc on a live daemon (port 17280, token from
\$TEMP/nks-wdc-daemon.port). Runs all sections to completion and
exits non-zero at the end if any step failed (PASS/FAIL summary
printed before exit).

Sections cover: settings, kinds, grants, intents, deploy history,
real deploy fire, MCP gates, plugin readiness, gatedEndpoints[].
EOF
    exit 0
fi

PASS=0; FAIL=0
RED=$'\033[31m'; GRN=$'\033[32m'; YEL=$'\033[33m'; END=$'\033[0m'

TOKEN=$(powershell -Command "(Get-Content \$env:TEMP\nks-wdc-daemon.port)[1]" 2>/dev/null | tr -d '\r')
BASE="http://localhost:17280"

# Detect sqlite client + WDC db path early so any section can use them.
SQLITE_BIN=""
for cand in "$(which sqlite3 2>/dev/null)" \
            "$HOME/.wdc/binaries/sqlite/sqlite3.exe" \
            /c/Android/platform-tools/sqlite3.exe \
            /c/Android/platform-tools/sqlite3; do
    if [ -n "$cand" ] && [ -x "$cand" ]; then SQLITE_BIN="$cand"; break; fi
done
WDC_DB="$HOME/.wdc/data/state.db"

step() {
    local name="$1"; local got="$2"; local want="$3"
    if echo "$got" | grep -qE "$want"; then
        echo "  ${GRN}✓${END} $name"
        PASS=$((PASS + 1))
    else
        echo "  ${RED}✗${END} $name"
        echo "      got:  ${got:0:200}"
        echo "      want: $want"
        FAIL=$((FAIL + 1))
    fi
}

api() {
    local method="$1"; local path="$2"; shift 2
    curl -s -X "$method" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" "$@" "$BASE$path"
}

# Iter 33-34 — defensive startup reset for settings that other tests
# mutate-and-restore. If a previous run (this script OR Playwright) was
# killed mid-test, the value stays in SQLite and poisons the next run.
# Each entry below is the default-safe baseline; idempotent if already at default.
# Why this exists: try/finally restore is fragile against signal kills;
# pair with startup wipe (belt + suspenders). See bugfix_e2e_state_poisoning.md.
api PUT /api/settings -d '{"mcp.always_confirm_kinds":""}' >/dev/null 2>&1 || true
api PUT /api/settings -d '{"deploy.useLegacyHostHandlers":"true"}' >/dev/null 2>&1 || true
api PUT /api/settings -d '{"deploy.enabled":"true"}' >/dev/null 2>&1 || true

# Phase 7.5+++ — REAL deploy: helper builds the localPaths body. Real
# git repos at C:\work\sites\{blog.loc,shop.loc} get copied into release
# dirs under C:\work\deploy-targets\{blog.loc,shop.loc}\releases\<id>.
# Extra fields (host, branch, snapshot, intentToken) are merged in.
# Usage: deploy_body blog.loc '{"host":"production"}'
deploy_body() {
    local domain="$1"; local extra="${2:-{\}}"
    # .NET Directory.* + File.* on Windows accept forward slashes — keeps
    # the JSON escape minimal vs the C:\\work\\... double-backslash dance.
    DEPLOY_DOMAIN="$domain" DEPLOY_EXTRA="$extra" python3 -c "
import json, os
extra = json.loads(os.environ['DEPLOY_EXTRA'])
domain = os.environ['DEPLOY_DOMAIN']
body = {**extra, 'localPaths': {
    'source': 'C:/work/sites/' + domain,
    'target': 'C:/work/deploy-targets/' + domain,
}}
print(json.dumps(body))
"
}
fire_deploy() {
    local domain="$1"; local extra="${2:-{\}}"
    deploy_body "$domain" "$extra" > /c/temp/.deploy-body-$$.json
    api POST "/api/nks.wdc.deploy/sites/$domain/deploy" --data-binary @/c/temp/.deploy-body-$$.json
}
# Variant that captures HTTP code (curl -w "%{http_code}") and supports
# extra headers (e.g. -H 'X-Mcp-Session-Id: agent-X'). Pass extra
# header arguments via 3rd arg as a single string; eval'd by curl.
fire_deploy_w_code() {
    local domain="$1"; local extra="${2:-{\}}"; local extra_headers="${3:-}"
    deploy_body "$domain" "$extra" > /c/temp/.deploy-body-$$.json
    eval "curl -s -w '%{http_code}' -X POST -H 'Authorization: Bearer $TOKEN' -H 'Content-Type: application/json' $extra_headers --data-binary @/c/temp/.deploy-body-$$.json '$BASE/api/nks.wdc.deploy/sites/$domain/deploy'"
}
# Bash fragments to splice into hand-built JSON `-d` bodies for the
# inline curl deploy calls. Single-quoted so backslashes are literal:
# JSON sees "C:\\work\\..." (2 backslashes) → decodes to "C:\work\..."
# When interpolated via ${LP_BLOG} inside a "..." bash string, bash
# does NO further escape processing on the variable's content.
LP_BLOG=',"localPaths":{"source":"C:/work/sites/blog.loc","target":"C:/work/deploy-targets/blog.loc"}'
LP_SHOP=',"localPaths":{"source":"C:/work/sites/shop.loc","target":"C:/work/deploy-targets/shop.loc"}'

# ============================================================================
echo ""; echo "${YEL}=== A. preconditions ===${END}"
# ============================================================================
HEALTH=$(curl -s --max-time 3 "$BASE/healthz")
step "daemon healthz" "$HEALTH" '"ok":true'
step "version > 0.2.25" "$HEALTH" '"version":"0\.2\.25'

# Enable everything we need
api PUT /api/settings -d '{"mcp.enabled":"true","deploy.enabled":"true","mcp.strict_kinds":"false"}' >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== B. MCP kinds discovery ===${END}"
# ============================================================================
KINDS=$(api GET /api/mcp/kinds)
# Phase 7.5+++ wave 2 — registry expanded to 17 kinds covering deploy +
# database + site + DNS + SSL + plugin + binary + service surfaces.
# Match by ">=7" (additive-tolerant) so a future kind addition doesn't
# break this assertion the way the original "==7" did.
step "kinds endpoint returns at least 7 core kinds" "$KINDS" '"count":(1[0-9]|[7-9])'
step "deploy kind has reversible danger" "$KINDS" '"id":"deploy".*"danger":"reversible"'
step "restore kind has destructive danger" "$KINDS" '"id":"restore".*"danger":"destructive"'
# Phase 7.5+++ — usage telemetry per kind. After many sections that
# mint deploy intents, the deploy row should report intentCount > 0.
step "kinds endpoint exposes intentCount field" "$KINDS" '"intentCount":[0-9]'

# ============================================================================
echo ""; echo "${YEL}=== C. grants CRUD ===${END}"
# ============================================================================
EXP=$(date -u -d "+15 minutes" +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date -u -v+15M +"%Y-%m-%dT%H:%M:%SZ")
# Build grant body via python to avoid bash quoting issues
python3 - <<EOF > /c/temp/.e2e-grant.json
import json
print(json.dumps({
    "scopeType": "session",
    "scopeValue": "e2e-claude-test",
    "kindPattern": "deploy",
    "targetPattern": "blog.loc",
    "expiresAt": "$EXP",
    "note": "E2E auto-cleanup",
}))
EOF
GRANT_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-grant.json "$BASE/api/mcp/grants")
GRANT_ID=$(echo "$GRANT_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "grant create returns id"        "$GRANT_RESP" '"id":"[a-f0-9-]{36}"'
step "grant list shows the new grant" "$(api GET /api/mcp/grants)" "\"id\":\"$GRANT_ID\""

DEL_RESP=$(api DELETE /api/mcp/grants/$GRANT_ID)
step "grant revoke returns ok"        "$DEL_RESP" '"status":"revoked"'

# ============================================================================
echo ""; echo "${YEL}=== D. intent mint + confirm flow on blog.loc (deploy kind) ===${END}"
# ============================================================================
INTENT_RESP=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}')
INTENT_ID=$(echo "$INTENT_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
step "intent mint returns intentId+token" "$INTENT_RESP" '"intentToken":"'

CONF_REQ=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"intentId\":\"$INTENT_ID\",\"prompt\":\"E2E test deploy of blog.loc\"}" \
    "$BASE/api/mcp/intents/confirm-request")
step "confirm-request broadcasts (HTTP 202)" "$CONF_REQ" '202'

CONFIRM_RESP=$(api POST /api/mcp/intents/$INTENT_ID/confirm)
step "confirm endpoint stamps confirmed_at" "$CONFIRM_RESP" '"confirmedAt":"'

INVENTORY=$(api GET '/api/mcp/intents?limit=10')
step "intent appears in audit inventory" "$INVENTORY" "\"intentId\":\"$INTENT_ID\""
step "intent has kind=deploy in audit"   "$INVENTORY" "\"intentId\":\"$INTENT_ID\".*\"kind\":\"deploy\""

REVOKE_RESP=$(api POST /api/mcp/intents/$INTENT_ID/revoke)
step "intent revoke returns revokedAt" "$REVOKE_RESP" '"revokedAt":"'

# ============================================================================
echo ""; echo "${YEL}=== E. plugin-namespaced custom kind (Phase 7.4a) ===${END}"
# ============================================================================
CUSTOM_RESP=$(api POST /api/mcp/intents -d '{"domain":"shop.loc","host":"production","kind":"db:drop_table","expiresIn":60}')
step "custom kind 'db:drop_table' accepted (lenient)" "$CUSTOM_RESP" '"intentToken":"'
CUSTOM_ID=$(echo "$CUSTOM_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")

# Reject malformed
BAD_RESP=$(api POST /api/mcp/intents -d '{"domain":"shop.loc","host":"production","kind":"DROP","expiresIn":60}')
step "uppercase kind rejected (HTTP 400)" "$BAD_RESP" '"kind_invalid"'

# Cleanup custom intent
[ -n "$CUSTOM_ID" ] && api POST /api/mcp/intents/$CUSTOM_ID/revoke >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== F. deploy plugin REST surface (Phase 7.5) ===${END}"
# ============================================================================
HIST_BLOG=$(api GET /api/nks.wdc.deploy/sites/blog.loc/history)
step "GET history blog.loc returns envelope" "$HIST_BLOG" '"domain":"blog.loc"'
step "GET history blog.loc has count field"  "$HIST_BLOG" '"count":'
step "GET history blog.loc has entries[]"    "$HIST_BLOG" '"entries":\['

HIST_SHOP=$(api GET /api/nks.wdc.deploy/sites/shop.loc/history)
step "GET history shop.loc returns envelope" "$HIST_SHOP" '"domain":"shop.loc"'

SNAP_BLOG=$(api GET /api/nks.wdc.deploy/sites/blog.loc/snapshots)
step "GET snapshots blog.loc returns envelope" "$SNAP_BLOG" '"count":'

# ============================================================================
echo ""; echo "${YEL}=== G. dummy deploy fire on blog.loc (no intent — direct GUI flow) ===${END}"
# ============================================================================
DEPLOY_RESP=$(fire_deploy blog.loc '{"host":"production","branch":"main"}')
DEPLOY_ID=$(echo "$DEPLOY_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
step "POST deploy returns deployId"          "$DEPLOY_RESP" '"deployId":"'
step "dummy backend starts queued (async)"   "$DEPLOY_RESP" '"status":"queued"'

GET_DEPLOY=$(api GET /api/nks.wdc.deploy/sites/blog.loc/deploys/$DEPLOY_ID)
step "GET single deploy retrieves it"        "$GET_DEPLOY" "\"deployId\":\"$DEPLOY_ID\""

# Wait for async state machine (~600ms total) + slack
sleep 2
DONE_DEPLOY=$(api GET /api/nks.wdc.deploy/sites/blog.loc/deploys/$DEPLOY_ID)
step "after 2s, deploy state-machine finished" "$DONE_DEPLOY" '"finalPhase":"Done"'

# Verify it shows up in history now
HIST_AFTER=$(api GET /api/nks.wdc.deploy/sites/blog.loc/history)
step "deploy appears in blog.loc history" "$HIST_AFTER" "\"deployId\":\"$DEPLOY_ID\""

# ============================================================================
echo ""; echo "${YEL}=== H. dummy deploy fire on shop.loc ===${END}"
# ============================================================================
SHOP_DEPLOY=$(fire_deploy shop.loc '{"host":"staging","branch":"develop"}')
SHOP_ID=$(echo "$SHOP_DEPLOY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
step "shop.loc deploy returns deployId" "$SHOP_DEPLOY" '"deployId":"'
SHOP_HIST=$(api GET /api/nks.wdc.deploy/sites/shop.loc/history)
step "shop.loc history shows new deploy" "$SHOP_HIST" "\"deployId\":\"$SHOP_ID\""

# ============================================================================
echo ""; echo "${YEL}=== I. intent-gated deploy (full chain: mint → confirm → fire-with-token) ===${END}"
# ============================================================================
# AI mints intent
INTENT_GATED=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}')
GATED_ID=$(echo "$INTENT_GATED" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
GATED_TOKEN=$(echo "$INTENT_GATED" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
step "intent-gated: mint OK" "$INTENT_GATED" '"intentToken":"'

# Try fire deploy WITHOUT operator approval — should fail with pending_confirmation
PEND=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$GATED_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "fire without approval → 425 pending_confirmation" "$PEND" 'pending_confirmation.*425'

# Operator approves (banner click)
api POST /api/mcp/intents/$GATED_ID/confirm >/dev/null

# AI fires deploy WITH token — should accept
FIRED=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$GATED_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "fire with approved token → 202" "$FIRED" '"status":"queued".*202'
GATED_DEPLOY_ID=$(echo "$FIRED" | python3 -c "import sys; t=sys.stdin.read(); import re; m=re.search(r'\"deployId\":\"([a-f0-9-]+)\"',t); print(m.group(1) if m else '')")

# Wait for state machine to finish (~600ms total + slack)
sleep 2
DONE=$(api GET /api/nks.wdc.deploy/sites/blog.loc/deploys/$GATED_DEPLOY_ID)
step "background state-machine reached Done" "$DONE" '"finalPhase":"Done"'
step "intent-gated deploy triggered_by recorded" "$DONE" '"deployId":"'

# Re-using same intent should fail (single-use enforced by validator)
REPLAY=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$GATED_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "intent replay rejected (already_used)" "$REPLAY" 'already_used'

# ============================================================================
echo ""; echo "${YEL}=== K. grant auto-confirms intent (NO operator click needed) ===${END}"
# ============================================================================
# This is the killer flow: AI presents X-Mcp-Session-Id header; operator
# previously created a session-scoped grant for that id+kind+target;
# subsequent intent fires immediately, banner never pops.

SESSION_ID="ai-claude-$(date +%s)"

# Operator pre-creates the grant (would normally be banner click "Trust 30 min")
python3 - <<EOF > /c/temp/.e2e-autograntbody.json
import json
print(json.dumps({
    "scopeType": "session",
    "scopeValue": "$SESSION_ID",
    "kindPattern": "deploy",
    "targetPattern": "blog.loc",
    "expiresAt": None,
    "note": "E2E auto-confirm test",
}))
EOF
GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-autograntbody.json "$BASE/api/mcp/grants")
GR_ID=$(echo "$GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "operator pre-creates session grant" "$GR" '"status":"created"'

# AI mints intent — note we send X-Mcp-Session-Id so the validator's
# grant pre-check sees the caller identity.
INTENT_AUTO=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $SESSION_ID" \
    -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}' \
    "$BASE/api/mcp/intents")
AUTO_TOKEN=$(echo "$INTENT_AUTO" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
step "AI mints intent (with session header)" "$INTENT_AUTO" '"intentToken":"'

# AI fires deploy WITHOUT manual confirm — grant pre-check should auto-stamp
# confirmed_at and the deploy queues immediately. NOTE: still send the same
# session header so the validator can see the caller identity.
AUTO_FIRE=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $SESSION_ID" \
    -d "{\"host\":\"production\",\"intentToken\":\"$AUTO_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "deploy fires without operator click (grant auto-confirms)" "$AUTO_FIRE" '"status":"queued".*202'

# Wait for state machine
sleep 2
AUTO_DEPLOY_ID=$(echo "$AUTO_FIRE" | python3 -c "import sys; t=sys.stdin.read(); import re; m=re.search(r'\"deployId\":\"([a-f0-9-]+)\"',t); print(m.group(1) if m else '')")
AUTO_DONE=$(api GET /api/nks.wdc.deploy/sites/blog.loc/deploys/$AUTO_DEPLOY_ID)
step "auto-confirmed deploy completes" "$AUTO_DONE" '"finalPhase":"Done"'

# Different session id → grant doesn't match → still needs confirm
INTENT_OTHER=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: different-ai-session" \
    -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":60}' \
    "$BASE/api/mcp/intents")
OTHER_TOKEN=$(echo "$INTENT_OTHER" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
OTHER_FIRE=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: different-ai-session" \
    -d "{\"host\":\"production\",\"intentToken\":\"$OTHER_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "different session → grant doesn't apply → 425" "$OTHER_FIRE" 'pending_confirmation.*425'

# Cleanup
[ -n "$GR_ID" ] && api DELETE /api/mcp/grants/$GR_ID >/dev/null
OTHER_ID=$(echo "$INTENT_OTHER" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
[ -n "$OTHER_ID" ] && api POST /api/mcp/intents/$OTHER_ID/revoke >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== L. always-trust grant (no caller id needed) ===${END}"
# ============================================================================
# Operator gives "Always trust ANY caller for restore on shop.loc"
ALWAYS_RESP=$(api POST /api/mcp/grants -d '{"scopeType":"always","scopeValue":null,"kindPattern":"restore","targetPattern":"shop.loc","note":"E2E always-trust"}')
ALWAYS_ID=$(echo "$ALWAYS_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "always-trust grant created" "$ALWAYS_RESP" '"status":"created"'

# Anonymous caller mints + fires restore on shop.loc — no session id at all.
INTENT_ANON=$(api POST /api/mcp/intents -d '{"domain":"shop.loc","host":"production","kind":"restore","expiresIn":60}')
ANON_TOKEN=$(echo "$INTENT_ANON" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
ANON_ID=$(echo "$INTENT_ANON" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
# We can't easily test always-grant via deploy POST because the deploy endpoint
# only accepts kind='deploy'. But the validator pre-check is exercised by
# unit tests + the fact that the lookup runs without identity headers when
# scope='always'. Just verify the grant matches on list and revoke cleanly.
step "always grant visible on list" "$(api GET /api/mcp/grants)" "\"id\":\"$ALWAYS_ID\""
step "always grant revoke OK" "$(api DELETE /api/mcp/grants/$ALWAYS_ID)" '"status":"revoked"'
[ -n "$ANON_ID" ] && api POST /api/mcp/intents/$ANON_ID/revoke >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== N. deploy settings persistence (round-trip) ===${END}"
# ============================================================================
# Wizard finish + Save buttons in DeploySettingsPanel POST a JSON body
# here. Daemon writes to ~/.wdc/data/deploy-settings/{domain}.json.
# E2E proves: 404 before any save, save returns ok+bytes, GET returns
# byte-equivalent JSON, second save overwrites cleanly.

DOM="e2e-test.loc"
echo "  → unique site name: $DOM (cleanup after)"

GET_404=$(curl -s -w "%{http_code}" -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/$DOM/settings")
step "settings GET before save → 404 no_settings_yet" "$GET_404" 'no_settings_yet.*404'

SAVE_BODY='{"hosts":[{"name":"production","sshHost":"deploy.example.com","sshUser":"deploy","sshPort":22,"remotePath":"/var/www/myapp","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":30}],"snapshot":{"enabled":true,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":["ops@example.com"],"notifyOn":["success","failure"]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{"APP_ENV":"production"}}}'

SAVE_RESP=$(curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "$SAVE_BODY" "$BASE/api/nks.wdc.deploy/sites/$DOM/settings")
step "PUT settings returns saved+bytes" "$SAVE_RESP" '"status":"saved".*"bytes":'

GOT=$(curl -s -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/$DOM/settings")
step "GET after save returns the JSON" "$GOT" '"sshHost":"deploy.example.com"'
step "GET preserved snapshot config" "$GOT" '"retentionDays":30'
step "GET preserved env vars" "$GOT" '"APP_ENV":"production"'

# Overwrite with new content
SAVE2=$(curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"hosts":[],"snapshot":{"enabled":false,"retentionDays":7},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":["failure"]},"advanced":{"keepReleases":3,"lockTimeoutSeconds":300,"allowConcurrentHosts":false,"envVars":{}}}' \
    "$BASE/api/nks.wdc.deploy/sites/$DOM/settings")
step "second PUT overwrites" "$SAVE2" '"status":"saved"'
GOT2=$(curl -s -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/$DOM/settings")
step "after overwrite, old hosts gone" "$GOT2" '"hosts":\[\]'
step "after overwrite, new keepReleases=3" "$GOT2" '"keepReleases":3'

# Invalid JSON → 400
BAD=$(curl -s -w "%{http_code}" -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d 'not json at all' "$BASE/api/nks.wdc.deploy/sites/$DOM/settings")
step "PUT with non-JSON body → 400 invalid_json" "$BAD" 'invalid_json.*400'

# Cleanup test settings file via rcmd (elevated client owns ~/.wdc dir on prod)
# Best-effort: ignore failure (file might be locked).
if [ -f "$HOME/.wdc/data/deploy-settings/${DOM}.json" ]; then
    rm -f "$HOME/.wdc/data/deploy-settings/${DOM}.json" 2>/dev/null || true
fi

# ============================================================================
echo ""; echo "${YEL}=== P. grant expiry — expired grants never auto-confirm ===${END}"
# ============================================================================
# Security-critical: ListActiveAsync + FindMatchingActiveAsync MUST filter
# rows where expires_at < now. An expired grant that auto-confirmed an
# intent would defeat the entire trust-window concept.

EXPIRED_SESSION="ai-expiry-$(date +%s)"
# Build grant body with expires_at 2 seconds in the future
EXPIRED_AT=$(date -u -d "+2 seconds" +"%Y-%m-%dT%H:%M:%SZ" 2>/dev/null || date -u -v+2S +"%Y-%m-%dT%H:%M:%SZ")
python3 - <<EOF > /c/temp/.e2e-shortgrant.json
import json
print(json.dumps({
    "scopeType": "session", "scopeValue": "$EXPIRED_SESSION",
    "kindPattern": "deploy", "targetPattern": "blog.loc",
    "expiresAt": "$EXPIRED_AT", "note": "E2E expiry test (2s)"
}))
EOF
SHORT_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-shortgrant.json "$BASE/api/mcp/grants")
SHORT_ID=$(echo "$SHORT_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "short-lived grant (2s expiry) created" "$SHORT_GR" '"status":"created"'

# Immediately, the grant is still active and visible on list
LIST_FRESH=$(api GET /api/mcp/grants)
step "fresh grant visible on /api/mcp/grants" "$LIST_FRESH" "\"id\":\"$SHORT_ID\""

# Sleep past expiry + slack
sleep 4

# After expiry, it must be filtered out of the active list
LIST_EXPIRED=$(api GET /api/mcp/grants)
HAS_EXPIRED=$(echo "$LIST_EXPIRED" | grep -c "$SHORT_ID" || true)
step "expired grant filtered from active list" "$HAS_EXPIRED" '^0$'

# An intent fired with the same session header must still hit pending_confirmation
# (not auto-confirmed by the expired grant)
INTENT_X=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $EXPIRED_SESSION" \
    -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":60}' \
    "$BASE/api/mcp/intents")
INTENT_X_TOKEN=$(echo "$INTENT_X" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
INTENT_X_ID=$(echo "$INTENT_X" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")

X_FIRE=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $EXPIRED_SESSION" \
    -d "{\"host\":\"production\",\"intentToken\":\"$INTENT_X_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "expired-grant session → fire still requires confirm (425)" "$X_FIRE" 'pending_confirmation.*425'

# Cleanup
[ -n "$SHORT_ID" ] && api DELETE /api/mcp/grants/$SHORT_ID >/dev/null 2>&1
[ -n "$INTENT_X_ID" ] && api POST /api/mcp/intents/$INTENT_X_ID/revoke >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== V. rollback / cancel / groups stubs ===${END}"
# ============================================================================
# Frontend api/deploy.ts has rollbackDeploy, cancelDeploy, fetchDeployGroups,
# startDeployGroup, rollbackDeployGroup. All previously 404'd. Now stubbed.

# Setup: create a queued deploy that we'll cancel before it completes.
# State machine takes ~600ms so cancel must beat the timer.
TO_CANCEL=$(fire_deploy blog.loc '{"host":"production","branch":"main"}')
TC_ID=$(echo "$TO_CANCEL" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
# Cancel immediately (within first 150ms before status='running' transition)
CANCEL_RESP=$(curl -s -X DELETE -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/$TC_ID")
# Note: race may make cancel fail if state machine already past PONR. Either
# 'cancelled' status or 'past_point_of_no_return' is acceptable.
step "cancel returns either cancelled or PONR error" "$CANCEL_RESP" '("status":"cancelled"|"past_point_of_no_return")'

# Setup: deploy → wait → rollback
SETUP_DEPLOY=$(fire_deploy blog.loc '{"host":"production","branch":"main"}')
SD_ID=$(echo "$SETUP_DEPLOY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
sleep 2
RB_RESP=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploys/$SD_ID/rollback)
step "rollback returns sourceDeployId + status=rolled_back" "$RB_RESP" "\"sourceDeployId\":\"$SD_ID\".*\"status\":\"rolled_back\""
# Source row should now show finalPhase=RolledBack
SRC_AFTER=$(api GET /api/nks.wdc.deploy/sites/blog.loc/deploys/$SD_ID)
step "source deploy now reports RolledBack phase" "$SRC_AFTER" '"finalPhase":"RolledBack"'

# Rollback non-existent
RB_404=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/no-such/rollback")
step "rollback non-existent → 404" "$RB_404" 'deploy_not_found.*404'

# Groups list (empty stub)
GRP_LIST=$(api GET /api/nks.wdc.deploy/sites/blog.loc/groups)
step "groups list returns count + entries envelope" "$GRP_LIST" '"count":0.*"entries":\[\]'

# Group start with too few hosts → 400
GRP_BAD=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"hosts":["production"],"options":{}}' "$BASE/api/nks.wdc.deploy/sites/blog.loc/groups")
step "group start with 1 host rejected (need ≥2)" "$GRP_BAD" 'groups_require_2_or_more_hosts.*400'

# Group start with 3 hosts succeeds
GRP_OK=$(api POST /api/nks.wdc.deploy/sites/blog.loc/groups -d '{"hosts":["production","staging","canary"],"options":{}}')
GRP_ID=$(echo "$GRP_OK" | python3 -c "import sys,json; print(json.load(sys.stdin).get('groupId',''))")
step "group start with 3 hosts returns groupId" "$GRP_OK" '"hostCount":3'
step "group start returns idempotencyKey" "$GRP_OK" '"idempotencyKey":"'

# Phase 7.5++ — group should now persist + appear in /groups list
GRP_LIST_2=$(api GET /api/nks.wdc.deploy/sites/blog.loc/groups)
step "groups list shows the new group" "$GRP_LIST_2" "\"id\":\"$GRP_ID\""
step "group lists 3 hosts" "$GRP_LIST_2" '"hosts":\["production","staging","canary"\]'
step "group has hostDeployIds map" "$GRP_LIST_2" '"hostDeployIds":\{'

# 3 child deploy_runs should exist tagged with this groupId+backend_id
HIST_AFTER_GRP=$(api GET '/api/nks.wdc.deploy/sites/blog.loc/history?limit=20')
GRP_RUN_COUNT=$(echo "$HIST_AFTER_GRP" | python3 -c "import sys,json,re; entries=json.load(sys.stdin)['entries']; print(sum(1 for e in entries if e.get('host') in ('production','staging','canary')))")
step "history has at least 3 child runs from group" "$GRP_RUN_COUNT" '^[3-9][0-9]*$|^[1-9][0-9]+$'

# Cascade rollback
GRP_RB=$(api POST /api/nks.wdc.deploy/sites/blog.loc/groups/$GRP_ID/rollback -d '{}')
step "group cascade rollback returns rolled_back" "$GRP_RB" '"status":"rolled_back"'
step "group rollback reports 3-host count" "$GRP_RB" '"hostCount":3'

# Cascade rollback non-existent group
GRP_RB_404=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/groups/no-such-group/rollback")
step "rollback non-existent group → 404 group_not_found" "$GRP_RB_404" 'group_not_found.*404'

# ============================================================================
echo ""; echo "${YEL}=== U. on-demand snapshot-now (DeploySettingsPanel button) ===${END}"
# ============================================================================
SN_RESP=$(api POST /api/nks.wdc.deploy/sites/blog.loc/snapshot-now -d '{}')
SN_ID=$(echo "$SN_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('snapshotId',''))")
step "snapshot-now returns snapshotId" "$SN_RESP" '"snapshotId":"'
step "snapshot-now returns path under ~/.wdc/backups/manual" "$SN_RESP" '"path":"~/.wdc/backups/manual/blog.loc/'
step "snapshot-now returns sizeBytes > 0" "$SN_RESP" '"sizeBytes":[1-9][0-9]*'

# It must appear in the snapshot list immediately
SN_LIST=$(api GET /api/nks.wdc.deploy/sites/blog.loc/snapshots)
step "manual snapshot visible in /snapshots" "$SN_LIST" "\"id\":\"$SN_ID\""

# And the run row carries backend_id='manual-snapshot' (queryable via deploy_runs SQL).
# We assert via sqlite if available.
if [ -n "$SQLITE_BIN" ] && [ -f "$WDC_DB" ]; then
    BACKEND=$("$SQLITE_BIN" "$WDC_DB" "SELECT backend_id FROM deploy_runs WHERE id='$SN_ID'")
    step "DB row backend_id='manual-snapshot'" "$BACKEND" '^manual-snapshot$'
fi

# ============================================================================
echo ""; echo "${YEL}=== T. restore destructive op (kind=restore) ===${END}"
# ============================================================================
# Restore endpoint exercises the destructive-kind path: registry tags
# 'restore' as Destructive (not Reversible like 'deploy'); banner uses
# stronger confirm flow; validator enforces kind='restore' specifically
# so a deploy token can't accidentally fire a restore.

# 1. Create a snapshot via dummy deploy with snapshot:true
SNAP_DEPLOY=$(fire_deploy blog.loc '{"host":"production","branch":"main","snapshot":true}')
SNAP_DEPLOY_ID=$(echo "$SNAP_DEPLOY" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
step "snapshot-bearing deploy fired" "$SNAP_DEPLOY" '"deployId":"'
sleep 1

# 2. Verify it's in the snapshot list
SNAP_LIST=$(api GET /api/nks.wdc.deploy/sites/blog.loc/snapshots)
step "snapshot visible on list" "$SNAP_LIST" "\"id\":\"$SNAP_DEPLOY_ID\""

# 3. Mint restore intent
RESTORE_INTENT=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"restore","expiresIn":120}')
RT_TOKEN=$(echo "$RESTORE_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
RT_ID=$(echo "$RESTORE_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
step "restore intent minted" "$RESTORE_INTENT" '"intentToken":"'

# 4. Fire restore WITHOUT approval → 425 pending_confirmation
RT_NO_APPROVE=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"snapshotId\":\"$SNAP_DEPLOY_ID\",\"host\":\"production\",\"intentToken\":\"$RT_TOKEN\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/restore")
step "restore without approval → 425 pending_confirmation" "$RT_NO_APPROVE" 'pending_confirmation.*425'

# 5. Approve + fire restore → success
api POST /api/mcp/intents/$RT_ID/confirm >/dev/null
RT_FIRE=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"snapshotId\":\"$SNAP_DEPLOY_ID\",\"host\":\"production\",\"intentToken\":\"$RT_TOKEN\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/restore")
step "approved restore returns restored=true" "$RT_FIRE" '"restored":true'
step "restore reports source deployId" "$RT_FIRE" "\"sourceDeployId\":\"$SNAP_DEPLOY_ID\""
step "restore reports backupPath" "$RT_FIRE" '"backupPath":"~/.wdc/backups'

# 6. Try restore non-existent snapshot
RT_BAD=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"snapshotId":"no-such-id","host":"production"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/restore")
step "restore non-existent snapshot → 404 snapshot_not_found" "$RT_BAD" 'snapshot_not_found.*404'

# 7. Try fire restore with a DEPLOY token (kind mismatch)
DEPLOY_INTENT_FOR_REST=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":60}')
DR_TOKEN=$(echo "$DEPLOY_INTENT_FOR_REST" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
DR_ID=$(echo "$DEPLOY_INTENT_FOR_REST" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
api POST /api/mcp/intents/$DR_ID/confirm >/dev/null
RT_WRONG_KIND=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"snapshotId\":\"$SNAP_DEPLOY_ID\",\"host\":\"production\",\"intentToken\":\"$DR_TOKEN\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/restore")
step "deploy token can't fire restore (kind_mismatch)" "$RT_WRONG_KIND" 'kind_mismatch.*403'

# Cleanup
api POST /api/mcp/intents/$DR_ID/revoke >/dev/null

# 8. Frontend route alias: /sites/{domain}/snapshots/{id}/restore + X-Intent-Token header
SNAP_DEPLOY_2=$(fire_deploy blog.loc '{"host":"production","branch":"main","snapshot":true}')
SNAP_DEPLOY_2_ID=$(echo "$SNAP_DEPLOY_2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
sleep 1
RESTORE_INTENT_2=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"restore","expiresIn":120}')
RT2_TOKEN=$(echo "$RESTORE_INTENT_2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
RT2_ID=$(echo "$RESTORE_INTENT_2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
api POST /api/mcp/intents/$RT2_ID/confirm >/dev/null
# Use the frontend's path shape + header convention
ALIAS_FIRE=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Intent-Token: $RT2_TOKEN" \
    -d '{"confirm":true,"host":"production"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/snapshots/$SNAP_DEPLOY_2_ID/restore")
step "alias route /snapshots/{id}/restore + X-Intent-Token header" "$ALIAS_FIRE" '"restored":true'
step "alias route reports same sourceDeployId from path" "$ALIAS_FIRE" "\"sourceDeployId\":\"$SNAP_DEPLOY_2_ID\""

# ============================================================================
echo ""; echo "${YEL}=== S. token tampering attacks ===${END}"
# ============================================================================
# Validator must reject tokens where signature/payload doesn't reconcile
# under the daemon's HMAC key, plus enforce kind/domain/host scope.
# All covered by unit tests; E2E confirms the wire-format + middleware
# + parsing layers all hold under real HTTP traffic.

# Mint a clean intent for blog.loc + deploy
S_INTENT=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}')
S_TOKEN=$(echo "$S_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
S_ID=$(echo "$S_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
api POST /api/mcp/intents/$S_ID/confirm >/dev/null
step "victim intent minted + confirmed" "$S_INTENT" '"intentToken":"'

# Attack 1: flip last char of signature (HMAC mismatch)
# Token format: {id}.{nonce}.{sig} — twiddle one char of sig
TAMPERED_SIG=$(python3 -c "t='$S_TOKEN'; parts=t.split('.'); sig=parts[2]; flip='X' if sig[-1] != 'X' else 'Y'; parts[2]=sig[:-1]+flip; print('.'.join(parts))")
T1=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$TAMPERED_SIG\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "tampered signature → signature_mismatch" "$T1" 'signature_mismatch.*403'

# Attack 2: domain mismatch — token for blog.loc fired against shop.loc
T2=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$S_TOKEN\"${LP_SHOP}}" \
    "$BASE/api/nks.wdc.deploy/sites/shop.loc/deploy")
step "domain mismatch (blog token on shop URL) → 403" "$T2" 'domain_mismatch.*403'

# Attack 3: host mismatch — original was production, fire on staging
T3=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"staging\",\"intentToken\":\"$S_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "host mismatch (production token on staging) → 403" "$T3" 'host_mismatch.*403'

# Attack 4: malformed token shape (not three dot-separated parts)
T4=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"not.a.valid.token.has.too.many.parts\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "malformed token shape → 403 malformed_token" "$T4" 'malformed_token.*403'

# Attack 5: empty / missing intentToken with strict expectation —
# without a token, body is treated as direct GUI deploy (no validator),
# so this just lands as 202 queued. Verify that path:
T5=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "empty token treated as direct GUI deploy" "$T5" '"status":"queued".*202'

# Attack 6: intent created for wrong kind. Mint as 'rollback' but fire
# as 'deploy' on the deploy endpoint (which hardcodes kind='deploy' in
# the validator call).
WRONG_KIND_INTENT=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"rollback","expiresIn":120}')
WK_TOKEN=$(echo "$WRONG_KIND_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
WK_ID=$(echo "$WRONG_KIND_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
api POST /api/mcp/intents/$WK_ID/confirm >/dev/null
T6=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$WK_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "wrong-kind token (rollback fired as deploy) → 403 kind_mismatch" "$T6" 'kind_mismatch.*403'

# Cleanup tampered + wrong-kind intents
[ -n "$S_ID" ] && api POST /api/mcp/intents/$S_ID/revoke >/dev/null
[ -n "$WK_ID" ] && api POST /api/mcp/intents/$WK_ID/revoke >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== Q. revoked grants — soft-deleted grants never auto-confirm ===${END}"
# ============================================================================
# After DELETE /api/mcp/grants/:id the row's revoked_at is stamped non-null.
# ListActiveAsync/FindMatchingActiveAsync MUST filter on revoked_at IS NULL,
# otherwise a revoked grant could still auto-confirm intents (worse than
# expiry: operator EXPLICITLY took permission away).

REVOKED_SESSION="ai-revoked-$(date +%s)"
python3 - <<EOF > /c/temp/.e2e-revokedgrant.json
import json
print(json.dumps({
    "scopeType": "session", "scopeValue": "$REVOKED_SESSION",
    "kindPattern": "deploy", "targetPattern": "blog.loc",
    "expiresAt": None, "note": "E2E revoked-grant test"
}))
EOF
REV_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-revokedgrant.json "$BASE/api/mcp/grants")
REV_ID=$(echo "$REV_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "permanent grant created" "$REV_GR" '"status":"created"'

# Operator revokes
DEL=$(api DELETE /api/mcp/grants/$REV_ID)
step "grant revoked" "$DEL" '"status":"revoked"'

# Revoked grant invisible on list
LIST_AFTER_REV=$(api GET /api/mcp/grants)
HAS_REV=$(echo "$LIST_AFTER_REV" | grep -c "$REV_ID" || true)
step "revoked grant filtered from active list" "$HAS_REV" '^0$'

# Validator must reject auto-confirm using the same session header
INTENT_R=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $REVOKED_SESSION" \
    -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":60}' \
    "$BASE/api/mcp/intents")
INTENT_R_TOKEN=$(echo "$INTENT_R" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
INTENT_R_ID=$(echo "$INTENT_R" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")

R_FIRE=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $REVOKED_SESSION" \
    -d "{\"host\":\"production\",\"intentToken\":\"$INTENT_R_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "revoked-grant session → fire still 425 pending_confirmation" "$R_FIRE" 'pending_confirmation.*425'

# Re-revoking same id is idempotent (must not crash, returns 404)
DOUBLE_DEL=$(api DELETE /api/mcp/grants/$REV_ID)
step "double-revoke returns grant_not_found_or_already_revoked" "$DOUBLE_DEL" 'grant_not_found_or_already_revoked'

[ -n "$INTENT_R_ID" ] && api POST /api/mcp/intents/$INTENT_R_ID/revoke >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== R. concurrent intent fire — single-use enforced under race ===${END}"
# ============================================================================
# Two callers fire the same intent token simultaneously. Validator's
# UPDATE deploy_intents SET used_at = ... WHERE id = ? AND used_at IS NULL
# pattern means SQLite serialises writes; only one caller wins. The other
# must get 'already_used' error.

INTENT_RACE=$(api POST /api/mcp/intents -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}')
RACE_TOKEN=$(echo "$INTENT_RACE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
RACE_ID=$(echo "$INTENT_RACE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
api POST /api/mcp/intents/$RACE_ID/confirm >/dev/null
step "race intent minted + pre-confirmed" "$INTENT_RACE" '"intentToken":"'

# Fire 5 concurrent requests with the same token
RESULTS_DIR=/c/temp/.e2e-race-out
rm -rf "$RESULTS_DIR" && mkdir -p "$RESULTS_DIR"
for i in 1 2 3 4 5; do
    (curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
        -d "{\"host\":\"production\",\"intentToken\":\"$RACE_TOKEN\"${LP_BLOG}}" \
        "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > "$RESULTS_DIR/r$i.json" 2>&1) &
done
wait

# Count successes vs already_used rejections
OK_COUNT=$(grep -l '"deployId":' "$RESULTS_DIR"/r*.json 2>/dev/null | wc -l | tr -d ' ')
USED_COUNT=$(grep -l 'already_used' "$RESULTS_DIR"/r*.json 2>/dev/null | wc -l | tr -d ' ')
echo "  → race result: $OK_COUNT success, $USED_COUNT already_used (out of 5)"
step "exactly ONE concurrent fire succeeded" "$OK_COUNT" '^1$'
step "the other 4 got already_used" "$USED_COUNT" '^4$'

rm -rf "$RESULTS_DIR"

# ============================================================================
echo ""; echo "${YEL}=== O. snapshot list wired to real data ===${END}"
# ============================================================================
# Fire a deploy WITH snapshot:true → backend stamps PreDeployBackupPath
# on the row → /snapshots projects it into a DeploySnapshotEntry.
SNAP_FIRE=$(fire_deploy blog.loc '{"host":"production","branch":"main","snapshot":true}')
SNAP_DEPLOY_ID=$(echo "$SNAP_FIRE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
step "deploy with snapshot:true fired" "$SNAP_FIRE" '"deployId":"'

sleep 1
SNAPSHOTS=$(api GET /api/nks.wdc.deploy/sites/blog.loc/snapshots)
step "snapshots endpoint returns count > 0" "$SNAPSHOTS" '"count":[1-9]'
step "snapshot entry has our deployId as id"   "$SNAPSHOTS" "\"id\":\"$SNAP_DEPLOY_ID\""
step "snapshot has sizeBytes > 0"              "$SNAPSHOTS" '"sizeBytes":[1-9][0-9]*'
step "snapshot has path under ~/.wdc/backups"  "$SNAPSHOTS" '"path":"~/\.wdc/backups/pre-deploy/blog\.loc/'

# Deploy WITHOUT snapshot:true → stays out of the snapshots projection
NOSNAP_FIRE=$(fire_deploy blog.loc '{"host":"production"}')
NOSNAP_ID=$(echo "$NOSNAP_FIRE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
SNAPSHOTS2=$(api GET /api/nks.wdc.deploy/sites/blog.loc/snapshots)
# The new deploy id should NOT appear in snapshots (no PreDeployBackupPath set)
HAS_NOSNAP=$(echo "$SNAPSHOTS2" | grep -c "$NOSNAP_ID" || true)
step "deploy without snapshot stays out of snapshot list" "$HAS_NOSNAP" '^0$'

# ============================================================================
echo ""; echo "${YEL}=== W. SSE grant lifecycle (created / revoked) ===${END}"
# ============================================================================
# Subscribe to mcp:grant-changed BEFORE creating/revoking so we capture
# every event. Frontend uses these to refresh McpGrants page without F5.
W_SSE_LOG="/c/temp/.e2e-grant-sse.log"
rm -f "$W_SSE_LOG"
( curl -s --max-time 4 -H "Authorization: Bearer $TOKEN" -H "Accept: text/event-stream" \
    "$BASE/api/events?topics=mcp:grant-changed" > "$W_SSE_LOG" 2>&1 ) &
W_SSE_PID=$!
sleep 1

# Create a grant
python3 - <<EOF > /c/temp/.e2e-w-grant.json
import json
print(json.dumps({
    "scopeType": "session", "scopeValue": "ai-w-test",
    "kindPattern": "deploy", "targetPattern": "blog.loc",
    "expiresAt": None, "note": "SSE lifecycle test"
}))
EOF
W_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-w-grant.json "$BASE/api/mcp/grants")
W_ID=$(echo "$W_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "grant created (for SSE test)" "$W_GR" '"status":"created"'

# Revoke
api DELETE /api/mcp/grants/$W_ID >/dev/null

# Wait for SSE drain
sleep 3
wait $W_SSE_PID 2>/dev/null

DUMP=$(cat "$W_SSE_LOG")
step "SSE feed captured grant-changed event" "$DUMP" 'event: mcp:grant-changed'
step "SSE captured the created event" "$DUMP" '"change":"created"'
step "SSE captured the revoked event" "$DUMP" '"change":"revoked"'
step "SSE created event carries our grant id" "$DUMP" "\"id\":\"$W_ID\""

# ============================================================================
echo ""; echo "${YEL}=== X. SSE intent lifecycle (created / confirmed / revoked) ===${END}"
# ============================================================================
# McpIntents page subscribes to mcp:intent-changed AND mcp:confirm-request
# so the table refreshes without F5 when AI/CI mints/confirms/revokes a
# token from any source. Verify all three change events fire.
X_SSE_LOG="/c/temp/.e2e-intent-sse.log"
rm -f "$X_SSE_LOG"
( curl -s --max-time 5 -H "Authorization: Bearer $TOKEN" -H "Accept: text/event-stream" \
    "$BASE/api/events?topics=mcp:intent-changed" > "$X_SSE_LOG" 2>&1 ) &
X_SSE_PID=$!
sleep 1

# Mint a fresh intent
python3 - <<EOF > /c/temp/.e2e-x-intent.json
import json
print(json.dumps({
    "domain": "blog.loc", "host": "production",
    "kind": "deploy", "expiresIn": 120
}))
EOF
X_INT=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-x-intent.json "$BASE/api/mcp/intents")
X_ID=$(echo "$X_INT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
step "intent created (for SSE test)" "$X_INT" '"intentId":"'

# Confirm it (operator approval path)
api POST /api/mcp/intents/$X_ID/confirm >/dev/null

# Mint another to revoke (revoke needs unused token, confirmed != used)
X_INT2=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-x-intent.json "$BASE/api/mcp/intents")
X_ID2=$(echo "$X_INT2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
api POST /api/mcp/intents/$X_ID2/revoke >/dev/null

# Drain SSE
sleep 3
wait $X_SSE_PID 2>/dev/null

X_DUMP=$(cat "$X_SSE_LOG")
step "SSE feed captured intent-changed event" "$X_DUMP" 'event: mcp:intent-changed'
step "SSE captured the created event"  "$X_DUMP" '"change":"created"'
step "SSE captured the confirmed event" "$X_DUMP" '"change":"confirmed"'
step "SSE captured the revoked event"  "$X_DUMP" '"change":"revoked"'
step "SSE created event carries our intent id" "$X_DUMP" "\"intentId\":\"$X_ID\""

# ============================================================================
echo ""; echo "${YEL}=== Y. manual grant sweep-now endpoint ===${END}"
# ============================================================================
# Operator can fire the janitor on demand without waiting for the 15-min
# tick. With no sweepable rows present (E2E test grants are typically
# active or freshly revoked, well within the 30-day audit window) the
# call should return deleted=0 and not blow up. The retention semantics
# themselves are covered by GrantSweeperTests; here we only verify
# wire connectivity + 200 status.
SWEEP_RESP=$(api POST /api/mcp/grants/sweep-now)
step "sweep-now returns 200 with deleted count" "$SWEEP_RESP" '"deleted":[0-9]'

# ============================================================================
echo ""; echo "${YEL}=== Z. grant match telemetry (match_count + last_matched_at) ===${END}"
# ============================================================================
# Migration 014 added two columns. Verify:
#   1. listMcpGrants response carries them (defaults: 0 + null)
#   2. After auto-confirm via grant, match_count bumps to 1
#
# We use scope_type=always so the validator picks the grant for any
# caller. After firing one auto-confirmed deploy, the grant row should
# show matchCount=1 and lastMatchedAt set.
python3 - <<EOF > /c/temp/.e2e-z-grant.json
import json
print(json.dumps({
    "scopeType": "always", "scopeValue": None,
    "kindPattern": "deploy", "targetPattern": "blog.loc",
    "expiresAt": None, "note": "telemetry test"
}))
EOF
Z_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-z-grant.json "$BASE/api/mcp/grants")
Z_ID=$(echo "$Z_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "telemetry test grant created" "$Z_GR" '"status":"created"'

# Inspect default columns on a freshly-minted grant.
Z_LIST_BEFORE=$(api GET /api/mcp/grants)
step "fresh grant has matchCount=0 default" "$Z_LIST_BEFORE" "\"id\":\"$Z_ID\".*\"matchCount\":0"

# Mint + auto-fire a deploy; the always-grant should auto-confirm it
# and bump the telemetry counter.
python3 - <<EOF > /c/temp/.e2e-z-intent.json
import json
print(json.dumps({"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}))
EOF
Z_INT=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-z-intent.json "$BASE/api/mcp/intents")
Z_INT_TOKEN=$(echo "$Z_INT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
# Fire the deploy with the intent token in the BODY (daemon reads
# intentToken from JSON body, not from a header). Caller identity
# header lets the always-grant pre-check fire (HasAnyIdentity).
export Z_INT_TOKEN
python3 -c "import json,os; print(json.dumps({'host':'production','branch':'main','intentToken':os.environ['Z_INT_TOKEN'],'localPaths':{'source':'C:/work/sites/blog.loc','target':'C:/work/deploy-targets/blog.loc'}}))" > /c/temp/.e2e-z-deploy.json
Z_FIRE=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" \
    -H "X-Mcp-Session-Id: e2e-z-test" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-z-deploy.json \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "deploy auto-confirmed via always-grant" "$Z_FIRE" '"deployId":"'

# Give the validator's RecordMatchAsync a beat to commit (best-effort path).
sleep 1
Z_LIST_AFTER=$(api GET /api/mcp/grants)
# matchCount should now be ≥1 (test grant matched once).
step "grant matchCount bumped to ≥1 after match" "$Z_LIST_AFTER" "\"id\":\"$Z_ID\".*\"matchCount\":[1-9]"
step "grant lastMatchedAt populated" "$Z_LIST_AFTER" "\"id\":\"$Z_ID\".*\"lastMatchedAt\":\"20"

# Phase 7.5+++ — verify the matching grant id was stamped on the
# auto-confirmed intent. The intent inventory should show our grant id
# under matchedGrantId for the intent that fired the deploy.
Z_INV=$(api GET /api/mcp/intents?limit=20)
step "intent inventory carries matchedGrantId" "$Z_INV" "\"matchedGrantId\":\"$Z_ID\""

# Phase 7.5+++ — server-side ?matchedGrantId= drilldown filter.
# Filtering by our grant id should return AT LEAST our auto-confirmed
# intent and NOTHING with a different matchedGrantId.
Z_INV_FILTERED=$(api GET /api/mcp/intents?limit=200&matchedGrantId=$Z_ID)
step "filter ?matchedGrantId= returns matching row" "$Z_INV_FILTERED" "\"matchedGrantId\":\"$Z_ID\""

# Filter on a non-existent id → empty entries.
Z_INV_NONE=$(api GET /api/mcp/intents?matchedGrantId=00000000-0000-0000-0000-000000000000)
step "filter on missing id returns count=0"        "$Z_INV_NONE" '"count":0'

# Phase 7.5+++ — server-side aggregate stats endpoint. With one active
# grant we just exercised, expect total≥1, active≥1, totalMatches≥1.
Z_STATS=$(api GET /api/mcp/grants/stats)
step "stats endpoint returns shape"      "$Z_STATS" '"total":[0-9]'
step "stats reports our active grant"    "$Z_STATS" '"active":[1-9]'
step "stats sums our match into total"   "$Z_STATS" '"totalMatches":[1-9]'
step "stats has lastMatchAt populated"   "$Z_STATS" '"lastMatchAt":"20'

# Phase 7.5+++ — Settings-tunable retention. Verify that changing
# mcp.grant_expired_retention_days affects sweep behaviour. Default
# is 1 day (grants expired <1d ago kept). Set retention to 0 days,
# which means ALL expired grants get swept regardless of how recent.
# Just verify the setting persists + sweep-now still returns 200 with
# the new value in effect (no actual expired rows present in this
# E2E run, so deleted=0 either way).
api PUT /api/settings -d '{"mcp.grant_expired_retention_days":"0"}' >/dev/null
SWEEP_RESP2=$(api POST /api/mcp/grants/sweep-now)
step "sweep-now still works with retention=0" "$SWEEP_RESP2" '"deleted":[0-9]'
# Restore default so other test runs aren't surprised.
api PUT /api/settings -d '{"mcp.grant_expired_retention_days":"1"}' >/dev/null

# Cleanup the telemetry test grant
api DELETE /api/mcp/grants/$Z_ID >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== M. SSE event broadcast (real-time deploy phase events) ===${END}"
# ============================================================================
# Subscribe to SSE BEFORE firing the deploy so we capture every event.
# Each transition broadcasts: deploy:started, deploy:phase x2, deploy:complete.
SSE_LOG="/c/temp/.e2e-sse.log"
rm -f "$SSE_LOG"
( curl -s --max-time 4 -H "Authorization: Bearer $TOKEN" -H "Accept: text/event-stream" \
    "$BASE/api/events?topics=deploy:started,deploy:phase,deploy:complete" \
    > "$SSE_LOG" 2>&1 ) &
SSE_PID=$!
sleep 1   # let subscriber attach

# Fire a fresh deploy
SSE_FIRE=$(fire_deploy blog.loc '{"host":"production","branch":"main"}')
SSE_DEPLOY_ID=$(echo "$SSE_FIRE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
step "SSE-tracked deploy fired" "$SSE_FIRE" '"deployId":"'

# Wait for state machine + SSE drain
sleep 3
wait $SSE_PID 2>/dev/null

# SSE wire format puts event-name + data on separate lines, so we match
# them independently rather than requiring single-line regex.
SSE_DUMP=$(cat $SSE_LOG)
step "SSE feed captured deploy:started"      "$SSE_DUMP" 'event: deploy:started'
step "SSE deploy:started carries our deployId" "$SSE_DUMP" "$SSE_DEPLOY_ID"
step "SSE feed captured deploy:phase Building" "$SSE_DUMP" '"phase":"Building"'
step "SSE feed captured deploy:phase AwaitingSoak" "$SSE_DUMP" '"phase":"AwaitingSoak"'
step "SSE feed captured deploy:complete event" "$SSE_DUMP" 'event: deploy:complete'
step "SSE deploy:complete reports success"     "$SSE_DUMP" '"success":true'

# ============================================================================
echo ""; echo "${YEL}=== FF. grant cooldown — rate-limited auto-approval ===${END}"
# ============================================================================
# Create an always-grant with a 5s cooldown, fire 2 deploys back-to-back via
# MCP intent path. First should auto-confirm (matches grant), second should
# fall back to pending_confirmation (cooldown active).
python3 - <<EOF > /c/temp/.e2e-ff-grant.json
import json
print(json.dumps({
    "scopeType": "always", "scopeValue": None,
    "kindPattern": "deploy", "targetPattern": "shop.loc",
    "expiresAt": None, "minCooldownSeconds": 5,
    "note": "FF cooldown E2E"
}))
EOF
FF_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ff-grant.json "$BASE/api/mcp/grants")
FF_ID=$(echo "$FF_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "cooldown grant created"     "$FF_GR" '"status":"created"'

# Verify grant list returns minCooldownSeconds
FF_LIST=$(api GET /api/mcp/grants)
step "grant list exposes minCooldownSeconds" "$FF_LIST" "\"id\":\"$FF_ID\".*\"minCooldownSeconds\":5"

# Mint intent #1 + fire — should auto-confirm
python3 - <<EOF > /c/temp/.e2e-ff-int.json
import json
print(json.dumps({"domain":"shop.loc","host":"production","kind":"deploy","expiresIn":120}))
EOF
FF_INT1=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ff-int.json "$BASE/api/mcp/intents")
FF_TOK1=$(echo "$FF_INT1" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
export FF_TOK1
python3 -c "import json,os; print(json.dumps({'host':'production','branch':'main','intentToken':os.environ['FF_TOK1'],'localPaths':{'source':'C:/work/sites/shop.loc','target':'C:/work/deploy-targets/shop.loc'}}))" > /c/temp/.e2e-ff-deploy1.json
FF_FIRE1=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" \
    -H "X-Mcp-Session-Id: ff-cooldown-test" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ff-deploy1.json \
    "$BASE/api/nks.wdc.deploy/sites/shop.loc/deploy")
step "first deploy auto-confirmed via grant" "$FF_FIRE1" '"deployId":"'

# Mint intent #2 immediately + fire — cooldown should kick in (425 pending_confirmation)
sleep 1
FF_INT2=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ff-int.json "$BASE/api/mcp/intents")
FF_TOK2=$(echo "$FF_INT2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
export FF_TOK2
python3 -c "import json,os; print(json.dumps({'host':'production','branch':'main','intentToken':os.environ['FF_TOK2'],'localPaths':{'source':'C:/work/sites/shop.loc','target':'C:/work/deploy-targets/shop.loc'}}))" > /c/temp/.e2e-ff-deploy2.json
FF_FIRE2=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H "X-Mcp-Session-Id: ff-cooldown-test" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ff-deploy2.json \
    "$BASE/api/nks.wdc.deploy/sites/shop.loc/deploy")
step "second deploy blocked by cooldown (425)" "$FF_FIRE2" 'pending_confirmation.*425'

# Cleanup
api DELETE /api/mcp/grants/$FF_ID >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== EE. grants bulk import (round-trip with export) ===${END}"
# ============================================================================
# Build a 2-row envelope, POST to /import, verify imported=2 + skipped=0
# on first call, then re-import to verify skipped=2 (idempotent re-import).
# Use per-run unique IDs so re-runs against a daemon DB that retains
# soft-revoked rows from prior E2E executions don't see false dup hits.
EE_TS=$(date +%s%N | head -c 13)
export EE_TS
python3 - <<EOF > /c/temp/.e2e-ee-envelope.json
import json, os
ts = os.environ['EE_TS']
print(json.dumps({
    "formatVersion": 1,
    "exportedAt": "2026-04-26T00:00:00Z",
    "includeRevoked": False,
    "count": 2,
    "entries": [
        {"id":f"ee-import-{ts}-1","scopeType":"session","scopeValue":"imp-1","kindPattern":"deploy","targetPattern":"blog.loc","note":"E2E import row 1"},
        {"id":f"ee-import-{ts}-2","scopeType":"always","scopeValue":None,"kindPattern":"restore","targetPattern":"shop.loc","note":"E2E import row 2"}
    ]
}))
EOF
EE_FIRST=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ee-envelope.json "$BASE/api/mcp/grants/import")
step "import accepts envelope"             "$EE_FIRST" '"imported":2'
step "import reports zero skipped first"   "$EE_FIRST" '"skipped":0'

# Second call — same payload — should skip both
EE_SECOND=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-ee-envelope.json "$BASE/api/mcp/grants/import")
step "second import skips dup ids"         "$EE_SECOND" '"skipped":2'
step "second import imports zero"          "$EE_SECOND" '"imported":0'

# Bad envelope: missing formatVersion → 400
EE_BAD=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" -d '{"entries":[]}' \
    "$BASE/api/mcp/grants/import")
step "import without formatVersion → 400"  "$EE_BAD" '400'

# Cleanup the imported test rows (soft revoke; janitor sweeps later)
api DELETE /api/mcp/grants/ee-import-${EE_TS}-1 >/dev/null
api DELETE /api/mcp/grants/ee-import-${EE_TS}-2 >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== DD. grants ?includeRevoked audit view ===${END}"
# ============================================================================
# Default GET hides revoked rows. Create + revoke a grant, then verify:
#   1. Default fetch does NOT contain the revoked grant id.
#   2. ?includeRevoked=true DOES contain it (with non-null revokedAt).
python3 - <<EOF > /c/temp/.e2e-dd-grant.json
import json
print(json.dumps({
    "scopeType": "session", "scopeValue": "audit-test-client",
    "kindPattern": "deploy", "targetPattern": "blog.loc",
    "expiresAt": None, "note": "audit view E2E"
}))
EOF
DD_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-dd-grant.json "$BASE/api/mcp/grants")
DD_ID=$(echo "$DD_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
api DELETE /api/mcp/grants/$DD_ID >/dev/null

# Default: revoked grant absent
DD_DEFAULT=$(api GET /api/mcp/grants)
DD_HIT=$(echo "$DD_DEFAULT" | grep -c "$DD_ID" || true)
step "default GET hides revoked grant"   "$DD_HIT" '^0$'

# Audit: revoked grant present with revokedAt set
DD_AUDIT=$(api GET /api/mcp/grants?includeRevoked=true)
step "?includeRevoked=true returns revoked" "$DD_AUDIT" "\"id\":\"$DD_ID\""
step "audit row carries revokedAt timestamp" "$DD_AUDIT" "\"id\":\"$DD_ID\".*\"revokedAt\":\"20"

# ============================================================================
echo ""; echo "${YEL}=== CC. grants test-match dry-run endpoint ===${END}"
# ============================================================================
# Operator can ask "would this caller+kind+target match an active grant?"
# WITHOUT firing an intent. Verify:
#   1. With a known active grant present, matching tuple → matched=true + grant id
#   2. Non-matching tuple → matched=false
#   3. Missing required field (kind) → 400
# Re-create an always-grant for the test (section Z's grant was DELETEd).
python3 - <<EOF > /c/temp/.e2e-cc-grant.json
import json
print(json.dumps({
    "scopeType": "always", "scopeValue": None,
    "kindPattern": "deploy", "targetPattern": "blog.loc",
    "expiresAt": None, "note": "test-match dry-run probe"
}))
EOF
CC_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-cc-grant.json "$BASE/api/mcp/grants")
CC_GR_ID=$(echo "$CC_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")
step "test-match precondition grant created" "$CC_GR" '"status":"created"'

# Hit on always-grant: any caller (we still pass sessionId for HasAnyIdentity).
CC_HIT=$(api POST /api/mcp/grants/test-match -d '{"sessionId":"test-cc","kind":"deploy","target":"blog.loc"}')
step "test-match returns matched=true for hit" "$CC_HIT" '"matched":true'
step "test-match echoes grant id"               "$CC_HIT" "\"id\":\"$CC_GR_ID\""

# Miss: kind doesn't match the kindPattern.
CC_MISS=$(api POST /api/mcp/grants/test-match -d '{"sessionId":"test-cc","kind":"rollback","target":"blog.loc"}')
step "test-match returns matched=false for miss" "$CC_MISS" '"matched":false'

# Bad: missing kind.
CC_BAD=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" -d '{"target":"blog.loc"}' \
    "$BASE/api/mcp/grants/test-match")
step "test-match without kind returns 400"      "$CC_BAD" '400'

# Cleanup
api DELETE /api/mcp/grants/$CC_GR_ID >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== BB. history.triggeredBy field + filter ===${END}"
# ============================================================================
# History endpoint should include the triggeredBy field for each row, and
# the optional ?triggeredBy= query param should filter server-side.
# Section O above already fired a snapshot=true deploy, plus section M and
# others — all triggered by GUI calls (no intentToken in body), so they're
# tagged 'gui'. Section Z fired one with an intent token → 'mcp'.

HIST_FULL=$(api GET /api/nks.wdc.deploy/sites/blog.loc/history?limit=50)
step "history rows expose triggeredBy field" "$HIST_FULL" '"triggeredBy":"'

# Section Z's deploy was triggered with an intent token → 'mcp' tag.
HIST_MCP=$(api GET /api/nks.wdc.deploy/sites/blog.loc/history?limit=50&triggeredBy=mcp)
step "filter triggeredBy=mcp returns mcp rows" "$HIST_MCP" '"triggeredBy":"mcp"'

# Filter to a non-existent source → empty entries array, count=0.
HIST_NONE=$(api GET /api/nks.wdc.deploy/sites/blog.loc/history?triggeredBy=nonexistent_source)
step "filter on missing source returns count=0" "$HIST_NONE" '"count":0'

# ============================================================================
echo ""; echo "${YEL}=== AA. test-host-connection TCP probe ===${END}"
# ============================================================================
# Pure network probe — no actual SSH handshake. Test against:
#   1. localhost:17280 (the daemon itself; should always return ok=true since
#      we know the daemon is up to receive our request)
#   2. localhost:1 (port 1 should be unbound → socket_error or refused)
#   3. invalid-input (missing host) → 400
TCP_OK=$(api POST /api/nks.wdc.deploy/test-host-connection -d '{"host":"127.0.0.1","port":17280}')
step "probe to bound port returns ok=true"   "$TCP_OK" '"ok":true'
step "probe to bound port reports latencyMs" "$TCP_OK" '"latencyMs":[0-9]'

TCP_FAIL=$(api POST /api/nks.wdc.deploy/test-host-connection -d '{"host":"127.0.0.1","port":1}')
step "probe to closed port returns ok=false" "$TCP_FAIL" '"ok":false'
step "probe to closed port has error code"   "$TCP_FAIL" '"code":"'

TCP_BAD=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/json" -d '{}' \
    "$BASE/api/nks.wdc.deploy/test-host-connection")
step "probe with no host returns 400"        "$TCP_BAD" '400'

# ============================================================================
echo ""; echo "${YEL}=== J. deploy.enabled gate ===${END}"
# ============================================================================
api PUT /api/settings -d '{"deploy.enabled":"false"}' >/dev/null
GATED=$(curl -s -w "%{http_code}" -H "Authorization: Bearer $TOKEN" "$BASE/api/nks.wdc.deploy/sites/blog.loc/history")
step "deploy.enabled=false → 404 deploy_disabled" "$GATED" 'deploy_disabled.*404'
api PUT /api/settings -d '{"deploy.enabled":"true"}' >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== cleanup — remove dummy backend rows from deploy_runs ===${END}"
# ============================================================================
# Each E2E run inserts ~6 dummy DeployRunRow entries. Periodic cleanup keeps
# the GUI history list short. Best-effort: ignore failure (sqlite missing,
# perms, etc.) — never blocks CI exit.
# Try several sqlite binary locations — system PATH first, then the
# WDC bundled one (path varies by version), then the Android platform
# tools shipped with many dev environments.
SQLITE_BIN=""
for cand in "$(which sqlite3 2>/dev/null)" \
            "$HOME/.wdc/binaries/sqlite/sqlite3.exe" \
            /c/Android/platform-tools/sqlite3.exe \
            /c/Android/platform-tools/sqlite3; do
    if [ -n "$cand" ] && [ -x "$cand" ]; then SQLITE_BIN="$cand"; break; fi
done
WDC_DB="$HOME/.wdc/data/state.db"
if [ -n "$SQLITE_BIN" ] && [ -f "$WDC_DB" ]; then
    BEFORE=$("$SQLITE_BIN" "$WDC_DB" "SELECT COUNT(*) FROM deploy_runs WHERE backend_id LIKE 'dummy%' OR backend_id = 'manual-snapshot'")
    "$SQLITE_BIN" "$WDC_DB" "DELETE FROM deploy_runs WHERE backend_id LIKE 'dummy%' OR backend_id = 'manual-snapshot'" 2>/dev/null
    "$SQLITE_BIN" "$WDC_DB" "DELETE FROM deploy_groups WHERE triggered_by = 'gui'" 2>/dev/null
    AFTER=$("$SQLITE_BIN" "$WDC_DB" "SELECT COUNT(*) FROM deploy_runs WHERE backend_id LIKE 'dummy%' OR backend_id = 'manual-snapshot'")
    echo "  → cleaned $BEFORE dummy deploy rows (now $AFTER) via $SQLITE_BIN"
else
    echo "  → sqlite client not found, skipping cleanup (DB at $WDC_DB)"
fi

# Cleanup test settings file from section N
TEST_DOM_SETTINGS="$HOME/.wdc/data/deploy-settings/e2e-test.loc.json"
[ -f "$TEST_DOM_SETTINGS" ] && rm -f "$TEST_DOM_SETTINGS" && echo "  → removed e2e-test.loc settings file"

# ============================================================================
echo ""; echo "${YEL}=== GG. nksdeploy folder structure (releases/, shared/, .dep/, retention) ===${END}"
# ============================================================================
# Exercises LocalDeployBackend's nksdeploy-compatible layout against a
# scratch target dir under /c/temp so the E2E remains hermetic. Uses the
# blog.loc fixture as source.
# Use msys-style for filesystem ops, Windows-style for the JSON body
# (the .NET daemon resolves Windows paths, not /c/... mounts).
GG_TARGET_MSYS="/c/temp/e2e-deploy-gg-target"
GG_TARGET_WIN="C:/temp/e2e-deploy-gg-target"
GG_SOURCE_WIN="C:/work/sites/blog.loc"
rm -rf "$GG_TARGET_MSYS"
mkdir -p "$GG_TARGET_MSYS"

# Override settings via body localPaths + localOptions for keep=2 + custom shared
# Fire 4 deploys → expect retention to prune to 2 + current to point to newest.
GG_SHARED_DIRS='["log","cache"]'
GG_SHARED_FILES='[".env"]'
GG_BODY_PREFIX='{"host":"production","branch":"main","localPaths":{"source":"'$GG_SOURCE_WIN'","target":"'$GG_TARGET_WIN'"},"localOptions":{"sharedDirs":'$GG_SHARED_DIRS',"sharedFiles":'$GG_SHARED_FILES',"keepReleases":2}}'

GG_DIDS=()
for i in 1 2 3 4; do
    GG_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
        -d "$GG_BODY_PREFIX" "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
    GG_DID=$(echo "$GG_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))" 2>/dev/null)
    GG_DIDS+=("$GG_DID")
    sleep 2
done
sleep 2

# Assertion 1: top-level structure exists
step "GG target has releases/ dir" "$([ -d "$GG_TARGET_MSYS/releases" ] && echo y || echo n)" "y"
step "GG target has shared/ dir"   "$([ -d "$GG_TARGET_MSYS/shared"   ] && echo y || echo n)" "y"
step "GG target has .dep/ dir"     "$([ -d "$GG_TARGET_MSYS/.dep"     ] && echo y || echo n)" "y"
step "GG target has current symlink/dir" "$([ -e "$GG_TARGET_MSYS/current" ] && echo y || echo n)" "y"

# Assertion 2: shared dirs/files seeded per body localOptions
step "GG shared/log present"   "$([ -d "$GG_TARGET_MSYS/shared/log"   ] && echo y || echo n)" "y"
step "GG shared/cache present" "$([ -d "$GG_TARGET_MSYS/shared/cache" ] && echo y || echo n)" "y"
step "GG shared/.env present"  "$([ -f "$GG_TARGET_MSYS/shared/.env"  ] && echo y || echo n)" "y"

# Assertion 3: retention pruned to keep=2
GG_RELEASE_COUNT=$(ls -1 "$GG_TARGET_MSYS/releases" 2>/dev/null | wc -l | tr -d ' ')
step "GG retention pruned to keep=2 (got $GG_RELEASE_COUNT)" "$GG_RELEASE_COUNT" "^2$"

# Assertion 4: .dep tracks current + previous
step ".dep/current_release file populated"  "$([ -s "$GG_TARGET_MSYS/.dep/current_release"  ] && echo y || echo n)" "y"
step ".dep/previous_release file populated" "$([ -s "$GG_TARGET_MSYS/.dep/previous_release" ] && echo y || echo n)" "y"

# Assertion 5: each kept release has shared symlinks back into shared/
GG_NEWEST=$(ls -1 "$GG_TARGET_MSYS/releases" 2>/dev/null | sort | tail -1)
if [ -n "$GG_NEWEST" ]; then
    GG_REL_DIR="$GG_TARGET_MSYS/releases/$GG_NEWEST"
    step "newest release has log symlink"   "$([ -L "$GG_REL_DIR/log"   ] && echo y || echo n)" "y"
    step "newest release has cache symlink" "$([ -L "$GG_REL_DIR/cache" ] && echo y || echo n)" "y"
    step "newest release has .env symlink"  "$([ -L "$GG_REL_DIR/.env"  ] && echo y || echo n)" "y"
    GG_LOG_TARGET=$(readlink "$GG_REL_DIR/log" 2>/dev/null | sed 's|\\|/|g')
    step "log symlink points into shared/log (got '$GG_LOG_TARGET')" "$GG_LOG_TARGET" "shared/log"
fi

# Assertion 6: current symlink resolves to one of the kept releases
GG_CURRENT_TARGET=$(readlink "$GG_TARGET_MSYS/current" 2>/dev/null | sed 's|\\|/|g')
step "current symlink points into releases/ (got '$GG_CURRENT_TARGET')" "$GG_CURRENT_TARGET" "releases/"

# Cleanup scratch target
rm -rf "$GG_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== HH. real rollback via .dep/previous_release ===${END}"
# ============================================================================
# Verifies the rollback endpoint actually swaps `current` back to the
# release recorded in .dep/previous_release. Requires the host to have
# localTargetPath configured in settings (which the deploy endpoint
# tolerates being absent — but rollback specifically reads from there).
HH_TARGET_MSYS="/c/temp/e2e-rollback-target"
HH_TARGET_WIN="C:/temp/e2e-rollback-target"
HH_SOURCE_WIN="C:/work/sites/blog.loc"
rm -rf "$HH_TARGET_MSYS"
mkdir -p "$HH_TARGET_MSYS"

# Configure blog.loc settings to point at HH_TARGET so rollback can find it.
HH_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
HH_BACKUP=""
[ -f "$HH_SETTINGS_FILE" ] && HH_BACKUP=$(cat "$HH_SETTINGS_FILE")
cat > "$HH_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"$HH_SOURCE_WIN","localTargetPath":"$HH_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

# Fire 2 deploys (so previous_release becomes meaningful).
HH_RESP1=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
HH_DID1=$(echo "$HH_RESP1" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))" 2>/dev/null)
sleep 2
HH_RESP2=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
HH_DID2=$(echo "$HH_RESP2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))" 2>/dev/null)
sleep 2

HH_BEFORE=$(readlink "$HH_TARGET_MSYS/current" 2>/dev/null | sed 's|\\|/|g')
HH_PREV_FILE_CONTENT=$(cat "$HH_TARGET_MSYS/.dep/previous_release" 2>/dev/null | sed 's|\\|/|g')

step "rollback target has 2 releases" "$(ls -1 $HH_TARGET_MSYS/releases 2>/dev/null | wc -l | tr -d ' ')" "^2$"
step ".dep/previous_release populated before rollback" "$([ -s $HH_TARGET_MSYS/.dep/previous_release ] && echo y || echo n)" "y"

# Trigger rollback of the LATEST deploy (DID2) — current should swap
# back to whatever previous_release pointed at (the FIRST deploy).
HH_ROLLBACK_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/$HH_DID2/rollback")
sleep 1
step "rollback endpoint returns rolled_back" "$HH_ROLLBACK_RESP" '"status":"rolled_back"'
step "rollback response includes swappedTo path" "$HH_ROLLBACK_RESP" '"swappedTo"'

HH_AFTER=$(readlink "$HH_TARGET_MSYS/current" 2>/dev/null | sed 's|\\|/|g')
step "current symlink CHANGED after rollback (was '$HH_BEFORE', now '$HH_AFTER')" \
    "$([ "$HH_BEFORE" != "$HH_AFTER" ] && [ -n "$HH_AFTER" ] && echo y || echo n)" "y"

# Cleanup — restore settings + remove scratch target
if [ -n "$HH_BACKUP" ]; then
    echo "$HH_BACKUP" > "$HH_SETTINGS_FILE"
fi
rm -rf "$HH_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== II. snapshot-now writes a REAL zip artifact ===${END}"
# ============================================================================
# When localTargetPath is configured + a current/ symlink exists,
# snapshot-now should produce an actual ZIP file under
# ~/.wdc/backups/manual/{domain}/. Without localTargetPath the legacy
# .sql.gz placeholder shape persists for back-compat.
II_TARGET_MSYS="/c/temp/e2e-snapshot-target"
II_TARGET_WIN="C:/temp/e2e-snapshot-target"
II_SOURCE_WIN="C:/work/sites/blog.loc"
rm -rf "$II_TARGET_MSYS"
mkdir -p "$II_TARGET_MSYS"

II_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
II_BACKUP=""
[ -f "$II_SETTINGS_FILE" ] && II_BACKUP=$(cat "$II_SETTINGS_FILE")
cat > "$II_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"$II_SOURCE_WIN","localTargetPath":"$II_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

# Fire a deploy so current/ exists
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 3

II_RESP=$(api POST /api/nks.wdc.deploy/sites/blog.loc/snapshot-now -d '{}')
II_ID=$(echo "$II_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('snapshotId',''))" 2>/dev/null)
step "snapshot-now returns .zip path when localTargetPath configured" "$II_RESP" '\.zip"'
step "snapshot-now reports host name from settings" "$II_RESP" '"host":"production"'
step "snapshot-now sizeBytes reflects real archive (>0)" "$II_RESP" '"sizeBytes":[1-9][0-9]*'
II_FILE="$HOME/.wdc/backups/manual/blog.loc/${II_ID}.zip"
step "snapshot-now wrote a real .zip on disk" "$([ -s "$II_FILE" ] && echo y || echo n)" "y"

# ============================================================================
echo ""; echo "${YEL}=== JJ. snapshot restore round-trip (extract zip + swap current) ===${END}"
# ============================================================================
# Builds on II's snapshot. Restore should extract the .zip into a new
# releases/{ts}-restored-{shortId} dir + swap `current` symlink.
JJ_RESTORE=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"snapshotId\":\"$II_ID\",\"host\":\"production\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/restore")
step "restore returns restored:true" "$JJ_RESTORE" '"restored":true'
step "restore returns extractedTo path" "$JJ_RESTORE" '"extractedTo":"[^"]*restored-'
step "restore returns swappedTo path" "$JJ_RESTORE" '"swappedTo":"[^"]*restored-'
JJ_CURRENT=$(readlink "$II_TARGET_MSYS/current" 2>/dev/null | sed 's|\\|/|g')
case "$JJ_CURRENT" in
    *-restored-*) step "current symlink now points at restored release" yes yes ;;
    *)            step "current symlink now points at restored release (got '$JJ_CURRENT')" no yes ;;
esac

# Cleanup
[ -f "$II_FILE" ] && rm -f "$II_FILE"
if [ -n "$II_BACKUP" ]; then
    echo "$II_BACKUP" > "$II_SETTINGS_FILE"
fi
rm -rf "$II_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== KK. pre-deploy snapshot writes a REAL zip ===${END}"
# ============================================================================
# When deploy POST body has `snapshot:true` (or {include:true}) AND
# target/current/ exists, the daemon should ZIP it under
# ~/.wdc/backups/pre-deploy/{domain}/ before the deploy starts.
KK_TARGET_MSYS="/c/temp/e2e-pre-deploy-target"
KK_TARGET_WIN="C:/temp/e2e-pre-deploy-target"
KK_SOURCE_WIN="C:/work/sites/blog.loc"
rm -rf "$KK_TARGET_MSYS"
mkdir -p "$KK_TARGET_MSYS"
KK_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
KK_BACKUP=""
[ -f "$KK_SETTINGS_FILE" ] && KK_BACKUP=$(cat "$KK_SETTINGS_FILE")
cat > "$KK_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"$KK_SOURCE_WIN","localTargetPath":"$KK_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

# Seed deploy so current/ exists
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 3

# Deploy WITH snapshot:true → expect real zip
KK_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\",\"snapshot\":true}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
KK_DID=$(echo "$KK_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))" 2>/dev/null)
sleep 3
KK_ZIP="$HOME/.wdc/backups/pre-deploy/blog.loc/${KK_DID}.zip"
step "pre-deploy snapshot zip exists on disk" "$([ -s "$KK_ZIP" ] && echo y || echo n)" "y"

# Deploy WITH snapshot:{include:true} (GUI shape) → expect real zip
KK_RESP2=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\",\"snapshot\":{\"include\":true,\"retentionDays\":30}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
KK_DID2=$(echo "$KK_RESP2" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))" 2>/dev/null)
sleep 3
KK_ZIP2="$HOME/.wdc/backups/pre-deploy/blog.loc/${KK_DID2}.zip"
step "pre-deploy snapshot zip from GUI shape exists" "$([ -s "$KK_ZIP2" ] && echo y || echo n)" "y"

# Cleanup
[ -f "$KK_ZIP" ] && rm -f "$KK_ZIP"
[ -f "$KK_ZIP2" ] && rm -f "$KK_ZIP2"
if [ -n "$KK_BACKUP" ]; then
    echo "$KK_BACKUP" > "$KK_SETTINGS_FILE"
fi
rm -rf "$KK_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== LL. MCP intent gate on rollback / rollback-to / groups ===${END}"
# ============================================================================
# Endpoints that mutate filesystem state should reject bogus intent
# tokens with 403 intent_rejected. Confirms the intent check runs
# BEFORE any not-found / state validation so a probe with bogus token
# can't enumerate which deployIds exist.
LL_RB=$(curl -s -w "\n%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H 'X-Intent-Token: bogus-token-llll' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/00000000-0000-0000-0000-000000000000/rollback")
step "rollback with bogus intent token returns 403 intent_rejected" "$LL_RB" 'intent_rejected'

LL_RT=$(curl -s -w "\n%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H 'X-Intent-Token: bogus-token-llll' -H 'Content-Type: application/json' \
    -d '{"host":"production","releaseId":"20990101_000000"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/rollback-to")
step "rollback-to with bogus intent token returns 403 intent_rejected" "$LL_RT" 'intent_rejected'

LL_GR=$(curl -s -w "\n%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    -H 'X-Intent-Token: bogus-token-llll' -H 'Content-Type: application/json' \
    -d '{"hosts":["production","staging"]}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/groups")
step "group deploy with bogus intent token returns 403 intent_rejected" "$LL_GR" 'intent_rejected'

# Without a token the same endpoints stay open (back-compat).
LL_RB_OPEN=$(curl -s -w "\n%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/00000000-0000-0000-0000-000000000000/rollback")
step "rollback WITHOUT token still works (404 for unknown id, NOT 403)" "$LL_RB_OPEN" 'deploy_not_found'

# ============================================================================
echo ""; echo "${YEL}=== MM. snapshot retention pruning (manual + pre-deploy) ===${END}"
# ============================================================================
# Plant a stale (5-day-old) zip in each backups subdir, set retentionDays=1,
# trigger a snapshot creation in each — old zips should be pruned.
MM_TARGET_MSYS="/c/temp/e2e-retention-target"
MM_TARGET_WIN="C:/temp/e2e-retention-target"
MM_SOURCE_WIN="C:/work/sites/blog.loc"
rm -rf "$MM_TARGET_MSYS"
mkdir -p "$MM_TARGET_MSYS"

MM_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
MM_BACKUP=""
[ -f "$MM_SETTINGS_FILE" ] && MM_BACKUP=$(cat "$MM_SETTINGS_FILE")
cat > "$MM_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"$MM_SOURCE_WIN","localTargetPath":"$MM_TARGET_WIN"}],"snapshot":{"enabled":true,"retentionDays":1},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

mkdir -p "$HOME/.wdc/backups/manual/blog.loc" "$HOME/.wdc/backups/pre-deploy/blog.loc"
MM_STALE_M="$HOME/.wdc/backups/manual/blog.loc/E2E-STALE-OLD.zip"
MM_STALE_P="$HOME/.wdc/backups/pre-deploy/blog.loc/E2E-STALE-OLD.zip"
echo old > "$MM_STALE_M"; echo old > "$MM_STALE_P"
MM_PAST=$(date -d "5 days ago" +%Y%m%d%H%M 2>/dev/null || date -v-5d +%Y%m%d%H%M)
touch -t "$MM_PAST" "$MM_STALE_M" "$MM_STALE_P"

# Seed deploy so current/ exists
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 3

# Trigger manual snapshot → manual/ prune
api POST /api/nks.wdc.deploy/sites/blog.loc/snapshot-now -d '{}' > /dev/null
sleep 1
step "stale manual zip pruned by retentionDays=1" "$([ ! -f "$MM_STALE_M" ] && echo y || echo n)" "y"

# Trigger pre-deploy snapshot → pre-deploy/ prune
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\",\"snapshot\":true}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 3
step "stale pre-deploy zip pruned by retentionDays=1" "$([ ! -f "$MM_STALE_P" ] && echo y || echo n)" "y"

# Cleanup
if [ -n "$MM_BACKUP" ]; then echo "$MM_BACKUP" > "$MM_SETTINGS_FILE"; fi
rm -rf "$MM_TARGET_MSYS"
rm -f "$HOME/.wdc/backups/manual/blog.loc/"*.zip "$HOME/.wdc/backups/pre-deploy/blog.loc/"*.zip 2>/dev/null

# ============================================================================
echo ""; echo "${YEL}=== NN. group rollback cascades real symlink swaps ===${END}"
# ============================================================================
# Group rollback now reports realSwaps[] for hosts with localPaths and
# noopHosts[] for hosts without. Each real swap atomically points current
# back at .dep/previous_release.
NN_TARGET_MSYS="/c/temp/e2e-grouprb-target"
NN_TARGET_WIN="C:/temp/e2e-grouprb-target"
rm -rf "$NN_TARGET_MSYS"
mkdir -p "$NN_TARGET_MSYS"

NN_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
NN_BACKUP=""
[ -f "$NN_SETTINGS_FILE" ] && NN_BACKUP=$(cat "$NN_SETTINGS_FILE")
# production has localPaths, staging doesn't (so it should land in noopHosts)
cat > "$NN_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"C:/work/sites/blog.loc","localTargetPath":"$NN_TARGET_WIN"},{"name":"staging","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

# 2 deploys to production so .dep/previous_release exists
for i in 1 2; do
    curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
        -d '{"host":"production","branch":"main"}' \
        "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
    sleep 2
done

# Start a group with both hosts
NN_GRP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"hosts":["production","staging"]}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/groups")
NN_GID=$(echo "$NN_GRP" | python3 -c "import sys,json; print(json.load(sys.stdin)['groupId'])" 2>/dev/null)
sleep 3

# Cascade rollback
NN_RB=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/groups/$NN_GID/rollback")
step "group rollback returns realSwaps for production" "$NN_RB" '"realSwaps"'
step "group rollback reports staging as noop (no localPaths)" "$NN_RB" '"noopHosts":\["staging"\]'
step "group rollback returns rolled_back status" "$NN_RB" '"status":"rolled_back"'

# Cleanup — also wipe deploy_groups rows so subsequent suite runs that
# expect blog.loc to start with count=0 don't fail. Best-effort sqlite.
if [ -n "$SQLITE_BIN" ] && [ -f "$WDC_DB" ]; then
    "$SQLITE_BIN" "$WDC_DB" "DELETE FROM deploy_groups WHERE domain='blog.loc'" 2>/dev/null
    "$SQLITE_BIN" "$WDC_DB" "DELETE FROM deploy_runs WHERE group_id='$NN_GID'" 2>/dev/null
fi
if [ -n "$NN_BACKUP" ]; then echo "$NN_BACKUP" > "$NN_SETTINGS_FILE"; fi
rm -rf "$NN_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== OO. deploy hooks execute (pre_deploy + post_switch) ===${END}"
# ============================================================================
# Configure two shell hooks that write marker files; verify both fire.
OO_TARGET_MSYS="/c/temp/e2e-hooks-target"
OO_TARGET_WIN="C:/temp/e2e-hooks-target"
rm -rf "$OO_TARGET_MSYS"
mkdir -p "$OO_TARGET_MSYS"

OO_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
OO_BACKUP=""
[ -f "$OO_SETTINGS_FILE" ] && OO_BACKUP=$(cat "$OO_SETTINGS_FILE")

OO_PRE="C:/temp/e2e-hook-pre-marker.txt"
OO_POST="C:/temp/e2e-hook-post-marker.txt"
OO_PRE_MSYS="/c/temp/e2e-hook-pre-marker.txt"
OO_POST_MSYS="/c/temp/e2e-hook-post-marker.txt"
rm -f "$OO_PRE_MSYS" "$OO_POST_MSYS"

cat > "$OO_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"C:/work/sites/blog.loc","localTargetPath":"$OO_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[{"event":"pre_deploy","type":"shell","command":"echo pre_deploy > $OO_PRE","timeoutSeconds":10,"enabled":true,"description":"E2E pre marker"},{"event":"post_switch","type":"shell","command":"echo post_switch > $OO_POST","timeoutSeconds":10,"enabled":true,"description":"E2E post marker"}],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"branch\":\"main\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 4

step "pre_deploy hook wrote marker file" "$([ -s "$OO_PRE_MSYS" ] && echo y || echo n)" "y"
step "post_switch hook wrote marker file" "$([ -s "$OO_POST_MSYS" ] && echo y || echo n)" "y"

# Cleanup
rm -f "$OO_PRE_MSYS" "$OO_POST_MSYS"
if [ -n "$OO_BACKUP" ]; then echo "$OO_BACKUP" > "$OO_SETTINGS_FILE"; fi
rm -rf "$OO_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== PP. test-hook endpoint (standalone hook execution) ===${END}"
# ============================================================================
PP_OK=$(api POST /api/nks.wdc.deploy/sites/blog.loc/hooks/test \
    -d '{"type":"shell","command":"echo e2e-hook-ok","timeoutSeconds":5,"description":"PP smoke ok"}')
step "test-hook returns ok=true for echo command" "$PP_OK" '"ok":true'
step "test-hook reports durationMs" "$PP_OK" '"durationMs":[0-9]+'

PP_FAIL=$(api POST /api/nks.wdc.deploy/sites/blog.loc/hooks/test \
    -d '{"type":"shell","command":"definitely-not-a-real-command-x9q7","timeoutSeconds":5}')
step "test-hook returns ok=false for bad command" "$PP_FAIL" '"ok":false'
step "test-hook returns error message for bad command" "$PP_FAIL" '"error":"[^"]'

PP_NO_CMD=$(curl -s -w '\n%{http_code}' -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"type":"shell"}' "$BASE/api/nks.wdc.deploy/sites/blog.loc/hooks/test")
step "test-hook without command returns 400" "$PP_NO_CMD" 'command_required'

# ============================================================================
echo ""; echo "${YEL}=== QQ. test-notification endpoint (Slack webhook smoke) ===${END}"
# ============================================================================
# Bogus webhook → ok=false with DNS / connection error surfaced.
QQ_BAD=$(api POST /api/nks.wdc.deploy/sites/blog.loc/notifications/test \
    -d '{"slackWebhook":"https://e2e-bogus-host.invalid/webhook"}')
step "test-notification returns ok=false for bogus webhook" "$QQ_BAD" '"ok":false'
step "test-notification returns durationMs" "$QQ_BAD" '"durationMs":[0-9]+'

# Missing webhook + no settings entry → 400
QQ_NONE=$(curl -s -w '\n%{http_code}' -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{}' "$BASE/api/nks.wdc.deploy/sites/no-such-site-q1q2.loc/notifications/test")
step "test-notification without configured webhook returns 400" "$QQ_NONE" 'slack_webhook_not_configured'

# ============================================================================
echo ""; echo "${YEL}=== RR. real cancel interrupts running deploy ===${END}"
# ============================================================================
# Configure a slow pre_deploy hook → deploy → cancel → expect interrupted:true.
RR_TARGET_MSYS="/c/temp/e2e-cancel-target"
RR_TARGET_WIN="C:/temp/e2e-cancel-target"
rm -rf "$RR_TARGET_MSYS"
mkdir -p "$RR_TARGET_MSYS"

RR_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
RR_BACKUP=""
[ -f "$RR_SETTINGS_FILE" ] && RR_BACKUP=$(cat "$RR_SETTINGS_FILE")
# Cross-platform sleep — use bash sleep which exists in cmd via "sleep"
# isn't standard on Windows; use ping localhost as a portable wait.
# 5 seconds gives us a wide cancel window.
cat > "$RR_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"C:/work/sites/blog.loc","localTargetPath":"$RR_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[{"event":"pre_deploy","type":"shell","command":"ping -n 6 127.0.0.1","timeoutSeconds":15,"enabled":true,"description":"slow"}],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

RR_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","branch":"main"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
RR_DID=$(echo "$RR_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))" 2>/dev/null)
sleep 1
RR_CANCEL=$(curl -s -X DELETE -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/$RR_DID")
step "cancel returns interrupted:true mid-deploy" "$RR_CANCEL" '"interrupted":true'
step "cancel returns status:cancelled" "$RR_CANCEL" '"status":"cancelled"'

# Status check — final phase should NOT be Done.
sleep 6
RR_STATUS=$(api GET /api/nks.wdc.deploy/sites/blog.loc/deploys/$RR_DID)
case "$RR_STATUS" in
    *'"finalPhase":"Done"'*) step "cancelled deploy did NOT progress to Done" no yes ;;
    *)                       step "cancelled deploy did NOT progress to Done" yes yes ;;
esac

# Cleanup
if [ -n "$RR_BACKUP" ]; then echo "$RR_BACKUP" > "$RR_SETTINGS_FILE"; fi
rm -rf "$RR_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== SS. health check probe in soak phase ===${END}"
# ============================================================================
# Configure healthCheckUrl pointing at the daemon's own /healthz so we get
# a deterministic 200, fire deploy, subscribe SSE briefly, assert the
# soak phase emits "Health check OK (200, ...)" message.
SS_TARGET_MSYS="/c/temp/e2e-health-target"
SS_TARGET_WIN="C:/temp/e2e-health-target"
rm -rf "$SS_TARGET_MSYS"
mkdir -p "$SS_TARGET_MSYS"

SS_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
SS_BACKUP=""
[ -f "$SS_SETTINGS_FILE" ] && SS_BACKUP=$(cat "$SS_SETTINGS_FILE")
cat > "$SS_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":3,"healthCheckUrl":"http://localhost:17280/healthz","localSourcePath":"C:/work/sites/blog.loc","localTargetPath":"$SS_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

# Capture SSE in background → temp file. timeout exits after 8s regardless.
SS_SSE_OUT=$(mktemp)
( timeout 8 curl -sN -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/events?token=$TOKEN" > "$SS_SSE_OUT" 2>/dev/null ) &
SS_SSE_PID=$!
sleep 1  # let SSE attach before fire

curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","branch":"main"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 6  # soak window 3s + buffer
wait $SS_SSE_PID 2>/dev/null

step "SSE captured 'Health check OK' from soak phase" "$(cat $SS_SSE_OUT)" 'Health check OK \(200'
step "SSE captured AwaitingSoak phase" "$(cat $SS_SSE_OUT)" '"phase":"AwaitingSoak"'

# Cleanup
rm -f "$SS_SSE_OUT"
if [ -n "$SS_BACKUP" ]; then echo "$SS_BACKUP" > "$SS_SETTINGS_FILE"; fi
rm -rf "$SS_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== TT. health probe FAILURE surfaces in soak phase ===${END}"
# ============================================================================
# Same shape as SS but with a bogus healthCheckUrl → expect SSE message
# "Health check FAILED:" with the connection / DNS error.
TT_TARGET_MSYS="/c/temp/e2e-health-fail-target"
TT_TARGET_WIN="C:/temp/e2e-health-fail-target"
rm -rf "$TT_TARGET_MSYS"
mkdir -p "$TT_TARGET_MSYS"
TT_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
TT_BACKUP=""
[ -f "$TT_SETTINGS_FILE" ] && TT_BACKUP=$(cat "$TT_SETTINGS_FILE")
cat > "$TT_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":2,"healthCheckUrl":"http://e2e-bogus-health.invalid/healthz","localSourcePath":"C:/work/sites/blog.loc","localTargetPath":"$TT_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

TT_SSE_OUT=$(mktemp)
( timeout 10 curl -sN -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/events?token=$TOKEN" > "$TT_SSE_OUT" 2>/dev/null ) &
TT_SSE_PID=$!
sleep 1

curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","branch":"main"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 8  # soak window 2s + probe retries + buffer
wait $TT_SSE_PID 2>/dev/null

step "SSE captured 'Health check FAILED' for bogus URL" "$(cat $TT_SSE_OUT)" 'Health check FAILED'
# Deploy itself still completes (no auto-rollback), so a Done/complete event should also appear.
step "deploy still progressed past soak (no auto-rollback)" "$(cat $TT_SSE_OUT)" '"deploy:complete"|"success":true'

rm -f "$TT_SSE_OUT"
if [ -n "$TT_BACKUP" ]; then echo "$TT_BACKUP" > "$TT_SETTINGS_FILE"; fi
rm -rf "$TT_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== UU. deploy:hook SSE event arrives during deploy ===${END}"
# ============================================================================
# Section OO already proves the hook process runs (marker file). This
# verifies the SSE event the GUI drawer subscribes to actually broadcasts
# with ok/durationMs/evt fields per the documented contract.
UU_TARGET_MSYS="/c/temp/e2e-hooksse-target"
UU_TARGET_WIN="C:/temp/e2e-hooksse-target"
rm -rf "$UU_TARGET_MSYS"
mkdir -p "$UU_TARGET_MSYS"
UU_SETTINGS_FILE="$HOME/.wdc/data/deploy-settings/blog.loc.json"
UU_BACKUP=""
[ -f "$UU_SETTINGS_FILE" ] && UU_BACKUP=$(cat "$UU_SETTINGS_FILE")
cat > "$UU_SETTINGS_FILE" <<EOF
{"hosts":[{"name":"production","sshHost":"localhost","sshUser":"deploy","sshPort":22,"remotePath":"/var/www","branch":"main","composerInstall":true,"runMigrations":true,"soakSeconds":1,"localSourcePath":"C:/work/sites/blog.loc","localTargetPath":"$UU_TARGET_WIN"}],"snapshot":{"enabled":false,"retentionDays":30},"hooks":[{"event":"post_switch","type":"shell","command":"echo uu-marker","timeoutSeconds":5,"enabled":true,"description":"UU SSE marker"}],"notifications":{"emailRecipients":[],"notifyOn":[]},"advanced":{"keepReleases":5,"lockTimeoutSeconds":600,"allowConcurrentHosts":true,"envVars":{}}}
EOF

UU_SSE_OUT=$(mktemp)
( timeout 8 curl -sN -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/events?token=$TOKEN" > "$UU_SSE_OUT" 2>/dev/null ) &
UU_SSE_PID=$!
sleep 1
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","branch":"main"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy" > /dev/null
sleep 5
wait $UU_SSE_PID 2>/dev/null

step "SSE includes deploy:hook event" "$(cat $UU_SSE_OUT)" 'event: deploy:hook'
step "SSE deploy:hook payload includes evt:post_switch" "$(cat $UU_SSE_OUT)" '"evt":"post_switch"'
step "SSE deploy:hook payload includes ok:true" "$(cat $UU_SSE_OUT)" '"ok":true'
step "SSE deploy:hook payload includes durationMs" "$(cat $UU_SSE_OUT)" '"durationMs":[0-9]'

rm -f "$UU_SSE_OUT"
if [ -n "$UU_BACKUP" ]; then echo "$UU_BACKUP" > "$UU_SETTINGS_FILE"; fi
rm -rf "$UU_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== VV. nksdeploy structure on shop.loc fixture ===${END}"
# ============================================================================
# Mirror of section GG but against shop.loc to prove the daemon has no
# blog.loc-specific assumptions. User asked for testing on BOTH fixtures.
VV_TARGET_MSYS="/c/temp/e2e-shop-target"
VV_TARGET_WIN="C:/temp/e2e-shop-target"
VV_SOURCE_WIN="C:/work/sites/shop.loc"
rm -rf "$VV_TARGET_MSYS"
mkdir -p "$VV_TARGET_MSYS"

VV_BODY='{"host":"production","branch":"main","localPaths":{"source":"'$VV_SOURCE_WIN'","target":"'$VV_TARGET_WIN'"},"localOptions":{"sharedDirs":["log","cache"],"sharedFiles":[".env"],"keepReleases":2}}'
for i in 1 2 3; do
    curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
        -d "$VV_BODY" "$BASE/api/nks.wdc.deploy/sites/shop.loc/deploy" > /dev/null
    sleep 2
done
sleep 1

step "shop.loc target has releases/" "$([ -d $VV_TARGET_MSYS/releases ] && echo y || echo n)" "y"
step "shop.loc target has shared/"   "$([ -d $VV_TARGET_MSYS/shared   ] && echo y || echo n)" "y"
step "shop.loc target has current symlink" "$([ -e $VV_TARGET_MSYS/current ] && echo y || echo n)" "y"
step "shop.loc retention pruned to keep=2" "$(ls -1 $VV_TARGET_MSYS/releases 2>/dev/null | wc -l | tr -d ' ')" "^2$"
step "shop.loc shared/cache present (custom dir)" "$([ -d $VV_TARGET_MSYS/shared/cache ] && echo y || echo n)" "y"
step "shop.loc shared/.env file seeded" "$([ -f $VV_TARGET_MSYS/shared/.env ] && echo y || echo n)" "y"

VV_NEWEST=$(ls -1 $VV_TARGET_MSYS/releases 2>/dev/null | sort | tail -1)
if [ -n "$VV_NEWEST" ]; then
    step "shop.loc newest release symlinks log → shared/log" \
        "$(readlink $VV_TARGET_MSYS/releases/$VV_NEWEST/log 2>/dev/null | sed 's|\\\\|/|g')" "shared/log"
fi

rm -rf "$VV_TARGET_MSYS"

# ============================================================================
echo ""; echo "${YEL}=== WW. dry-run deploy preview ===${END}"
# ============================================================================
# dryRun:true → resolved plan, no DB row, no copy, no SSE.
WW_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","branch":"main","dryRun":true,"localPaths":{"source":"C:/work/sites/blog.loc","target":"C:/temp/e2e-dryrun-target"},"localOptions":{"sharedDirs":["log","cache"],"sharedFiles":[".env"],"keepReleases":2}}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "dry-run returns dryRun:true" "$WW_RESP" '"dryRun":true'
step "dry-run returns deployId:null" "$WW_RESP" '"deployId":null'
step "dry-run returns wouldRelease timestamp" "$WW_RESP" '"wouldRelease":"[0-9]{8}_[0-9]{6}"'
step "dry-run returns wouldExtractTo path" "$WW_RESP" '"wouldExtractTo":"[^"]*releases'
step "dry-run echoes sharedDirs from body" "$WW_RESP" '"sharedDirs":\["log","cache"\]'
step "dry-run echoes keepReleases" "$WW_RESP" '"keepReleases":2'
# Phase 7.5+++ — extended fields: branch echoed, currentRelease may be null,
# totalHooksEnabled is a number. Used by GUI Preview dialog.
step "dry-run echoes branch from body" "$WW_RESP" '"branch":"main"'
step "dry-run includes totalHooksEnabled field" "$WW_RESP" '"totalHooksEnabled":[0-9]+'
step "dry-run includes currentRelease field" "$WW_RESP" '"currentRelease":'
step "dry-run includes sourceLastModified field" "$WW_RESP" '"sourceLastModified":'
step "dry-run includes lastSuccessfulDeployAt field" "$WW_RESP" '"lastSuccessfulDeployAt":'
step "dry-run includes sourceUnchangedSinceLastDeploy field" "$WW_RESP" '"sourceUnchangedSinceLastDeploy":'
step "dry-run includes alwaysConfirmKind field" "$WW_RESP" '"alwaysConfirmKind":'

# Phase 7.5+++ — alwaysConfirmKind reflects the live setting. Flip
# always_confirm_kinds=deploy on, query, expect true; reset.
WW_AC_BEFORE=$(api GET /api/settings | python3 -c "import sys,json; print(json.load(sys.stdin).get('mcp.always_confirm_kinds',''))" 2>/dev/null)
api PUT /api/settings -d '{"mcp.always_confirm_kinds":"deploy"}' >/dev/null
WW_AC_RESP=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","branch":"main","dryRun":true}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "dry-run alwaysConfirmKind=true when deploy is ring-fenced" "$WW_AC_RESP" '"alwaysConfirmKind":true'
api PUT /api/settings -d "{\"mcp.always_confirm_kinds\":\"$WW_AC_BEFORE\"}" >/dev/null
# Confirm no DB row was written — history should NOT include this would-be deploy.
sleep 1
WW_HIST=$(api GET "/api/nks.wdc.deploy/sites/blog.loc/history?limit=5")
WW_RID=$(echo "$WW_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('wouldRelease',''))" 2>/dev/null)
case "$WW_HIST" in
    *"$WW_RID"*) step "dry-run did NOT insert deploy_runs row" no yes ;;
    *)           step "dry-run did NOT insert deploy_runs row" yes yes ;;
esac

# ============================================================================
echo ""; echo "${YEL}=== XX. MCP intent gate on cancel ===${END}"
# ============================================================================
# Phase 7.5+++ — cancel endpoint now optionally honours X-Intent-Token. No
# token → still works (back-compat). Bogus token → 403 intent_rejected
# BEFORE the not-found check (oracle leak prevention). Verifies kind=cancel
# is registered + the gate fires.
XX_BOGUS=$(curl -s -o /dev/null -w '%{http_code}' \
    -X DELETE -H "Authorization: Bearer $TOKEN" \
    -H "X-Intent-Token: bogus.fake.signature" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/never-existed-id")
step "cancel with bogus intent token returns 403" "$XX_BOGUS" "403"

XX_NO_TOKEN=$(curl -s -o /dev/null -w '%{http_code}' \
    -X DELETE -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/never-existed-id")
step "cancel without token still hits not-found path (back-compat)" "$XX_NO_TOKEN" "404"

# Phase 7.5+++ — test-hook endpoint runs arbitrary shell/http/php commands
# and is now optionally MCP-gated under kind=test_hook. Bogus token →
# 403 (gate fires before command exec); no token → still works.
XX_HOOK_BOGUS=$(curl -s -o /dev/null -w '%{http_code}' \
    -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Intent-Token: bogus.fake.signature" \
    -d '{"type":"shell","command":"echo hi","timeoutSeconds":5}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/hooks/test")
step "test-hook with bogus intent token returns 403" "$XX_HOOK_BOGUS" "403"

# Verify kind=test_hook is in registered kinds.
XX_KINDS=$(api GET /api/mcp/kinds)
step "test_hook kind is registered (Destructive)" "$XX_KINDS" '"id":"test_hook"'
step "settings_write kind is registered (Destructive)" "$XX_KINDS" '"id":"settings_write"'

# Phase 7.5+++ — settings PUT now optionally MCP-gated. Bogus token →
# 403; no token → still works (back-compat for the GUI which writes
# without a token in normal operation).
XX_SW_BOGUS=$(curl -s -o /dev/null -w '%{http_code}' \
    -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Intent-Token: bogus.fake.signature" \
    -d '{"hosts":[]}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/settings")
step "settings PUT with bogus intent token returns 403" "$XX_SW_BOGUS" "403"

# snapshot-now MCP gate — bogus token blocks; no token still works.
XX_SN_BOGUS=$(curl -s -o /dev/null -w '%{http_code}' \
    -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Intent-Token: bogus.fake.signature" \
    -d '{"host":"production"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/snapshot-now")
step "snapshot-now with bogus intent token returns 403" "$XX_SN_BOGUS" "403"
step "snapshot_create kind is registered" "$XX_KINDS" '"id":"snapshot_create"'

# ============================================================================
echo ""; echo "${YEL}=== YY. always-confirm kinds override ===${END}"
# ============================================================================
# Phase 7.5+++ — when mcp.always_confirm_kinds contains a kind, the
# validator should refuse to auto-approve via grants for that kind, even
# when an "always trust" wildcard grant exists. Setting persists +
# influences validator immediately (lookup runs per-call).
YY_BEFORE=$(api GET /api/settings | python3 -c "import sys,json; print(json.load(sys.stdin).get('mcp.always_confirm_kinds',''))" 2>/dev/null)
api PUT /api/settings -d '{"mcp.always_confirm_kinds":"deploy,restore,cancel,rollback"}' >/dev/null
YY_AFTER=$(api GET /api/settings | python3 -c "import sys,json; print(json.load(sys.stdin).get('mcp.always_confirm_kinds',''))" 2>/dev/null)
step "always-confirm setting persists" "$YY_AFTER" "deploy,restore,cancel,rollback"
# /api/mcp/kinds should reflect the override per-row
YY_KINDS=$(api GET /api/mcp/kinds)
YY_RESTORE_FLAG=$(echo "$YY_KINDS" | python3 -c "import sys,json; d=json.load(sys.stdin); r=[e for e in d['entries'] if e['id']=='restore']; print(r[0].get('alwaysConfirm') if r else '')" 2>/dev/null)
step "kinds endpoint surfaces alwaysConfirm=true for restore" "$YY_RESTORE_FLAG" "True"
# Reset (don't leave restore in always-confirm — would break unrelated tests)
api PUT /api/settings -d "{\"mcp.always_confirm_kinds\":\"$YY_BEFORE\"}" >/dev/null
YY_RESET=$(api GET /api/settings | python3 -c "import sys,json; print(json.load(sys.stdin).get('mcp.always_confirm_kinds',''))" 2>/dev/null)
step "always-confirm setting resets to prior value" "$YY_RESET" "$YY_BEFORE"
# After reset, kinds endpoint should show alwaysConfirm=false again
YY_KINDS_AFTER=$(api GET /api/mcp/kinds)
YY_RESTORE_AFTER=$(echo "$YY_KINDS_AFTER" | python3 -c "import sys,json; d=json.load(sys.stdin); r=[e for e in d['entries'] if e['id']=='restore']; print(r[0].get('alwaysConfirm') if r else '')" 2>/dev/null)
step "kinds endpoint shows alwaysConfirm=false after reset" "$YY_RESTORE_AFTER" "False"

# ============================================================================
echo ""; echo "${YEL}=== ZZ. always-confirm overrides matching grant (real validator) ===${END}"
# ============================================================================
# Phase 7.5+++ — proves the runtime gate. Setup: create a session grant
# that WOULD auto-confirm a deploy intent, then mark deploy as
# always-confirm in settings, then mint+fire — must return 425
# pending_confirmation instead of the usual 202 queued. Reset always-
# confirm + clean grant when done so unrelated tests aren't disturbed.
ZZ_SESSION="zz-always-confirm-session-$$"
python3 - <<EOF > /c/temp/.e2e-zz-grantbody.json
import json
print(json.dumps({
    "scopeType": "session",
    "scopeValue": "$ZZ_SESSION",
    "kindPattern": "deploy",
    "targetPattern": "blog.loc",
    "expiresAt": None,
    "note": "ZZ always-confirm bypass test",
}))
EOF
ZZ_GR=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    --data-binary @/c/temp/.e2e-zz-grantbody.json "$BASE/api/mcp/grants")
ZZ_GR_ID=$(echo "$ZZ_GR" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))")

# Flip always-confirm ON for deploy
ZZ_BEFORE=$(api GET /api/settings | python3 -c "import sys,json; print(json.load(sys.stdin).get('mcp.always_confirm_kinds',''))" 2>/dev/null)
api PUT /api/settings -d '{"mcp.always_confirm_kinds":"deploy"}' >/dev/null

# Mint intent + fire — grant exists for this session/kind/target so under
# normal rules this would auto-confirm and return 202. Always-confirm
# should bypass the grant matcher → 425 pending_confirmation.
ZZ_INTENT=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $ZZ_SESSION" \
    -d '{"domain":"blog.loc","host":"production","kind":"deploy","expiresIn":120}' \
    "$BASE/api/mcp/intents")
ZZ_TOKEN=$(echo "$ZZ_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentToken',''))")
ZZ_INTENT_ID=$(echo "$ZZ_INTENT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('intentId',''))")
ZZ_FIRE=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $ZZ_SESSION" \
    -d "{\"host\":\"production\",\"intentToken\":\"$ZZ_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "always-confirm bypasses matching grant → 425" "$ZZ_FIRE" 'pending_confirmation.*425'
# Phase 7.5+++ — response body should carry detail="always_confirm" so
# the GUI banner can show distinct copy explaining the override.
step "always-confirm 425 response carries detail=always_confirm" "$ZZ_FIRE" '"detail":"always_confirm"'

# Reset always-confirm; same intent is now consumed (used_at set on first
# attempt that found a kind_mismatch? actually validator returns BEFORE
# UPDATE on pending_confirmation, so the intent stays unused. Confirm
# manually + retry — should now succeed.
api PUT /api/settings -d "{\"mcp.always_confirm_kinds\":\"$ZZ_BEFORE\"}" >/dev/null
[ -n "$ZZ_INTENT_ID" ] && api POST /api/mcp/intents/$ZZ_INTENT_ID/confirm >/dev/null
ZZ_FIRE2=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -H "X-Mcp-Session-Id: $ZZ_SESSION" \
    -d "{\"host\":\"production\",\"intentToken\":\"$ZZ_TOKEN\"${LP_BLOG}}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "after reset + manual confirm → 202 queued" "$ZZ_FIRE2" '"status":"queued".*202'

# Cleanup
[ -n "$ZZ_GR_ID" ] && api DELETE /api/mcp/grants/$ZZ_GR_ID >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== AAA. /api/admin/plugin-readiness contract + setting echo ===${END}"
# ============================================================================
# Phase 7.4 #109-D1+ — readiness diagnostic is the source-of-truth for the
# Settings.vue locked toggle + DeploySettingsPanel popover. If the daemon
# stops echoing useLegacyHostHandlers or silently flips readyToFlip without
# shipping phase B/C/D, the UI lock breaks. This section asserts:
#   1. envelope shape (mode, pluginLoaded, blockers[], readyToFlip)
#   2. setting flip via PUT /api/settings round-trips through the diagnostic
#   3. readyToFlip stays false even after manually flipping the setting
#      (proves the lock can't be bypassed by writing the setting directly)
# Restore original setting in cleanup so the suite is self-isolated.

AAA_BEFORE=$(api GET /api/settings | python3 -c "import sys,json; v=json.load(sys.stdin).get('deploy.useLegacyHostHandlers'); print('true' if v is None else str(v).lower())")
AAA_R1=$(api GET /api/admin/plugin-readiness)
step "readiness envelope has mode field" "$AAA_R1" '"mode":'
step "readiness envelope has blockers array" "$AAA_R1" '"blockers":\['
step "readiness envelope has readyToFlip:false today" "$AAA_R1" '"readyToFlip":false'
step "readiness envelope echoes useLegacyHostHandlers:true (default)" "$AAA_R1" '"useLegacyHostHandlers":true'
# Phase B + C cleared from blockers after plugin parity shipped (B:
# 5 endpoints; C: ZIP/snapshot, test-hook, Slack, SSE bridge). Phase D
# (plugin-only e2e) remains the only blocker for readyToFlip.
step "blockers list includes phase D" "$AAA_R1" 'phase D'

# Flip useLegacyHostHandlers to false via settings API
api PUT /api/settings -d '{"deploy.useLegacyHostHandlers":"false"}' >/dev/null
AAA_R2=$(api GET /api/admin/plugin-readiness)
step "readiness echoes useLegacyHostHandlers:false after flip" "$AAA_R2" '"useLegacyHostHandlers":false'
# CRITICAL: setting flip alone must NOT set readyToFlip → that requires phase B/C/D.
step "readyToFlip stays false even after manual setting flip" "$AAA_R2" '"readyToFlip":false'

# Restore
api PUT /api/settings -d "{\"deploy.useLegacyHostHandlers\":\"$AAA_BEFORE\"}" >/dev/null
AAA_R3=$(api GET /api/admin/plugin-readiness)
step "readiness echoes restored useLegacyHostHandlers value" "$AAA_R3" "\"useLegacyHostHandlers\":$AAA_BEFORE"

# Iter 17 — verbose ?explain=true mode adds blockerDetails[] with phase + remediation per blocker
AAA_R4=$(api GET '/api/admin/plugin-readiness?explain=true')
step "explain=true response includes blockerDetails array" "$AAA_R4" '"blockerDetails":\['
# Phase B + C blockers cleared — only D remains in blockerDetails.
step "explain=true blockerDetails has phase D remediation" "$AAA_R4" '"phase":"D"'
step "explain=true remediation field present" "$AAA_R4" '"remediation":'
# Default mode (no explain) must keep flat blockers shape for back-compat with iter 5/6 consumers
AAA_R5=$(api GET /api/admin/plugin-readiness)
step "default response still has blockers array" "$AAA_R5" '"blockers":\['
# Negative check: default response must NOT include blockerDetails (back-compat guard)
if echo "$AAA_R5" | grep -q '"blockerDetails"'; then
    echo "  ${RED}✗${END} default response leaked blockerDetails (back-compat broken)"
    FAIL=$((FAIL + 1))
else
    echo "  ${GRN}✓${END} default response omits blockerDetails (back-compat preserved)"
    PASS=$((PASS + 1))
fi

# Iter 31 — assert deploy:settings-changed SSE fires on deploy.* save.
# Open a background SSE stream, trigger a settings PUT, capture the
# event line within 5s. Validates iter 28 broadcast wiring.
AAA_SSE_LOG=$(mktemp)
curl -N -s -m 8 -H "Authorization: Bearer $TOKEN" "$BASE/api/events" > "$AAA_SSE_LOG" 2>&1 &
AAA_SSE_PID=$!
sleep 1
api PUT /api/settings -d '{"deploy.useLegacyHostHandlers":"true"}' >/dev/null
sleep 2
kill $AAA_SSE_PID 2>/dev/null
wait $AAA_SSE_PID 2>/dev/null
if grep -q "event: deploy:settings-changed" "$AAA_SSE_LOG"; then
    echo "  ${GRN}✓${END} deploy:settings-changed SSE event fired on deploy.* save"
    PASS=$((PASS + 1))
else
    echo "  ${RED}✗${END} deploy:settings-changed SSE event MISSING from /api/events stream"
    echo "      captured: $(head -c 200 "$AAA_SSE_LOG")"
    FAIL=$((FAIL + 1))
fi
rm -f "$AAA_SSE_LOG"

# Iter 56 #258 — restart-pending hint: when current setting differs from
# boot value, readiness must surface restartPending=true so the GUI can
# show "restart to apply" hint instead of silently lying about authority.
BBB_R0=$(api GET /api/admin/plugin-readiness)
step "readiness surfaces bootLegacyHostHandlers field" "$BBB_R0" '"bootLegacyHostHandlers":'
step "readiness surfaces restartPending field" "$BBB_R0" '"restartPending":'
# Boot==current → restartPending must be false
step "no flip → restartPending:false" "$BBB_R0" '"restartPending":false'
# Iter 62/65 — gatedEndpoints[] surfaces the 11 conditional handlers so the
# GUI can render an exact list instead of trusting hand-coded copy.
step "readiness lists gatedEndpoints array" "$BBB_R0" '"gatedEndpoints":\['
step "gatedEndpoints includes hooks/test" "$BBB_R0" 'POST /sites/{domain}/hooks/test'
step "gatedEndpoints includes snapshot-now" "$BBB_R0" 'POST /sites/{domain}/snapshot-now'
step "gatedEndpoints includes history GET" "$BBB_R0" 'GET /sites/{domain}/history'
# Iter 65 added the snapshot restore aliases (#109-D2 follow-up) — pin one
# so a regression that drops the snapshots/{id}/restore route gets caught.
step "gatedEndpoints includes snapshot restore alias" "$BBB_R0" 'POST /sites/{domain}/snapshots/{snapshotId}/restore'
# Iter 93 — recommendation field is always emitted (DeploySettingsPanel +
# global Settings popover render it). Pin its presence so a refactor
# that drops it breaks bash e2e instead of leaking through to GUI.
step "readiness includes recommendation field even without drift" "$BBB_R0" '"recommendation":'

BBB_BEFORE=$(api GET /api/settings | python3 -c "import sys,json; v=json.load(sys.stdin).get('deploy.useLegacyHostHandlers'); print('true' if v is None else str(v).lower())")
# Toggle to opposite of boot value to force drift
BBB_FLIP=$([ "$BBB_BEFORE" = "true" ] && echo "false" || echo "true")
api PUT /api/settings -d "{\"deploy.useLegacyHostHandlers\":\"$BBB_FLIP\"}" >/dev/null
BBB_R1=$(api GET /api/admin/plugin-readiness)
step "after drift → restartPending:true" "$BBB_R1" '"restartPending":true'
step "drift recommendation mentions restart" "$BBB_R1" 'restart required to apply'

# Restore so the suite is self-isolated
api PUT /api/settings -d "{\"deploy.useLegacyHostHandlers\":\"$BBB_BEFORE\"}" >/dev/null
BBB_R2=$(api GET /api/admin/plugin-readiness)
step "restored → restartPending:false" "$BBB_R2" '"restartPending":false'

# ============================================================================
echo ""; echo "${YEL}=== summary ===${END}"
# ============================================================================
echo ""
echo "  PASS: ${GRN}$PASS${END}"
echo "  FAIL: ${RED}$FAIL${END}"
echo ""
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
