<template>
  <el-card class="service-card" :class="[`status-${service.status}`]" shadow="hover">
    <template #header>
      <div class="card-header">
        <span class="card-name">{{ service.name }}</span>
        <el-tag :type="tagType" size="small" effect="dark">{{ service.status }}</el-tag>
      </div>
    </template>

    <div class="card-stats">
      <div class="stat">
        <span class="label">CPU</span>
        <span class="value">{{ service.cpuPercent.toFixed(1) }}%</span>
      </div>
      <div class="stat">
        <span class="label">RAM</span>
        <span class="value">{{ formatMem(service.memoryBytes) }}</span>
      </div>
      <div class="stat">
        <span class="label">Uptime</span>
        <span class="value">{{ formatUptime(service.uptimeSeconds) }}</span>
      </div>
    </div>

    <div class="card-actions">
      <el-button
        size="small"
        type="success"
        :disabled="service.status === 'running' || busy"
        :loading="busy && pendingAction === 'start'"
        @click="act('start')"
      >Start</el-button>
      <el-button
        size="small"
        type="danger"
        :disabled="service.status === 'stopped' || busy"
        :loading="busy && pendingAction === 'stop'"
        @click="act('stop')"
      >Stop</el-button>
      <el-button
        size="small"
        :disabled="service.status === 'stopped' || busy"
        :loading="busy && pendingAction === 'restart'"
        @click="act('restart')"
      >Restart</el-button>
    </div>
  </el-card>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ServiceInfo } from '../../api/types'
import { useServicesStore } from '../../stores/services'

const props = defineProps<{ service: ServiceInfo }>()
const servicesStore = useServicesStore()
const pendingAction = ref<'start' | 'stop' | 'restart' | null>(null)

const busy = computed(() => servicesStore.isBusy(props.service.id))

const tagType = computed(() => {
  const map: Record<string, '' | 'success' | 'warning' | 'danger' | 'info'> = {
    running: 'success',
    stopped: 'info',
    starting: 'warning',
    error: 'danger',
  }
  return map[props.service.status] ?? 'info'
})

async function act(action: 'start' | 'stop' | 'restart') {
  pendingAction.value = action
  try {
    await servicesStore[action](props.service.id)
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
