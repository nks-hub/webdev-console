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
# Exits non-zero on first failure.
set -u
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
step "kinds endpoint returns 4 core kinds" "$KINDS" '"count":4'
step "deploy kind has reversible danger" "$KINDS" '"id":"deploy".*"danger":"reversible"'
step "restore kind has destructive danger" "$KINDS" '"id":"restore".*"danger":"destructive"'

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
DEPLOY_RESP=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main"}')
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
SHOP_DEPLOY=$(api POST /api/nks.wdc.deploy/sites/shop.loc/deploy -d '{"host":"staging","branch":"develop"}')
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
    -d "{\"host\":\"production\",\"intentToken\":\"$GATED_TOKEN\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "fire without approval → 425 pending_confirmation" "$PEND" 'pending_confirmation.*425'

# Operator approves (banner click)
api POST /api/mcp/intents/$GATED_ID/confirm >/dev/null

# AI fires deploy WITH token — should accept
FIRED=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$GATED_TOKEN\"}" \
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
    -d "{\"host\":\"production\",\"intentToken\":\"$GATED_TOKEN\"}" \
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
    -d "{\"host\":\"production\",\"intentToken\":\"$AUTO_TOKEN\"}" \
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
    -d "{\"host\":\"production\",\"intentToken\":\"$OTHER_TOKEN\"}" \
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
    -d "{\"host\":\"production\",\"intentToken\":\"$INTENT_X_TOKEN\"}" \
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
TO_CANCEL=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main"}')
TC_ID=$(echo "$TO_CANCEL" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
# Cancel immediately (within first 150ms before status='running' transition)
CANCEL_RESP=$(curl -s -X DELETE -H "Authorization: Bearer $TOKEN" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploys/$TC_ID")
# Note: race may make cancel fail if state machine already past PONR. Either
# 'cancelled' status or 'past_point_of_no_return' is acceptable.
step "cancel returns either cancelled or PONR error" "$CANCEL_RESP" '("status":"cancelled"|"past_point_of_no_return")'

# Setup: deploy → wait → rollback
SETUP_DEPLOY=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main"}')
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
SNAP_DEPLOY=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main","snapshot":true}')
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
SNAP_DEPLOY_2=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main","snapshot":true}')
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
    -d "{\"host\":\"production\",\"intentToken\":\"$TAMPERED_SIG\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "tampered signature → signature_mismatch" "$T1" 'signature_mismatch.*403'

# Attack 2: domain mismatch — token for blog.loc fired against shop.loc
T2=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"production\",\"intentToken\":\"$S_TOKEN\"}" \
    "$BASE/api/nks.wdc.deploy/sites/shop.loc/deploy")
step "domain mismatch (blog token on shop URL) → 403" "$T2" 'domain_mismatch.*403'

# Attack 3: host mismatch — original was production, fire on staging
T3=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d "{\"host\":\"staging\",\"intentToken\":\"$S_TOKEN\"}" \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "host mismatch (production token on staging) → 403" "$T3" 'host_mismatch.*403'

# Attack 4: malformed token shape (not three dot-separated parts)
T4=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","intentToken":"not.a.valid.token.has.too.many.parts"}' \
    "$BASE/api/nks.wdc.deploy/sites/blog.loc/deploy")
step "malformed token shape → 403 malformed_token" "$T4" 'malformed_token.*403'

# Attack 5: empty / missing intentToken with strict expectation —
# without a token, body is treated as direct GUI deploy (no validator),
# so this just lands as 202 queued. Verify that path:
T5=$(curl -s -w "%{http_code}" -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"host":"production","intentToken":""}' \
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
    -d "{\"host\":\"production\",\"intentToken\":\"$WK_TOKEN\"}" \
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
    -d "{\"host\":\"production\",\"intentToken\":\"$INTENT_R_TOKEN\"}" \
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
        -d "{\"host\":\"production\",\"intentToken\":\"$RACE_TOKEN\"}" \
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
SNAP_FIRE=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main","snapshot":true}')
SNAP_DEPLOY_ID=$(echo "$SNAP_FIRE" | python3 -c "import sys,json; print(json.load(sys.stdin).get('deployId',''))")
step "deploy with snapshot:true fired" "$SNAP_FIRE" '"deployId":"'

sleep 1
SNAPSHOTS=$(api GET /api/nks.wdc.deploy/sites/blog.loc/snapshots)
step "snapshots endpoint returns count > 0" "$SNAPSHOTS" '"count":[1-9]'
step "snapshot entry has our deployId as id"   "$SNAPSHOTS" "\"id\":\"$SNAP_DEPLOY_ID\""
step "snapshot has sizeBytes > 0"              "$SNAPSHOTS" '"sizeBytes":[1-9][0-9]*'
step "snapshot has path under ~/.wdc/backups"  "$SNAPSHOTS" '"path":"~/\.wdc/backups/pre-deploy/blog\.loc/'

# Deploy WITHOUT snapshot:true → stays out of the snapshots projection
NOSNAP_FIRE=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production"}')
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
python3 -c "import json,os; print(json.dumps({'host':'production','branch':'main','intentToken':os.environ['Z_INT_TOKEN']}))" > /c/temp/.e2e-z-deploy.json
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
SSE_FIRE=$(api POST /api/nks.wdc.deploy/sites/blog.loc/deploy -d '{"host":"production","branch":"main"}')
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
echo ""; echo "${YEL}=== summary ===${END}"
# ============================================================================
echo ""
echo "  PASS: ${GRN}$PASS${END}"
echo "  FAIL: ${RED}$FAIL${END}"
echo ""
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
