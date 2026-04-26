<template>
  <div class="group-history">
    <div class="group-history-header">
      <h4 class="group-history-title">Deploy group history</h4>
      <el-button link size="small" :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> Refresh
      </el-button>
    </div>

    <el-empty
      v-if="!entries.length && !loading"
      :image-size="80"
      description="No multi-host deploy groups recorded yet"
    />

    <el-table
      v-else
      :data="entries"
      stripe
      size="small"
      :empty-text="'No history'"
      row-key="id"
      :expand-row-keys="expandedKeys"
      @expand-change="onExpandChange"
    >
      <!-- Expandable row shows the per-host deploys table -->
      <el-table-column type="expand">
        <template #default="{ row }">
          <div class="group-children">
            <div class="group-children-title">Per-host deploys</div>
            <el-table
              :data="hostRows(row)"
              size="small"
              :empty-text="'No per-host data recorded yet'"
            >
              <el-table-column prop="host" label="Host" width="160" />
              <el-table-column prop="deployId" label="Deploy ID">
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
                    aria-label="Open this per-host deploy in the drawer"
                    @click="onOpenHostRun(hr.host, hr.deployId)"
                  >
                    Open
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
            <p v-if="row.errorMessage" class="group-error" role="status">
              <strong>Error:</strong> {{ row.errorMessage }}
            </p>
          </div>
        </template>
      </el-table-column>

      <el-table-column prop="startedAt" label="When" width="170">
        <template #default="{ row }">
          <span class="mono">{{ formatDate(row.startedAt) }}</span>
        </template>
      </el-table-column>

      <el-table-column prop="hosts" label="Hosts" min-width="180">
        <template #default="{ row }">
          <span class="hosts-summary">
            {{ row.hosts.length }} host{{ row.hosts.length === 1 ? '' : 's' }}:
            <code class="mono">{{ row.hosts.join(', ') }}</code>
          </span>
        </template>
      </el-table-column>

      <el-table-column prop="phase" label="Phase" width="180">
        <template #default="{ row }">
          <el-tag
            :type="phaseTagType(row.phase)"
            size="small"
            effect="plain"
            :aria-label="`Phase ${row.phase}`"
          >
            <el-icon class="phase-icon" aria-hidden="true">
              <component :is="phaseIcon(row.phase)" />
            </el-icon>
            {{ row.phase }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column prop="triggeredBy" label="By" width="80">
        <template #default="{ row }">
          <el-tag size="small" effect="plain" type="info">{{ row.triggeredBy }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column label="Duration" width="110">
        <template #default="{ row }">
          <span v-if="row.completedAt" class="mono">{{ formatDuration(row) }}</span>
          <span v-else class="muted">running</span>
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
            aria-label="Cascade rollback every committed host in this group"
            @click="onRollbackGroup(row)"
          >
            <el-icon><RefreshRight /></el-icon> Rollback
          </el-button>
          <el-button
            v-if="canReplay(row)"
            size="small"
            link
            type="primary"
            :loading="replayingId === row.id"
            :disabled="(replayingId !== null && replayingId !== row.id) || rollingBackId !== null"
            aria-label="Re-run this group with the same hosts list"
            @click="onReplayGroup(row)"
          >
            <el-icon><Refresh /></el-icon> Replay
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  Refresh,
  CircleCheck,
  Warning,
  CircleClose,
  Loading,
  RefreshRight,
} from '@element-plus/icons-vue'
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

async function onReplayGroup(row: DeployGroupEntry): Promise<void> {
  if (replayingId.value !== null) return
  try {
    await ElMessageBox.confirm(
      `Re-run this group with the same hosts (${row.hosts.length})?\n\n` +
        `Hosts: ${row.hosts.join(', ')}\n` +
        `Original group: ${row.id.slice(0, 8)}…\n\n` +
        `A new groupId will be minted; the original row stays unchanged ` +
        `as audit history.`,
      'Confirm group replay',
      { type: 'warning', confirmButtonText: 'Replay group', cancelButtonText: 'Cancel' },
    )
  } catch { return }

  replayingId.value = row.id
  try {
    const result = await startDeployGroup(props.domain, row.hosts)
    ElMessage.success(`Replay started — new group ${result.groupId.slice(0, 8)}…`)
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
  justify-content: space-between;
  margin-bottom: 8px;
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
