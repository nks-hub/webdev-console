<template>
  <div class="dashboard-page">
    <!-- Page header -->
    <div class="page-header">
      <div class="header-title-block">
        <span class="page-title">Services</span>
        <span class="page-count" v-if="services.length > 0">
          <span class="count-running">{{ runningCount }}</span>
          <span class="count-sep">/</span>
          <span class="count-total">{{ totalCount }}</span>
          <span class="count-label">running</span>
        </span>
      </div>
      <div class="header-actions">
        <el-button
          type="success"
          size="small"
          :loading="startingAll"
          :disabled="allRunning || !daemonStore.connected"
          @click="startAll"
        >
          Start All
        </el-button>
        <el-button
          type="danger"
          size="small"
          :loading="stoppingAll"
          :disabled="noneRunning || !daemonStore.connected"
          @click="stopAll"
        >
          Stop All
        </el-button>
      </div>
    </div>

    <!-- Offline state -->
    <div v-if="!daemonStore.connected" class="offline-banner">
      <el-skeleton :rows="5" animated style="padding: 0 16px;" />
      <el-alert
        type="warning"
        title="Connecting to daemon..."
        description="Waiting for NKS WDC daemon on port 5199"
        :closable="false"
        show-icon
        style="margin: 16px;"
      />
    </div>

    <template v-else>
      <!-- Service list -->
      <div class="service-list">
        <div
          v-for="service in services"
          :key="service.id"
          class="service-row"
          :class="[`row-${statusText(service)}`]"
        >
          <!-- Status dot -->
          <span class="status-dot" :class="`dot-${statusText(service)}`" />

          <!-- Name + version -->
          <div class="svc-identity">
            <span class="svc-name">{{ service.displayName || service.id }}</span>
            <span class="svc-version mono" v-if="service.version">v{{ service.version }}</span>
          </div>

          <!-- Port -->
          <div class="svc-port" v-if="getPort(service)">
            <span class="port-label">Port</span>
            <span class="port-value mono">{{ getPort(service) }}</span>
          </div>

          <!-- CPU/RAM/Uptime when running -->
          <div class="svc-metrics" v-if="isRunning(service)">
            <span class="metric mono">CPU <em>{{ formatCpu(service.cpuPercent) }}</em></span>
            <span class="metric mono">MEM <em>{{ formatMem(service.memoryBytes ?? 0) }}</em></span>
            <span class="metric mono" v-if="service.uptime">UP <em>{{ formatUptime(service.uptime) }}</em></span>
          </div>
          <div class="svc-metrics" v-else>
            <span class="status-label" :class="`status-${statusText(service)}`">{{ statusText(service) }}</span>
          </div>

          <!-- Actions -->
          <div class="svc-actions">
            <el-button
              size="small"
              text
              class="action-btn"
              @click="openLogs(service.id)"
            >
              Logs
            </el-button>
            <el-button
              size="small"
              text
              class="action-btn"
              @click="openConfig(service.id)"
            >
              Config
            </el-button>
            <el-switch
              :model-value="isRunning(service)"
              :loading="servicesStore.isBusy(service.id)"
              :disabled="!daemonStore.connected"
              size="large"
              class="svc-toggle"
              @change="toggleService(service.id, $event)"
            />
          </div>
        </div>

        <!-- Empty state -->
        <el-empty
          v-if="services.length === 0"
          description="No services registered. Check daemon configuration."
          :image-size="64"
          class="empty-state"
        />
      </div>

      <!-- Metrics sparklines -->
      <div class="metrics-section" v-if="daemonStore.cpuHistory.length > 2">
        <div class="metrics-grid">
          <div class="metric-card">
            <div class="metric-card-header">
              <span class="metric-card-title">CPU Usage</span>
              <span class="metric-card-value mono">{{ totalCpu.toFixed(1) }}%</span>
            </div>
            <MetricsChart :data="daemonStore.cpuHistory" color="#6366f1" />
          </div>
          <div class="metric-card">
            <div class="metric-card-header">
              <span class="metric-card-title">Memory</span>
              <span class="metric-card-value mono">{{ totalRamMB }} MB</span>
            </div>
            <MetricsChart :data="daemonStore.ramHistory" color="#22c55e" />
          </div>
        </div>
      </div>
    </template>

    <!-- Logs drawer -->
    <el-drawer
      v-model="logsDrawer.open"
      :title="`Logs — ${logsDrawer.serviceId}`"
      direction="rtl"
      size="520px"
    >
      <div class="log-viewer">
        <div v-if="logsDrawer.loading" class="log-loading">Loading...</div>
        <pre v-else class="log-pre">{{ logsDrawer.content }}</pre>
      </div>
    </el-drawer>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { fetchServiceLogs } from '../../api/daemon'
