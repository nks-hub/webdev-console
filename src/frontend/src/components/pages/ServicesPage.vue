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

    <div v-else-if="services.length === 0" class="list-empty">
      <p>{{ t('servicesPage.empty') }}</p>
      <el-button type="primary" @click="router.push('/binaries')">{{ t('servicesPage.openBinaries') }}</el-button>
    </div>

    <div v-else-if="visible.length === 0" class="list-empty">
      <p>{{ filter === 'problems' ? t('servicesPage.noProblems') : t('servicesPage.emptyFilter') }}</p>
      <el-button @click="setFilter('all')">{{ t('servicesPage.showAll') }}</el-button>
    </div>

    <!-- List (left) and the selected service (right). The panel says what
         is going on in words, keeps the last failed action's error, shows
         the tail of the service's own log and offers the usual ways out.
         Stacks under 75rem. -->
    <div v-else class="split">
      <div class="list">
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
              :class="{ 'is-selected': selected?.id === svc.id, 'is-missing': stateOf(svc) === 'notInstalled' }"
              @click="select(svc.id)"
            >
              <!-- The row is clickable with the mouse; the name is a real
                   button for keyboard and screen readers, marked current
                   when its service is the one shown in the panel. -->
              <th scope="row">
                <button
                  type="button"
                  class="row-select"
                  :aria-current="selected?.id === svc.id ? 'true' : undefined"
                  aria-controls="svc-panel"
                  @click.stop="select(svc.id)"
                >{{ shortName(svc) }}</button>
              </th>
              <td>
                <span class="status" :class="`st-${stateOf(svc)}`">
                  <el-icon :size="14" aria-hidden="true">
                    <component :is="STATE_ICONS[stateOf(svc)]" :class="{ spin: isTransient(stateOf(svc)) }" />
                  </el-icon>
                  {{ t(`servicesPage.status.${stateOf(svc)}`) }}
                </span>
              </td>
              <td class="num mono">{{ svc.port ?? '—' }}</td>
              <td v-if="uiMode.isAdvanced" class="mono muted">{{ svc.version || '—' }}</td>
            </tr>
          </tbody>
        </table>
        <p v-if="hiddenCount > 0" class="hidden-note">
          {{ t('servicesPage.hiddenByPlugins', { n: hiddenCount }, hiddenCount) }}
          <el-button text size="small" @click="router.push('/plugins')">{{ t('servicesPage.openPlugins') }}</el-button>
        </p>
      </div>

      <section v-if="selected" id="svc-panel" class="panel" :aria-labelledby="`svc-${selected.id}`">
        <h2 :id="`svc-${selected.id}`" class="panel-title">{{ shortName(selected) }}</h2>
        <!-- Only the state sentence is live; the metrics below change with
             every poll and would be read out every few seconds. -->
        <p class="panel-status" :class="`st-${selectedState}`" role="status" aria-live="polite">
          <el-icon :size="16" aria-hidden="true">
            <component :is="STATE_ICONS[selectedState]" :class="{ spin: isTransient(selectedState) }" />
          </el-icon>
          <span>{{ t(`servicesPage.explain.${selectedState}`, { name: shortName(selected) }) }}</span>
        </p>

        <ServiceErrorNote :service-id="selected.id" />

        <dl v-if="selectedState !== 'notInstalled'" class="dl">
          <div v-if="selectedState === 'running' && selected.uptimeSeconds != null">
            <dt>{{ t('servicesPage.facts.uptime') }}</dt><dd class="mono">{{ formatUptime(selected.uptimeSeconds) }}</dd>
          </div>
          <div v-if="selected.version"><dt>{{ t('servicesPage.facts.version') }}</dt><dd class="mono">{{ selected.version }}</dd></div>
          <div v-if="portsLabel(selected)"><dt>{{ t('servicesPage.facts.ports') }}</dt><dd class="mono">{{ portsLabel(selected) }}</dd></div>
          <template v-if="selectedState === 'running'">
            <div v-if="selected.pid"><dt>PID</dt><dd class="mono">{{ selected.pid }}</dd></div>
            <div><dt>CPU</dt><dd class="mono">{{ (selected.cpuPercent ?? 0).toFixed(1) }} %</dd></div>
            <div><dt>RAM</dt><dd class="mono">{{ formatMem(selected.memoryBytes ?? 0) }}</dd></div>
          </template>
        </dl>

        <div class="panel-actions">
          <el-button v-if="selectedState === 'notInstalled'" type="primary" @click="install(selected)">
            {{ t('servicesPage.install') }}
          </el-button>
          <template v-else-if="selectedState === 'running'">
            <el-button :loading="servicesStore.isBusy(selected.id)" @click="act(selected, 'restart')">{{ t('servicesPage.restart') }}</el-button>
            <el-button type="danger" :loading="servicesStore.isBusy(selected.id)" @click="act(selected, 'stop')">{{ t('servicesPage.stop') }}</el-button>
          </template>
          <el-button
            v-else-if="!isTransient(selectedState)"
            type="primary"
            :loading="servicesStore.isBusy(selected.id)"
            @click="act(selected, 'start')"
          >{{ isProblemState(selectedState) ? t('servicesPage.fixRestart') : t('servicesPage.start') }}</el-button>
          <template v-if="selectedState !== 'notInstalled'">
            <el-button v-if="selected.id !== 'cloudflare'" @click="router.push(`/service/${encodeURIComponent(selected.id)}/config`)">
              {{ t('servicesPage.fixConfig') }}
            </el-button>
            <el-button v-if="manageRouteOf(selected.id)" @click="router.push(manageRouteOf(selected.id)!)">
              {{ t('servicesPage.manage') }}
            </el-button>
          </template>
        </div>

        <PluginAutostartSwitch v-if="selectedState !== 'notInstalled'" :plugin-id="`nks.wdc.${selected.id}`" />

        <template v-if="selectedState !== 'notInstalled'">
          <div class="log-head">
            <h3 class="log-title">{{ t('servicesPage.logTitle') }}</h3>
            <el-button size="small" text :loading="loadingLogs.has(selected.id)" @click="loadLogs(selected.id)">
              {{ logs[selected.id] ? t('common.refresh') : t('servicesPage.showLog') }}
            </el-button>
          </div>
          <pre v-if="logs[selected.id]?.length" class="log mono"><code>{{ logs[selected.id].join('\n') }}</code></pre>
          <p v-else-if="logErrors[selected.id]" class="log-empty">{{ t('servicesPage.logFailed', { err: logErrors[selected.id] }) }}</p>
          <p v-else-if="logs[selected.id]" class="log-empty">{{ t('servicesPage.logEmpty') }}</p>
        </template>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch, type Component } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Check, Close, Download, Loading, Remove, WarningFilled } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore, type ServiceOp } from '../../stores/services'
