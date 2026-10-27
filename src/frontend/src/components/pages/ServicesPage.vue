<template>
  <div class="wdc-page services-page">
    <header class="page-head">
      <div>
        <h1 class="page-title">{{ t('servicesPage.title') }}</h1>
        <p class="page-sub">{{ summary }}</p>
      </div>
      <div class="tray" role="group" :aria-label="t('servicesPage.filterLabel')">
        <button
          v-for="f in filters"
          :key="f.id"
          type="button"
          class="tray-key"
          :aria-pressed="filter === f.id"
          @click="setFilter(f.id)"
        >{{ f.label }} <span class="mono tray-count">{{ f.count }}</span></button>
      </div>
    </header>

    <p v-if="!daemonStore.connected" class="offline-note" role="status">{{ t('dashboard.connectingTitle') }}</p>

    <div v-else-if="visible.length === 0" class="list-empty">
      <p>{{ filter === 'problems' ? t('servicesPage.noProblems') : t('servicesPage.empty') }}</p>
      <el-button v-if="filter !== 'all'" @click="setFilter('all')">{{ t('servicesPage.showAll') }}</el-button>
    </div>

    <!-- List (left) and the selected service (right). The panel says what
         is going on in words, shows the tail of the service's own log and
         offers the usual ways out. Stacks under 75rem. -->
    <div v-else class="split">
      <table class="table">
        <caption class="visually-hidden">{{ t('servicesPage.title') }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ t('overview.stack.service') }}</th>
            <th scope="col">{{ t('overview.stack.status') }}</th>
            <th scope="col" class="num">{{ t('overview.stack.port') }}</th>
            <th v-if="uiMode.isAdvanced" scope="col">{{ t('overview.stack.version') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="svc in visible"
            :key="svc.id"
            :class="{ 'is-selected': selected?.id === svc.id }"
            @click="select(svc.id)"
          >
            <!-- The row is clickable with the mouse; the name is a real
                 button for keyboard and screen readers, pressed when its
                 service is the one shown in the panel. -->
            <th scope="row">
              <button
                type="button"
                class="row-select"
                :aria-pressed="selected?.id === svc.id"
                :aria-controls="`svc-panel`"
                @click.stop="select(svc.id)"
              >{{ shortName(svc) }}</button>
            </th>
            <td>
              <span class="status" :class="`st-${stateOf(svc)}`">
                <el-icon :size="14" aria-hidden="true">
                  <Check v-if="stateOf(svc) === 'running'" />
                  <Close v-else-if="stateOf(svc) === 'crashed'" />
                  <Loading v-else-if="stateOf(svc) === 'starting' || stateOf(svc) === 'stopping'" class="spin" />
                  <Remove v-else />
                </el-icon>
                {{ t(`overview.status.${stateOf(svc)}`) }}
              </span>
            </td>
            <td class="num mono">{{ portOf(svc) ?? '—' }}</td>
            <td v-if="uiMode.isAdvanced" class="mono muted">{{ svc.version || '—' }}</td>
          </tr>
        </tbody>
      </table>

      <section v-if="selected" id="svc-panel" class="panel" aria-live="polite" :aria-labelledby="`svc-${selected.id}`">
        <h2 :id="`svc-${selected.id}`" class="panel-title">{{ shortName(selected) }}</h2>
        <p class="panel-status" :class="`st-${stateOf(selected)}`">
          <el-icon :size="16" aria-hidden="true">
            <Check v-if="stateOf(selected) === 'running'" />
            <Close v-else-if="stateOf(selected) === 'crashed'" />
            <Remove v-else />
          </el-icon>
          <span>{{ t(`servicesPage.explain.${stateOf(selected)}`) }}</span>
        </p>

        <dl v-if="stateOf(selected) === 'running'" class="dl">
          <div><dt>{{ t('servicesPage.facts.uptime') }}</dt><dd class="mono">{{ formatUptime(selected.uptimeSeconds ?? 0) }}</dd></div>
          <div v-if="selected.pid"><dt>PID</dt><dd class="mono">{{ selected.pid }}</dd></div>
          <div><dt>CPU</dt><dd class="mono">{{ (selected.cpuPercent ?? 0).toFixed(1) }} %</dd></div>
          <div><dt>RAM</dt><dd class="mono">{{ formatMem(selected.memoryBytes ?? 0) }}</dd></div>
          <div v-if="selected.version"><dt>{{ t('overview.stack.version') }}</dt><dd class="mono">{{ selected.version }}</dd></div>
        </dl>

        <div class="log-head">
          <h3 class="log-title">{{ t('servicesPage.logTitle') }}</h3>
          <el-button size="small" text :loading="loadingLogs.has(selected.id)" @click="loadLogs(selected.id)">
            {{ logs[selected.id] ? t('common.refresh') : t('servicesPage.showLog') }}
          </el-button>
        </div>
        <pre v-if="logs[selected.id]?.length" class="log mono"><code>{{ logs[selected.id].join('\n') }}</code></pre>
        <p v-else-if="logs[selected.id]" class="log-empty">{{ t('servicesPage.logEmpty') }}</p>

        <div class="panel-actions">
          <template v-if="stateOf(selected) === 'running'">
            <el-button :loading="servicesStore.isBusy(selected.id)" @click="act(selected, 'restart')">{{ t('overview.actions.restart') }}</el-button>
            <el-button type="danger" :loading="servicesStore.isBusy(selected.id)" @click="act(selected, 'stop')">{{ t('servicesPage.stop') }}</el-button>
          </template>
          <el-button
            v-else-if="stateOf(selected) !== 'starting' && stateOf(selected) !== 'stopping'"
            type="primary"
            :loading="servicesStore.isBusy(selected.id)"
            @click="act(selected, 'start')"
          >{{ stateOf(selected) === 'crashed' ? t('servicesPage.fixRestart') : t('overview.actions.start') }}</el-button>
          <el-button v-if="selected.id !== 'cloudflare'" @click="router.push(`/service/${encodeURIComponent(selected.id)}/config`)">
            {{ t('servicesPage.fixConfig') }}
          </el-button>
        </div>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Check, Close, Loading, Remove } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { usePluginsStore } from '../../stores/plugins'
import { useUiModeStore } from '../../stores/uiMode'
import { fetchServiceLogs } from '../../api/daemon'
import type { ServiceInfo } from '../../api/types'
import { errorMessage } from '../../utils/errors'

/**
 * All services in one place, in both UI modes: a list and the selected
 * service. A crashed service is selected first and its log tail loads on
 * its own, so "why is it not running" is answered without a click.
 */
type Filter = 'all' | 'running' | 'problems'
type State = 'stopped' | 'starting' | 'running' | 'stopping' | 'crashed' | 'disabled'
const STATES: State[] = ['stopped', 'starting', 'running', 'stopping', 'crashed', 'disabled']

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const pluginsStore = usePluginsStore()
const uiMode = useUiModeStore()

function queryFilter(): Filter {
  const q = route.query.filter
  return q === 'running' || q === 'problems' ? q : 'all'
}
const filter = ref<Filter>(queryFilter())
watch(() => route.query.filter, () => {
  if (route.path === '/services') filter.value = queryFilter()
})
function setFilter(f: Filter) {
  filter.value = f
  void router.replace({ query: { ...route.query, filter: f === 'all' ? undefined : f } })
}

const logs = ref<Record<string, string[]>>({})
const loadingLogs = ref<Set<string>>(new Set())
const selectedId = ref<string | null>(null)

function stateOf(svc: ServiceInfo): State {
  const byNumber = STATES[svc.state ?? -1]
  if (byNumber) return byNumber
  return (STATES as string[]).includes(svc.status ?? '') ? svc.status as State : 'stopped'
}
// Crashed is a fault; stopped is a choice.
const isProblem = (s: ServiceInfo) => stateOf(s) === 'crashed'

const services = computed(() =>
  (daemonStore.services as ServiceInfo[]).filter(s => pluginsStore.isServiceVisible(s.id)))
const runningCount = computed(() => services.value.filter(s => stateOf(s) === 'running').length)
const problemCount = computed(() => services.value.filter(isProblem).length)

const filters = computed(() => [
  { id: 'all' as const, label: t('servicesPage.filter.all'), count: services.value.length },
  { id: 'running' as const, label: t('servicesPage.filter.running'), count: runningCount.value },
  { id: 'problems' as const, label: t('servicesPage.filter.problems'), count: problemCount.value },
])

const summary = computed(() => {
  const base = t('servicesPage.summary', { running: runningCount.value, total: services.value.length })
  return problemCount.value
    ? `${base} · ${t('overview.summaryCrashed', { n: problemCount.value }, problemCount.value)}`
    : base
})

// Declared before the computeds: the immediate watch on `selected` below
// sorts by shortName() during setup, before later consts are initialised.
const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
}

