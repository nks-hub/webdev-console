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
            {{ t('deploy.drawer.title') }}
            <small v-if="run">{{ run.domain }} → {{ run.host }}</small>
          </h3>
          <div class="drawer-subline mono" aria-live="polite">
            {{ run ? statusLine : t('deploy.drawer.noActive') }}
          </div>
        </div>
        <div class="drawer-actions">
          <HealthBadge v-if="run" :state="healthState" />
          <el-button text @click="deployStore.closeDrawer()" :aria-label="t('deploy.drawer.closeAria')">
            <el-icon><Close /></el-icon>
          </el-button>
        </div>
      </div>
    </template>

    <div v-if="run" class="drawer-body">
      <!-- Phase 6.19a — link to the parent group when this run was part
           of a multi-host fan-out. Shows above the waterfall so it's
           visible at a glance; clicking navigates to the per-site
           Deploy tab → Groups sub-tab. The drawer stays open so the
           operator can flip between drawer + group context. -->
      <el-alert
        v-if="run.groupId"
        type="info"
        :closable="false"
        show-icon
        class="drawer-group-link"
      >
        <template #title>
          {{ t('deploy.drawer.groupLink') }}
          <code class="mono">{{ run.groupId.slice(0, 8) }}…</code>
        </template>
        <el-button link type="primary" size="small" @click="goToGroups">
          {{ t('deploy.drawer.viewGroup') }}
        </el-button>
      </el-alert>

      <StepWaterfall
        :steps="waterfallSteps"
        :current-idx="currentIdx"
        :point-of-no-return-idx="ponrIdx"
        :show-ponr-alert="run.isPastPonr"
      />

      <!-- Phase 7.5+++ — hook execution panel. Only renders when at
           least one hook fired (so deploys without configured hooks
           keep the drawer compact). One row per fire with status icon,
           phase tag, type tag, label, duration. Failed hooks expand
           the error message inline. -->
      <div v-if="run.hooks && run.hooks.length > 0" class="hook-panel">
        <div class="hook-panel-header">
          <span>{{ t('deploy.drawer.hooksTitle') }}</span>
          <el-tag size="small" effect="plain">{{ run.hooks.length }}</el-tag>
        </div>
        <ul class="hook-list">
          <li v-for="(h, i) in run.hooks" :key="i" class="hook-row"
              :class="{ 'hook-failed': !h.ok }">
            <span class="hook-status" :class="{ ok: h.ok, fail: !h.ok }">
              {{ h.ok ? '✓' : '✗' }}
            </span>
            <el-tag :type="hookEvtTagType(h.evt)" size="small" effect="dark" class="hook-evt">
              {{ h.evt }}
            </el-tag>
            <el-tag size="small" effect="plain" class="hook-type">{{ h.type }}</el-tag>
            <span class="hook-label">{{ h.label }}</span>
            <span class="hook-duration muted">{{ h.durationMs }} ms</span>
            <div v-if="!h.ok && h.error" class="hook-error mono">{{ h.error }}</div>
          </li>
        </ul>
      </div>

      <el-collapse class="drawer-collapse">
        <el-collapse-item :title="t('deploy.drawer.rawOutput')">
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
          {{ t('deploy.drawer.cancel') }}
        </el-button>
        <el-button
          v-if="run.isPastPonr && !run.isTerminal"
          type="danger"
          plain
          disabled
          :title="t('deploy.drawer.cancelDisabledTitle')"
        >
          {{ t('deploy.drawer.cancelDisabled') }}
        </el-button>
        <el-button v-if="run.isTerminal && run.success === false" type="primary" @click="$emit('retry', run)">
          {{ t('deploy.drawer.retry') }}
        </el-button>
      </div>
    </div>
    <div v-else class="drawer-empty">
      <el-empty :description="t('deploy.drawer.noDeploy')" />
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
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Close } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'

const { t } = useI18n()
const router = useRouter()
import StepWaterfall, { type WaterfallStep } from './StepWaterfall.vue'
import RawOutputPane from './RawOutputPane.vue'
import HealthBadge from './HealthBadge.vue'

defineEmits<{
  retry: [run: NonNullable<ReturnType<typeof useDeployStore>['activeRun']>]
}>()

const deployStore = useDeployStore()
const run = computed(() => deployStore.activeRun)

// Phase 7.5+++ — color hook tags by lifecycle phase, mirroring the
// settings Hooks tab. Helps the operator scan a long hook list and
// see which stage each one fired at without reading the label.
function hookEvtTagType(evt: string): 'success' | 'warning' | 'danger' | 'info' | 'primary' {
  if (evt === 'pre_deploy' || evt === 'post_fetch') return 'info'
  if (evt === 'pre_switch' || evt === 'post_switch') return 'primary'
  if (evt === 'on_failure' || evt === 'on_rollback') return 'danger'
  return 'info'
}
const open = computed(() => deployStore.isDrawerOpen)

