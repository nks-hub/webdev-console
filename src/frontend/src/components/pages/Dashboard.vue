<template>
  <div class="dashboard-page">
    <!-- Page header -->
    <div class="page-header">
      <div class="header-title-block">
        <span class="page-title">{{ $t('dashboard.title') }}</span>
        <span class="page-subtitle">
          <template v-if="uiMode.isAdvanced">v{{ appVersion }} · </template>{{ daemonStore.connected ? $t('header.connected') : 'Connecting...' }}
        </span>
      </div>
      <div class="header-actions">
        <el-button
          size="small"
          @click="$router.push({ path: '/sites', query: { create: '1' } })"
        >{{ $t('dashboard.newSite') }}</el-button>
        <el-button
          v-if="uiMode.isAdvanced"
          type="success"
          size="small"
          :loading="startingAll"
          :disabled="allRunning || !daemonStore.connected"
          @click="startAll"
        >
          {{ $t('dashboard.startAll') }}
        </el-button>
        <el-button
          v-if="uiMode.isAdvanced"
          type="danger"
          size="small"
          :loading="stoppingAll"
          :disabled="noneRunning || !daemonStore.connected"
          @click="stopAll"
        >
          {{ $t('dashboard.stopAll') }}
        </el-button>
      </div>
    </div>

    <!-- Simple mode: metric tiles + quick actions -->
    <template v-if="uiMode.isSimple">
      <div class="simple-dashboard">
        <div class="simple-hero">
          <h1 class="hero-title">{{ $t('dashboard.simple.welcomeTitle') }}</h1>
          <p class="hero-summary">
            {{ $t('dashboard.simple.summary', {
              sites: sitesCount,
              running: runningServices,
              total: totalServices,
              hits: totalHits,
              errors: totalErrors
            }) }}
          </p>
        </div>

        <div v-if="aggregatesLoading" v-loading="true" style="height: 80px; margin-bottom: 12px;" />

        <div class="simple-tiles">
          <SimpleMetricTile
            :label="$t('dashboard.simple.tiles.sites')"
            :value="sitesCount"
            icon="Link"
            @click="router.push('/sites')"
          />
          <SimpleMetricTile
            :label="$t('dashboard.simple.tiles.services')"
            :value="`${runningServices}/${totalServices}`"
            icon="Monitor"
            :variant="runningServices < totalServices ? 'warning' : 'success'"
            @click="router.push('/services')"
          />
          <SimpleMetricTile
            :label="$t('dashboard.simple.tiles.hits')"
            :value="totalHits"
            icon="DataLine"
            @click="router.push('/sites')"
          />
          <SimpleMetricTile
            :label="$t('dashboard.simple.tiles.errors')"
            :value="totalErrors"
            icon="WarningFilled"
            :variant="totalErrors > 0 ? 'danger' : 'default'"
            @click="router.push('/sites')"
          />
        </div>

        <div class="simple-quick-actions">
          <el-button type="primary" size="large" @click="router.push('/sites?create=1')">
            + {{ $t('dashboard.simple.quickActions.newSite') }}
          </el-button>
          <el-button size="large" @click="openMailpit">
            {{ $t('dashboard.simple.quickActions.mailpit') }}
          </el-button>
          <el-button size="large" @click="router.push('/settings?tab=backup')">
            {{ $t('dashboard.simple.quickActions.backup') }}
          </el-button>
        </div>
      </div>
    </template>

    <template v-else>
    <!-- Offline state -->
    <div v-if="!daemonStore.connected" class="offline-banner">
      <el-skeleton :rows="5" animated style="padding: 0 16px;" />
      <el-alert
        type="warning"
        title="Connecting to daemon..."
        description="Waiting for NKS WDC daemon connection..."
        :closable="false"
        show-icon
        style="margin: 16px;"
      />
    </div>

    <template v-else>
      <!-- 1. Quick stats strip (top) — at-a-glance counters -->
      <div class="stats-grid">
        <div class="stat-card stat-clickable" @click="$router.push('/sites')">
          <el-icon class="stat-icon"><Monitor /></el-icon>
          <div class="stat-content">
            <div class="stat-value mono">{{ sitesStore.sites.length }}</div>
            <div class="stat-label">Sites</div>
          </div>
        </div>
        <div class="stat-card">
          <el-icon class="stat-icon stat-icon-running"><VideoPlay /></el-icon>
          <div class="stat-content">
            <div class="stat-value mono">{{ runningCount }} / {{ totalCount }}</div>
            <div class="stat-label">Services running</div>
          </div>
        </div>
        <div class="stat-card stat-clickable" @click="$router.push('/settings')">
          <el-icon class="stat-icon"><Grid /></el-icon>
          <div class="stat-content">
            <div class="stat-value mono">{{ daemonStore.status?.plugins ?? 0 }}</div>
            <div class="stat-label">Plugins loaded</div>
          </div>
        </div>
        <div class="stat-card">
          <el-icon class="stat-icon"><Timer /></el-icon>
          <div class="stat-content">
            <div class="stat-value mono">{{ formatUptime(daemonStore.status?.uptime ?? 0) }}</div>
            <div class="stat-label">Daemon uptime</div>
          </div>
        </div>
        <div class="stat-card" v-if="nodeProcessCount >= 0">
          <el-icon class="stat-icon" :class="nodeProcessCount > 0 ? 'stat-icon-running' : ''"><ChromeFilled /></el-icon>
          <div class="stat-content">
            <div class="stat-value mono">{{ nodeProcessCount }}</div>
            <div class="stat-label">Node.js processes</div>
          </div>
        </div>
        <div class="stat-card" v-if="lastBackupAge">
          <el-icon class="stat-icon"><Cpu /></el-icon>
          <div class="stat-content">
            <div class="stat-value mono">{{ lastBackupAge }}</div>
            <div class="stat-label">Last backup</div>
          </div>
        </div>
      </div>

      <!-- 2. Metric charts (CPU + Memory) -->
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

      <!-- 3. Services list (main panel) -->
      <div class="services-panel">
        <div class="panel-header">
          <span class="panel-title">{{ $t('dashboard.services') }}</span>
          <span class="panel-count" v-if="services.length > 0">
            <span class="count-running">{{ runningCount }}</span>
            <span class="count-sep">/</span>
            <span class="count-total">{{ totalCount }}</span>
            <span class="count-label">running</span>
          </span>
        </div>
        <div class="service-list">
        <div
          v-for="service in services"
          :key="service.id"
          class="service-row"
          :class="[`row-${statusText(service)}`]"
        >
          <ServiceIcon :service="service.id" :active="isRunning(service)" />
          <span class="status-dot" :class="`dot-${statusText(service)}`" />

          <!-- Name + version -->
          <div class="svc-identity">
            <span class="svc-name">{{ service.displayName || service.id }}</span>
            <span class="svc-version mono" v-if="service.version">v{{ service.version }}</span>
          </div>

          <!-- Port -->
          <div class="svc-port" v-if="getPort(service)">
            <span class="port-label">{{ $t('dashboard.port') }}</span>
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
              {{ $t('dashboard.logs') }}
            </el-button>
            <el-button
              size="small"
              text
              class="action-btn"
              @click="openConfig(service.id)"
            >
              {{ $t('dashboard.config') }}
            </el-button>
            <!-- Transitional states (starting=1, stopping=3) must block further
                 toggles AND show the spinner so the user doesn't think their
                 click was lost. Without this the switch would flip OFF as
                 soon as state goes to 3, leaving the user clicking to start
                 while the daemon is still gracefully stopping Apache. -->
            <el-switch
              :model-value="isRunning(service) || isTransitioning(service)"
              :loading="servicesStore.isBusy(service.id) || isTransitioning(service)"
              :disabled="!daemonStore.connected || isTransitioning(service)"
              size="large"
              class="svc-toggle"
              :title="transitionTitle(service)"
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
      </div>

      <!-- 4. Quick actions bar (shortcuts) -->
      <div class="quick-actions">
        <el-button size="small" @click="openMailpit">{{ $t('dashboard.openMailpit') }}</el-button>
        <el-button size="small" @click="$router.push('/ssl')">{{ $t('dashboard.sslManager') }}</el-button>
        <el-button size="small" @click="$router.push('/databases')">{{ $t('nav.databases') }}</el-button>
        <el-button size="small" @click="$router.push('/binaries')">{{ $t('dashboard.binaries') }}</el-button>
        <el-button size="small" @click="$router.push('/cloudflare')">{{ $t('dashboard.tunnel') }}</el-button>
      </div>

      <!-- Recent activity — Phase 4 plan item. Reads config_history via
           /api/activity so users can see what's happened recently without
           opening a service's log viewer. Empty state hidden when nothing
           has been recorded yet (first-run). -->
      <div class="activity-section" v-if="activity.length > 0">
        <div class="section-header">
          <span class="section-title">Recent activity</span>
          <el-button size="small" text @click="loadActivity" :loading="activityLoading">{{ $t('common.refresh') }}</el-button>
        </div>
        <el-timeline class="activity-timeline">
          <el-timeline-item
            v-for="row in activity"
            :key="row.id"
            :timestamp="row.createdAt"
            :type="activityColor(row.operation)"
            size="normal"
            placement="top"
          >
            <div class="activity-row">
              <span class="activity-op mono">{{ row.operation }}</span>
              <span class="activity-entity">{{ row.entityType }}</span>
              <span class="activity-name mono" v-if="row.entityName">{{ row.entityName }}</span>
              <span class="activity-source" v-if="row.source && row.source !== 'app'">via {{ row.source }}</span>
            </div>
          </el-timeline-item>
        </el-timeline>
      </div>
    </template>
    </template><!-- /v-else advanced -->

    <!-- Logs: full-view modal (NOT drawer). Opens large el-dialog with xterm-backed viewer. -->
    <el-dialog
      v-model="logsDialog.open"
      :title="`Logs — ${logsDialog.serviceId}`"
      width="92%"
      top="3vh"
      class="full-dialog"
      align-center
      destroy-on-close
    >
      <LogViewer
        v-if="logsDialog.open && logsDialog.serviceId"
        :service-id="logsDialog.serviceId"
      />
    </el-dialog>

    <!-- Config: slide-in side panel (NOT drawer, NOT full-view route). -->
    <ConfigSidePanel
      :open="configPanel.open"
      :service-id="configPanel.serviceId"
      @close="closeConfig"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Monitor, VideoPlay, Grid, Timer, ChromeFilled, Cpu } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { ElMessage, ElNotification } from 'element-plus'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'