const visible = computed(() => {
  const list = [...services.value].sort((a, b) =>
    Number(isProblem(b)) - Number(isProblem(a)) || shortName(a).localeCompare(shortName(b)))
  if (filter.value === 'running') return list.filter(s => stateOf(s) === 'running')
  if (filter.value === 'problems') return list.filter(isProblem)
  return list
})

const selected = computed<ServiceInfo | null>(() =>
  visible.value.find(s => s.id === selectedId.value) ?? visible.value[0] ?? null)

function select(id: string) {
  selectedId.value = id
}

// Load the log tail of a crashed service as soon as it is shown.
watch(selected, (svc) => {
  if (svc && isProblem(svc) && !logs.value[svc.id] && !loadingLogs.value.has(svc.id)) void loadLogs(svc.id)
}, { immediate: true })

function shortName(svc: ServiceInfo): string {
  return SHORT_NAMES[svc.displayName ?? ''] || svc.displayName || svc.id
}
const KNOWN_PORTS: Record<string, number> = { apache: 80, mysql: 3306, redis: 6379, mailpit: 8025 }
function portOf(svc: ServiceInfo & { port?: number }): number | null {
  return svc.port ?? KNOWN_PORTS[svc.id] ?? null
}
function formatMem(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(0)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}
function formatUptime(s: number): string {
  if (s < 60) return `${s}s`
  if (s < 3600) return `${Math.floor(s / 60)}m`
  return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
}

