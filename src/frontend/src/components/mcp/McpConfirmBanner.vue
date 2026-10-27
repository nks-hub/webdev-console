<template>
  <!--
    Stack of AI requests waiting for the operator.
    WCAG: aria-live=assertive so screen readers announce each new request;
    role=alert on each entry. Focus is never moved here: a request can
    arrive while the operator types elsewhere, and Enter or Space must not
    approve it. Approving opens a confirmation (typing the target for
    anything that cannot be undone). Escape on Approve closes the banner.
  -->
  <div
    v-if="store.count > 0"
    class="mcp-confirm-stack"
    aria-live="assertive"
    aria-atomic="false"
    :aria-label="t('mcp.banner.stackLabel')"
  >
    <div
      v-for="item in store.list"
      :key="item.intentId"
      class="mcp-confirm-banner"
      :class="{ 'mcp-confirm-banner--destructive': item.kindDanger !== 'reversible' }"
      role="alert"
    >
      <div class="mcp-confirm-icon" aria-hidden="true">
        <el-icon :size="20"><Warning /></el-icon>
      </div>
      <div class="mcp-confirm-body">
        <div class="mcp-confirm-title">
          {{ item.kind ? t('mcp.banner.kindRequest', { kind: mcpKindLabel(item) }) : t('mcp.banner.genericRequest') }}
        </div>
        <div v-if="mcpTargetLabel(item)" class="mcp-confirm-target">
          {{ t('mcp.banner.target') }} <code class="mono">{{ mcpTargetLabel(item) }}</code>
        </div>
        <!-- The description comes from the AI and may not match the action. -->
        <div class="mcp-confirm-prompt">
          <span class="mcp-confirm-prompt-label">{{ t('mcp.banner.aiSays') }}</span>
          {{ item.prompt || t('mcp.banner.noDescription') }}
        </div>
        <!-- Phase 7.5+++ — explainer chip when the operator's always-confirm
             override is what caused this banner (the AI has trust grants
             but they were skipped by the kind ring-fence). -->
        <div v-if="item.alwaysConfirm" class="mcp-confirm-always-tag">
          <el-icon><Lock /></el-icon>
          {{ t('mcp.banner.alwaysConfirmExplain') }}
        </div>
        <div class="mcp-confirm-meta">
          {{ ageLabel(item.receivedAt) }}
          <!-- Phase 6.14b — live expiry countdown. Renders only inside the
               last 60 s window (urgency signal); earlier the banner just
               shows age. Color escalates: warning under 60 s, danger
               under 15 s. -->
          <span
            v-if="expiryRemaining(item) !== null"
            class="mcp-confirm-expiry"
            :class="expiryClass(item)"
            role="status"
          >
            · {{ t('mcp.banner.expiresIn', { seconds: expiryRemaining(item) }) }}
          </span>
        </div>
      </div>
      <div class="mcp-confirm-actions">
        <el-button
          type="primary"
          size="default"
          :loading="busy.has(item.intentId)"
          :disabled="declining.has(item.intentId)"
          @click="approve(item)"
          @keydown.esc.prevent="dismiss(item.intentId)"
        >
          {{ t('mcp.banner.approve') }}
        </el-button>
        <!-- Phase 7.3 — trust shortcuts: approve this request and create
             a grant for the same action on the same target, scoped to the
             asking AI run (30 min) or AI client (permanent) when the
             daemon knows them. Hidden when the request lacks the action
             or a real target (the grant would cover more than was shown)
             and for always-confirm kinds (the grant would never apply). -->
        <template v-if="canTrust(item)">
          <el-button
            size="default"
            :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
            :title="trustScopeText(item, 30)"
            @click="approveAndTrust(item, 30)"
          >
            {{ t('mcp.banner.trust30Min') }}
          </el-button>
          <el-button
            size="default"
            :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
            :title="trustScopeText(item, null)"
            @click="approveAndTrust(item, null)"
          >
            {{ t('mcp.banner.alwaysTrust') }}
          </el-button>
        </template>
        <!-- Phase 6.17a — inline revoke. Dismiss only removes the banner
             locally (the AI keeps waiting, the request stays in Requests);
             Decline ALSO calls the revoke endpoint server-side so any AI
             client that tries to fire the token sees `already_used`. -->
        <el-button
          type="danger"
          plain
          size="default"
          :loading="declining.has(item.intentId)"
          :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
          :title="t('mcp.banner.declineAria')"
          :aria-label="t('mcp.banner.declineAria')"
          @click="decline(item.intentId)"
        >
          {{ t('mcp.banner.decline') }}
        </el-button>
        <el-button
          size="default"
          :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
          :title="t('mcp.banner.dismissHint')"
          :aria-label="t('mcp.banner.dismissHint')"
          @click="dismiss(item.intentId)"
        >
          {{ t('mcp.banner.dismiss') }}
        </el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Warning, Lock } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useMcpConfirmStore, type PendingConfirm } from '../../stores/mcpConfirm'