import type { ServiceInfo } from '../../api/types'
import MetricsChart from '../shared/MetricsChart.vue'
import LogViewer from '../shared/LogViewer.vue'
import ServiceIcon from '../shared/ServiceIcon.vue'
import ConfigSidePanel from '../shared/ConfigSidePanel.vue'
import SimpleMetricTile from '../common/SimpleMetricTile.vue'

const router = useRouter()

const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'

const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()
const uiMode = useUiModeStore()

const startingAll = ref(false)
const stoppingAll = ref(false)

const services = computed(() => daemonStore.services)
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter(s => s.state === 2 || s.status === 'running').length)
const allRunning = computed(() => totalCount.value > 0 && runningCount.value === totalCount.value)
const noneRunning = computed(() => runningCount.value === 0)

// Simple mode metrics
const sitesCount = computed(() => sitesStore.sites.length)
const runningServices = computed(() => daemonStore.services.filter(s => s.state === 2).length)
const totalServices = computed(() => daemonStore.services.length)
const totalHits = ref(0)
const totalErrors = ref(0)
const aggregatesLoading = ref(false)

async function loadAggregates() {
  aggregatesLoading.value = true
  const domains = sitesStore.sites.map(s => s.domain)
  const results = await Promise.allSettled(domains.map(async (domain: string) => {
    try {
      const [metricsR, errorsR] = await Promise.allSettled([
        fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/metrics/history?minutes=1440&limit=24`, { headers: sitesStore.authHeaders() }),
        fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/logs/errors?limit=100`, { headers: sitesStore.authHeaders() }),
      ])
      let hits = 0, errs = 0
      if (metricsR.status === 'fulfilled' && metricsR.value.ok) {
        const data = await metricsR.value.json() as any
        const samples = Array.isArray(data) ? data : (data.samples ?? [])
        hits = samples.reduce((sum: number, s: any) => sum + (s.requests ?? s.hits ?? 0), 0)
      }
      if (errorsR.status === 'fulfilled' && errorsR.value.ok) {
        const data = await errorsR.value.json() as any
        const entries = Array.isArray(data) ? data : (data.entries ?? [])
        const cutoff = Date.now() - 24 * 60 * 60 * 1000
        errs = entries.filter((e: any) => {
          if (!e.timestamp) return true
          const t = new Date(e.timestamp).getTime()
          return !isNaN(t) && t > cutoff
        }).length
      }
      return { hits, errs }
    } catch { return { hits: 0, errs: 0 } }
  }))
  totalHits.value = results.reduce((sum, r) => r.status === 'fulfilled' ? sum + r.value.hits : sum, 0)
  totalErrors.value = results.reduce((sum, r) => r.status === 'fulfilled' ? sum + r.value.errs : sum, 0)
  aggregatesLoading.value = false
}
const totalCpu = computed(() => services.value.reduce((s, x) => s + (x.cpuPercent ?? 0), 0))
const totalRamMB = computed(() => Math.round(services.value.reduce((s, x) => s + (x.memoryBytes ?? 0), 0) / 1024 / 1024))

