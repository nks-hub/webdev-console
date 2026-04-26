<template>
  <div class="group-history">
    <div class="group-history-header">
      <h4 class="group-history-title">{{ t('deploy.groupHistory.title') }}</h4>
      <!-- Phase 6.18a — group success-rate at a glance. all_succeeded /
           total of terminal groups (excludes in-flight from denominator
           since they haven't decided yet). Hidden when no terminal
           groups exist. -->
      <div v-if="successRate !== null" class="group-success-rate" role="status">
        <el-tag :type="rateTagType(successRate)" size="small" effect="plain">
          {{ t('deploy.groupHistory.successRate', { ok: successCount, total: terminalCount }) }}
          <span class="rate-pct">({{ Math.round(successRate * 100) }}%)</span>
        </el-tag>
      </div>
      <el-button link size="small" :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> {{ t('deploy.groupHistory.refresh') }}
      </el-button>
    </div>

    <el-empty
      v-if="!entries.length && !loading"
      :image-size="80"
      :description="t('deploy.groupHistory.noGroups')"
    />

    <el-table
      v-else
      :data="entries"
      stripe
      size="small"
      :empty-text="t('deploy.groupHistory.noHistory')"
      row-key="id"
      :expand-row-keys="expandedKeys"
      @expand-change="onExpandChange"
    >
      <!-- Expandable row shows the per-host deploys table -->
      <el-table-column type="expand">
        <template #default="{ row }">
          <div class="group-children">
            <div class="group-children-title">{{ t('deploy.groupHistory.perHost') }}</div>
            <el-table
              :data="hostRows(row)"
              size="small"
              :empty-text="t('deploy.groupHistory.noPerHost')"
            >
              <el-table-column prop="host" :label="t('deploy.groupHistory.col.host')" width="160" />
              <el-table-column prop="deployId" :label="t('deploy.groupHistory.col.deployId')">
                <template #default="{ row: hr }">
                  <code v-if="hr.deployId" class="mono">{{ hr.deployId.slice(0, 8) }}…</code>
                  <span v-else class="muted">—</span>
                </template>
              </el-table-column>
              <el-table-column label="" width="100" align="right">
                <template #default="{ row: hr }">
                  <el-button
                    v-if="hr.deployId"
                    size="small"
                    link
                    type="primary"
                    :loading="openingDeployId === hr.deployId"
                    :aria-label="t('deploy.groupHistory.openAria')"
                    @click="onOpenHostRun(hr.host, hr.deployId)"
                  >
                    {{ t('deploy.groupHistory.open') }}
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
            <p v-if="row.errorMessage" class="group-error" role="status">
              <strong>{{ t('deploy.groupHistory.errorLabel') }}</strong> {{ row.errorMessage }}
            </p>
          </div>
        </template>
      </el-table-column>

      <el-table-column prop="startedAt" :label="t('deploy.groupHistory.col.when')" width="170">
        <template #default="{ row }">
          <span class="mono">{{ formatDate(row.startedAt) }}</span>
        </template>
      </el-table-column>

      <el-table-column prop="hosts" :label="t('deploy.groupHistory.col.hosts')" min-width="180">
        <template #default="{ row }">
          <span class="hosts-summary">
            {{ t('deploy.groupHistory.hostsCount', row.hosts.length, { n: row.hosts.length }) }}:
            <code class="mono">{{ row.hosts.join(', ') }}</code>
          </span>
        </template>
      </el-table-column>

      <el-table-column prop="phase" :label="t('deploy.groupHistory.col.phase')" width="180">
        <template #default="{ row }">
          <el-tag
            :type="phaseTagType(row.phase)"
            size="small"
            effect="plain"
            :aria-label="t('deploy.groupHistory.phaseAria', { phase: row.phase })"
          >
            <el-icon class="phase-icon" aria-hidden="true">
              <component :is="phaseIcon(row.phase)" />
            </el-icon>
            {{ row.phase }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column prop="triggeredBy" :label="t('deploy.groupHistory.col.by')" width="80">
        <template #default="{ row }">
          <el-tag size="small" effect="plain" type="info">{{ row.triggeredBy }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column :label="t('deploy.groupHistory.col.duration')" width="110">
        <template #default="{ row }">
          <span v-if="row.completedAt" class="mono">{{ formatDuration(row) }}</span>
          <span v-else class="muted">{{ t('deploy.groupHistory.running') }}</span>
        </template>
      </el-table-column>

      <el-table-column label="" width="170" align="right">
        <template #default="{ row }">
          <el-button
            v-if="canRollback(row)"
            size="small"
            link
            type="warning"
            :loading="rollingBackId === row.id"
            :disabled="(rollingBackId !== null && rollingBackId !== row.id) || replayingId !== null"
            :aria-label="t('deploy.groupHistory.rollbackAria')"
            @click="onRollbackGroup(row)"
          >
            <el-icon><RefreshRight /></el-icon> {{ t('deploy.groupHistory.rollback') }}
          </el-button>
          <el-button
            v-if="canReplay(row)"
            size="small"
            link
            type="primary"
            :loading="replayingId === row.id"
            :disabled="(replayingId !== null && replayingId !== row.id) || rollingBackId !== null"
            :aria-label="t('deploy.groupHistory.replayAria')"
            @click="onReplayGroup(row)"
          >
            <el-icon><Refresh /></el-icon> {{ t('deploy.groupHistory.replay') }}
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Refresh,
  CircleCheck,
  Warning,
  CircleClose,
  Loading,
  RefreshRight,
} from '@element-plus/icons-vue'

const { t } = useI18n()
import {
  fetchDeployGroups,
  rollbackDeployGroup,
  startDeployGroup,
  type DeployGroupEntry,
  type DeployGroupPhase,
} from '../../api/deploy'
import { subscribeEventsMap } from '../../api/daemon'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'

interface Props {
  domain: string
  /**
   * Auto-refresh interval in seconds. 0 = no auto refresh (default).
   * The page can opt-in if it wants to poll while a deploy is mid-flight.
   */
  refreshSeconds?: number
}

const props = withDefaults(defineProps<Props>(), { refreshSeconds: 0 })

const entries = ref<DeployGroupEntry[]>([])
const loading = ref(false)
const expandedKeys = ref<string[]>([])
const openingDeployId = ref<string | null>(null)
const rollingBackId = ref<string | null>(null)
const replayingId = ref<string | null>(null)
const deployStore = useDeployStore()

let timer: ReturnType<typeof setInterval> | null = null
let unsubscribeSse: (() => void) | null = null
// Pending refresh debounce — many deploy:group-event events fire per second
// during a fan-out. Rather than refetching the list per event, we coalesce
// into one refresh after a quiet 500 ms window.
let refreshDebounceTimer: ReturnType<typeof setTimeout> | null = null

/**
 * Phase 6.18a — group success-rate metric over TERMINAL groups only.
 * In-flight groups (initializing/preflight/deploying/awaiting_all_soak/
 * rolling_back_all) don't count in the denominator since their outcome
 * isn't decided yet. Returns null when zero terminal groups present so
 * the header tag hides cleanly on a fresh project.
 */
const TERMINAL_PHASES: DeployGroupPhase[] = [
  'all_succeeded', 'partial_failure', 'rolled_back', 'group_failed',
]
const terminalCount = computed(() =>
  entries.value.filter((e) => TERMINAL_PHASES.includes(e.phase)).length)
const successCount = computed(() =>
  entries.value.filter((e) => e.phase === 'all_succeeded').length)
const successRate = computed<number | null>(() => {
  if (terminalCount.value === 0) return null
  return successCount.value / terminalCount.value
})
function rateTagType(rate: number): 'success' | 'warning' | 'danger' | 'info' {
  if (rate >= 0.9) return 'success'
  if (rate >= 0.7) return 'warning'
  if (rate > 0) return 'danger'
  return 'info'
}

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const result = await fetchDeployGroups(props.domain, 50)
    entries.value = result.entries
  } finally {
    loading.value = false
  }
}