import { createMcpGrant, revokeIntent, type McpGrantCreateBody } from '../../api/daemon'
import { confirmMcpApproval, isMcpTargetMarker, mcpTargetLabel } from '../../utils/mcpApproval'
import { mcpKindLabel } from '../../utils/mcpKindLabel'

const { t } = useI18n()
const store = useMcpConfirmStore()

// In-flight approval calls — keeps the user from double-clicking before the
// daemon responds. Cleared on completion (whether success or 4xx).
const busy = ref<Set<string>>(new Set())
// Phase 6.17a — separate "declining" set so Decline/Approve can each
// disable the OTHER without sharing a single is-busy flag.
const declining = ref<Set<string>>(new Set())

// Phase 6.14b — tick used for the expiry countdown reactivity. Updated
// once per second by the timer below; computed countdown reads it so
// the banner re-renders without manual nudges.
const tickNow = ref(Date.now())
let tickTimer: ReturnType<typeof setInterval> | null = null

function setBusy(intentId: string, on: boolean): void {
  const next = new Set(busy.value)
  if (on) next.add(intentId)
  else next.delete(intentId)
  busy.value = next
}

async function approve(item: PendingConfirm): Promise<void> {
  if (busy.value.has(item.intentId)) return
  if (!(await confirmMcpApproval(item))) return
  setBusy(item.intentId, true)
  try {
    if (await store.approve(item.intentId)) ElMessage.success(t('mcp.toast.approveSuccess'))
    else ElMessage.info(t('mcp.banner.toastAlreadyConsumed'))
  } catch (err) {
    ElMessage.error(t('mcp.toast.approveFailed', { error: err instanceof Error ? err.message : String(err) }))
  } finally {
    setBusy(item.intentId, false)
  }
}

function canTrust(item: PendingConfirm): boolean {
  return !!item.kind && !isMcpTargetMarker(item.domain) && !item.alwaysConfirm
}

/**
 * Who a trust grant from this banner covers: the 30-minute one the asking
 * AI run (MCP session), the permanent one the asking AI client; every AI
 * client only when the daemon could not tell who asked.
 */
function trustScope(item: PendingConfirm, minutes: number | null): Pick<McpGrantCreateBody, 'scopeType' | 'scopeValue'> {
  if (minutes !== null && item.sessionId) return { scopeType: 'session', scopeValue: item.sessionId }
  if (item.clientId) return { scopeType: 'api_key', scopeValue: item.clientId }
  return { scopeType: 'always', scopeValue: null }
}

function trustScopeText(item: PendingConfirm, minutes: number | null): string {
  const scope = trustScope(item, minutes)
  const who = scope.scopeType === 'session'
    ? t('mcp.banner.forSession')
    : scope.scopeType === 'api_key'
      ? t('mcp.banner.forClient', { client: (scope.scopeValue ?? '').replace(/^client:/, '') })
      : t('mcp.banner.forAll')
  const rule = { kind: mcpKindLabel(item), target: item.domain ?? '', who }
  return minutes === null
    ? t('mcp.banner.trustAlwaysRule', rule)
    : t('mcp.banner.trustMinutesRule', { ...rule, minutes })
}

