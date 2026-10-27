<template>
  <div class="sd-section">
    <h2 class="sd-section-title">{{ t('sites.detail.simple.services.title') }}</h2>
    <div
      v-for="svc in visibleServices"
      :key="svc.id"
      class="sd-service-row"
    >
      <HealthStatusDot :level="stateToLevel(svc)" :pulse="isTransitioning(svc)" />
      <span class="sd-service-name">{{ svc.label }}</span>
      <span class="sd-service-uptime">{{ svc.state === 'missing' ? t('sites.empty.missing') : uptimeLabel(svc) }}</span>
      <el-button
        v-if="svc.state === 'missing'"
        size="small"
        :loading="siteState.installing.has('apache')"
        @click="siteState.install('apache')"
      >{{ t('sites.banner.installApache') }}</el-button>
      <el-button
        v-else
        size="small"
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
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { useSiteStateStore } from '../../stores/siteState'
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
const siteState = useSiteStateStore()

const restartLoading = reactive<Record<string, boolean>>({})

const site = computed(() => sitesStore.sites.find(s => s.domain === props.domain) ?? null)

interface ServiceRow {
  id: string
  label: string
  /** Service state, or 'missing' for a web server that is not installed. */
  state: number | string
  startedAt?: string
}

const visibleServices = computed<ServiceRow[]>(() => {
  const svcs = daemonStore.services
  const out: ServiceRow[] = []
  const apache = svcs.find(x => x.id === 'apache')
  if (apache) out.push({ id: apache.id, label: 'Apache', state: apache.state ?? 0, startedAt: apache.startedAt })
  else if (siteState.webServer === 'missing') out.push({ id: 'apache', label: 'Apache', state: 'missing' })
  const php = site.value?.phpVersion
  if (php && php !== 'none') {
    const phpSvc = svcs.find(x => x.id === `php-${php}`) ?? svcs.find(x => x.id === 'php')
    if (phpSvc) out.push({ id: phpSvc.id, label: `PHP ${php}`, state: phpSvc.state ?? 0, startedAt: phpSvc.startedAt })
  }
  const tunnel = site.value?.cloudflare?.enabled ? svcs.find(x => x.id === 'cloudflare') : undefined
  if (tunnel) out.push({ id: tunnel.id, label: t('siteEdit.ov.tunnel'), state: tunnel.state ?? 0, startedAt: tunnel.startedAt })
  return out
})

function isRunning(svc: { state: number | string }): boolean {
  return svc.state === 2 || svc.state === 'running'
}
function isTransitioning(svc: { state: number | string }): boolean {
  return svc.state === 1 || svc.state === 3
}
function stateToLevel(svc: { state: number | string }): 'ok' | 'warn' | 'err' | 'muted' {
  if (isRunning(svc)) return 'ok'
  if (isTransitioning(svc)) return 'warn'
  return 'muted'
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
.sd-section-title {
  margin: 0 0 var(--wdc-space-3);
  font-size: 0.93rem;
  color: var(--wdc-text-2);
}
.sd-service-row {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto auto;
  gap: var(--wdc-space-2-5);
  align-items: center;
  padding: var(--wdc-space-2-5) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.sd-service-row:last-child { border-bottom: 0; }
.sd-service-name { font-size: 0.93rem; font-weight: 500; }
.sd-service-uptime {
  color: var(--wdc-text-2);
  font-size: 0.82rem;
  font-variant-numeric: tabular-nums;
}
/* Per-service dot delegated to HealthStatusDot shared primitive
   (plan §6). State→level mapping kept in stateToLevel() above. */

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