async function loadLogs(id: string) {
  loadingLogs.value = new Set(loadingLogs.value).add(id)
  try {
    const lines = await fetchServiceLogs(id, 30)
    logs.value = { ...logs.value, [id]: Array.isArray(lines) ? lines : [] }
  } catch (e) {
    ElMessage.error(t('toast.loadFailed', { err: errorMessage(e) }))
  } finally {
    const next = new Set(loadingLogs.value)
    next.delete(id)
    loadingLogs.value = next
  }
}

async function act(svc: ServiceInfo, op: 'start' | 'stop' | 'restart') {
  const name = shortName(svc)
  try {
    if (op === 'start') {
      await servicesStore.start(svc.id)
      ElMessage.success(t('toast.serviceStarted', { name }))
    } else if (op === 'stop') {
      await servicesStore.stop(svc.id)
      ElMessage.success(t('toast.serviceStopped', { name }))
    } else {
      await servicesStore.restart(svc.id)
      ElMessage.success(t('overview.restarted', { name }))
    }
  } catch (e) {
    const key = op === 'stop' ? 'toast.stopFailed' : op === 'restart' ? 'toast.restartFailed' : 'toast.startFailed'
    ElMessage.error(t(key, { err: errorMessage(e) }))
    void loadLogs(svc.id)
  }
}
</script>

<style scoped>
.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
}
.page-title { font-size: 1.728rem; line-height: 1.1; letter-spacing: -0.028em; }
.page-sub { margin-top: var(--wdc-space-2); color: var(--wdc-text-2); }
.offline-note { color: var(--wdc-text-2); }

.tray {
  display: inline-flex;
  gap: var(--wdc-space-0-5);
  padding: var(--wdc-space-0-5);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface-2);
}
.tray-key {
  min-height: 30px;
  padding: 0 var(--wdc-space-3);
  border: 1px solid transparent;
  border-radius: var(--wdc-radius);
  background: transparent;
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.87rem;
  cursor: pointer;
}
@media (hover: hover) { .tray-key:hover { color: var(--wdc-text); } }
.tray-key[aria-pressed="true"] {
  background: var(--wdc-key-face);
  border-color: var(--wdc-border-strong);
  color: var(--wdc-text);
  box-shadow: var(--wdc-shadow-key-press);
}
.tray-key:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 1px; }
.tray-count { margin-left: var(--wdc-space-1); color: var(--wdc-text-3); font-size: 0.78rem; }