/**
 * Phase 7.3 — approve THIS intent AND create a grant for the same kind on
 * the same target (never a wildcard), so the next identical request is
 * approved without asking. `expiresMin === null` means the grant never
 * expires. The confirmation spells out the rule before anything is saved.
 *
 * A failed grant does not block the approval the operator asked for.
 */
async function approveAndTrust(item: PendingConfirm, expiresMin: number | null): Promise<void> {
  if (busy.value.has(item.intentId) || !canTrust(item)) return
  if (!(await confirmMcpApproval(item, trustScopeText(item, expiresMin)))) return
  setBusy(item.intentId, true)
  try {
    const body: McpGrantCreateBody = {
      ...trustScope(item, expiresMin),
      kindPattern: item.kind!,
      targetPattern: item.domain!,
      expiresAt: expiresMin === null
        ? null
        : new Date(Date.now() + expiresMin * 60_000).toISOString(),
      grantedBy: 'banner',
      note: expiresMin === null
        ? `Always trust ${item.kind} on ${item.domain}`
        : `Trust ${item.kind} on ${item.domain} for ${expiresMin} min`,
    }
    try {
      await createMcpGrant(body)
      ElMessage.success(expiresMin === null
        ? t('mcp.banner.toastAlwaysTrusted')
        : t('mcp.banner.toastTrustedFor', { minutes: expiresMin }))
    } catch (err) {
      ElMessage.warning(t('mcp.banner.toastGrantFailed', {
        error: err instanceof Error ? err.message : String(err),
      }))
    }
    await store.approve(item.intentId)
  } catch (err) {
    ElMessage.error(t('mcp.toast.approveFailed', { error: err instanceof Error ? err.message : String(err) }))
  } finally {
    setBusy(item.intentId, false)
  }
}

function dismiss(intentId: string): void {
  store.dismiss(intentId)
}

/**
 * Phase 6.17a — decline + revoke. Like dismiss, but ALSO calls the
 * daemon's POST /api/mcp/intents/{id}/revoke so the AI client trying
 * to fire this token sees `already_used` immediately rather than
 * waiting for the natural expiry.
 *
 * Failure to revoke (network blip, intent already consumed by a race)
 * is non-fatal — we drop the banner regardless. The toast tells the
 * user what happened.
 */
async function decline(intentId: string): Promise<void> {
  if (declining.value.has(intentId)) return
  const next = new Set(declining.value)
  next.add(intentId)
  declining.value = next
  try {
    await revokeIntent(intentId)
    ElMessage.success(t('mcp.banner.toastDeclined'))
  } catch (err) {
    // 409 already_used is the most likely error here (race with the
    // sweeper or a confirmed/consumed intent we hadn't refreshed). Log
    // a softer message in that case so the user isn't alarmed.
    const msg = err instanceof Error ? err.message : String(err)
    if (msg.includes('already_used')) {
      ElMessage.info(t('mcp.banner.toastAlreadyConsumed'))
    } else {
      ElMessage.warning(t('mcp.banner.toastDeclineFailed', { error: msg }))
    }
  } finally {
    const cleared = new Set(declining.value)
    cleared.delete(intentId)
    declining.value = cleared
    store.dismiss(intentId)
  }
}

function ageLabel(receivedAt: number): string {
  const sec = Math.floor((tickNow.value - receivedAt) / 1000)
  if (sec < 5) return t('mcp.banner.ageJustNow')
  if (sec < 60) return t('mcp.banner.ageSeconds', { seconds: sec })
  return t('mcp.banner.ageMinutes', { minutes: Math.floor(sec / 60), seconds: sec % 60 })
}

/**
 * Phase 6.14b — return seconds until expiry, or null if no expiresAt
 * was provided OR more than 60 seconds remain (we only show the
 * countdown inside the urgency window — no need to clutter the banner
 * for fresh intents).
 */
function expiryRemaining(item: PendingConfirm): number | null {
  if (!item.expiresAt) return null
  const expiryMs = new Date(item.expiresAt).getTime()
  if (Number.isNaN(expiryMs)) return null
  const sec = Math.max(0, Math.round((expiryMs - tickNow.value) / 1000))
  if (sec > 60) return null
  return sec
}

