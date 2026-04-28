<template>
  <div class="cmd-center">
    <!-- Phase 7.5+++ — top-of-tab status banner: aggregate counts,
         success rate, last-deploy age, per-host strip. Auto-hidden when
         no history. -->
    <DeployStatusBanner :entries="history" />
    <!-- Phase 7.5+++ — quick-deploy bar above the host grid. Only renders
         when hosts exist; lets the operator fire a deploy at one host or
         all of them without going through the confirm modal. -->
    <DeployQuickBar v-if="hosts.length > 0" :domain="domain" :hosts="hosts" />
    <div v-if="hosts.length === 0" class="cmd-empty">
      <el-empty :description="t('deploy.commandCenter.noHosts')" />
    </div>
    <template v-else>
      <!-- Group multi-select toolbar — only renders when 2+ hosts exist
           since a single-host site can't be a group target. -->
      <div v-if="hosts.length >= 2" class="cmd-group-toolbar" role="toolbar"
           :aria-label="t('deploy.commandCenter.groupToolbarAria')">
        <el-checkbox
          :model-value="allSelected"
          :indeterminate="someSelected && !allSelected"
          :aria-label="t('deploy.commandCenter.selectAllAria')"
          @update:model-value="(v: any) => onToggleAll(!!v)"
        >
          {{ t('deploy.commandCenter.selectAllLabel') }}
        </el-checkbox>
        <span v-if="selectedHosts.size > 0" class="cmd-group-count muted">
          {{ t('deploy.commandCenter.selectedCount', { n: selectedHosts.size }) }}
        </span>
        <el-button
          v-if="selectedHosts.size >= 2"
          type="primary"
          size="small"
          @click="onDeployGroup"
        >
          {{ t('deploy.commandCenter.deployGroupBtn', { n: selectedHosts.size }) }}
        </el-button>
      </div>

      <div class="cmd-grid">
        <HostCard
          v-for="host in hosts"
          :key="host"
          :host="host"
          :last-deploy="lastDeployByHost.get(host) ?? null"
          :active-run="activeRunByHost.get(host) ?? null"
          :selectable="hosts.length >= 2"
          :selected="selectedHosts.has(host)"
          @deploy="onDeploy(host)"
          @rollback="onRollback(host)"
          @toggle-select="(v: boolean) => toggleHost(host, v)"
        />
      </div>
    </template>

    <DiffSummary :diff="diff" />

    <PreflightChecklist :checks="checks" :loading="checksLoading" @rerun="$emit('rerun-preflight')" />

    <DeployHistoryTable :entries="history" @refresh="$emit('refresh-history')" @rollback="onHistoryRollback" />

    <DeployConfirmModal
      v-model="confirmModalOpen"
      :domain="domain"
      :host="confirmHost"
      :last-deploy="lastDeployByHost.get(confirmHost) ?? null"
      @confirmed="confirmDeploy($event)"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'

const { t } = useI18n()
import HostCard from './HostCard.vue'
import DiffSummary, { type DeployDiff } from './DiffSummary.vue'
import PreflightChecklist, { type PreflightCheck } from './PreflightChecklist.vue'
import DeployHistoryTable from './DeployHistoryTable.vue'
import DeployConfirmModal from './DeployConfirmModal.vue'
import DeployStatusBanner from './DeployStatusBanner.vue'
import DeployQuickBar from './DeployQuickBar.vue'
import type { DeployHistoryEntryDto } from '../../api/deploy'

const props = defineProps<{
  domain: string
  hosts: string[]
  history: DeployHistoryEntryDto[]
  diff: DeployDiff | null
  checks: PreflightCheck[]
  checksLoading?: boolean
}>()

defineEmits<{
  'rerun-preflight': []
  'refresh-history': []
}>()

const deployStore = useDeployStore()

const confirmModalOpen = ref(false)
const confirmHost = ref('')

// Phase 6.10 — multi-select state for group deploy. Lives in this
// component (not the store) since it's purely transient UI state.
const selectedHosts = ref<Set<string>>(new Set())
const allSelected = computed(() =>
  props.hosts.length > 0 && selectedHosts.value.size === props.hosts.length)
const someSelected = computed(() => selectedHosts.value.size > 0)

function toggleHost(host: string, value: boolean): void {
  const next = new Set(selectedHosts.value)
  if (value) next.add(host)
  else next.delete(host)
  selectedHosts.value = next
}

function onToggleAll(value: boolean): void {
  selectedHosts.value = value ? new Set(props.hosts) : new Set()
}

