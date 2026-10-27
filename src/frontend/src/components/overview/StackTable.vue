<template>
  <section class="stack" aria-labelledby="ov-stack">
    <div class="sec-head">
      <h2 id="ov-stack" class="ov-h2">{{ t('overview.stack.title') }}</h2>
      <router-link class="link" to="/services">{{ t('overview.stack.all') }}</router-link>
    </div>

    <div v-if="!daemonStore.connected && !rows.length" class="skeleton" aria-busy="true" :aria-label="t('overview.stack.loading')">
      <div v-for="n in 4" :key="n" class="skel-row" />
    </div>

    <div v-else-if="!rows.length" class="empty">
      <p class="empty-title">{{ t('overview.stack.empty') }}</p>
      <p>{{ t('overview.stack.emptyHint') }}</p>
      <el-button type="primary" @click="router.push('/binaries')">{{ t('overview.stack.openBinaries') }}</el-button>
    </div>

    <div v-else class="table-wrap">
    <table class="table">
      <caption class="visually-hidden">{{ t('overview.stack.caption') }}</caption>
      <thead>
        <tr>
          <th scope="col">{{ t('overview.stack.service') }}</th>
          <th scope="col">{{ t('overview.stack.status') }}</th>
          <th v-if="advanced" scope="col" class="num">{{ t('overview.stack.port') }}</th>
          <th v-if="advanced" scope="col">{{ t('overview.stack.version') }}</th>
          <th v-if="advanced" scope="col" class="num">{{ t('overview.stack.usage') }}</th>
          <th scope="col"><span class="visually-hidden">{{ t('overview.stack.actions') }}</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.svc.id" :class="{ 'row-err': row.state === 'crashed', 'row-missing': row.state === 'notInstalled' }">
          <th scope="row">{{ shortServiceName(row.svc) }}</th>
          <td>
            <span class="status" :class="`st-${row.state}`">
              <el-icon v-if="row.state === 'running'" :size="14" aria-hidden="true"><Check /></el-icon>
              <el-icon v-else-if="row.state === 'crashed'" :size="14" aria-hidden="true"><WarningFilled /></el-icon>
              <el-icon v-else-if="row.state === 'starting' || row.state === 'stopping'" :size="14" aria-hidden="true"><Loading class="spin" /></el-icon>
              <el-icon v-else-if="row.state === 'notInstalled'" :size="14" aria-hidden="true"><Download /></el-icon>
              <span v-else class="ring" aria-hidden="true" />
              {{ statusLabel(row) }}
            </span>
          </td>
          <td v-if="advanced" class="num mono">{{ row.port ?? '—' }}</td>
          <td v-if="advanced" class="mono muted">{{ row.svc.version || '—' }}</td>
          <td v-if="advanced" class="num mono muted">
            <template v-if="row.state === 'running'">{{ formatPercent(row.svc.cpuPercent ?? 0, localeName) }} · {{ formatBytes(row.svc.memoryBytes ?? 0, localeName) }}</template>
            <template v-else>—</template>
          </td>
          <td class="act">
            <div class="act-row">
              <el-button
                v-if="row.state === 'notInstalled'"
                size="small"
                @click="router.push({ path: '/binaries', query: { app: row.svc.binaryApp ?? row.svc.id } })"
              >{{ t('overview.actions.install') }}</el-button>
              <el-button
                v-else-if="row.state === 'crashed'"
                size="small"
                @click="router.push({ path: '/services', query: { filter: 'problems' } })"
              >{{ t('overview.actions.why') }}</el-button>
              <el-button
                v-else-if="row.state === 'running'"
                size="small"
                :loading="servicesStore.isBusy(row.svc.id)"
                :disabled="!daemonStore.connected"
                @click="runServiceAction(row.svc, 'restart')"
              >{{ t('overview.actions.restart') }}</el-button>
              <el-button
                v-else-if="row.state === 'stopped' || row.state === 'disabled'"
                size="small"
                :loading="servicesStore.isBusy(row.svc.id)"
                :disabled="!daemonStore.connected"
                @click="runServiceAction(row.svc, 'start')"
              >{{ t('overview.actions.start') }}</el-button>
              <el-button v-else size="small" loading disabled>{{ statusLabel(row) }}</el-button>
              <el-dropdown v-if="advanced && row.state !== 'notInstalled'" trigger="click" @command="(c: 'logs' | 'config') => c === 'logs' ? emit('logs', row.svc.id) : emit('config', row.svc.id)">
                <el-button text size="small" class="more-btn" :aria-label="t('overview.actions.more', { name: shortServiceName(row.svc) })">
                  <el-icon :size="14"><MoreFilled /></el-icon>
                </el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item command="logs">{{ t('overview.actions.logs') }}</el-dropdown-item>
                    <el-dropdown-item command="config">{{ t('overview.actions.config') }}</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </td>
        </tr>
      </tbody>
    </table>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Check, Download, Loading, MoreFilled, WarningFilled } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useUiModeStore } from '../../stores/uiMode'