function expiryClass(item: PendingConfirm): string {
  const sec = expiryRemaining(item)
  if (sec === null) return ''
  if (sec <= 15) return 'expiry-danger'
  return 'expiry-warning'
}

onMounted(() => {
  tickTimer = setInterval(() => {
    tickNow.value = Date.now()
    // Auto-dismiss expired entries — approving an expired intent would
    // surface a 403 from the validator, so we drop the banner with a
    // toast as soon as the wall-clock passes expiresAt.
    for (const item of store.list) {
      if (!item.expiresAt) continue
      const expiryMs = new Date(item.expiresAt).getTime()
      if (!Number.isNaN(expiryMs) && expiryMs <= tickNow.value) {
        store.dismiss(item.intentId)
        ElMessage.warning(t('mcp.banner.toastExpired', { kind: mcpKindLabel(item) || item.intentId.slice(0, 8) }))
      }
    }
  }, 1000)
})

onBeforeUnmount(() => {
  if (tickTimer !== null) clearInterval(tickTimer)
})
</script>

<style scoped>
.mcp-confirm-stack {
  position: fixed;
  top: 12px;
  right: 12px;
  z-index: 3000;
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2);
  max-width: min(440px, calc(100vw - 24px));
}

.mcp-confirm-banner {
  display: grid;
  grid-template-columns: auto 1fr auto;
  gap: var(--wdc-space-3-5);
  align-items: start;
  padding: var(--wdc-space-3-5) var(--wdc-space-4);
  background: rgba(245, 158, 11, 0.10);
  border: 1px solid var(--wdc-status-starting);
  border-left-width: 5px;
  border-radius: var(--wdc-radius-lg);
  box-shadow: var(--wdc-shadow-card);
  /* Non-color signal too — WCAG 1.4.1 Use of Color */
}
.mcp-confirm-banner--destructive {
  background: rgba(248, 113, 113, 0.10);
  border-color: var(--wdc-status-error);
  border-left-width: 6px;
}

.mcp-confirm-icon {
  color: var(--el-color-warning, #e6a23c);
  padding-top: var(--wdc-space-0-5);
}

.mcp-confirm-body {
  min-width: 0;
}

.mcp-confirm-title {
  font-weight: 600;
  font-size: 14px;
  color: var(--el-text-color-primary);
  margin-bottom: var(--wdc-space-1);
}
.mcp-confirm-target {
  font-size: 13px;
  color: var(--el-text-color-regular);
  margin-bottom: var(--wdc-space-1);
  word-break: break-word;
}

.mcp-confirm-prompt {
  font-size: 13px;
  color: var(--el-text-color-regular);
  word-break: break-word;
  margin-bottom: var(--wdc-space-1);
  padding-left: var(--wdc-space-2);
  border-left: 2px solid var(--wdc-border);
}
.mcp-confirm-prompt-label {
  display: block;
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.mcp-confirm-meta {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}
.mcp-confirm-always-tag {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  font-size: 12px;
  font-weight: 500;
  color: var(--el-color-warning-dark-2);
  background: var(--el-color-warning-light-9);
  padding: var(--wdc-space-1-5) var(--wdc-space-3);
  border-radius: var(--wdc-radius-sm);
  margin: var(--wdc-space-1) 0;
  border-left: 3px solid var(--el-color-warning);
}

.mcp-confirm-expiry {
  font-variant-numeric: tabular-nums;
  font-weight: 600;
}
.expiry-warning { color: var(--el-color-warning); }
.expiry-danger { color: var(--el-color-danger); }
.mono { font-family: var(--el-font-family-monospace, monospace); }

.mcp-confirm-actions {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1-5);
  justify-self: end;
}
/* Element Plus spaces sibling buttons with a left margin; the column
   layout stacks them, so drop it. */
.mcp-confirm-actions :deep(.el-button + .el-button) { margin-left: 0; }

@media (prefers-reduced-motion: reduce) {
  .mcp-confirm-banner {
    transition: none;
  }
}
</style>