function scheduleRefresh(): void {
  if (refreshDebounceTimer !== null) clearTimeout(refreshDebounceTimer)
  refreshDebounceTimer = setTimeout(() => {
    refreshDebounceTimer = null
    refresh()
  }, 500)
}

function onExpandChange(row: DeployGroupEntry, expanded: DeployGroupEntry[]): void {
  expandedKeys.value = expanded.map((r) => r.id)
}

/**
 * Phase 6.13a — group rollback eligibility. We allow rollback whenever
 * the group has at least one committed per-host deploy (i.e. some host
 * crossed PONR and produced a release that could be rewound). Terminal
 * states still get the button so operators can roll back a "succeeded"
 * group if a downstream issue is discovered later.
 *
 * Excluded states: initializing/preflight/deploying — fan-out hasn't
 * completed yet, rollback would race the in-flight deploys. Also
 * `rolled_back` (already done) and `group_failed` (no commits to undo).
 */
function canRollback(row: DeployGroupEntry): boolean {
  if (row.phase === 'initializing' || row.phase === 'preflight' ||
      row.phase === 'deploying' || row.phase === 'rolling_back_all' ||
      row.phase === 'rolled_back' || row.phase === 'group_failed') {
    return false
  }
  return Object.keys(row.hostDeployIds ?? {}).length > 0
}

