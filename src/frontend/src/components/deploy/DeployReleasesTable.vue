<template>
  <!--
    Phase 7.5+++ Deploy detail redesign — Releases sub-tab.
    Pivots flat history rows by releaseId so an operator can see
    "release 20260426_182331 was deployed to production at 18:23 then
    to staging 4 minutes later" as a single row instead of scrolling
    a chronological history.

    Rendered on top of the same /api/.../history payload — no new
    endpoint needed. Works because LocalDeployBackend writes a UTC
    yyyyMMdd_HHmmss releaseId per deploy, so deploys started in the
    same second on different hosts share the id.
  -->
  <div class="releases-pane">
    <div class="releases-toolbar">
      <h3 class="releases-title">{{ t('deploy.releases.title') }}</h3>
      <span class="muted">
        {{ t('deploy.releases.summary', { releases: groups.length, deploys: entries.length }) }}
      </span>
      <el-button size="small" plain class="refresh-btn" @click="$emit('refresh')">
        {{ t('deploy.releases.refresh') }}
      </el-button>
    </div>

    <div v-if="groups.length === 0" class="releases-empty">
      <el-empty :description="t('deploy.releases.empty')" :image-size="80" />
    </div>

    <el-table
      v-else
      :data="groups"
      :empty-text="t('deploy.releases.empty')"
      :aria-label="t('deploy.releases.title')"
      class="releases-table"
      row-key="releaseId"
    >
      <el-table-column type="expand">
        <template #default="{ row }">
          <div class="releases-detail">
            <el-table :data="row.entries" size="small" class="releases-detail-table">
              <el-table-column :label="t('deploy.releases.col.host')" prop="host" min-width="120">
                <template #default="{ row: e }">
                  <span class="mono">{{ e.host }}</span>
                </template>
              </el-table-column>
              <el-table-column :label="t('deploy.releases.col.startedAt')" min-width="160">
                <template #default="{ row: e }">
                  {{ new Date(e.startedAt).toLocaleString() }}
                </template>
              </el-table-column>
              <el-table-column :label="t('deploy.releases.col.duration')" width="100">
                <template #default="{ row: e }">
                  {{ formatDuration(e) }}
                </template>
              </el-table-column>
              <el-table-column :label="t('deploy.releases.col.phase')" width="120">
                <template #default="{ row: e }">
                  <el-tag :type="phaseTagType(e.finalPhase)" size="small" effect="plain">
                    {{ e.finalPhase }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column :label="t('deploy.releases.col.triggeredBy')" width="100">
                <template #default="{ row: e }">
                  <span v-if="e.triggeredBy" class="muted mono">{{ e.triggeredBy }}</span>
                  <span v-else class="muted">—</span>
                </template>
              </el-table-column>
              <el-table-column :label="t('deploy.releases.col.actions')" width="140" align="right">
                <template #default="{ row: e }">
                  <!-- Phase 7.5+++ — rollback to a SPECIFIC historical
                       release. Only meaningful when the release isn't the
                       active one + has a releaseId we can pin to the
                       releases/ dir. The button confirms before firing. -->
                  <el-button
                    size="small"
                    type="warning"
                    plain
                    :disabled="!e.releaseId"
                    @click="onRollbackToRelease(e)"
                  >
                    {{ t('deploy.releases.rollbackToBtn') }}
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </template>
      </el-table-column>

      <el-table-column :label="t('deploy.releases.col.releaseId')" min-width="180">
        <template #default="{ row }">
          <span class="mono">{{ row.releaseId }}</span>
        </template>
      </el-table-column>

      <el-table-column :label="t('deploy.releases.col.firstSeen')" min-width="160">
        <template #default="{ row }">
          {{ new Date(row.firstSeen).toLocaleString() }}
        </template>
      </el-table-column>

      <el-table-column :label="t('deploy.releases.col.hosts')" min-width="160">
        <template #default="{ row }">
          <el-tag
            v-for="h in row.hostList"
            :key="h.host"
            :type="h.failed ? 'danger' : 'success'"
            size="small"
            effect="plain"
            class="host-pill"
          >
            {{ h.host }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column :label="t('deploy.releases.col.deploys')" width="90" align="center">
        <template #default="{ row }">
          <el-badge :value="row.entries.length" type="info" />
        </template>
      </el-table-column>

      <el-table-column :label="t('deploy.releases.col.successRate')" width="120" align="center">
        <template #default="{ row }">
          <span :class="successClass(row.successRate)">{{ row.successRate }}%</span>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { DeployHistoryEntryDto } from '../../api/deploy'
import { rollbackToRelease } from '../../api/deploy'

const { t } = useI18n()
const props = defineProps<{ entries: DeployHistoryEntryDto[]; domain: string }>()
const emit = defineEmits<{ refresh: [] }>()

async function onRollbackToRelease(entry: DeployHistoryEntryDto): Promise<void> {
  if (!entry.releaseId) return
  try {
    await ElMessageBox.confirm(
      t('deploy.releases.rollbackToConfirmMessage', {
        host: entry.host,
        releaseId: entry.releaseId,
      }),
      t('deploy.releases.rollbackToConfirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('deploy.releases.rollbackToBtn'),
      },
    )
  } catch { return }
  try {
    const r = await rollbackToRelease(props.domain, entry.host, entry.releaseId)
    if (r.error) {
      ElMessage.error(r.error)
    } else {
      ElMessage.success(t('deploy.releases.rollbackToSuccess', { releaseId: entry.releaseId }))
      emit('refresh')
    }
  } catch (e) {
    ElMessage.error((e as Error).message || 'Rollback failed')
  }
}

interface ReleaseHostBadge { host: string; failed: boolean }
interface ReleaseGroup {
  releaseId: string
  firstSeen: string
  entries: DeployHistoryEntryDto[]
  hostList: ReleaseHostBadge[]
  successRate: number
}

/**
 * Group history entries by releaseId. Untagged rows (older deploys
 * before the releaseId column existed, or rollbacks) bucket under
 * a synthesised "(no release id)" key so they remain visible.
 *
 * Returns groups newest-first by firstSeen so the most recent release
 * appears at the top — matches operator mental model when triaging.
 */
const groups = computed<ReleaseGroup[]>(() => {
  const m = new Map<string, DeployHistoryEntryDto[]>()
  for (const e of props.entries) {
    const key = e.releaseId ?? '(no release id)'
    const arr = m.get(key) ?? []
    arr.push(e)
    m.set(key, arr)
  }
  return Array.from(m.entries()).map(([releaseId, arr]) => {
    const sorted = [...arr].sort((a, b) =>
      new Date(a.startedAt).getTime() - new Date(b.startedAt).getTime())
    const firstSeen = sorted[0]?.startedAt ?? ''
    // Per-host aggregation — last status per host wins so the badge
    // shows the most recent outcome, not the first attempt.
    const hostState = new Map<string, boolean>()
    for (const e of sorted) {
      hostState.set(e.host, e.finalPhase !== 'Done')
    }
    const hostList: ReleaseHostBadge[] = Array.from(hostState.entries())
      .map(([host, failed]) => ({ host, failed }))
      .sort((a, b) => a.host.localeCompare(b.host))
    const successes = arr.filter((e) => e.finalPhase === 'Done').length
    const successRate = arr.length === 0 ? 0 : Math.round((successes / arr.length) * 100)
    return { releaseId, firstSeen, entries: sorted, hostList, successRate }
  }).sort((a, b) =>
    new Date(b.firstSeen).getTime() - new Date(a.firstSeen).getTime())
})

function phaseTagType(phase: string): 'success' | 'warning' | 'danger' | 'info' {
  if (phase === 'Done') return 'success'
  if (phase === 'Failed' || phase === 'RolledBack') return 'danger'
  if (phase === 'AwaitingSoak' || phase === 'Switching') return 'warning'
  return 'info'
}

function successClass(pct: number): string {
  if (pct >= 95) return 'rate-good'
  if (pct >= 70) return 'rate-warn'
  return 'rate-bad'
}

function formatDuration(e: DeployHistoryEntryDto): string {
  if (!e.completedAt) return '—'
  try {
    const ms = new Date(e.completedAt).getTime() - new Date(e.startedAt).getTime()
    if (ms < 1000) return `${ms} ms`
    const s = Math.round(ms / 1000)
    if (s < 60) return `${s} s`
    return `${Math.floor(s / 60)}m ${s % 60}s`
  } catch { return '—' }
}
</script>

<style scoped>
.releases-pane { display: flex; flex-direction: column; gap: 16px; }
.releases-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
}
.releases-title { margin: 0; font-size: 16px; }
.refresh-btn { margin-left: auto; }
.releases-empty { padding: 32px 0; }
.releases-table { width: 100%; }
.releases-detail { padding: 8px 32px; background: var(--el-fill-color-lighter); }
.releases-detail-table { width: 100%; }
.host-pill { margin-right: 4px; margin-bottom: 2px; }
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.rate-good { color: var(--el-color-success); font-weight: 600; }
.rate-warn { color: var(--el-color-warning); font-weight: 600; }
.rate-bad  { color: var(--el-color-danger);  font-weight: 600; }
</style>