// Convert the rolling event list into a 16-row waterfall. Each known nksdeploy
// step maps to one row; unmapped events stay logged in the raw pane only.
// Ordering matches the deploy lifecycle so the waterfall reads top-down.
// pre_deploy_backup runs FIRST when Snapshot.Include=true (Phase 6.2),
// before any code touches the remote — keeping it at the top makes the
// snapshot/code/health phases visually distinct.
const KNOWN_STEPS: { key: string; label: string }[] = [
  { key: 'pre_deploy_backup', label: 'pre-deploy DB snapshot' },
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
]

const ponrIdx = computed(() => KNOWN_STEPS.findIndex(s => s.key === 'symlink_switch'))

const stepStates = computed<Map<string, WaterfallStep['state']>>(() => {
  const m = new Map<string, WaterfallStep['state']>()
  if (!run.value) return m
  for (const ev of run.value.events) {
    const k = ev.step.replace(/[:\-/]/g, '_')
    if (ev.step === 'deploy_complete') continue
    // Step lifecycle inferred from message + final terminal phase.
    // Order matters: failed/error MUST be checked BEFORE done, because a
    // failure message may also contain "completed" or "ok" as part of the
    // larger context (e.g. "step completed with errors").
    const msg = ev.message.toLowerCase()
    if (msg.includes('failed') || msg.includes('error') || msg.includes('refused')) {
      m.set(k, 'failed')
    } else if (msg.includes('skipped')) {
      m.set(k, 'skipped')
    } else if (
      msg.includes('completed') || msg.includes(' ok') || msg.startsWith('ok') ||
      msg.includes('snapshot ok') || msg.includes('done')
    ) {
      m.set(k, 'done')
    } else {
      m.set(k, 'running')
    }
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
    return run.value.success
      ? t('deploy.drawer.completedSuccess')
      : t('deploy.drawer.failed', { message: run.value.latestMessage })
  }
  return t('deploy.drawer.phaseInline', {
    phase: run.value.latestPhase,
    step: run.value.latestStep,
    message: run.value.latestMessage,
  })
})

const healthState = computed<'healthy' | 'degraded' | 'down' | 'unknown'>(() => {
  if (!run.value) return 'unknown'
  if (run.value.isTerminal) return run.value.success ? 'healthy' : 'down'
  if (run.value.isPastPonr) return 'degraded'
  return 'unknown'
})

/**
 * Phase 6.19a — navigate to the per-site Deploy tab and request the
 * Groups sub-tab. We push to /sites/{domain}/edit with a query hint
 * the SiteEdit page reads on mount to switch its inner tab. Drawer
 * stays open so the operator can flip back/forth between drawer +
 * group context without losing the run state.
 */
function goToGroups(): void {
  if (!run.value) return
  router.push({
    path: `/sites/${encodeURIComponent(run.value.domain)}/edit`,
    query: { tab: 'deploy', deployTab: 'groups' },
  })
}

async function onCancel(): Promise<void> {
  if (!run.value) return
  try {
    await ElMessageBox.confirm(
      t('deploy.drawer.cancelConfirmMessage', { domain: run.value.domain }),
      t('deploy.drawer.cancelConfirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('deploy.drawer.cancelYes'),
        cancelButtonText: t('deploy.drawer.cancelKeep'),
      },
    )
  } catch { return }

  try {
    await deployStore.cancel(run.value.domain, run.value.deployId)
    ElMessage.success(t('deploy.drawer.cancelToast'))
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.drawer.cancelFailed'))
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

/* Phase 7.5+++ — hook execution panel */
.hook-panel {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 8px 12px;
  background: var(--el-fill-color-lighter);
}
.hook-panel-header {
  display: flex; align-items: center; gap: 8px;
  font-weight: 600; font-size: 13px; margin-bottom: 6px;
}
.hook-list { list-style: none; padding: 0; margin: 0; }
.hook-row {
  display: flex; align-items: center; gap: 8px;
  padding: 4px 0;
  font-size: 12px;
  flex-wrap: wrap;
}
.hook-row.hook-failed { background: var(--el-color-danger-light-9); padding-left: 4px; border-radius: 3px; }
.hook-status { font-weight: 700; width: 14px; text-align: center; }
.hook-status.ok { color: var(--el-color-success); }
.hook-status.fail { color: var(--el-color-danger); }
.hook-evt, .hook-type { flex-shrink: 0; }
.hook-label { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.hook-duration { font-size: 11px; flex-shrink: 0; }
.hook-error {
  flex-basis: 100%; padding-left: 22px;
  font-size: 11px;
  color: var(--el-color-danger);
  word-break: break-all;
}
.muted { color: var(--el-text-color-secondary); }
</style>
