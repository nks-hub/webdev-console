<template>
  <div class="cmd-center">
    <div v-if="hosts.length === 0" class="cmd-empty">
      <el-empty description="No deploy hosts configured" />
    </div>
    <div v-else class="cmd-grid">
      <HostCard
        v-for="host in hosts"
        :key="host"
        :host="host"
        :last-deploy="lastDeployByHost.get(host) ?? null"
        @deploy="onDeploy(host)"
        @rollback="onRollback(host)"
      />
    </div>

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
</style>
