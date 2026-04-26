<template>
  <el-drawer
    :model-value="open"
    direction="rtl"
    :modal="false"
    :show-close="false"
    :close-on-press-escape="true"
    :close-on-click-modal="false"
    size="40%"
    :destroy-on-close="false"
    @update:model-value="(v: boolean) => { if (!v) deployStore.closeDrawer() }"
  >
    <template #header>
      <div class="drawer-head">
        <div>
          <h3 class="drawer-title">
            Deploy
            <small v-if="run">{{ run.domain }} → {{ run.host }}</small>
          </h3>
          <div class="drawer-subline mono" aria-live="polite">
            {{ run ? statusLine : 'No active deploy' }}
          </div>
        </div>
        <div class="drawer-actions">
          <HealthBadge v-if="run" :state="healthState" />
          <el-button text @click="deployStore.closeDrawer()" :aria-label="'Close drawer'">
            <el-icon><Close /></el-icon>
          </el-button>
        </div>
      </div>
    </template>

    <div v-if="run" class="drawer-body">
      <StepWaterfall
        :steps="waterfallSteps"
        :current-idx="currentIdx"
        :point-of-no-return-idx="ponrIdx"
        :show-ponr-alert="run.isPastPonr"
      />

      <el-collapse class="drawer-collapse">
        <el-collapse-item title="Raw output">
          <RawOutputPane :lines="rawLines" />
        </el-collapse-item>
      </el-collapse>

      <div class="drawer-footer">
        <el-button
          v-if="!run.isTerminal && !run.isPastPonr"
          type="warning"
          plain
          @click="onCancel"
        >
          Cancel
        </el-button>
        <el-button
          v-if="run.isPastPonr && !run.isTerminal"
          type="danger"
          plain
          disabled
          :title="'Cannot cancel after point of no return — use rollback'"
        >
          Cancel disabled (past PONR)
        </el-button>
        <el-button v-if="run.isTerminal && run.success === false" type="primary" @click="$emit('retry', run)">
          Retry
        </el-button>
      </div>
    </div>
    <div v-else class="drawer-empty">
      <el-empty description="No deploy in flight" />
    </div>
  </el-drawer>
</template>

<script setup lang="ts">
/**
 * Persistent right-side drawer mounted at app shell. Survives router
 * navigation so a long deploy keeps showing progress while the user
 * browses other sites. Per v2 ui-ux: NOT modal (so user can keep working),
 * focus NOT trapped (drawer state mirrors aria-live updates).
 */
import { computed } from 'vue'
import { Close } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'
import StepWaterfall, { type WaterfallStep } from './StepWaterfall.vue'
import RawOutputPane from './RawOutputPane.vue'
import HealthBadge from './HealthBadge.vue'

defineEmits<{
  retry: [run: NonNullable<ReturnType<typeof useDeployStore>['activeRun']>]
}>()

const deployStore = useDeployStore()
const run = computed(() => deployStore.activeRun)
const open = computed(() => deployStore.isDrawerOpen)

// Convert the rolling event list into a 16-row waterfall. Each known nksdeploy
// step maps to one row; unmapped events stay logged in the raw pane only.
const KNOWN_STEPS: { key: string; label: string }[] = [
  { key: 'git_pull', label: 'git pull' },
  { key: 'composer_install', label: 'composer install' },
  { key: 'npm_build', label: 'npm/vite build' },
  { key: 'shared_links', label: 'shared symlinks' },
  { key: 'writable', label: 'writable dirs' },
  { key: 'cache_clear', label: 'cache clear' },
  { key: 'doctrine_proxy', label: 'doctrine proxies' },
  { key: 'schema_update', label: 'schema update' },
  { key: 'di_warmup', label: 'DI warmup' },
  { key: 'latte_warmup', label: 'latte warmup' },
  { key: 'symlink_switch', label: 'symlink switch (PONR)' },
  { key: 'fpm_reload', label: 'php-fpm reload' },
  { key: 'opcache_clear', label: 'opcache clear' },
  { key: 'health_check', label: 'health check' },
  { key: 'cleanup_releases', label: 'cleanup releases' },
  { key: 'database_snapshot', label: 'db snapshot' },
]

const ponrIdx = computed(() => KNOWN_STEPS.findIndex(s => s.key === 'symlink_switch'))

const stepStates = computed<Map<string, WaterfallStep['state']>>(() => {
  const m = new Map<string, WaterfallStep['state']>()
  if (!run.value) return m
  for (const ev of run.value.events) {
    const k = ev.step.replace(/[:\-/]/g, '_')
    if (ev.step === 'deploy_complete') continue
    // Step lifecycle inferred from message + final terminal phase.
    if (ev.message.includes('completed') || ev.message.includes('OK')) m.set(k, 'done')
    else if (ev.message.includes('skipped')) m.set(k, 'skipped')
    else if (ev.message.includes('failed') || ev.message.includes('error')) m.set(k, 'failed')
    else m.set(k, 'running')
  }
  return m
})

const waterfallSteps = computed<WaterfallStep[]>(() =>
  KNOWN_STEPS.map(s => ({
    label: s.label,
    state: stepStates.value.get(s.key) ?? 'pending',
  })))

const currentIdx = computed(() => {
  // First running, else index of last completed.
  const runningIdx = waterfallSteps.value.findIndex(s => s.state === 'running')
  if (runningIdx >= 0) return runningIdx
  let last = -1
  waterfallSteps.value.forEach((s, i) => { if (s.state === 'done' || s.state === 'failed') last = i })
  return last
})

const rawLines = computed(() =>
  run.value?.events.map(e => `[${e.timestamp.slice(11, 19)}] ${e.step}: ${e.message}`) ?? [])

const statusLine = computed(() => {
  if (!run.value) return ''
  if (run.value.isTerminal) {
    return run.value.success ? 'Completed successfully' : `Failed: ${run.value.latestMessage}`
  }
  return `${run.value.latestPhase} — ${run.value.latestStep} ${run.value.latestMessage}`
})

const healthState = computed<'healthy' | 'degraded' | 'down' | 'unknown'>(() => {
  if (!run.value) return 'unknown'
  if (run.value.isTerminal) return run.value.success ? 'healthy' : 'down'
  if (run.value.isPastPonr) return 'degraded'
  return 'unknown'
})

async function onCancel(): Promise<void> {
  if (!run.value) return
  try {
    await ElMessageBox.confirm(
      `Cancel deploy of ${run.value.domain}?`,
      'Cancel deploy',
      { type: 'warning', confirmButtonText: 'Yes, cancel', cancelButtonText: 'Keep running' },
    )
  } catch { return }

  try {
    await deployStore.cancel(run.value.domain, run.value.deployId)
    ElMessage.success('Cancellation requested')
  } catch (e) {
    ElMessage.error((e as Error).message || 'Cancel failed')
  }
}
</script>

<style scoped>
.drawer-head {
  display: flex; align-items: flex-start; justify-content: space-between; gap: 12px;
}
.drawer-title { margin: 0; font-size: 1.05rem; }
.drawer-title small { color: var(--el-text-color-secondary); margin-left: 8px; font-weight: normal; font-size: 0.85em; }
.drawer-subline { color: var(--el-text-color-secondary); font-size: 12px; margin-top: 2px; }
.drawer-actions { display: flex; align-items: center; gap: 8px; }

.drawer-body { display: flex; flex-direction: column; gap: 16px; }
.drawer-collapse { border: none; }
.drawer-footer { display: flex; gap: 8px; justify-content: flex-end; }
.drawer-empty { padding: 24px 0; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
</style>
