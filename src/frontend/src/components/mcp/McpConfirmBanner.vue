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
    aria-label="MCP deploy approval requests"
  >
    <div
      v-for="(item, index) in store.list"
      :key="item.intentId"
      class="mcp-confirm-banner"
      role="alert"
    >
      <div class="mcp-confirm-icon" aria-hidden="true">
        <el-icon :size="20"><Warning /></el-icon>
      </div>
      <div class="mcp-confirm-body">
        <div class="mcp-confirm-title">
          <span v-if="item.kind">AI requests <strong>{{ item.kind }}</strong></span>
          <span v-else>AI is requesting a destructive operation</span>
          <span v-if="item.domain && item.host">
            on <code class="mono">{{ item.domain }} → {{ item.host }}</code>
          </span>
        </div>
        <div class="mcp-confirm-prompt">
          {{ item.prompt || 'No description provided.' }}
        </div>
        <div class="mcp-confirm-meta">
          intent <code>{{ shortId(item.intentId) }}</code>
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
            · expires in {{ expiryRemaining(item) }}s
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
          Approve
        </el-button>
        <el-button
          size="default"
          :disabled="busy.has(item.intentId)"
          @click="dismiss(item.intentId)"
        >
          Dismiss
        </el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Warning } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useMcpConfirmStore, type PendingConfirm } from '../../stores/mcpConfirm'

const store = useMcpConfirmStore()

// In-flight approval calls — keeps the user from double-clicking before the
// daemon responds. Cleared on completion (whether success or 4xx).
const busy = ref<Set<string>>(new Set())

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

function dismiss(intentId: string): void {
  store.dismiss(intentId)
}

function shortId(id: string): string {
  // Show just the first 8 chars of the UUID — enough for cross-reference
  // with daemon logs without overwhelming the banner layout.
  return id.length > 12 ? id.slice(0, 8) : id
}

function ageLabel(receivedAt: number): string {
  const sec = Math.floor((tickNow.value - receivedAt) / 1000)
  if (sec < 5) return 'just now'
  if (sec < 60) return `${sec}s ago`
  return `${Math.floor(sec / 60)}m ${sec % 60}s ago`
}

/**
 * Phase 6.14b — return seconds until expiry, or null if no expiresAt
 * was provided OR more than 60 seconds remain (we only show the
 * countdown inside the urgency window — no need to clutter the banner
 * for fresh intents).
 */
function expiryRemaining(item: PendingConfirm): number | null {
  if (!item.expiresAt) return null
  const t = new Date(item.expiresAt).getTime()
  if (Number.isNaN(t)) return null
  const sec = Math.max(0, Math.round((t - tickNow.value) / 1000))
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
      const t = new Date(item.expiresAt).getTime()
      if (!Number.isNaN(t) && t <= tickNow.value) {
        store.dismiss(item.intentId)
        ElMessage.warning(
          `Intent ${item.intentId.slice(0, 8)} expired before approval — banner dismissed`,
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
