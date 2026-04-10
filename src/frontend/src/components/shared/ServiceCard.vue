<template>
  <el-card class="service-card" :class="[`status-${statusText}`]" shadow="hover">
    <template #header>
      <div class="card-header">
        <span class="card-name">{{ service.displayName || service.name || service.id }}</span>
        <el-tag :type="tagType" size="small" effect="dark">{{ statusText }}</el-tag>
      </div>
    </template>

    <div v-if="isRunning" class="card-stats">
      <div class="stat">
        <span class="label">CPU</span>
        <span class="value">{{ (service.cpuPercent ?? 0).toFixed(1) }}%</span>
      </div>
      <div class="stat">
        <span class="label">RAM</span>
        <span class="value">{{ formatMem(service.memoryBytes ?? 0) }}</span>
      </div>
      <div class="stat">
        <span class="label">PID</span>
        <span class="value">{{ service.pid ?? '-' }}</span>
      </div>
    </div>
    <div v-else class="card-stats-placeholder">
      <span style="color: var(--el-text-color-secondary); font-size: 0.85rem;">Service not running</span>
    </div>

    <div class="card-actions">
      <el-button size="small" type="success" :disabled="isRunning || busy"
        :loading="busy && pendingAction === 'start'" @click="act('start')">Start</el-button>
      <el-button size="small" type="danger" :disabled="isStopped || busy"
        :loading="busy && pendingAction === 'stop'" @click="act('stop')">Stop</el-button>
      <el-button size="small" :disabled="isStopped || busy"
        :loading="busy && pendingAction === 'restart'" @click="act('restart')">Restart</el-button>
    </div>
  </el-card>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage, ElNotification } from 'element-plus'
import type { ServiceInfo } from '../../api/types'
import { useServicesStore } from '../../stores/services'

const props = defineProps<{ service: any }>()
const servicesStore = useServicesStore()
const pendingAction = ref<'start' | 'stop' | 'restart' | null>(null)

const busy = computed(() => servicesStore.isBusy(props.service.id))

// Map C# ServiceState enum: 0=Stopped, 1=Starting, 2=Running, 3=Stopping, 4=Crashed, 5=Disabled
const stateLabels: Record<number, string> = { 0: 'stopped', 1: 'starting', 2: 'running', 3: 'stopping', 4: 'crashed', 5: 'disabled' }
const statusText = computed(() => stateLabels[props.service.state] ?? props.service.status ?? 'unknown')
const isRunning = computed(() => props.service.state === 2)
const isStopped = computed(() => props.service.state === 0)

const tagType = computed(() => {
  const map: Record<string, '' | 'success' | 'warning' | 'danger' | 'info'> = {
    running: 'success', stopped: 'info', starting: 'warning',
    stopping: 'warning', crashed: 'danger', disabled: 'info',
  }
  return map[statusText.value] ?? 'info'
})

async function act(action: 'start' | 'stop' | 'restart') {
  pendingAction.value = action
  try {
    await servicesStore[action](props.service.id)
    ElMessage.success(`${props.service.name}: ${action} succeeded`)
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err)
    ElNotification({
      title: `${props.service.name} — ${action} failed`,
      message,
      type: 'error',
      duration: 5000,
    })
  } finally {
    pendingAction.value = null
  }
}

function formatMem(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function formatUptime(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`
}
</script>

<style scoped>
.service-card { min-width: 220px; }
.card-header { display: flex; align-items: center; justify-content: space-between; }
.card-name { font-size: 0.9rem; font-weight: 600; }
.card-stats { display: flex; flex-direction: column; gap: 6px; margin-bottom: 14px; }
.stat { display: flex; justify-content: space-between; font-size: 0.82rem; }
.label { color: var(--el-text-color-secondary); }
.value { font-family: monospace; }
.card-actions { display: flex; gap: 6px; }

.status-running { border-top: 2px solid var(--el-color-success); }
.status-stopped { border-top: 2px solid var(--el-color-info); }
.status-error   { border-top: 2px solid var(--el-color-danger); }
.status-starting { border-top: 2px solid var(--el-color-warning); }
</style>
