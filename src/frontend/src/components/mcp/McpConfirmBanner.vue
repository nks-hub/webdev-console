<template>
  <!--
    Stack of pending MCP destructive-operation confirmation banners.
    WCAG: aria-live=assertive so screen readers announce each new banner;
    role=alert on each entry; first banner gets initial focus on appear so
    keyboard users land on the Approve/Dismiss buttons without tab-hunting.
    Keyboard: Enter on Approve approves; Escape dismisses.
  -->
  <div
    v-if="store.count > 0"
    class="mcp-confirm-stack"
    aria-live="assertive"
    aria-atomic="false"
    :aria-label="t('mcp.banner.stackLabel')"
  >
    <div
      v-for="(item, index) in store.list"
      :key="item.intentId"
      class="mcp-confirm-banner"
      :class="{ 'mcp-confirm-banner--destructive': item.kindDanger === 'destructive' }"
      role="alert"
    >
      <div class="mcp-confirm-icon" aria-hidden="true">
        <el-icon :size="20"><Warning /></el-icon>
      </div>
      <div class="mcp-confirm-body">
        <div class="mcp-confirm-title">
          <i18n-t v-if="item.kind" keypath="mcp.banner.kindRequest" tag="span">
            <template #kind><strong>{{ item.kindLabel || item.kind }}</strong></template>
          </i18n-t>
          <span v-else>{{ t('mcp.banner.genericRequest') }}</span>
          <i18n-t v-if="item.domain && item.host" keypath="mcp.banner.onTarget" tag="span">
            <template #domain><code class="mono">{{ item.domain }}</code></template>
            <template #host><code class="mono">{{ item.host }}</code></template>
          </i18n-t>
        </div>
        <div class="mcp-confirm-prompt">
          {{ item.prompt || t('mcp.banner.noDescription') }}
        </div>
        <!-- Phase 7.5+++ — explainer chip when the operator's always-confirm
             override is what caused this banner (the AI has trust grants
             but they were skipped by the kind ring-fence). Helps the
             operator understand WHY they're being asked despite earlier
             "trust 30 min" / "always trust" choices. -->
        <div v-if="item.alwaysConfirm" class="mcp-confirm-always-tag">
          <el-icon><Lock /></el-icon>
          {{ t('mcp.banner.alwaysConfirmExplain') }}
        </div>
        <div class="mcp-confirm-meta">
          {{ t('mcp.banner.intent') }} <code>{{ shortId(item.intentId) }}</code>
          · {{ ageLabel(item.receivedAt) }}
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
          :ref="el => index === 0 && setFirstApproveRef(el)"
          type="primary"
          size="default"
          :loading="busy.has(item.intentId)"
          @click="approve(item.intentId)"
          @keydown.esc.prevent="dismiss(item.intentId)"
        >
          {{ t('mcp.banner.approve') }}
        </el-button>
        <!-- Phase 7.3 — persistent trust shortcuts. Both create an
             mcp_session_grants row server-side BEFORE approving the
             current intent so future identical requests skip the
             banner entirely. "30 min" scopes by session id (per-agent),
             "always" by api_key id (per-credential, survives session
             rotation). Falls back to a plain approve if neither id is
             known (no MCP middleware headers). -->
        <el-button
          size="default"
          :loading="busy.has(item.intentId)"
          :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
          @click="approveAndTrust(item, 30)"
        >
          {{ t('mcp.banner.trust30Min') }}
        </el-button>
        <el-button
          size="default"
          :loading="busy.has(item.intentId)"
          :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
          @click="approveAndTrust(item, null)"
        >
          {{ t('mcp.banner.alwaysTrust') }}
        </el-button>
        <!-- Phase 6.17a — inline revoke. Dismiss only removes the banner
             locally (intent expires naturally); Decline ALSO calls the
             revoke endpoint server-side so any AI client that tries to
             fire the token sees `already_used` immediately. -->
        <el-button
          type="danger"
          plain
          size="default"
          :loading="declining.has(item.intentId)"
          :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
          :aria-label="t('mcp.banner.declineAria')"
          @click="decline(item.intentId)"
        >
          {{ t('mcp.banner.decline') }}
        </el-button>
        <el-button
          size="default"
          :disabled="busy.has(item.intentId) || declining.has(item.intentId)"
          @click="dismiss(item.intentId)"
        >
          {{ t('mcp.banner.dismiss') }}
        </el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Warning, Lock } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useMcpConfirmStore, type PendingConfirm } from '../../stores/mcpConfirm'
import { createMcpGrant, revokeIntent, type McpGrantCreateBody } from '../../api/daemon'

const { t } = useI18n()
const store = useMcpConfirmStore()

// In-flight approval calls — keeps the user from double-clicking before the
// daemon responds. Cleared on completion (whether success or 4xx).
const busy = ref<Set<string>>(new Set())
// Phase 6.17a — separate "declining" set so Decline/Approve can each
// disable the OTHER without sharing a single is-busy flag (which would
// prevent users from clicking Decline while the Approve call is in
// flight if both shared `busy`).
const declining = ref<Set<string>>(new Set())

// Phase 6.14b — tick used for the expiry countdown reactivity. Updated
// once per second by the timer below; computed countdown reads it so
// the banner re-renders without manual nudges.
const tickNow = ref(Date.now())
let tickTimer: ReturnType<typeof setInterval> | null = null

const firstApproveRef = ref<unknown>(null)
function setFirstApproveRef(el: unknown): void {
  firstApproveRef.value = el
}