/**
 * Phase 6.14a — re-run a failed group with the same hosts list.
 * Useful when a transient infra failure (network blip, full disk on
 * one host) torpedoed the original fan-out — replay starts fresh
 * with all original hosts. Shown only on terminal-failed states so
 * a successful group doesn't get an accidental "replay" click.
 */
function canReplay(row: DeployGroupEntry): boolean {
  if (row.phase !== 'group_failed' && row.phase !== 'partial_failure' &&
      row.phase !== 'rolled_back') {
    return false
  }
  return row.hosts.length > 0
}

/**
 * Phase 6.15b — return the host names that did NOT complete successfully.
 * "Failed" = anything in hostStatuses NOT equal to 'completed'. Hosts
 * with no entry in hostStatuses are also treated as failed (the run
 * never started or its row vanished — still want to retry them).
 */
function failedHosts(row: DeployGroupEntry): string[] {
  const statuses = row.hostStatuses ?? {}
  return row.hosts.filter((h) => (statuses[h] ?? 'unknown') !== 'completed')
}

async function onReplayGroup(row: DeployGroupEntry): Promise<void> {
  if (replayingId.value !== null) return

  const failed = failedHosts(row)
  const allHosts = row.hosts
  const hasMixedOutcome = failed.length > 0 && failed.length < allHosts.length

  // When some hosts succeeded and others failed (typical partial_failure
  // case), offer the operator the choice of replaying ONLY the failed
  // ones (faster, doesn't disturb successful releases) or all hosts
  // (full re-run for consistency).
  let hostsToReplay: string[]
  if (hasMixedOutcome) {
    let choice: string
    try {
      choice = await ElMessageBox.confirm(
        `Original group had mixed outcomes:\n` +
          `  Succeeded: ${allHosts.length - failed.length} (${allHosts.filter((h) => !failed.includes(h)).join(', ')})\n` +
          `  Failed: ${failed.length} (${failed.join(', ')})\n\n` +
          `Replay only the FAILED hosts (recommended) or ALL hosts?`,
        'Replay subset?',
        {
          confirmButtonText: `Failed only (${failed.length})`,
          cancelButtonText: `All hosts (${allHosts.length})`,
          distinguishCancelAndClose: true,
          type: 'warning',
        },
      )
    } catch (e) {
      // ElMessageBox throws "cancel" for the cancel button and "close"
      // when dismissed via X/Esc. We treat cancel as "all hosts" choice;
      // close = abort the replay entirely.
      const action = (e as Error).message === 'close' || e === 'close' ? 'close' : 'cancel'
      if (action === 'close') return
      choice = 'cancel'
    }
    hostsToReplay = choice === 'confirm' ? failed : allHosts
  } else {
    try {
      await ElMessageBox.confirm(
        `Re-run this group with the same hosts (${allHosts.length})?\n\n` +
          `Hosts: ${allHosts.join(', ')}\n` +
          `Original group: ${row.id.slice(0, 8)}…\n\n` +
          `A new groupId will be minted; the original row stays unchanged ` +
          `as audit history.`,
        'Confirm group replay',
        { type: 'warning', confirmButtonText: 'Replay group', cancelButtonText: 'Cancel' },
      )
    } catch { return }
    hostsToReplay = allHosts
  }

  replayingId.value = row.id
  try {
    const result = await startDeployGroup(props.domain, hostsToReplay)
    ElMessage.success(
      `Replay started (${hostsToReplay.length} hosts) — new group ${result.groupId.slice(0, 8)}…`,
    )
    await refresh()
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Replay failed to start: ${msg}`)
  } finally {
    replayingId.value = null
  }
}

async function onRollbackGroup(row: DeployGroupEntry): Promise<void> {
  if (rollingBackId.value !== null) return
  const hostCount = Object.keys(row.hostDeployIds ?? {}).length
  try {
    await ElMessageBox.confirm(
      `Cascade-rollback every committed host in this group?\n\n` +
        `Hosts to roll back: ${hostCount}\n` +
        `Group: ${row.id.slice(0, 8)}…\n\n` +
        `Rollback rewinds the release symlink only — pre-deploy DB ` +
        `snapshots are NOT auto-restored (operator-driven via Settings tab).`,
      'Confirm group rollback',
      { type: 'warning', confirmButtonText: 'Roll back group', cancelButtonText: 'Cancel' },
    )
  } catch { return }

  rollingBackId.value = row.id
  try {
    await rollbackDeployGroup(props.domain, row.id)
    ElMessage.success(`Group rollback dispatched (${hostCount} hosts)`)
    // Refresh to reflect rolling_back_all → rolled_back/partial_failure
    await refresh()
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Group rollback failed: ${msg}`)
  } finally {
    rollingBackId.value = null
  }
}

