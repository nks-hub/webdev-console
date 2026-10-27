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
            :disabled="startable.length === 0 || !daemonStore.connected"
            @click="startAll"
          >{{ $t('dashboard.startAll') }}</el-button>
          <el-button
            :loading="stoppingAll"
            :disabled="running.length === 0 || !daemonStore.connected"
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

    <!-- What to do now (left) and the state of the stack (right). With at
         most one thing to do, the list becomes a strip above the table. -->
    <div class="overview-grid" :class="{ 'is-stacked': attentionCount <= 1 }">
      <AttentionList ref="attention" />
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
        </dl>
      </section>

      <!-- Current value next to each chart instead of a second row of the same numbers. -->
      <div v-if="daemonStore.connected" class="metrics-grid">
        <section class="metric-panel" aria-labelledby="metric-cpu">
          <div class="metric-head">
            <h2 id="metric-cpu" class="metric-title">{{ $t('dashboard.metrics.cpuUsage') }}</h2>
            <span class="metric-now mono">{{ formatPercent(totalCpu, localeName) }}</span>
            <span class="metric-range">{{ $t('dashboard.metrics.range') }}</span>
          </div>
          <MetricsChart v-if="daemonStore.cpuHistory.length > 2" :data="daemonStore.cpuHistory" tone="accent" unit="%" />
          <p v-else class="metric-wait">{{ $t('dashboard.metrics.collecting') }}</p>
        </section>
        <section class="metric-panel" aria-labelledby="metric-ram">
          <div class="metric-head">
            <h2 id="metric-ram" class="metric-title">{{ $t('dashboard.metrics.memory') }}</h2>
            <span class="metric-now mono">{{ formatBytes(totalRamBytes, localeName) }}</span>
            <span class="metric-range">{{ $t('dashboard.metrics.range') }}</span>
          </div>
          <MetricsChart v-if="daemonStore.ramHistory.length > 2" :data="daemonStore.ramHistory" tone="success" unit="MB" />
          <p v-else class="metric-wait">{{ $t('dashboard.metrics.collecting') }}</p>
        </section>
      </div>

      <!-- Shortcuts. Plugin-contributed buttons come through <PluginSlot>. -->
      <div class="quick-actions">
        <el-tooltip v-if="mailpit" :disabled="mailpitUsable" :content="$t('dashboard.mailpitDown')" placement="top">
          <span>
            <el-button size="small" :disabled="!mailpitUsable" @click="openMailpit">{{ $t('dashboard.openMailpit') }}</el-button>
          </span>
        </el-tooltip>
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
            <span class="activity-op">{{ activityLabel('op', row.operation) }}</span>
            <span class="activity-entity">{{ activityLabel('entity', row.entityType) }}</span>
            <span class="activity-name mono" v-if="row.entityName">{{ row.entityName }}</span>
            <span class="activity-source" v-if="row.source && row.source !== 'app'">{{ $t('dashboard.activity.via', { source: activityLabel('source', row.source) }) }}</span>
            <time class="activity-time mono" :datetime="row.createdAt">{{ formatWhen(row.createdAt) }}</time>
          </li>
        </ol>
      </section>
    </template>

    <!-- Logs: full-view modal (NOT drawer). Opens large el-dialog with xterm-backed viewer. -->
    <el-dialog
      v-model="logsDialog.open"
      :title="$t('overview.logsTitle', { name: logsDialog.name })"
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
import { computed, defineAsyncComponent, onActivated, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useNow } from '@vueuse/core'
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
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { usePluginsStore } from '../../stores/plugins'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'
import MetricsChart from '../shared/MetricsChart.vue'
import LogViewer from '../shared/LogViewer.vue'
import ConfigSidePanel from '../shared/ConfigSidePanel.vue'
import PluginSlot from '../shared/PluginSlot.vue'
import { useI18n } from 'vue-i18n'
import { isRunning, serviceViewState, shortServiceName } from '../../utils/serviceState'
import { runServiceBatch } from '../../utils/serviceActions'
import { formatBytes, formatDuration, formatPercent } from '../../utils/format'
import { lastBackup, loadLastBackup } from '../../utils/lastBackup'
import { openMailpit } from '../../utils/mailpit'

const { t, te, locale } = useI18n()
const localeName = computed(() => String(locale.value))

const router = useRouter()

const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()
const uiMode = useUiModeStore()
const pluginsStore = usePluginsStore()

const startingAll = ref(false)
const stoppingAll = ref(false)

// The same visible-services set as the menu badge and the status bar.
const services = computed(() => daemonStore.visibleServices)
const running = computed(() => daemonStore.runningServices)
// "Start all" only touches what can start: installed and stopped.
const startable = computed(() => services.value.filter(s => serviceViewState(s) === 'stopped'))
const mailpit = computed(() => services.value.find(s => s.id === 'mailpit'))
const mailpitUsable = computed(() => !!mailpit.value && isRunning(mailpit.value))

