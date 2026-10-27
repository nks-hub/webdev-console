<template>
  <div class="dashboard-page overview-page">
    <header class="page-head">
      <div>
        <h1 class="page-title">{{ $t('nav.overview') }}</h1>
        <p class="page-sub">{{ summary }}</p>
      </div>
      <div class="head-actions">
        <template v-if="uiMode.isAdvanced">
          <el-button
            :loading="startingAll"
            :disabled="allRunning || !daemonStore.connected"
            @click="startAll"
          >{{ $t('dashboard.startAll') }}</el-button>
          <el-button
            :loading="stoppingAll"
            :disabled="noneRunning || !daemonStore.connected"
            @click="stopAll"
          >{{ $t('dashboard.stopAll') }}</el-button>
        </template>
        <el-button type="primary" @click="$router.push({ path: '/sites', query: { create: '1' } })">
          {{ $t('dashboard.newSite') }}
        </el-button>
      </div>
    </header>

    <p v-if="!daemonStore.connected" class="offline-note" role="status">
      <el-icon :size="16" aria-hidden="true"><Loading class="spin" /></el-icon>
      {{ $t('dashboard.connectingDesc') }}
    </p>

    <!-- What to do now (left) and the state of the stack (right). Shared
         by both densities; Advanced adds columns and the sections below. -->
    <div class="overview-grid">
      <AttentionList />
      <StackTable @logs="openLogs" @config="openConfig" />
    </div>

    <DashboardSimple v-if="uiMode.isSimple" />

    <template v-else>
      <section class="figures-sec" :aria-label="$t('overview.figures.title')">
        <dl class="figures">
          <div class="figure is-link">
            <dt>{{ $t('dashboard.stats.sites') }}</dt>
            <dd class="mono"><router-link class="figure-link" to="/sites">{{ sitesStore.sites.length }}</router-link></dd>
          </div>
          <div class="figure is-link">
            <dt>{{ $t('dashboard.stats.pluginsEnabled') }}</dt>
            <dd class="mono"><router-link class="figure-link" to="/plugins">{{ pluginsStore.enabledPlugins.length }} / {{ pluginsStore.manifests.length || (daemonStore.status?.plugins ?? 0) }}</router-link></dd>
          </div>
          <div class="figure">
            <dt>{{ $t('dashboard.stats.daemonUptime') }}</dt>
            <dd class="mono">{{ formatUptime(daemonStore.status?.uptime ?? 0) }}</dd>
          </div>
          <div v-if="nodeProcessCount >= 0 && pluginsStore.isServiceVisible('node')" class="figure">
            <dt>{{ $t('dashboard.stats.nodeProcesses') }}</dt>
            <dd class="mono">{{ nodeProcessCount }}</dd>
          </div>
          <div v-if="lastBackupAge" class="figure is-link">
            <dt>{{ $t('dashboard.stats.lastBackup') }}</dt>
            <dd class="mono"><router-link class="figure-link" to="/backups">{{ lastBackupAge }}</router-link></dd>
          </div>
          <div class="figure">
            <dt>{{ $t('dashboard.metrics.cpuUsage') }}</dt>
            <dd class="mono">{{ totalCpu.toFixed(1) }} %</dd>
          </div>
          <div class="figure">
            <dt>{{ $t('dashboard.metrics.memory') }}</dt>
            <dd class="mono">{{ totalRamMB }} MB</dd>
          </div>
        </dl>
      </section>

      <div v-if="daemonStore.cpuHistory.length > 2" class="metrics-grid">
        <section class="metric-panel">
          <h2 class="metric-title">{{ $t('dashboard.metrics.cpuUsage') }}</h2>
          <MetricsChart :data="daemonStore.cpuHistory" tone="accent" />
        </section>
        <section class="metric-panel">
          <h2 class="metric-title">{{ $t('dashboard.metrics.memory') }}</h2>
          <MetricsChart :data="daemonStore.ramHistory" tone="success" />
        </section>
      </div>

      <!-- Shortcuts. Plugin-contributed buttons come through <PluginSlot>. -->
      <div class="quick-actions">
        <el-button size="small" @click="openMailpit">{{ $t('dashboard.openMailpit') }}</el-button>
        <el-button size="small" @click="$router.push('/databases')">{{ $t('nav.databases') }}</el-button>
        <el-button size="small" @click="$router.push('/binaries')">{{ $t('dashboard.binaries') }}</el-button>
        <PluginSlot name="dashboard-quick-actions" :context="{ size: 'small' }" />
      </div>

      <!-- Recent activity from /api/activity (config_history). Hidden
           until something has been recorded. -->
      <section v-if="activity.length > 0" class="activity-section">
        <div class="section-header">
          <h2 class="section-title">{{ $t('dashboard.recentActivity') }}</h2>
          <el-button size="small" text @click="loadActivity" :loading="activityLoading">{{ $t('common.refresh') }}</el-button>
        </div>
        <ol class="activity-list">
          <li v-for="row in activity" :key="row.id" class="activity-row">
            <span class="activity-dot" :class="`tone-${activityColor(row.operation)}`" aria-hidden="true" />
            <span class="activity-op mono">{{ row.operation }}</span>
            <span class="activity-entity">{{ row.entityType }}</span>
            <span class="activity-name mono" v-if="row.entityName">{{ row.entityName }}</span>
            <span class="activity-source" v-if="row.source && row.source !== 'app'">via {{ row.source }}</span>
            <time class="activity-time mono" :datetime="row.createdAt">{{ formatWhen(row.createdAt) }}</time>
          </li>
        </ol>
      </section>
    </template>

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
import { computed, defineAsyncComponent, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Loading } from '@element-plus/icons-vue'
import AttentionList from '../overview/AttentionList.vue'
import StackTable from '../overview/StackTable.vue'