// Node.js process count — shown in the stat cards when the plugin is loaded.
// -1 means "not fetched yet / plugin not available" and hides the card.
const nodeProcessCount = ref(-1)
async function loadNodeProcessCount() {
  try {
    const { fetchNodeSites } = await import('../../api/daemon')
    const list = await fetchNodeSites()
    nodeProcessCount.value = list.filter(p => p.state === 2).length
  } catch { nodeProcessCount.value = -1 }
}

// Last backup age — shown in stat cards
const lastBackupAge = ref('')
async function loadLastBackup() {
  try {
    const { fetchBackups } = await import('../../api/daemon')
    const data = await fetchBackups()
    if (data.backups.length > 0) {
      const age = Date.now() - new Date(data.backups[0].createdUtc).getTime()
      const h = Math.floor(age / 3600000)
      lastBackupAge.value = h < 1 ? 'Just now' : h < 24 ? `${h}h ago` : `${Math.floor(h / 24)}d ago`
    }
  } catch { /* optional */ }
}

// Recent activity timeline — Phase 4 plan item. Backed by /api/activity
// which queries the config_history SQLite table. Loaded on mount and
// refreshable via the section-header button. Empty array (first-run or
// migration-in-flight) keeps the section hidden.
interface ActivityRow {
  id: number
  entityType: string
  entityName?: string
  operation: string
  changedFields?: string
  source?: string
  createdAt: string
}
const activity = ref<ActivityRow[]>([])