const attention = ref<InstanceType<typeof AttentionList> | null>(null)
const attentionCount = computed(() => attention.value?.count ?? 0)

const sitesCount = computed(() => sitesStore.sites.length)
const totalCpu = computed(() => services.value.reduce((s, x) => s + (x.cpuPercent ?? 0), 0))
const totalRamBytes = computed(() => services.value.reduce((s, x) => s + (x.memoryBytes ?? 0), 0))

// One-sentence state of the stack under the page title.
const summary = computed(() => {
  if (!daemonStore.connected) return t('dashboard.connectingTitle')
  const total = services.value.length
  if (total === 0) return t('overview.summaryNoServices', { sites: sitesCount.value }, sitesCount.value)
  let text = t('overview.summary', { running: running.value.length, total, sites: sitesCount.value }, sitesCount.value)
  const crashed = daemonStore.crashedServices.length
  if (crashed > 0) text += ` · ${t('overview.summaryCrashed', { n: crashed }, crashed)}`
  const missing = services.value.filter(s => serviceViewState(s) === 'notInstalled').length
  if (missing > 0) text += ` · ${t('overview.summaryNotInstalled', { n: missing }, missing)}`
  return text
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

// Last backup age, recomputed from the timestamp as time passes instead of
// frozen at the moment the page was first opened.
const now = useNow({ interval: 30_000 })
const lastBackupAge = computed(() => {
  const at = lastBackup.value.createdAt
  if (at === null) return ''
  const minutes = Math.max(0, Math.floor((now.value.getTime() - at) / 60000))
  const rtf = new Intl.RelativeTimeFormat(localeName.value, { numeric: 'auto' })
  if (minutes < 60) return rtf.format(-minutes, 'minute')
  return minutes < 1440 ? rtf.format(-Math.floor(minutes / 60), 'hour') : rtf.format(-Math.floor(minutes / 1440), 'day')
})

// Recent activity timeline — Phase 4 plan item. Backed by /api/activity
// which queries the config_history SQLite table. Loaded when the page is
// shown and refreshable via the section-header button. Empty array (first-run or
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

// Operation, entity and source codes come straight from the daemon; show a
// translated word when we know the code, the raw code otherwise.
function activityLabel(kind: 'op' | 'entity' | 'source', code: string): string {
  const key = `dashboard.activity.${kind}.${(code || '').toLowerCase()}`
  return te(key) ? t(key) : code
}

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

// The page stays mounted (keep-alive), so everything that changes outside it
// (backups, activity, sites) is reloaded each time it is shown again.
function loadAll() {
  if (!daemonStore.connected) return
  void sitesStore.load()
  void loadActivity()
  void loadNodeProcessCount()
  void loadLastBackup()
}
onActivated(loadAll)
watch(() => daemonStore.connected, (connected) => { if (connected) loadAll() })

function formatUptime(uptime: string | number): string {
  const loc = localeName.value
  if (typeof uptime === 'number') return formatDuration(uptime, loc)
  // TimeSpan string "HH:MM:SS.xxx"
  const parts = String(uptime).split(':')
  if (parts.length >= 3) {
    return formatDuration(parseInt(parts[0]) * 3600 + parseInt(parts[1]) * 60 + parseInt(parts[2]), loc)
  }
  return String(uptime)
}

async function startAll() {
  startingAll.value = true
  try {
    await runServiceBatch('start', startable.value)
  } finally {
    startingAll.value = false
  }
}

async function stopAll() {
  stoppingAll.value = true
  try {
    await runServiceBatch('stop', running.value)
  } finally {
    stoppingAll.value = false
  }
}

// Logs dialog (full-view modal, NOT drawer)
const logsDialog = reactive({
  open: false,
  serviceId: '',
  name: '',
})

function openLogs(id: string) {
  logsDialog.serviceId = id
  const svc = services.value.find(s => s.id === id)
  logsDialog.name = svc ? shortServiceName(svc) : id
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

/* Attention (5) | Stack (7); stacks under 75rem, and whenever there is at
   most one thing to do (a strip above the table beats a mostly empty column). */
.overview-grid {
  display: grid;
  grid-template-columns: minmax(0, 5fr) minmax(0, 7fr);
  gap: var(--wdc-space-6-5);
  align-items: start;
}
.overview-grid.is-stacked { grid-template-columns: minmax(0, 1fr); gap: var(--wdc-space-5); }
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
.metric-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: var(--wdc-space-1) var(--wdc-space-3);
}
.metric-title {
  font-size: 0.93rem;
  font-weight: 500;
  color: var(--wdc-text-2);
}
.metric-now { font-size: 1.2rem; color: var(--wdc-text); }
.metric-range { margin-left: auto; font-size: 0.78rem; color: var(--wdc-text-3); }
.metric-wait { margin: 0; font-size: 0.87rem; color: var(--wdc-text-3); }

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
.activity-op { color: var(--wdc-text); font-weight: 500; }
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
