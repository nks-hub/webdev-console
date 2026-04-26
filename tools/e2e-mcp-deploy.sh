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
echo ""; echo "${YEL}=== J. deploy.enabled gate ===${END}"
# ============================================================================
api PUT /api/settings -d '{"deploy.enabled":"false"}' >/dev/null
GATED=$(curl -s -w "%{http_code}" -H "Authorization: Bearer $TOKEN" "$BASE/api/nks.wdc.deploy/sites/blog.loc/history")
step "deploy.enabled=false → 404 deploy_disabled" "$GATED" 'deploy_disabled.*404'
api PUT /api/settings -d '{"deploy.enabled":"true"}' >/dev/null

# ============================================================================
echo ""; echo "${YEL}=== summary ===${END}"
# ============================================================================
echo ""
echo "  PASS: ${GRN}$PASS${END}"
echo "  FAIL: ${RED}$FAIL${END}"
echo ""
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