import { ElMessage, ElNotification } from 'element-plus'
import MetricsChart from '../shared/MetricsChart.vue'

const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()

const startingAll = ref(false)
const stoppingAll = ref(false)

const services = computed(() => daemonStore.services)
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter((s: any) => s.state === 2 || s.status === 'running').length)
const allRunning = computed(() => totalCount.value > 0 && runningCount.value === totalCount.value)
const noneRunning = computed(() => runningCount.value === 0)
const totalCpu = computed(() => services.value.reduce((s, x: any) => s + (x.cpuPercent ?? 0), 0))
const totalRamMB = computed(() => Math.round(services.value.reduce((s, x: any) => s + (x.memoryBytes ?? 0), 0) / 1024 / 1024))

onMounted(() => { void sitesStore.load() })

const stateLabels: Record<number, string> = {
  0: 'stopped', 1: 'starting', 2: 'running', 3: 'stopping', 4: 'crashed', 5: 'disabled',
}

const KNOWN_PORTS: Record<string, number> = {
  apache: 80, mysql: 3306, redis: 6379, mailpit: 8025, php: 9084,
}
function getPort(svc: any): number | null {
  return svc.port || KNOWN_PORTS[svc.id] || null
}

function statusText(service: any): string {
  return stateLabels[service.state] ?? service.status ?? 'unknown'
}

function isRunning(service: any): boolean {
  return service.state === 2 || service.status === 'running'
}

function formatCpu(val: number): string {
  return `${(val ?? 0).toFixed(1)}%`
}

function formatMem(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

function formatUptime(uptime: string | number): string {
  if (typeof uptime === 'number') {
    const s = uptime
    if (s < 60) return `${s}s`
    if (s < 3600) return `${Math.floor(s / 60)}m`
    return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
  }
  // TimeSpan string "HH:MM:SS.xxx"
  const parts = String(uptime).split(':')
  if (parts.length >= 3) {
    const h = parseInt(parts[0]), m = parseInt(parts[1])
    if (h > 0) return `${h}h ${m}m`
    if (m > 0) return `${m}m`
    return `${parseInt(parts[2])}s`
  }
  return String(uptime)
}

async function toggleService(id: string, value: boolean | string | number) {
  const on = Boolean(value)
  const svc = services.value.find((s: any) => s.id === id)
  const name = svc ? (svc.displayName || svc.id) : id
  try {
    if (on) {
      await servicesStore.start(id)
      ElMessage.success(`${name}: started`)
    } else {
      await servicesStore.stop(id)
      ElMessage.success(`${name}: stopped`)
    }
  } catch (err: unknown) {
    const message = err instanceof Error ? err.message : String(err)
    ElNotification({ title: `${name} — action failed`, message, type: 'error', duration: 5000 })
  }
}

async function startAll() {
  startingAll.value = true
  try {
    await Promise.allSettled(
      services.value
        .filter((s: any) => s.state === 0 || s.status === 'stopped')
        .map((s: any) => servicesStore.start(s.id))
    )
  } finally {
    startingAll.value = false
  }
}

async function stopAll() {
  stoppingAll.value = true
  try {
    await Promise.allSettled(
      services.value
        .filter((s: any) => s.state === 2 || s.status === 'running')
        .map((s: any) => servicesStore.stop(s.id))
    )
  } finally {
    stoppingAll.value = false
  }
}

// Logs drawer
const logsDrawer = reactive({
  open: false,
  serviceId: '',
  content: '',
  loading: false,
})

async function openLogs(id: string) {
  logsDrawer.serviceId = id
  logsDrawer.content = ''
  logsDrawer.loading = true
  logsDrawer.open = true
  try {
    const lines = await fetchServiceLogs(id, 300)
    logsDrawer.content = Array.isArray(lines) ? lines.join('\n') : String(lines)
  } catch (err: unknown) {
    logsDrawer.content = err instanceof Error ? err.message : 'Failed to load logs'
  } finally {
    logsDrawer.loading = false
  }
}

function openConfig(id: string) {
  ElMessage.info(`Config for ${id} — coming soon`)
}
</script>

<style scoped>
.dashboard-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

/* ─── Page header ─────────────────────────────────────────────────────────── */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px 12px;
  border-bottom: 1px solid var(--wdc-border);
}

