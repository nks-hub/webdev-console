<template>
  <section class="stack" aria-labelledby="ov-stack">
    <div class="sec-head">
      <h2 id="ov-stack" class="ov-h2">{{ t('overview.stack.title') }}</h2>
      <router-link class="link" to="/services">{{ t('overview.stack.all') }}</router-link>
    </div>

    <div v-if="!daemonStore.connected && !services.length" class="skeleton" aria-busy="true" :aria-label="t('overview.stack.loading')">
      <div v-for="n in 4" :key="n" class="skel-row" />
    </div>

    <div v-else-if="!services.length" class="empty">
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
          <th scope="col" class="num">{{ t('overview.stack.port') }}</th>
          <th v-if="advanced" scope="col">{{ t('overview.stack.version') }}</th>
          <th v-if="advanced" scope="col" class="num">{{ t('overview.stack.usage') }}</th>
          <th scope="col"><span class="visually-hidden">{{ t('overview.stack.actions') }}</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="svc in services" :key="svc.id" :class="{ 'row-err': state(svc) === 'crashed' }">
          <th scope="row">{{ shortName(svc) }}</th>
          <td>
            <span class="status" :class="`st-${state(svc)}`">
              <el-icon :size="14" aria-hidden="true">
                <Check v-if="state(svc) === 'running'" />
                <Close v-else-if="state(svc) === 'crashed'" />
                <Loading v-else-if="state(svc) === 'starting' || state(svc) === 'stopping'" class="spin" />
                <Remove v-else />
              </el-icon>
              {{ t(`overview.status.${state(svc)}`) }}
            </span>
          </td>
          <td class="num mono">{{ portOf(svc) ?? '—' }}</td>
          <td v-if="advanced" class="mono muted">{{ svc.version || '—' }}</td>
          <td v-if="advanced" class="num mono muted">
            <template v-if="state(svc) === 'running'">{{ formatCpu(svc.cpuPercent) }} · {{ formatMem(svc.memoryBytes ?? 0) }}</template>
            <template v-else>—</template>
          </td>
          <td class="act">
            <div class="act-row">
              <el-button
                v-if="state(svc) === 'crashed'"
                size="small"
                @click="router.push({ path: '/services', query: { filter: 'problems' } })"
              >{{ t('overview.actions.why') }}</el-button>
              <el-button
                v-else-if="state(svc) === 'running'"
                size="small"
                :loading="servicesStore.isBusy(svc.id)"
                :disabled="!daemonStore.connected"
                @click="run(svc, 'restart')"
              >{{ t('overview.actions.restart') }}</el-button>
              <el-button
                v-else-if="state(svc) === 'stopped' || state(svc) === 'disabled'"
                size="small"
                :loading="servicesStore.isBusy(svc.id)"
                :disabled="!daemonStore.connected"
                @click="run(svc, 'start')"
              >{{ t('overview.actions.start') }}</el-button>
              <el-button v-else size="small" loading disabled>{{ t(`overview.status.${state(svc)}`) }}</el-button>
              <el-dropdown v-if="advanced" trigger="click" @command="(c: 'logs' | 'config') => c === 'logs' ? emit('logs', svc.id) : emit('config', svc.id)">
                <el-button text size="small" class="more-btn" :aria-label="t('overview.actions.more', { name: shortName(svc) })">
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
import { Check, Close, Loading, Remove, MoreFilled } from '@element-plus/icons-vue'
import { ElMessage, ElNotification } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { usePluginsStore } from '../../stores/plugins'
import { useUiModeStore } from '../../stores/uiMode'
import type { ServiceInfo } from '../../api/types'

const emit = defineEmits<{ logs: [id: string]; config: [id: string] }>()

const { t } = useI18n()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const pluginsStore = usePluginsStore()
const uiMode = useUiModeStore()
const advanced = computed(() => uiMode.isAdvanced)

const services = computed(() => daemonStore.services.filter(s => pluginsStore.isServiceVisible(s.id)))

type State = 'stopped' | 'starting' | 'running' | 'stopping' | 'crashed' | 'disabled'
const STATES: State[] = ['stopped', 'starting', 'running', 'stopping', 'crashed', 'disabled']
function state(svc: ServiceInfo): State {
  const byNumber = STATES[svc.state ?? -1]
  if (byNumber) return byNumber
  return (STATES as string[]).includes(svc.status ?? '') ? svc.status as State : 'stopped'
}

const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
}
function shortName(svc: ServiceInfo): string {
  return SHORT_NAMES[svc.displayName ?? ''] || svc.displayName || svc.id
}

const KNOWN_PORTS: Record<string, number> = { apache: 80, mysql: 3306, redis: 6379, mailpit: 8025 }
function portOf(svc: ServiceInfo & { port?: number }): number | null {
  return svc.port ?? KNOWN_PORTS[svc.id] ?? null
}

function formatCpu(val?: number): string {
  return `${(val ?? 0).toFixed(1)} %`
}
function formatMem(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(0)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
}

async function run(svc: ServiceInfo, op: 'start' | 'restart') {
  const name = shortName(svc)
  try {
    if (op === 'start') {
      await servicesStore.start(svc.id)
      ElMessage.success(t('toast.serviceStarted', { name }))
    } else {
      await servicesStore.restart(svc.id)
      ElMessage.success(t('overview.restarted', { name }))
    }
  } catch (err) {
    ElNotification({
      title: t('overview.actionFailed', { name }),
      message: err instanceof Error ? err.message : String(err),
      type: 'error',
      duration: 5000,
    })
  }
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
.st-stopped, .st-disabled { color: var(--wdc-text-3); }
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