// Lazy-load the simple-mode dashboard. Advanced users (default for
// developers) never render the simple surface, so its 4 KPI tiles +
// recent-sites widget shouldn't sit in the Dashboard chunk that
// every cold start pays for. defineAsyncComponent emits a separate
// chunk that loads only when uiMode flips to 'simple'.
const DashboardSimple = defineAsyncComponent(() => import('./DashboardSimple.vue'))
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { usePluginsStore } from '../../stores/plugins'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'
import MetricsChart from '../shared/MetricsChart.vue'
import LogViewer from '../shared/LogViewer.vue'
import ConfigSidePanel from '../shared/ConfigSidePanel.vue'
import PluginSlot from '../shared/PluginSlot.vue'
import { useAppVersion } from '../../utils/appVersion'
import { useI18n } from 'vue-i18n'

const { t, locale } = useI18n()

const router = useRouter()

const versionRef = useAppVersion()
const appVersion = computed(() => versionRef.value.full)

const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()
const uiMode = useUiModeStore()
const pluginsStore = usePluginsStore()

const startingAll = ref(false)
const stoppingAll = ref(false)

// F91.3: hide services belonging to disabled plugins from every Dashboard
// tile / counter / quick-action. isServiceVisible fails open until plugin
// manifests load so the initial render isn't empty.
const services = computed(() =>
  daemonStore.services.filter(s => pluginsStore.isServiceVisible(s.id))
)
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter(s => s.state === 2 || s.status === 'running').length)
const allRunning = computed(() => totalCount.value > 0 && runningCount.value === totalCount.value)
const noneRunning = computed(() => runningCount.value === 0)

// Advanced-mode summary numbers — sitesCount + runningServices are
// also used by header chips. Simple-mode aggregates (hits, errors,
// loading state) live in DashboardSimple.vue.
const sitesCount = computed(() => sitesStore.sites.length)
const runningServices = computed(() => services.value.filter(s => s.state === 2).length)
const totalServices = computed(() => services.value.length)
const totalCpu = computed(() => services.value.reduce((s, x) => s + (x.cpuPercent ?? 0), 0))
const totalRamMB = computed(() => Math.round(services.value.reduce((s, x) => s + (x.memoryBytes ?? 0), 0) / 1024 / 1024))

// One-sentence state of the stack under the page title.
const crashedCount = computed(() => services.value.filter(s => s.state === 4).length)
const summary = computed(() => {
  if (!daemonStore.connected) return t('dashboard.connectingTitle')
  if (totalServices.value === 0) return t('overview.summaryNoServices', { sites: sitesCount.value }, sitesCount.value)
  const base = t('overview.summary', { running: runningServices.value, total: totalServices.value, sites: sitesCount.value }, sitesCount.value)
  return crashedCount.value > 0
    ? `${base} · ${t('overview.summaryCrashed', { n: crashedCount.value }, crashedCount.value)}`
    : base
})

