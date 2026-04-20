<template>
  <div class="svc-card" :class="[`svc-${statusText}`]">
    <!-- Header -->
    <div class="svc-header">
      <div class="svc-name-row">
        <span class="svc-status-dot" :class="`dot-${statusText}`" />
        <span class="svc-name">{{ service.displayName || service.name || service.id }}</span>
      </div>
      <el-tag :type="tagType" size="small" effect="dark" class="svc-badge">
        {{ statusText }}
      </el-tag>
    </div>

    <!-- Metrics (only when running) -->
    <div v-if="isRunning" class="svc-metrics">
      <!-- CPU bar -->
      <div class="metric-row">
        <span class="metric-label">CPU</span>
        <div class="metric-bar-wrap">
          <div class="metric-bar" :style="{ width: cpuBarWidth }">
            <div class="metric-bar-fill cpu-fill" :style="{ width: '100%' }" />
          </div>
        </div>
        <span class="metric-value">{{ cpuText }}</span>
      </div>

      <!-- RAM bar -->
      <div class="metric-row">
        <span class="metric-label">RAM</span>
        <div class="metric-bar-wrap">
          <div class="metric-bar" style="width: 100%">
            <div class="metric-bar-fill ram-fill" :style="{ width: ramBarWidth }" />
          </div>
        </div>
        <span class="metric-value">{{ formatMem(service.memoryBytes ?? 0) }}</span>
      </div>

      <!-- Uptime + PID -->
      <div class="svc-meta">
        <span class="meta-item">
          <span class="meta-label">uptime</span>
          <span class="meta-val">{{ formatUptime(service.uptimeSeconds ?? 0) }}</span>
        </span>
        <span class="meta-item" v-if="service.pid">
          <span class="meta-label">pid</span>
          <span class="meta-val">{{ service.pid }}</span>
        </span>
        <span class="meta-item" v-if="service.version">
          <span class="meta-label">v</span>
          <span class="meta-val">{{ service.version }}</span>
        </span>
      </div>
    </div>

    <div v-else class="svc-offline">
      <span v-if="statusText === 'crashed'" class="offline-crashed">Process crashed</span>
      <span v-else-if="statusText === 'starting'" class="offline-starting">Starting...</span>
      <span v-else class="offline-stopped">Service not running</span>
      <span class="meta-item" v-if="service.version" style="margin-top: 4px;">
        <span class="meta-label">v</span>
        <span class="meta-val">{{ service.version }}</span>
      </span>
    </div>

    <!-- Actions -->
    <div class="svc-actions">
      <el-button
        size="small"
        type="success"
        plain
        :disabled="isRunning || busy"
        :loading="busy && pendingAction === 'start'"
        @click="act('start')"
      >
        Start
      </el-button>
      <el-button
        size="small"
        type="danger"
        plain
        :disabled="isStopped || busy"
        :loading="busy && pendingAction === 'stop'"
        @click="act('stop')"
      >
        Stop
      </el-button>
      <el-button
        size="small"
        plain
        :disabled="isStopped || busy"
        :loading="busy && pendingAction === 'restart'"
        @click="act('restart')"
      >
        Restart
      </el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage, ElNotification } from 'element-plus'
import { useServicesStore } from '../../stores/services'
import type { ServiceInfo } from '../../api/types'

const props = defineProps<{ service: ServiceInfo }>()
const servicesStore = useServicesStore()
const pendingAction = ref<'start' | 'stop' | 'restart' | null>(null)

const busy = computed(() => servicesStore.isBusy(props.service.id))

const stateLabels: Record<number, string> = {
  0: 'stopped', 1: 'starting', 2: 'running', 3: 'stopping', 4: 'crashed', 5: 'disabled',
}
const statusText = computed(() => {
  const st = props.service.state
  return (st != null ? stateLabels[st] : undefined) ?? props.service.status ?? 'unknown'
})
const isRunning = computed(() => props.service.state === 2)
const isStopped = computed(() => props.service.state === 0 || props.service.state === 5)

const tagType = computed((): '' | 'success' | 'warning' | 'danger' | 'info' => {
  const map: Record<string, '' | 'success' | 'warning' | 'danger' | 'info'> = {
    running: 'success', stopped: 'info', starting: 'warning',
    stopping: 'warning', crashed: 'danger', disabled: 'info',
  }
  return map[statusText.value] ?? 'info'
})

