<template>
  <div class="cmd-center">
    <div v-if="hosts.length === 0" class="cmd-empty">
      <el-empty description="No deploy hosts configured" />
    </div>
    <template v-else>
      <!-- Group multi-select toolbar — only renders when 2+ hosts exist
           since a single-host site can't be a group target. -->
      <div v-if="hosts.length >= 2" class="cmd-group-toolbar" role="toolbar"
           aria-label="Multi-host group deploy">
        <el-checkbox
          :model-value="allSelected"
          :indeterminate="someSelected && !allSelected"
          aria-label="Select all hosts for group deploy"
          @update:model-value="(v: any) => onToggleAll(!!v)"
        >
          Select hosts to deploy as group
        </el-checkbox>
        <span v-if="selectedHosts.size > 0" class="cmd-group-count muted">
          {{ selectedHosts.size }} selected
        </span>
        <el-button
          v-if="selectedHosts.size >= 2"
          type="primary"
          size="small"
          @click="onDeployGroup"
        >
          Deploy {{ selectedHosts.size }} hosts as group
        </el-button>
      </div>

      <div class="cmd-grid">
        <HostCard
          v-for="host in hosts"
          :key="host"
          :host="host"
          :last-deploy="lastDeployByHost.get(host) ?? null"
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
      @confirmed="confirmDeploy"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'
import HostCard from './HostCard.vue'
import DiffSummary, { type DeployDiff } from './DiffSummary.vue'
import PreflightChecklist, { type PreflightCheck } from './PreflightChecklist.vue'
import DeployHistoryTable from './DeployHistoryTable.vue'
import DeployConfirmModal from './DeployConfirmModal.vue'
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
    ElMessage.warning('Select at least 2 hosts for a group deploy')
    return
  }
  try {
    await ElMessageBox.confirm(
      `Deploy ${props.domain} to ${hosts.length} hosts atomically?\n\n` +
        `Hosts: ${hosts.join(', ')}\n\n` +
        `If any host fails after the symlink switch, all committed hosts will be rolled back automatically.`,
      'Confirm group deploy',
      { type: 'warning', confirmButtonText: 'Deploy as group', cancelButtonText: 'Cancel' },
    )
  } catch { return }
  try {
    const groupId = await deployStore.startGroupDeploy(props.domain, hosts)
    ElMessage.success(`Group deploy started — see Groups tab (id: ${groupId.slice(0, 8)})`)
    selectedHosts.value = new Set()
  } catch (e) {
    ElMessage.error((e as Error).message || 'Group deploy failed to start')
  }
}

const lastDeployByHost = computed<Map<string, DeployHistoryEntryDto>>(() => {
  const m = new Map<string, DeployHistoryEntryDto>()
  for (const h of props.history) {
    if (!m.has(h.host)) m.set(h.host, h)
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
    ElMessage.warning('No deploy to rollback')
    return
  }
  try {
    await ElMessageBox.confirm(
      `Roll back ${props.domain} → ${host} to the previous release?`,
      'Confirm rollback',
      { type: 'warning', confirmButtonText: 'Yes, rollback', cancelButtonText: 'Cancel' },
    )
  } catch { return }
  try {
    await deployStore.rollback(props.domain, last.deployId)
    ElMessage.success('Rollback dispatched')
  } catch (e) {
    ElMessage.error((e as Error).message || 'Rollback failed')
  }
}

async function onHistoryRollback(entry: DeployHistoryEntryDto): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `Roll back to release from ${new Date(entry.startedAt).toLocaleString()}?`,
      'Confirm rollback',
      { type: 'warning' },
    )
  } catch { return }
  try {
    await deployStore.rollback(props.domain, entry.deployId)
    ElMessage.success('Rollback dispatched')
  } catch (e) {
    ElMessage.error((e as Error).message || 'Rollback failed')
  }
}

async function confirmDeploy(): Promise<void> {
  try {
    await deployStore.startDeploy(props.domain, confirmHost.value)
    ElMessage.success(`Deploy started — watch the drawer`)
  } catch (e) {
    ElMessage.error((e as Error).message || 'Deploy failed to start')
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