.split {
  display: grid;
  grid-template-columns: minmax(0, 7fr) minmax(0, 5fr);
  gap: var(--wdc-space-6-5);
  align-items: start;
}

.table { width: 100%; border-collapse: collapse; font-size: 0.87rem; }
.table th, .table td {
  padding: var(--wdc-space-3);
  text-align: left;
  border-bottom: 1px solid var(--wdc-border);
  vertical-align: middle;
}
.table th:first-child, .table td:first-child { padding-left: var(--wdc-space-2); }
.table thead th {
  font-family: var(--wdc-font-display);
  font-weight: 500;
  color: var(--wdc-text-3);
  border-bottom-color: var(--wdc-border-strong);
}
.table tbody th {
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.93rem;
  color: var(--wdc-text);
}
.table tbody tr { cursor: pointer; transition: background-color var(--wdc-dur-micro) var(--wdc-ease-out); }
@media (hover: hover) { .table tbody tr:hover { background: var(--wdc-hover); } }
.table tbody tr.is-selected { background: var(--wdc-accent-dim); }
.row-select {
  padding: 0;
  border: 0;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}
.row-select:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; border-radius: var(--wdc-radius-xs); }
.num { text-align: right !important; font-variant-numeric: tabular-nums; }
.muted { color: var(--wdc-text-2); }

.status { display: inline-flex; align-items: center; gap: var(--wdc-space-1-5); white-space: nowrap; }
.st-running .el-icon { color: var(--wdc-status-running); }
.st-crashed { color: var(--wdc-status-error); }
.st-starting .el-icon, .st-stopping .el-icon { color: var(--wdc-status-starting); }
.st-stopped, .st-disabled { color: var(--wdc-text-3); }
.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

.panel {
  display: grid;
  gap: var(--wdc-space-4);
  padding-left: var(--wdc-space-5);
  border-left: 1px solid var(--wdc-border);
  min-width: 0;
}
.panel-title { font-size: 1.2rem; }
.panel-status {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: flex-start;
  color: var(--wdc-text);
  line-height: 1.45;
}
.panel-status .el-icon { margin-top: var(--wdc-space-0-5); flex-shrink: 0; }
.panel-status.st-crashed { color: var(--wdc-text); }
.panel-status.st-crashed .el-icon { color: var(--wdc-status-error); }
.panel-status.st-stopped .el-icon, .panel-status.st-disabled .el-icon { color: var(--wdc-text-3); }

.dl { margin: 0; display: grid; }
.dl > div {
  display: grid;
  grid-template-columns: 8rem minmax(0, 1fr);
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-2) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.dl dt { font-family: var(--wdc-font-display); font-weight: 500; color: var(--wdc-text-2); }
.dl dd { margin: 0; }

.log-head { display: flex; align-items: baseline; justify-content: space-between; }
.log-title { font-size: 0.93rem; font-weight: 500; color: var(--wdc-text-2); }
/* Graphite log box: same in both themes, like a terminal. */
.log {
  margin: 0;
  max-height: 260px;
  overflow: auto;
  padding: var(--wdc-space-3);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-log-bg);
  color: var(--wdc-log-text);
  font-size: 0.74rem;
  line-height: 1.6;
}
.log code { white-space: pre-wrap; overflow-wrap: anywhere; background: none; }
.log-empty { color: var(--wdc-text-3); font-size: 0.87rem; }

.panel-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.panel-actions :deep(.el-button + .el-button) { margin-left: 0; }

.list-empty {
  display: grid;
  justify-items: start;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-5) 0;
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
  color: var(--wdc-text-2);
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}

@media (max-width: 75rem) {
  .split { grid-template-columns: minmax(0, 1fr); }
  .panel { padding-left: 0; padding-top: var(--wdc-space-4); border-left: 0; border-top: 1px solid var(--wdc-border); }
}
</style>