const cpuPercent = computed(() => Math.min(props.service.cpuPercent ?? 0, 100))
const cpuText = computed(() => `${cpuPercent.value.toFixed(1)}%`)
const cpuBarWidth = computed(() => `${cpuPercent.value}%`)

// RAM bar: cap display at 500 MB for visual scale
const RAM_MAX = 500 * 1024 * 1024
const ramBarWidth = computed(() => {
  const pct = Math.min((props.service.memoryBytes ?? 0) / RAM_MAX, 1) * 100
  return `${pct}%`
})

async function act(action: 'start' | 'stop' | 'restart') {
  pendingAction.value = action
  try {
    await servicesStore[action](props.service.id)
    const svcName = props.service.displayName || props.service.id
    ElMessage.success(`${svcName}: ${action} succeeded`)
  } catch (err: unknown) {
    const svcName = props.service.displayName || props.service.id
    const message = err instanceof Error ? err.message : String(err)
    ElNotification({
      title: `${svcName} — ${action} failed`,
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
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

function formatUptime(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  return `${h}h ${m}m`
}
</script>

<style scoped>
.svc-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 14px;
  transition: border-color 0.12s, background 0.12s;
  border-left-width: 4px;
  border-left-style: solid;
}

/* Flat redesign: hover just brightens the border, no drop shadow */
.svc-card:hover {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-surface-2);
}

.svc-running  { border-left-color: var(--wdc-status-running); }
.svc-stopped  { border-left-color: var(--wdc-status-stopped); }
.svc-crashed  { border-left-color: var(--wdc-status-error); }
.svc-starting { border-left-color: var(--wdc-status-starting); }
.svc-stopping { border-left-color: var(--wdc-status-starting); }
.svc-disabled { border-left-color: var(--wdc-border); }
.svc-unknown  { border-left-color: var(--wdc-border); }

.svc-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.svc-name-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.svc-status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

/* Flat: no glow, rely on saturated token colors for visibility */
.dot-running  { background: var(--wdc-status-running); }
.dot-stopped  { background: var(--wdc-status-stopped); }
.dot-crashed  { background: var(--wdc-status-error); }
.dot-starting { background: var(--wdc-status-starting); animation: pulse 1s infinite; }
.dot-stopping { background: var(--wdc-status-starting); animation: pulse 1s infinite; }
.dot-disabled { background: var(--el-border-color); }
.dot-unknown  { background: var(--el-border-color); }

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}

.svc-name {
  font-size: 1rem;
  font-weight: 600;
  color: var(--wdc-text);
}

.svc-badge { flex-shrink: 0; }

.svc-metrics {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.metric-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.metric-label {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
  width: 32px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  flex-shrink: 0;
}

.metric-bar-wrap {
  flex: 1;
  height: 8px;
  background: var(--wdc-elevated);
  border-radius: 4px;
  overflow: hidden;
}

.metric-bar {
  height: 100%;
  border-radius: 3px;
  overflow: hidden;
}

.metric-bar-fill {
  height: 100%;
  border-radius: 3px;
  transition: width 0.4s ease;
}

.cpu-fill { background: var(--wdc-accent); opacity: 0.7; }
.ram-fill { background: var(--wdc-status-running); opacity: 0.6; }

.metric-value {
  font-size: 0.75rem;
  font-family: monospace;
  color: var(--el-text-color-regular);
  width: 52px;
  text-align: right;
  flex-shrink: 0;
}

.svc-meta {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 4px;
}

.meta-item {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.meta-label {
  font-size: 0.68rem;
  color: var(--el-text-color-secondary);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.meta-val {
  font-size: 0.72rem;
  font-family: monospace;
  color: var(--el-text-color-regular);
}

.svc-offline {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-height: 42px;
  justify-content: center;
}

.svc-actions {
  display: flex;
  gap: 8px;
}

.svc-actions .el-button {
  flex: 1;
  font-size: 0.82rem;
}

.offline-crashed  { font-size: 0.78rem; color: var(--wdc-status-error); }
.offline-starting { font-size: 0.78rem; color: var(--wdc-status-starting); }
.offline-stopped  { font-size: 0.78rem; color: var(--wdc-text-3); }
</style>
