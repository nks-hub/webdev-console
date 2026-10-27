import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { confirmIntent, fetchPendingIntents, revokeIntent, type IntentInventoryEntry } from '../api/daemon'

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
 *
 * Entries are also removed when the daemon reports the intent settled
 * (`mcp:intent-changed` confirmed / revoked / consumed / expired), whichever
 * surface settled it, and the queue is reloaded from the daemon on app start
 * and every reconnect so requests that arrived while WDC was closed are not
 * lost.
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
  /**
   * Phase 6.14b — wire-format kind id (deploy/rollback/cancel/restore
   * or any plugin-registered id like "nksbackup:restore").
   * Phase 7.4a opened this from a hardcoded enum to an open string.
   */
  kind?: string
  /**
   * Phase 7.4c — human-readable label registered for this kind in
   * IDestructiveOperationKinds. Banner prefers this over the raw id.
   * Null when the kind isn't (or is no longer) in the registry —
   * banner falls back to the id.
   */
  kindLabel?: string
  /**
   * Phase 7.4c — danger level the kind was registered with
   * ('reversible' | 'destructive'). Banner uses this to escalate
   * visual treatment for destructive ops.
   */
  kindDanger?: 'reversible' | 'destructive'
  /** Phase 7.4c — which plugin owns the kind ('core' for legacy seeded). */
  kindPluginId?: string
  /** Phase 6.14b — target domain for clarity in the banner. */
  domain?: string
  /** Phase 6.14b — target host (or synthetic group/restore markers). */
  host?: string
  /**
   * Phase 7.5+++ — true when the operator has marked this kind in
   * mcp.always_confirm_kinds. Banner uses this to show distinct copy
   * explaining that grants were skipped by operator override (rather
   * than the more common "no matching grant" case).
   */
  alwaysConfirm?: boolean
  /**
   * MCP session (one AI client process) and AI client id
   * (`client:<name>`) that asked. The banner scopes its trust shortcuts to
   * them; absent from daemons before migration 020.
   */
  sessionId?: string
  clientId?: string
}

/** `mcp:intent-changed` change values after which nobody needs to approve the intent. */
export const SETTLED_CHANGES: ReadonlySet<string> = new Set(['confirmed', 'revoked', 'consumed', 'expired'])

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
    kind?: string
    kindLabel?: string
    kindDanger?: 'reversible' | 'destructive'
    kindPluginId?: string
    domain?: string
    host?: string
    alwaysConfirm?: boolean
    sessionId?: string | null
    clientId?: string | null
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
      kindLabel: payload.kindLabel,
      kindDanger: payload.kindDanger,
      kindPluginId: payload.kindPluginId,
      domain: payload.domain,
      host: payload.host,
      alwaysConfirm: payload.alwaysConfirm === true,
      sessionId: payload.sessionId ?? undefined,
      clientId: payload.clientId ?? undefined,
    })
    pending.value = next
  }

  /**
   * Replace the queue with the daemon's list of requests still waiting for
   * the operator. Called on app start and on every daemon reconnect so a
   * request that arrived while WDC was closed or reloading can still be
   * approved. Entries the daemon no longer lists as pending are dropped.
   * MCP disabled (404) or an unreachable daemon leaves the queue untouched.
   */
  async function loadPending(): Promise<void> {
    const startedAt = Date.now()
    let entries: IntentInventoryEntry[]
    try {
      entries = (await fetchPendingIntents()).entries ?? []
    } catch {
      return
    }
    const next = new Map<string, PendingConfirm>()
    for (const e of entries) {
      if (e.state !== 'pending_confirmation') continue
      const existing = pending.value.get(e.intentId)
      const created = Date.parse(e.confirmRequestedAt ?? e.createdAt)
      next.set(e.intentId, existing ?? {
        intentId: e.intentId,
        prompt: e.prompt ?? undefined,
        receivedAt: Number.isNaN(created) ? Date.now() : created,
        expiresAt: e.expiresAt,
        kind: e.kind,
        kindLabel: e.kindLabel ?? undefined,
        kindDanger: e.kindDanger ?? undefined,
        kindPluginId: e.kindPluginId ?? undefined,
        domain: e.domain,
        host: e.host,
        alwaysConfirm: e.alwaysConfirm === true,
        sessionId: e.sessionId ?? undefined,
        clientId: e.clientId ?? undefined,
      })
    }
    // Keep banners whose SSE event arrived while the fetch was in flight.
    for (const [id, item] of pending.value) {
      if (!next.has(id) && item.receivedAt >= startedAt) next.set(id, item)
    }
    pending.value = next
  }

  /**
   * Daemon lifecycle event for an intent. Any settled state closes the
   * banner, whichever surface (toast, Intents page, grant, agent) settled it.
   */
  function handleIntentChanged(evt: { intentId?: string; change?: string } | null | undefined): void {
    if (!evt?.intentId) return
    if (evt.change && SETTLED_CHANGES.has(evt.change)) remove(evt.intentId)
  }

  /**
   * Reject: revoke server-side so the agent cannot fire it, then drop
   * locally. Resolves false when the intent was already settled elsewhere.
   */
  async function reject(intentId: string): Promise<boolean> {
    let rejected = true
    try {
      await revokeIntent(intentId)
    } catch (err) {
      // 409 already_used / 404 gone: the intent is settled either way.
      const msg = err instanceof Error ? err.message : String(err)
      if (!/HTTP (409|404|410)\b/.test(msg)) throw err
      rejected = false
    }
    remove(intentId)
    return rejected
  }

  /**
   * Approve via the daemon endpoint, then drop from local queue. Resolves
   * false when the intent was already settled (approved, revoked, expired)
   * elsewhere, so callers can say so instead of claiming the approval.
   */
  async function approve(intentId: string): Promise<boolean> {
    let approved = true
    try {
      await confirmIntent(intentId)
    } catch (err) {
      // 409 (already confirmed elsewhere) and 404/410 (expired) settle the
      // request either way, so the banner goes. Anything else (daemon down,
      // 401, 500) means the approval did NOT happen: keep the banner and let
      // the caller tell the user, instead of looking approved while the AI
      // request quietly expires.
      const msg = err instanceof Error ? err.message : String(err)
      if (!/HTTP (409|404|410)\b/.test(msg)) throw err
      approved = false
    }
    remove(intentId)
    return approved
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
    loadPending,
    handleIntentChanged,
    approve,
    reject,
    dismiss,
    remove,
  }
})