.header-title-block {
  display: flex;
  align-items: baseline;
  gap: 10px;
}

.page-title {
  font-size: 0.95rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}

.page-count {
  display: flex;
  align-items: baseline;
  gap: 3px;
  font-size: 0.78rem;
}

.count-running {
  color: var(--wdc-status-running);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.count-sep {
  color: var(--wdc-text-3);
}

.count-total {
  color: var(--wdc-text-2);
  font-variant-numeric: tabular-nums;
}

.count-label {
  color: var(--wdc-text-3);
  margin-left: 2px;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

/* ─── Offline banner ──────────────────────────────────────────────────────── */
.offline-banner {
  padding: 16px 20px;
}

/* ─── Service list ────────────────────────────────────────────────────────── */
.service-list {
  display: flex;
  flex-direction: column;
}

.service-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 0 20px;
  height: 52px;
  border-bottom: 1px solid var(--wdc-border);
  transition: background 0.1s;
  cursor: default;
}

.service-row:last-child {
  border-bottom: none;
}

.service-row:hover {
  background: var(--wdc-hover);
}

/* ─── Status dot ──────────────────────────────────────────────────────────── */
.status-dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  flex-shrink: 0;
}

.dot-running {
  background: var(--wdc-status-running);
  box-shadow: 0 0 5px var(--wdc-status-running);
}

.dot-stopped {
  background: var(--wdc-status-stopped);
}

.dot-starting,
.dot-stopping {
  background: var(--wdc-status-starting);
  animation: svc-pulse 1s ease-in-out infinite;
}

.dot-crashed {
  background: var(--wdc-status-error);
  box-shadow: 0 0 5px var(--wdc-status-error);
}

.dot-disabled,
.dot-unknown {
  background: var(--wdc-border-strong);
}

@keyframes svc-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}

/* ─── Service identity ────────────────────────────────────────────────────── */
.svc-identity {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
  flex: 1;
}

.svc-name {
  font-size: 0.92rem;
  font-weight: 600;
  color: var(--wdc-text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.svc-version {
  font-size: 0.75rem;
  color: var(--wdc-text-3);
  white-space: nowrap;
  flex-shrink: 0;
}

/* ─── Port ────────────────────────────────────────────────────────────────── */
.svc-port {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
  width: 110px;
}

.port-label {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.port-value {
  font-size: 0.8rem;
  color: var(--wdc-text-2);
}

/* ─── Metrics / status text ───────────────────────────────────────────────── */
.svc-metrics {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-shrink: 0;
  width: 170px;
}

.metric {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  white-space: nowrap;
}

.metric em {
  font-style: normal;
  color: var(--wdc-text-2);
  font-weight: 500;
}

.status-label {
  font-size: 0.72rem;
  font-weight: 500;
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.status-stopped  { color: var(--wdc-text-3); }
.status-starting,
.status-stopping { color: var(--wdc-status-starting); }
.status-crashed  { color: var(--wdc-status-error); }
.status-disabled { color: var(--wdc-text-3); }
.status-unknown  { color: var(--wdc-text-3); }

/* ─── Actions ─────────────────────────────────────────────────────────────── */
.svc-actions {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
  margin-left: auto;
}

.action-btn {
  font-size: 0.75rem !important;
  color: var(--wdc-text-3) !important;
  padding: 0 8px !important;
  height: 26px !important;
  line-height: 26px !important;
}

.action-btn:hover {
  color: var(--wdc-text) !important;
}

.svc-toggle {
  margin-left: 8px;
}

/* ─── Mono util ───────────────────────────────────────────────────────────── */
.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', 'Fira Code', monospace;
}

/* ─── Empty state ─────────────────────────────────────────────────────────── */
.empty-state {
  padding: 48px 0;
}

/* ─── Log drawer ──────────────────────────────────────────────────────────── */
.log-viewer {
  height: 100%;
  overflow: auto;
}

.log-pre {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.75rem;
  line-height: 1.6;
  color: var(--wdc-text-2);
  white-space: pre-wrap;
  word-break: break-all;
}

.log-loading {
  color: var(--wdc-text-3);
  font-size: 0.82rem;
  padding: 16px 0;
}

.metrics-section {
  padding: 0 16px 16px;
}

.metrics-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.metric-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 12px 16px;
}

.metric-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}

.metric-card-title {
  font-size: 0.78rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--wdc-text-2);
}

.metric-card-value {
  font-size: 0.85rem;
  color: var(--wdc-text);
}
</style>
