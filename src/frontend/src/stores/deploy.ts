import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import {
  startDeploy as apiStartDeploy,
  getDeployStatus,
  getDeployHistory,
  rollbackDeploy as apiRollback,
  cancelDeploy as apiCancel,
  startDeployGroup as apiStartGroup,
  type DeployEventDto,
  type DeployHistoryEntryDto,
  type DeployResultDto,
} from '../api/deploy'

/**
 * Pinia store for the NksDeploy plugin's UI state. Composition API per the
 * project convention (see src/frontend/src/stores/sites.ts). The state is
 * keyed by deployId so the persistent right-side `<DeployRunDrawer>` can
 * follow a run across route changes — `activeRun` is whichever run the user
 * most recently triggered or selected, or null when nothing is in flight.
 *
 * SSE wiring: external code (App.vue or a startup composable) calls
 * subscribeEventsMap with `'deploy:event': (e) => deployStore.handleSseEvent(e)`
 * so events from EVERY active deploy flow into one place. The store routes
 * events to the right run by `deployId`.
 */
export const useDeployStore = defineStore('deploy', () => {
  /** All known runs the renderer has seen (in-flight + recently completed). */
  const runs = ref<Map<string, DeployRunState>>(new Map())

  /** Currently visible run in the deploy drawer. Null = drawer closed. */
  const activeRunId = ref<string | null>(null)

  /** Per-domain history cache so the history page re-renders without refetching. */
  const historyByDomain = ref<Map<string, DeployHistoryEntryDto[]>>(new Map())

  const activeRun = computed<DeployRunState | null>(() =>
    activeRunId.value ? runs.value.get(activeRunId.value) ?? null : null)

  const isDrawerOpen = computed(() => activeRunId.value !== null)

  async function startDeploy(
    domain: string,
    host: string,
    options?: {
      backendOptions?: Record<string, unknown>
      /** Phase 6.12c — pre-deploy snapshot opt-in. */
      snapshot?: { include: boolean; retentionDays?: number }
    },
  ): Promise<string | null> {
    // Backwards-compat: callers passing `Record<string, unknown>` directly
    // (legacy 2-arg form) still work — we treat that as backendOptions.
    const opts = options ?? {}
    const resp = await apiStartDeploy(domain, host, {
      backendOptions: opts.backendOptions,
      snapshot: opts.snapshot,
    })
    if (resp.deployId) {
      // Seed an empty run row so the drawer can render immediately even
      // before the first SSE event lands.
      runs.value.set(resp.deployId, {
        deployId: resp.deployId,
        domain,
        host,
        startedAt: new Date().toISOString(),
        events: [],
        latestPhase: 'Queued',
        latestStep: '',
        latestMessage: '',
        isPastPonr: false,
        isTerminal: false,
        success: null,
      })
      activeRunId.value = resp.deployId
    }
    return resp.deployId
  }

  /**
   * SSE handler. Fed by App.vue's subscribeEventsMap call:
   *   subscribeEventsMap({ 'deploy:event': (d) => deployStore.handleSseEvent(d as DeployEventDto) })
   */
  function handleSseEvent(evt: DeployEventDto): void {
    let run = runs.value.get(evt.deployId)
    if (!run) {
      // First we hear of this deploy — likely triggered from MCP, CLI, or
      // another renderer window. Synthesize a row so the drawer can
      // surface it.
      run = {
        deployId: evt.deployId,
        domain: '?',
        host: '?',
        startedAt: evt.timestamp,
        events: [],
        latestPhase: evt.phase,
        latestStep: evt.step,
        latestMessage: evt.message,
        isPastPonr: evt.isPastPonr,
        isTerminal: evt.isTerminal,
        success: null,
      }
      runs.value.set(evt.deployId, run)
    }
    run.events.push(evt)
    run.latestPhase = evt.phase
    run.latestStep = evt.step
    run.latestMessage = evt.message
    if (evt.isPastPonr) run.isPastPonr = true
    if (evt.isTerminal) {
      run.isTerminal = true
      run.success = evt.phase === 'Done'
      run.completedAt = evt.timestamp
    }
    // Reactivity nudge: ref<Map> updates need a manual re-assign for Vue
    // to pick up nested mutations on the keyed object.
    runs.value = new Map(runs.value)
  }

  /**
   * Phase 7.5+++ — record a `deploy:hook` SSE event onto the matching
   * run state. App.vue's subscribeEventsMap routes 'deploy:hook' here.
   * Tolerates the run not yet existing (rare race where a hook fires
   * before the seed event); creates a placeholder row in that case.
   */
  function handleHookEvent(evt: { deployId: string } & DeployHookEvent): void {
    let run = runs.value.get(evt.deployId)
    if (!run) {
      run = {
        deployId: evt.deployId,
        domain: '?',
        host: '?',
        startedAt: new Date().toISOString(),
        events: [],
        latestPhase: 'Building',
        latestStep: '',
        latestMessage: '',
        isPastPonr: false,
        isTerminal: false,
        success: null,
        hooks: [],
      }
      runs.value.set(evt.deployId, run)
    }
    if (!run.hooks) run.hooks = []
    run.hooks.push({
      evt: evt.evt, type: evt.type, label: evt.label,
      ok: evt.ok, durationMs: evt.durationMs, error: evt.error,
    })
    runs.value = new Map(runs.value)
  }

  async function refreshStatus(domain: string, deployId: string): Promise<DeployResultDto> {
    const r = await getDeployStatus(domain, deployId)
    const existing = runs.value.get(deployId)
    if (existing) {
      existing.success = r.success
      existing.completedAt = r.completedAt ?? undefined
      existing.latestPhase = r.finalPhase
      runs.value = new Map(runs.value)
    }
    return r
  }

  async function refreshHistory(domain: string, limit = 50): Promise<DeployHistoryEntryDto[]> {
    const r = await getDeployHistory(domain, limit)
    historyByDomain.value.set(domain, r.entries)
    historyByDomain.value = new Map(historyByDomain.value)
    return r.entries
  }

  async function rollback(domain: string, deployId: string): Promise<void> {
    await apiRollback(domain, deployId)
    // Mark the source run as rolled-back locally; the new rollback run will
    // surface via SSE as a separate deployId.
    const r = runs.value.get(deployId)
    if (r) {
      r.latestPhase = 'RolledBack'
      runs.value = new Map(runs.value)
    }
  }

  async function cancel(domain: string, deployId: string): Promise<void> {
    await apiCancel(domain, deployId)
    // Don't optimistically mutate — the daemon emits a Cancelled SSE event
    // that handleSseEvent will pick up. The 409 path (past PONR) bubbles
    // through as a thrown Error which the caller (DeployConfirmModal etc.)
    // surfaces to the user.
  }

  /**
   * Phase 6.10 — fan out a deploy across N hosts of the same site.
   * Returns the groupId so the caller can navigate to the Groups
   * sub-tab and watch progress live via the SSE-driven history table.
   */
  async function startGroupDeploy(domain: string, hosts: string[]): Promise<string> {
    const result = await apiStartGroup(domain, hosts)
    return result.groupId
  }

  function openDrawerFor(deployId: string): void {
    if (runs.value.has(deployId)) {
      activeRunId.value = deployId
    }
  }

  /**
   * Phase 6.9 — drill into a HISTORICAL run (no live SSE replay). Used by
   * DeployGroupHistoryTable's per-host expand row click. Fetches the
   * terminal status snapshot, synthesises a DeployRunState with empty
   * events (the run has already ended; we only show its final state),
   * and opens the drawer. If the run is already in the local map (live
   * deploy in progress), we skip the fetch and just activate it.
   */
  async function loadAndOpenHistorical(
    domain: string,
    deployId: string,
    host: string,
  ): Promise<void> {
    if (runs.value.has(deployId)) {
      activeRunId.value = deployId
      return
    }
    try {
      const status = await getDeployStatus(domain, deployId)
      const synthetic: DeployRunState = {
        deployId,
        domain,
        host,
        startedAt: status.startedAt,
        completedAt: status.completedAt ?? undefined,
        events: [],
        latestPhase: status.finalPhase,
        latestStep: status.errorMessage ? 'error' : (status.success ? 'deploy_complete' : 'unknown'),
        latestMessage: status.errorMessage ?? (status.success ? 'completed' : 'finished'),
        isPastPonr: false,
        isTerminal: true,
        success: status.success,
        groupId: status.groupId ?? null,
      }
      const next = new Map(runs.value)
      next.set(deployId, synthetic)
      runs.value = next
      activeRunId.value = deployId
    } catch (err) {
      // Surface to caller — the drawer simply won't open.
      throw err instanceof Error ? err : new Error(String(err))
    }
  }

  function closeDrawer(): void {
    activeRunId.value = null
  }

  return {
    runs,
    activeRun,
    activeRunId,
    isDrawerOpen,
    historyByDomain,
    startDeploy,
    handleSseEvent,
    handleHookEvent,
    refreshStatus,
    refreshHistory,
    rollback,
    cancel,
    openDrawerFor,
    loadAndOpenHistorical,
    startGroupDeploy,
    closeDrawer,
  }
})

/**
 * Per-deploy state held in the Pinia store. Mirrors the server-side
 * deploy_runs row plus the rolling list of SSE events for the StepWaterfall
 * component.
 */
export interface DeployRunState {
  deployId: string
  domain: string
  host: string
  startedAt: string
  completedAt?: string
  events: DeployEventDto[]
  latestPhase: string
  latestStep: string
  latestMessage: string
  isPastPonr: boolean
  isTerminal: boolean
  success: boolean | null
  /** Phase 6.19a — populated by loadAndOpenHistorical when the daemon row carries it. */
  groupId?: string | null
  /** Phase 7.5+++ — accumulated deploy:hook SSE events for visual feedback in the drawer. */
  hooks?: DeployHookEvent[]
}

/** Phase 7.5+++ — one hook fire as broadcast by the daemon. */
export interface DeployHookEvent {
  evt: string         // pre_deploy | post_fetch | pre_switch | post_switch | on_failure
  type: string        // shell | http | php
  label: string
  ok: boolean
  durationMs: number
  error?: string
}