import type { ServiceInfo } from '../../api/types'
import { serviceViewState, shortServiceName, type ServiceViewState } from '../../utils/serviceState'
import { runServiceAction } from '../../utils/serviceActions'
import { servicePort } from '../../utils/servicePorts'
import { portsLabel, stateKeyOf } from '../../utils/services'
import { formatBytes, formatPercent } from '../../utils/format'

const emit = defineEmits<{ logs: [id: string]; config: [id: string] }>()

const { t, locale } = useI18n()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const uiMode = useUiModeStore()
const advanced = computed(() => uiMode.isAdvanced)
const localeName = computed(() => String(locale.value))

interface Row { svc: ServiceInfo; state: ServiceViewState; port: string | null }

// Installed services first; the ones still to install go to the bottom.
const rows = computed<Row[]>(() => {
  const all = daemonStore.visibleServices.map(svc => {
    // A service that stopped because its start failed is a problem row, like a crashed one.
    const raw = serviceViewState(svc)
    const state = raw === 'stopped' && svc.startError ? 'crashed' : raw
    // The daemon reports the configured ports ("SMTP 1025 · HTTP 8025"); the settings table is the fallback.
    const port = portsLabel(svc) ?? servicePort(svc.id)?.toString() ?? null
    return { svc, state, port: state === 'notInstalled' ? null : port }
  })
  return [...all.filter(r => r.state !== 'notInstalled'), ...all.filter(r => r.state === 'notInstalled')]
})

function statusLabel(row: Row): string {
  if (row.state === 'crashed' && stateKeyOf(row.svc) === 'startFailed') return t('overview.status.startFailed')
  return t(`overview.status.${row.state}`)
}
</script>

<style scoped>
.sec-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--wdc-space-4);
  margin-bottom: var(--wdc-space-3);
}
.ov-h2 { font-size: 1.2rem; }
.link {
  font-size: 0.87rem;
  color: var(--wdc-text-2);
  text-decoration: underline;
  text-decoration-color: var(--wdc-border-strong);
  text-underline-offset: 0.2em;
}
.link:hover { text-decoration-color: var(--wdc-accent); color: var(--wdc-text); }

.table-wrap { overflow-x: auto; }
.table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.87rem;
}
.table th, .table td {
  padding: var(--wdc-space-2-5) var(--wdc-space-3);
  text-align: left;
  border-bottom: 1px solid var(--wdc-border);
  vertical-align: middle;
}
.table th:first-child, .table td:first-child { padding-left: 0; }
.table th:last-child, .table td:last-child { padding-right: 0; }
.table thead th {
  font-family: var(--wdc-font-display);
  font-weight: 500;
  color: var(--wdc-text-3);
  white-space: nowrap;
  border-bottom-color: var(--wdc-border-strong);
}
.table tbody th {
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.93rem;
  color: var(--wdc-text);
}
.table tbody tr { transition: background-color var(--wdc-dur-micro) var(--wdc-ease-out); }
@media (hover: hover) {
  .table tbody tr:hover { background: var(--wdc-hover); }
}
.num { text-align: right !important; font-variant-numeric: tabular-nums; white-space: nowrap; }
.more-btn { min-width: 28px; padding: 0 var(--wdc-space-1-5) !important; }
.muted { color: var(--wdc-text-2); }
.act { width: 1%; white-space: nowrap; }
.act-row { display: flex; justify-content: flex-end; align-items: center; gap: var(--wdc-space-1); }

.status {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  white-space: nowrap;
}
.st-running .el-icon { color: var(--wdc-status-running); }
.st-crashed { color: var(--wdc-status-error); }
.st-starting .el-icon, .st-stopping .el-icon { color: var(--wdc-status-starting); }
.st-stopped, .st-disabled, .st-notInstalled { color: var(--wdc-text-3); }
/* Empty ring for "stopped": a minus-in-circle reads as "remove". */
.ring {
  width: 10px;
  height: 10px;
  margin: 0 2px;
  border: 2px solid currentColor;
  border-radius: 50%;
  box-sizing: border-box;
}
.row-missing th { color: var(--wdc-text-3); }
.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

.skeleton { border-top: 1px solid var(--wdc-border); }
.skel-row {
  height: 44px;
  border-bottom: 1px solid var(--wdc-border);
  background: linear-gradient(90deg, var(--wdc-surface), var(--wdc-surface-2), var(--wdc-surface));
  background-size: 200% 100%;
  animation: skel 1.4s ease-in-out infinite;
}
@keyframes skel { from { background-position: 100% 0; } to { background-position: -100% 0; } }

.empty {
  display: grid;
  gap: var(--wdc-space-3);
  justify-items: start;
  padding: var(--wdc-space-5) 0;
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
  color: var(--wdc-text-2);
}
.empty-title { font-family: var(--wdc-font-display); font-weight: 500; color: var(--wdc-text); }

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}
</style>