async function onOpenHostRun(host: string, deployId: string): Promise<void> {
  if (openingDeployId.value !== null) return
  openingDeployId.value = deployId
  try {
    await deployStore.loadAndOpenHistorical(props.domain, deployId, host)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Could not open run: ${msg}`)
  } finally {
    openingDeployId.value = null
  }
}

function hostRows(row: DeployGroupEntry): Array<{ host: string; deployId: string | null }> {
  // Combine the static hosts list with the deployIds map: not every host
  // necessarily has a deployId (e.g. preflight failed before the per-host
  // deploy started). Render every host with deployId or null.
  return row.hosts.map((host) => ({
    host,
    deployId: row.hostDeployIds?.[host] ?? null,
  }))
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString()
}

function formatDuration(row: DeployGroupEntry): string {
  if (!row.completedAt) return ''
  const ms = new Date(row.completedAt).getTime() - new Date(row.startedAt).getTime()
  if (ms < 1000) return `${ms} ms`
  const sec = Math.round(ms / 1000)
  if (sec < 60) return `${sec}s`
  return `${Math.floor(sec / 60)}m ${sec % 60}s`
}

const phaseTagType = (p: DeployGroupPhase) =>
  p === 'all_succeeded' ? 'success'
    : p === 'group_failed' || p === 'partial_failure' ? 'danger'
    : p === 'rolled_back' ? 'warning'
    : 'info'

const phaseIcon = (p: DeployGroupPhase) =>
  p === 'all_succeeded' ? CircleCheck
    : p === 'group_failed' ? CircleClose
    : p === 'partial_failure' ? Warning
    : p === 'rolled_back' || p === 'rolling_back_all' ? RefreshRight
    : Loading

onMounted(() => {
  refresh()
  if (props.refreshSeconds > 0) {
    timer = setInterval(refresh, props.refreshSeconds * 1000)
  }
  // Live updates via the deploy:group-event SSE channel. Every per-host
  // event AND every group-level state transition triggers a debounced
  // refresh — the list reads come from SQLite and are cheap, so simple
  // re-fetch beats incremental patch logic for now.
  unsubscribeSse = subscribeEventsMap({
    'deploy:group-event': (data) => {
      // Filter to this site only — the SSE channel is global. Without the
      // domain check, opening Site A would refresh whenever Site B's
      // group fires, wasting cycles.
      const evt = data as { domain?: string; groupId?: string }
      // Group events don't carry domain directly; the join key is
      // groupId → check our current entries (cheap O(N) over <=50 rows).
      // If the groupId matches one we know about, refresh.
      if (evt.groupId && entries.value.some((e) => e.id === evt.groupId)) {
        scheduleRefresh()
        return
      }
      // Unknown groupId — could be a brand-new group on THIS site (we
      // haven't seen it yet) OR another site's. Only way to know is to
      // refetch and let the server filter; refresh anyway. This is the
      // edge case that justifies the debounce.
      scheduleRefresh()
    },
  })
})

onBeforeUnmount(() => {
  if (timer !== null) clearInterval(timer)
  if (unsubscribeSse !== null) unsubscribeSse()
  if (refreshDebounceTimer !== null) clearTimeout(refreshDebounceTimer)
})

watch(() => props.domain, () => refresh())
</script>

<style scoped>
.group-history-header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
}
.group-history-header .group-history-title {
  margin-right: auto;
}
.rate-pct {
  font-variant-numeric: tabular-nums;
  margin-left: 4px;
}
.group-success-rate {
  display: inline-flex;
  align-items: center;
}
.group-history-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
}
.mono {
  font-family: var(--el-font-family-monospace, monospace);
  font-size: 12px;
}
.muted {
  color: var(--el-text-color-secondary);
}
.hosts-summary {
  font-size: 13px;
}
.phase-icon {
  margin-right: 4px;
  vertical-align: -2px;
}
.group-children {
  padding: 8px 24px 12px;
  background: var(--el-fill-color-light);
}
.group-children-title {
  font-weight: 600;
  font-size: 12px;
  margin-bottom: 6px;
  color: var(--el-text-color-regular);
}
.group-error {
  margin-top: 8px;
  padding: 6px 8px;
  background: var(--el-color-danger-light-9, #fef0f0);
  border-left: 3px solid var(--el-color-danger);
  border-radius: 3px;
  font-size: 12px;
}
</style>
