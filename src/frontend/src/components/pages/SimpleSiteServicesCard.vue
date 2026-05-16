<template>
  <div class="sd-section">
    <h3 class="sd-section-title">{{ t('sites.detail.simple.services.title') }}</h3>
    <div
      v-for="svc in visibleServices"
      :key="svc.id"
      class="sd-service-row"
    >
      <span class="sd-status-dot" :class="stateClass(svc)" />
      <span class="sd-service-name">{{ svc.label }}</span>
      <span class="sd-service-uptime">{{ uptimeLabel(svc) }}</span>
      <el-button
        size="large"
        :loading="restartLoading[svc.id]"
        :disabled="isTransitioning(svc)"
        @click="restartService(svc)"
      >
        {{ isRunning(svc) ? t('sites.detail.simple.services.restart') : t('sites.detail.simple.services.start') }}
      </el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { errorMessage } from '../../utils/errors'

defineOptions({ name: 'SimpleSiteServicesCard' })

// Extracted from SiteDetailSimple.vue — the "Stav služeb" panel summarises
// the services this site depends on (Apache, the chosen PHP version,
// MySQL/MariaDB, Cloudflare tunnel when enabled). Each row exposes a
// Start/Restart button that delegates to servicesStore.{start,restart}.
//
// The parent owns the SiteConfig lookup; we accept only the domain and
// resolve the live SiteInfo via the shared sitesStore so the component
// stays in sync when the parent saves a config change (php version flip
// changes which PHP service appears here).

const props = defineProps<{ domain: string }>()

const { t } = useI18n()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()

const restartLoading = reactive<Record<string, boolean>>({})

const site = computed(() => sitesStore.sites.find(s => s.domain === props.domain) ?? null)

interface ServiceRow {
  id: string
  label: string
  state: number | string
  startedAt?: string
}

const visibleServices = computed<ServiceRow[]>(() => {
  const svcs = daemonStore.services
  const out: ServiceRow[] = []
  const add = (id: string, label: string) => {
    const s = svcs.find(x => x.id === id)
    if (s) out.push({ id: s.id, label, state: s.state ?? 0, startedAt: s.startedAt })
  }
  add('apache', 'Apache')
  if (site.value?.phpVersion) {
    const phpSvc = svcs.find(x => x.id === `php-${site.value!.phpVersion}`)
      ?? svcs.find(x => x.id === 'php')
    if (phpSvc) {
      out.push({
        id: phpSvc.id,
        label: `PHP ${site.value!.phpVersion}`,
        state: phpSvc.state ?? 0,
        startedAt: phpSvc.startedAt,
      })
    }
  }
  const dbSvc = svcs.find(x => x.id === 'mysql') ?? svcs.find(x => x.id === 'mariadb')
  if (dbSvc) {
    out.push({
      id: dbSvc.id,
      label: dbSvc.id === 'mysql' ? 'MySQL' : 'MariaDB',
      state: dbSvc.state ?? 0,
      startedAt: dbSvc.startedAt,
    })
  }
  if (site.value?.cloudflare?.enabled) add('cloudflared', 'Cloudflare Tunnel')
  return out
})

function isRunning(svc: { state: number | string }): boolean {
  return svc.state === 2 || svc.state === 'running'
}
function isTransitioning(svc: { state: number | string }): boolean {
  return svc.state === 1 || svc.state === 3
}
function stateClass(svc: { state: number | string }): string {
  if (isRunning(svc)) return 'dot-running'
  if (isTransitioning(svc)) return 'dot-transition'
  return 'dot-stopped'
}
function uptimeLabel(svc: { state: number | string; startedAt?: string }): string {
  // Three distinct states — the prior version flattened "running with
  // unknown uptime" into the same "Zastaveno" label as a genuinely
  // stopped service, which contradicted the green dot beside it.
  if (!isRunning(svc)) return t('common.stopped')
  if (!svc.startedAt) return t('common.running')
  const ms = Date.now() - new Date(svc.startedAt).getTime()
  const min = Math.floor(ms / 60_000)
  const h = Math.floor(min / 60)
  const d = Math.floor(h / 24)
  if (d > 0) return `${d}d ${h % 24}h`
  if (h > 0) return `${h}h ${min % 60}m`
  if (min > 0) return `${min}m`
  return `<1m`
}

async function restartService(svc: ServiceRow) {
  restartLoading[svc.id] = true
  try {
    if (isRunning(svc)) {
      await servicesStore.restart(svc.id)
      ElMessage.success(t('sites.detail.simple.services.restarted', { name: svc.label }))
    } else {
      await servicesStore.start(svc.id)
      ElMessage.success(t('sites.detail.simple.services.started', { name: svc.label }))
    }
  } catch (e) {
    ElMessage.error(errorMessage(e))
  } finally {
    restartLoading[svc.id] = false
  }
}
</script>

<style scoped>
.sd-section {
  margin-top: 18px;
  padding: 16px;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface-2);
}
.sd-section-title {
  margin: 0 0 12px;
  font-size: 14px;
  font-weight: 600;
  color: var(--wdc-text-2);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.sd-service-row {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto auto;
  gap: 10px;
  align-items: center;
  padding: 10px 0;
  border-bottom: 1px solid var(--wdc-border);
}
.sd-service-row:last-child { border-bottom: 0; }
.sd-service-name { font-size: 14px; font-weight: 500; }
.sd-service-uptime {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}
.sd-status-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}
.dot-running {
  background: var(--el-color-success);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--el-color-success) 25%, transparent);
}
.dot-stopped { background: var(--el-color-info); }
.dot-transition {
  background: var(--el-color-info);
  animation: dot-pulse 1.2s ease-in-out infinite;
}
@keyframes dot-pulse { 0%, 100% { opacity: 0.5 } 50% { opacity: 1 } }

@media (max-width: 760px) {
  .sd-service-row {
    grid-template-columns: 16px minmax(0, 1fr) auto;
  }
  .sd-service-row :deep(.el-button) {
    grid-column: 2 / -1;
    justify-self: stretch;
  }
}
</style>