// Activity timestamps: time today, otherwise a short date.
function formatWhen(iso: string): string {
  const d = new Date(iso)
  if (isNaN(d.getTime())) return iso
  const sameDay = d.toDateString() === new Date().toDateString()
  return sameDay
    ? d.toLocaleTimeString(String(locale.value), { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleDateString(String(locale.value), { day: 'numeric', month: 'short' })
}

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
      const rtf = new Intl.RelativeTimeFormat(String(locale.value), { numeric: 'auto' })
      lastBackupAge.value = h < 1
        ? rtf.format(-Math.max(0, Math.floor(age / 60000)), 'minute')
        : h < 24 ? rtf.format(-h, 'hour') : rtf.format(-Math.floor(h / 24), 'day')
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
  // Simple-mode aggregates (hits, errors) are loaded by DashboardSimple
  // on its own onMounted — no need to duplicate the fetch from here.
})

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


// Simple-mode apache-stopped banner + start button moved to DashboardSimple.

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
.overview-page { gap: var(--wdc-space-6); }

.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
}
.page-title {
  font-size: 1.728rem;
  line-height: 1.1;
  letter-spacing: -0.028em;
}
.page-sub {
  margin-top: var(--wdc-space-2);
  color: var(--wdc-text-2);
  max-width: 70ch;
}
.head-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
}
.head-actions :deep(.el-button + .el-button) { margin-left: 0; }

.offline-note {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  color: var(--wdc-text-2);
}
.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

/* Attention (5) | Stack (7); stacks under 75rem. */
.overview-grid {
  display: grid;
  grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
  gap: var(--wdc-space-6-5);
  align-items: start;
}
@media (max-width: 75rem) {
  .overview-grid { grid-template-columns: minmax(0, 1fr); }
}

/* Figures: one hairline row of label/value pairs instead of tiles. */
.figures {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
  margin: 0;
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
}
.figure {
  display: grid;
  gap: var(--wdc-space-1);
  padding: var(--wdc-space-3) var(--wdc-space-4) var(--wdc-space-3) 0;
}
.figure + .figure { padding-left: var(--wdc-space-4); border-left: 1px solid var(--wdc-border); }
.figure dt {
  font-family: var(--wdc-font-display);
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}
.figure dd {
  margin: 0;
  font-size: 1.2rem;
  color: var(--wdc-text);
}
.figure-link { color: inherit; text-decoration: none; }
@media (hover: hover) {
  .figure-link:hover { color: var(--wdc-accent-ink); text-decoration: underline; text-decoration-color: var(--wdc-accent); }
}
.figure-link:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; border-radius: var(--wdc-radius-xs); }

.metrics-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr));
  gap: var(--wdc-space-5);
}
.metric-panel {
  display: grid;
  gap: var(--wdc-space-2);
}
.metric-title {
  font-size: 0.93rem;
  font-weight: 500;
  color: var(--wdc-text-2);
}

.quick-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
}
.quick-actions :deep(.el-button + .el-button) { margin-left: 0; }

.section-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  margin-bottom: var(--wdc-space-2);
}
.section-title { font-size: 1.2rem; }
.activity-list {
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--wdc-border);
}
.activity-row {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-2) 0;
  border-bottom: 1px solid var(--wdc-border);
  font-size: 0.87rem;
}
.activity-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  align-self: center;
  background: var(--wdc-text-3);
}
.tone-success { background: var(--wdc-status-running); }
.tone-danger { background: var(--wdc-status-error); }
.tone-primary { background: var(--wdc-accent); }
.tone-warning { background: var(--wdc-status-starting); }
.activity-op { color: var(--wdc-text); }
.activity-entity, .activity-source { color: var(--wdc-text-2); }
.activity-name { color: var(--wdc-text); }
.activity-time { margin-left: auto; color: var(--wdc-text-3); font-size: 0.78rem; }
:global(.full-dialog) {
  height: 94vh;
}
:global(.full-dialog .el-dialog__body) {
  height: calc(94vh - 80px);
  overflow: hidden;
  padding: 0;
}
</style>