const activityLoading = ref(false)
async function loadActivity() {
  activityLoading.value = true
  try {
    // Previously this rolled its own port/token resolver with a stale
    // 5146 fallback (correct default is 5199) and accepted getPort()==0
    // through `??`. Delegating to daemonBaseUrl/daemonAuthHeaders keeps
    // the whole frontend on one resolution path.
    const r = await fetch(`${daemonBaseUrl()}/api/activity?limit=20`, {
      headers: authHeaders(),
    })
    if (!r.ok) return
    const data = await r.json()
    activity.value = Array.isArray(data) ? data : (data?.entries ?? [])
  } catch { /* offline — leave prior list in place */ }
  finally { activityLoading.value = false }
}

function activityColor(operation: string): 'primary' | 'success' | 'warning' | 'danger' | 'info' {
  const op = (operation || '').toLowerCase()
  if (op.includes('create') || op.includes('insert')) return 'success'
  if (op.includes('delete') || op.includes('remove')) return 'danger'
  if (op.includes('update') || op.includes('edit') || op.includes('apply')) return 'primary'
  return 'info'
}

onMounted(async () => {
  await sitesStore.load()
  void loadActivity()
  void loadNodeProcessCount()
  void loadLastBackup()
  if (uiMode.isSimple) void loadAggregates()
})

const stateLabels: Record<number, string> = {
  0: 'stopped', 1: 'starting', 2: 'running', 3: 'stopping', 4: 'crashed', 5: 'disabled',
}

const KNOWN_PORTS: Record<string, number> = {
  apache: 80, mysql: 3306, redis: 6379, mailpit: 8025, php: 9084,
}
function getPort(svc: ServiceInfo & { port?: number }): number | null {
  return svc.port ?? KNOWN_PORTS[svc.id] ?? null
}