import { usePluginsStore } from '../../stores/plugins'
import { useUiModeStore } from '../../stores/uiMode'
import { fetchServiceLogs } from '../../api/daemon'
import type { ServiceInfo } from '../../api/types'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'
import ServiceErrorNote from '../shared/ServiceErrorNote.vue'
import {
  binaryAppOf, cleanError, formatMem, formatUptime, isProblemState, manageRouteOf,
  portsLabel, shortName, stateOf, type ServiceState,
} from '../../utils/services'

/**
 * All services in one place, in both UI modes: a list and the selected
 * service. A service with a problem is selected first and its log tail
 * loads on its own, so "why is it not running" is answered without a click.
 */
type Filter = 'all' | 'running' | 'problems' | 'notInstalled'
const FILTERS: Filter[] = ['all', 'running', 'problems', 'notInstalled']

const STATE_ICONS: Record<ServiceState, Component> = {
  running: Check,
  stopped: Remove,
  starting: Loading,
  stopping: Loading,
  crashed: Close,
  disabled: WarningFilled,
  notInstalled: Download,
}
const isTransient = (s: ServiceState) => s === 'starting' || s === 'stopping'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const pluginsStore = usePluginsStore()
const uiMode = useUiModeStore()

function queryFilter(): Filter {
  const q = route.query.filter
  return FILTERS.includes(q as Filter) ? q as Filter : 'all'
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
const logErrors = ref<Record<string, string>>({})
const loadingLogs = ref<Set<string>>(new Set())
const selectedId = ref<string | null>(null)

// /service/:id redirects here with ?service=<id>.
watch(() => route.query.service, (id) => {
  if (route.path === '/services' && typeof id === 'string' && id) selectedId.value = id
}, { immediate: true })

// A failed action counts as a problem until the next successful one.
const isProblem = (s: ServiceInfo) => isProblemState(stateOf(s)) || !!servicesStore.lastError[s.id]

const services = computed(() =>
  (daemonStore.services as ServiceInfo[]).filter(s => pluginsStore.isServiceVisible(s.id)))
const hiddenCount = computed(() => daemonStore.services.length - services.value.length)
const installed = computed(() => services.value.filter(s => stateOf(s) !== 'notInstalled'))
const runningCount = computed(() => services.value.filter(s => stateOf(s) === 'running').length)
const problemCount = computed(() => services.value.filter(isProblem).length)
const notInstalledCount = computed(() => services.value.length - installed.value.length)

const filters = computed(() => [
  { id: 'all' as const, label: t('servicesPage.filter.all'), count: services.value.length },
  { id: 'running' as const, label: t('servicesPage.filter.running'), count: runningCount.value },
  { id: 'problems' as const, label: t('servicesPage.filter.problems'), count: problemCount.value },
  { id: 'notInstalled' as const, label: t('servicesPage.filter.notInstalled'), count: notInstalledCount.value },
])

const summary = computed(() => {
  const total = installed.value.length
  const parts = [t('servicesPage.summary', { total, running: runningCount.value }, total)]
  if (notInstalledCount.value) parts.push(t('servicesPage.summaryNotInstalled', { n: notInstalledCount.value }, notInstalledCount.value))
  if (problemCount.value) parts.push(t('servicesPage.summaryProblems', { n: problemCount.value }, problemCount.value))
  return parts.join(' · ')
})

// Problems first, services that are not installed last, the rest by name.
const rank = (s: ServiceInfo) => isProblem(s) ? 0 : stateOf(s) === 'notInstalled' ? 2 : 1
const visible = computed(() => {
  const list = [...services.value].sort((a, b) =>
    rank(a) - rank(b) || shortName(a).localeCompare(shortName(b)))
  if (filter.value === 'running') return list.filter(s => stateOf(s) === 'running')
  if (filter.value === 'problems') return list.filter(isProblem)
  if (filter.value === 'notInstalled') return list.filter(s => stateOf(s) === 'notInstalled')
  return list
})

const selected = computed<ServiceInfo | null>(() =>
  visible.value.find(s => s.id === selectedId.value) ?? visible.value[0] ?? null)
const selectedState = computed<ServiceState>(() => stateOf(selected.value))

function select(id: string) {
  selectedId.value = id
}

// Load the log tail of a crashed service as soon as it is shown.
watch(selected, (svc) => {
  if (svc && isProblemState(stateOf(svc)) && !logs.value[svc.id] && !loadingLogs.value.has(svc.id)) void loadLogs(svc.id)
}, { immediate: true })

async function loadLogs(id: string) {
  loadingLogs.value = new Set(loadingLogs.value).add(id)
  try {
    const lines = await fetchServiceLogs(id, 30)
    logs.value = { ...logs.value, [id]: Array.isArray(lines) ? lines : [] }
    const nextErrors = { ...logErrors.value }
    delete nextErrors[id]
    logErrors.value = nextErrors
  } catch (e) {
    logs.value = { ...logs.value, [id]: [] }
    logErrors.value = { ...logErrors.value, [id]: cleanError(e) }
  } finally {
    const next = new Set(loadingLogs.value)
    next.delete(id)
    loadingLogs.value = next
  }
}

async function act(svc: ServiceInfo, op: ServiceOp) {
  const ok = await servicesStore.act(svc.id, op, shortName(svc))
  if (!ok) void loadLogs(svc.id)
}

function install(svc: ServiceInfo) {
  void router.push({ path: '/binaries', query: { app: binaryAppOf(svc.id) } })
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
  flex-wrap: wrap;
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
.list { min-width: 0; }

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
.table tbody tr.is-missing th { color: var(--wdc-text-2); }
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
.hidden-note { margin-top: var(--wdc-space-3); color: var(--wdc-text-3); font-size: 0.82rem; }

.status { display: inline-flex; align-items: center; gap: var(--wdc-space-1-5); white-space: nowrap; }
.st-running .el-icon { color: var(--wdc-status-running); }
.st-crashed, .st-disabled { color: var(--wdc-status-error); }
.st-starting .el-icon, .st-stopping .el-icon { color: var(--wdc-status-starting); }
.st-stopped, .st-notInstalled { color: var(--wdc-text-3); }
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
.panel-status.st-crashed, .panel-status.st-disabled { color: var(--wdc-text); }
.panel-status.st-crashed .el-icon, .panel-status.st-disabled .el-icon { color: var(--wdc-status-error); }
.panel-status.st-stopped .el-icon, .panel-status.st-notInstalled .el-icon { color: var(--wdc-text-3); }

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