async function onDeployGroup(): Promise<void> {
  const hosts = Array.from(selectedHosts.value)
  if (hosts.length < 2) {
    ElMessage.warning(t('deploy.commandCenter.groupNeedTwo'))
    return
  }
  try {
    await ElMessageBox.confirm(
      t('deploy.commandCenter.groupConfirmMessage', {
        domain: props.domain, n: hosts.length, hosts: hosts.join(', '),
      }),
      t('deploy.commandCenter.groupConfirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('deploy.commandCenter.groupConfirmBtn'),
        cancelButtonText: t('deploy.commandCenter.cancel'),
      },
    )
  } catch { return }
  try {
    const groupId = await deployStore.startGroupDeploy(props.domain, hosts)
    ElMessage.success(t('deploy.commandCenter.groupStarted', { id: groupId.slice(0, 8) }))
    selectedHosts.value = new Set()
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.commandCenter.groupFailed'))
  }
}

const lastDeployByHost = computed<Map<string, DeployHistoryEntryDto>>(() => {
  const m = new Map<string, DeployHistoryEntryDto>()
  for (const h of props.history) {
    if (!m.has(h.host)) m.set(h.host, h)
  }
  return m
})

/**
 * Phase 6.17b — pick the most recent NON-TERMINAL run per host from the
 * deploy store so HostCard can render a live phase tag inline. Filters
 * to runs for the current domain so we don't surface another site's
 * deploy if the store still holds a run from a previous navigation.
 *
 * Reactive over deployStore.runs because Pinia's `runs` ref triggers
 * recomputation whenever events arrive via SSE → handleSseEvent.
 */
const activeRunByHost = computed<Map<string, ReturnType<typeof Array.from<unknown>>[number] | null>>(() => {
  const m = new Map<string, ReturnType<typeof Array.from<unknown>>[number] | null>()
  for (const run of deployStore.runs.values()) {
    if (run.domain !== props.domain) continue
    if (run.isTerminal) continue
    const existing = m.get(run.host)
    // Prefer the newest startedAt when multiple non-terminal runs exist
    // for the same host (rare — usually only one in flight at a time).
    if (!existing || new Date(run.startedAt).getTime() > new Date((existing as { startedAt: string }).startedAt).getTime()) {
      m.set(run.host, run)
    }
  }
  return m
})

function onDeploy(host: string): void {
  confirmHost.value = host
  confirmModalOpen.value = true
}

async function onRollback(host: string): Promise<void> {
  const last = lastDeployByHost.value.get(host)
  if (!last) {
    ElMessage.warning(t('deploy.commandCenter.noPreviousDeploy'))
    return
  }
  try {
    await ElMessageBox.confirm(
      t('deploy.commandCenter.rollbackHostMessage', { domain: props.domain, host }),
      t('deploy.commandCenter.rollbackConfirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('deploy.commandCenter.rollbackBtn'),
        cancelButtonText: t('deploy.commandCenter.cancel'),
      },
    )
  } catch { return }
  try {
    await deployStore.rollback(props.domain, last.deployId)
    ElMessage.success(t('deploy.commandCenter.rollbackDispatched'))
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.commandCenter.rollbackFailed'))
  }
}

async function onHistoryRollback(entry: DeployHistoryEntryDto): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('deploy.commandCenter.rollbackHistoryMessage', { when: new Date(entry.startedAt).toLocaleString() }),
      t('deploy.commandCenter.rollbackConfirmTitle'),
      { type: 'warning' },
    )
  } catch { return }
  try {
    await deployStore.rollback(props.domain, entry.deployId)
    ElMessage.success(t('deploy.commandCenter.rollbackDispatched'))
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.commandCenter.rollbackFailed'))
  }
}

async function confirmDeploy(opts: { snapshot: boolean }): Promise<void> {
  try {
    await deployStore.startDeploy(props.domain, confirmHost.value, {
      snapshot: opts.snapshot ? { include: true, retentionDays: 30 } : undefined,
    })
    ElMessage.success(opts.snapshot
      ? t('deploy.commandCenter.deployStartedSnap')
      : t('deploy.commandCenter.deployStarted'))
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.commandCenter.deployFailed'))
  }
}
</script>

<style scoped>
.cmd-center { display: flex; flex-direction: column; gap: 24px; container-type: inline-size; }
.cmd-grid {
  display: grid;
  gap: 16px;
  grid-template-columns: 1fr;
}
@container (min-width: 720px) { .cmd-grid { grid-template-columns: repeat(2, 1fr); } }
@container (min-width: 1100px) { .cmd-grid { grid-template-columns: repeat(3, 1fr); } }
.cmd-empty { padding: 32px 0; }
.cmd-group-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 12px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
  margin-bottom: 12px;
}
.cmd-group-count {
  font-size: 13px;
  margin-left: auto;
}
.muted { color: var(--el-text-color-secondary); }
</style>