function statusText(service: ServiceInfo): string {
  return stateLabels[service.state ?? 0] ?? service.status ?? 'unknown'
}

function isRunning(service: ServiceInfo): boolean {
  return service.state === 2 || service.status === 'running'
}

// Transitional states: 1=starting, 3=stopping. During these the switch must
// be frozen — pretending the process is already in the target state would
// let the user queue conflicting commands while the daemon is still in the
// middle of a graceful start/stop cycle (Apache graceful stop can take 30s).
function isTransitioning(service: ServiceInfo): boolean {
  return service.state === 1 || service.state === 3
    || service.status === 'starting' || service.status === 'stopping'
}

function transitionTitle(service: ServiceInfo): string {
  if (service.state === 1) return `${service.displayName || service.id}: starting…`
  if (service.state === 3) return `${service.displayName || service.id}: stopping…`
  return ''
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
  const svc = services.value.find(s => s.id === id)
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
        .filter(s => s.state === 0 || s.status === 'stopped')
        .map(s => servicesStore.start(s.id))
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
        .filter(s => s.state === 2 || s.status === 'running')
        .map(s => servicesStore.stop(s.id))
    )
  } finally {
    stoppingAll.value = false
  }
}

// Logs dialog (full-view modal, NOT drawer)
const logsDialog = reactive({
  open: false,
  serviceId: '',
})

function openMailpit() {
  const mailpitUrl = 'http://127.0.0.1:8025'
  if (window.electronAPI?.openExternal) window.electronAPI.openExternal(mailpitUrl)
  else window.open(mailpitUrl, '_blank')
}

function openLogs(id: string) {
  logsDialog.serviceId = id
  logsDialog.open = true
}

// Config opens a slide-in SIDE PANEL (per user directive: "config editor
// jako postranní panel místo drawer"). NOT a router-view navigation, NOT
// an el-drawer modal. Closing restores the Dashboard state unchanged.
const configPanel = reactive({
  open: false,
  serviceId: null as string | null,
})
function openConfig(id: string) {
  // Cloudflare Tunnel has a dedicated configuration page that manages the
  // whole thing via the Cloudflare REST API — the generic ConfigSidePanel
  // (which expects file-based service configs like httpd.conf) is useless
  // for it and would show "No config files found" with a 500 in the panel.
  // Send it straight to /cloudflare instead.
  if (id === 'cloudflare') {
    router.push('/cloudflare')
    return
  }
  configPanel.serviceId = id
  configPanel.open = true
}
function closeConfig() {
  configPanel.open = false
  // Keep serviceId set briefly so the slide-out animation doesn't jump;
  // ConfigSidePanel ignores serviceId when open=false.
}
</script>

<style scoped>
.dashboard-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

/* Full-view dialog override: use most of the viewport so editing feels like a page */
:global(.full-dialog) {
  height: 94vh;
}
:global(.full-dialog .el-dialog__body) {
  height: calc(94vh - 80px);
  overflow: hidden;
  padding: 0;
}

/* ─── Page header ─────────────────────────────────────────────────────────── */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
  background: linear-gradient(180deg, rgba(255, 255, 255, 0.03), transparent);
}

.header-title-block {
  display: flex;
  align-items: baseline;
  gap: 10px;
}

.page-title {
  font-size: 1.18rem;
  font-weight: 800;
  color: var(--wdc-text);
  letter-spacing: -0.02em;
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
  gap: 14px;
  padding: 0 24px;
  height: 58px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  transition: background 0.1s, border-color 0.1s;
  cursor: default;
}

.service-row:last-child {
  border-bottom: none;
}

.service-row:hover {
  background: rgba(255, 255, 255, 0.025);
}

/* ─── Status dot ──────────────────────────────────────────────────────────── */
.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

/* Flat: no glow, pure saturated dots */
.dot-running { background: var(--wdc-status-running); }
.dot-stopped { background: var(--wdc-status-stopped); }

.dot-starting,
.dot-stopping {
  background: var(--wdc-status-starting);
  animation: svc-pulse 1s ease-in-out infinite;
}