// Focus the first banner's Approve button whenever the queue grows from
// 0 → N or a new banner becomes the topmost. Skipped when the operator
// has already moved focus elsewhere intentionally.
watch(
  () => store.count,
  async (now, before) => {
    if (now > 0 && (before ?? 0) === 0) {
      await nextTick()
      const ref_ = firstApproveRef.value as { focus?: () => void } | null
      ref_?.focus?.()
    }
  },
)

async function approve(intentId: string): Promise<void> {
  if (busy.value.has(intentId)) return
  const next = new Set(busy.value)
  next.add(intentId)
  busy.value = next
  try {
    await store.approve(intentId)
  } finally {
    const cleared = new Set(busy.value)
    cleared.delete(intentId)
    busy.value = cleared
  }
}

/**
 * Phase 7.3 — approve THIS intent AND create a persistent grant so future
 * identical requests skip the banner. `expiresMin === null` means the
 * grant never expires (always-trust); a number is added to "now" as
 * minutes until expiry.
 *
 * Scope strategy: until the daemon's confirm-request SSE event carries
 * the calling caller's session/api-key id, we use scope_type='always'
 * (any caller) but narrow by kind+target so the grant only covers the
 * specific operation the user was shown. Approving "deploy on blog.loc"
 * doesn't auto-confirm "rollback on shop.loc". When the daemon starts
 * forwarding caller ids in the SSE payload we can flip to scope='session'
 * for the timed variant and 'api_key' for the permanent one.
 *
 * Errors are non-fatal — if grant creation fails (mcp.enabled flipped
 * mid-flight, network blip), we still proceed with the one-shot approve
 * so the user's primary action ("ship this deploy") completes.
 */
async function approveAndTrust(item: PendingConfirm, expiresMin: number | null): Promise<void> {
  if (busy.value.has(item.intentId)) return
  const next = new Set(busy.value)
  next.add(item.intentId)
  busy.value = next
  try {
    const body: McpGrantCreateBody = {
      scopeType: 'always',
      scopeValue: null,
      kindPattern: item.kind ?? '*',
      targetPattern: item.domain ?? '*',
      expiresAt: expiresMin === null
        ? null
        : new Date(Date.now() + expiresMin * 60_000).toISOString(),
      grantedBy: 'banner',
      note: expiresMin === null
        ? `Always trust ${item.kind ?? 'any'} on ${item.domain ?? 'any'}`
        : `Trust ${item.kind ?? 'any'} on ${item.domain ?? 'any'} for ${expiresMin} min`,
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
  } finally {
    const cleared = new Set(busy.value)
    cleared.delete(item.intentId)
    busy.value = cleared
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

function shortId(id: string): string {
  // Show just the first 8 chars of the UUID — enough for cross-reference
  // with daemon logs without overwhelming the banner layout.
  return id.length > 12 ? id.slice(0, 8) : id
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
    // toast as soon as the wall-clock passes expiresAt. Operator can
    // re-mint a fresh intent if they still want to act.
    for (const item of store.list) {
      if (!item.expiresAt) continue
      const expiryMs = new Date(item.expiresAt).getTime()
      if (!Number.isNaN(expiryMs) && expiryMs <= tickNow.value) {
        store.dismiss(item.intentId)
        ElMessage.warning(
          t('mcp.banner.toastExpired', { id: item.intentId.slice(0, 8) }),
        )
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
  gap: 8px;
  max-width: min(440px, calc(100vw - 24px));
}

.mcp-confirm-banner {
  display: grid;
  grid-template-columns: auto 1fr auto;
  gap: 12px;
  align-items: start;
  padding: 12px 14px;
  background: var(--el-color-warning-light-9, #fdf6ec);
  border: 1px solid var(--el-color-warning, #e6a23c);
  border-left-width: 4px;
  border-radius: 6px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.12);
  /* Use a non-color signal too — WCAG 1.4.1 Use of Color */
}
/* Phase 7.4c — destructive kinds get a danger-colored border + heavier
   left rule. The icon is already a Warning glyph so screen readers still
   announce "warning"; only the visual treatment escalates here. */
.mcp-confirm-banner--destructive {
  background: var(--el-color-danger-light-9, #fef0f0);
  border-color: var(--el-color-danger, #f56c6c);
  border-left-width: 6px;
}

.mcp-confirm-icon {
  color: var(--el-color-warning, #e6a23c);
  padding-top: 2px;
}

.mcp-confirm-body {
  min-width: 0;
}

.mcp-confirm-title {
  font-weight: 600;
  font-size: 14px;
  color: var(--el-text-color-primary);
  margin-bottom: 4px;
}

.mcp-confirm-prompt {
  font-size: 13px;
  color: var(--el-text-color-regular);
  word-break: break-word;
  margin-bottom: 4px;
}

.mcp-confirm-meta {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}
.mcp-confirm-always-tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  font-weight: 500;
  color: var(--el-color-warning-dark-2);
  background: var(--el-color-warning-light-9);
  padding: 4px 8px;
  border-radius: 4px;
  margin: 4px 0;
  border-left: 3px solid var(--el-color-warning);
}

.mcp-confirm-meta code {
  font-family: var(--el-font-family-monospace, monospace);
  background: var(--el-fill-color-light);
  padding: 1px 4px;
  border-radius: 3px;
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
  gap: 6px;
  justify-self: end;
}

@media (prefers-reduced-motion: reduce) {
  .mcp-confirm-banner {
    transition: none;
  }
}
</style>
