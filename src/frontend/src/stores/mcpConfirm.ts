import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { confirmIntent } from '../api/daemon'

/**
 * Pinia store for MCP destructive-operation confirmation banners (Phase 5.5
 * Mode A). The daemon broadcasts an `mcp:confirm-request` SSE event when an
 * AI client crafts a deploy intent; the App.vue subscription pushes that
 * payload into <see cref="addPending"/>. McpConfirmBanner renders the queue.
 *
 * The pending Map is keyed by `intentId` so duplicate broadcasts (network
 * retry, daemon restart replay) don't stack a second banner for the same
 * intent. Approved entries are removed on the daemon's 200 response;
 * dismissed entries are removed locally — the underlying intent expires
 * naturally and the IntentSweeperService GCs the row within a day.
 */
export interface PendingConfirm {
  intentId: string
  /** AI-supplied human-readable description of what the AI wants to do. */
  prompt?: string
  /** Local timestamp the banner appeared — used for ordering + age display. */
  receivedAt: number
  /**
   * Phase 6.14b — server-side intent expiry. ISO-8601 string. When
   * present the banner shows a live countdown; when the wall clock
   * passes it the banner auto-dismisses with a toast since approving
   * an expired intent would surface a 403 from the validator anyway.
   */
  expiresAt?: string
  /** Phase 6.14b — verb (deploy/rollback/cancel/restore) for the banner action label. */
  kind?: 'deploy' | 'rollback' | 'cancel' | 'restore'
  /** Phase 6.14b — target domain for clarity in the banner. */
  domain?: string
  /** Phase 6.14b — target host (or synthetic group/restore markers). */
  host?: string
}

export const useMcpConfirmStore = defineStore('mcpConfirm', () => {
  const pending = ref<Map<string, PendingConfirm>>(new Map())

  const list = computed<PendingConfirm[]>(() =>
    Array.from(pending.value.values()).sort((a, b) => a.receivedAt - b.receivedAt))

  const count = computed(() => pending.value.size)

  /** Called by the SSE subscription in App.vue. Idempotent on intentId. */
  function addPending(payload: {
    intentId: string
    prompt?: string
    expiresAt?: string
    kind?: 'deploy' | 'rollback' | 'cancel' | 'restore'
    domain?: string
    host?: string
  }): void {
    if (!payload?.intentId) return
    if (pending.value.has(payload.intentId)) return
    const next = new Map(pending.value)
    next.set(payload.intentId, {
      intentId: payload.intentId,
      prompt: payload.prompt,
      receivedAt: Date.now(),
      expiresAt: payload.expiresAt,
      kind: payload.kind,
      domain: payload.domain,
      host: payload.host,
    })
    pending.value = next
  }

  /** Approve via the daemon endpoint, then drop from local queue. */
  async function approve(intentId: string): Promise<void> {
    try {
      await confirmIntent(intentId)
    } catch (err) {
      // 409 (already_confirmed) is benign — daemon stamped from another
      // window or the user double-clicked. 404 means the intent expired
      // before approval; the AI side will surface its own error. Either
      // way the banner should disappear so we drop it locally.
      // eslint-disable-next-line no-console
      console.warn('[mcpConfirm] approve failed', intentId, err)
    } finally {
      remove(intentId)
    }
  }

  /** User dismissed the banner without approving — token expires naturally. */
  function dismiss(intentId: string): void {
    remove(intentId)
  }

  function remove(intentId: string): void {
    if (!pending.value.has(intentId)) return
    const next = new Map(pending.value)
    next.delete(intentId)
    pending.value = next
  }

  return {
    pending,
    list,
    count,
    addPending,
    approve,
    dismiss,
  }
})