.dot-crashed { background: var(--wdc-status-error); }

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
  font-size: 0.95rem;
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
  gap: 8px;
  flex-shrink: 0;
  width: 240px;
  justify-content: flex-start;
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

/* ─── Dashboard subtitle ────────────────────────────────────────────────── */
.page-subtitle {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  font-weight: 500;
}

/* ─── Stats strip (top row) ─────────────────────────────────────────────── */
.stats-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
  padding: 18px 24px 8px;
}

.stat-card {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 16px 18px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  transition: border-color 0.12s, transform 0.1s;
}
.stat-card.stat-clickable { cursor: pointer; }
.stat-card.stat-clickable:hover {
  border-color: var(--wdc-accent);
  transform: translateY(-1px);
}

.stat-icon {
  font-size: 1.8rem;
  color: var(--wdc-text-3);
  line-height: 1;
  width: 34px;
  text-align: center;
}
.stat-icon-running {
  color: var(--wdc-status-running);
  font-size: 1rem;
}

.stat-content {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.stat-value {
  font-size: 1.3rem;
  font-weight: 800;
  color: var(--wdc-text);
  line-height: 1.1;
  letter-spacing: -0.01em;
}

.stat-label {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
}

/* ─── Metrics charts row ─────────────────────────────────────────────────── */
.metrics-section {
  padding: 8px 24px 16px;
  flex-shrink: 0;
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
  padding: 14px 16px;
  height: 140px;
  overflow: hidden;
}

/* ─── Services panel ─────────────────────────────────────────────────────── */
.services-panel {
  margin: 0 24px 16px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}

.panel-header {
  display: flex;
  align-items: baseline;
  gap: 12px;
  padding: 14px 20px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface-2);
}

.panel-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}

.panel-count {
  display: flex;
  align-items: baseline;
  gap: 3px;
  font-size: 0.78rem;
  font-family: 'JetBrains Mono', monospace;
}

/* ─── Quick actions ──────────────────────────────────────────────────────── */
.quick-actions {
  display: flex;
  gap: 8px;
  padding: 4px 24px 20px;
  flex-wrap: wrap;
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

/* Config drawer */
.config-files {
  height: 100%;
  overflow: auto;
}

.config-file-header {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.config-file-name {
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--wdc-text);
}

.config-file-path {
  font-size: 0.7rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
  /* Prevent long Windows paths (C:\Users\...\httpd.conf) from overflowing
     the drawer header. Per ui-audit 2026-04-11 §5. */
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 320px;
}

.config-pre {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
  line-height: 1.6;
  color: var(--wdc-text-2);
  white-space: pre-wrap;
  word-break: break-all;
  padding: 12px;
  background: var(--wdc-bg);
  border-radius: var(--wdc-radius-sm);
  max-height: 500px;
  overflow: auto;
}

/* ─── Simple mode dashboard ──────────────────────────────────────────────── */
.simple-dashboard {
  display: flex;
  flex-direction: column;
  gap: 24px;
  padding: 32px;
  max-width: 960px;
  margin: 0 auto;
}

.simple-hero {
  text-align: center;
}

.hero-title {
  font-size: 28px;
  font-weight: 700;
  margin: 0 0 8px;
}

.hero-summary {
  color: var(--el-text-color-secondary);
  font-size: 14px;
  margin: 0;
}

.simple-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
}

.simple-quick-actions {
  display: flex;
  gap: 12px;
  justify-content: center;
  flex-wrap: wrap;
  padding-top: 8px;
}

/* Recent activity timeline (Phase 4) */
.activity-section {
  margin-top: 24px;
  padding: 0 24px 24px;
}
.activity-section .section-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}
.activity-section .section-title {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--wdc-text-3);
}
.activity-timeline {
  padding-left: 4px;
}
.activity-row {
  display: flex;
  align-items: baseline;
  gap: 10px;
  font-size: 0.82rem;
}
.activity-op {
  font-weight: 600;
  color: var(--wdc-text);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.activity-entity {
  color: var(--wdc-text-2);
}
.activity-name {
  color: var(--wdc-accent);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.76rem;
}
.activity-source {
  color: var(--wdc-text-3);
  font-size: 0.72rem;
  margin-left: auto;
}
</style>
