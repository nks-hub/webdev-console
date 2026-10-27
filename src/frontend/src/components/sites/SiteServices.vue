<template>
  <section class="svc-section" :aria-labelledby="`${uid}-title`">
    <h2 :id="`${uid}-title`" class="svc-title">{{ t('sites.detail.simple.services.title') }}</h2>
    <p class="svc-note">{{ t('sites.detail.simple.services.shared') }}</p>
    <div v-for="row in rows" :key="row.id" class="svc-row">
      <HealthStatusDot :level="row.level" :pulse="row.transitional" />
      <span class="svc-name">{{ row.label }}</span>
      <span class="svc-status">{{ row.status }}<template v-if="row.uptime"> · {{ row.uptime }}</template></span>
      <el-button
        v-if="row.action"
        size="small"
        :loading="row.busy"
        @click="row.action.run()"
      >{{ row.action.label }}</el-button>
      <p v-if="row.error" class="svc-error">{{ row.error }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import type { ServiceInfo } from '../../api/types'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import { useSiteStateStore } from '../../stores/siteState'
import { formatDuration } from '../../utils/format'
import { installRouteOf, stateKeyOf } from '../../utils/services'
import { serviceViewState, shortServiceName, type ServiceViewState } from '../../utils/serviceState'
import HealthStatusDot from '../shared/HealthStatusDot.vue'

defineOptions({ name: 'SiteServices' })

/**
 * The services this site depends on (the web server, its PHP version, the
 * Cloudflare tunnel when it is on), each with the word for its state and the
 * one button that fits it. The services are shared by all sites; the card says so.
 */
const props = defineProps<{ domain: string }>()

const { t, locale } = useI18n()
const router = useRouter()
const uid = useId()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()
const siteState = useSiteStateStore()

const site = computed(() => sitesStore.sites.find(s => s.domain === props.domain) ?? null)

interface Row {
  id: string
  label: string
  status: string
  uptime: string
  level: 'ok' | 'warn' | 'err' | 'muted'
  transitional: boolean
  busy: boolean
  error: string
  action: { label: string; run: () => unknown } | null
}

const LEVEL: Record<ServiceViewState, Row['level']> = {
  running: 'ok', starting: 'warn', stopping: 'warn', crashed: 'err', disabled: 'err', stopped: 'muted', notInstalled: 'muted',
}

function rowFor(svc: ServiceInfo, label: string): Row {
  const view = serviceViewState(svc)
  const name = shortServiceName(svc)
  const transitional = view === 'starting' || view === 'stopping'
  const action: Row['action'] =
    view === 'running' ? { label: t('sites.detail.simple.services.restart'), run: () => servicesStore.act(svc.id, 'restart', name) }
    : view === 'notInstalled' ? { label: t('sites.empty.install'), run: () => (svc.id === 'apache' ? siteState.install('apache') : router.push(installRouteOf(svc))) }
    : transitional ? null
    : { label: t('sites.detail.simple.services.start'), run: () => servicesStore.act(svc.id, 'start', name) }
  return {
    id: svc.id,
    label,
    status: t(`servicesPage.status.${stateKeyOf(svc)}`),
    uptime: view === 'running' && svc.uptimeSeconds ? formatDuration(svc.uptimeSeconds, locale.value) : '',
    level: LEVEL[view],
    transitional,
    busy: servicesStore.isBusy(svc.id) || (svc.id === 'apache' && (siteState.starting || siteState.installing.has('apache'))),
    error: view === 'running' ? '' : servicesStore.lastError[svc.id] ?? '',
    action,
  }
}

const rows = computed<Row[]>(() => {
  const svcs = daemonStore.services
  const out: Row[] = []
  const apache = svcs.find(x => x.id === 'apache')
  if (apache) out.push(rowFor(apache, 'Apache'))
  else if (siteState.webServer === 'missing') {
    // The daemon lists no web server at all: offer to install it from here.
    out.push(rowFor({ id: 'apache', displayName: 'Apache', installed: false, pid: null, cpuPercent: 0, memoryBytes: 0 }, 'Apache'))
  }
  const php = site.value && siteState.runtimeOf(site.value)
  if (php?.kind === 'php') {
    const wanted = site.value?.phpVersion
    const phpSvc = svcs.find(x => x.id === `php-${wanted}`) ?? svcs.find(x => x.id === 'php')
    if (phpSvc) out.push(rowFor(phpSvc, php.label))
  }
  const tunnel = site.value?.cloudflare?.enabled ? svcs.find(x => x.id === 'cloudflare') : undefined
  if (tunnel) out.push(rowFor(tunnel, t('siteEdit.ov.tunnel')))
  return out
})
</script>

<style scoped>
.svc-title {
  margin: 0 0 var(--wdc-space-1);
  font-size: 0.93rem;
  color: var(--wdc-text-2);
}
.svc-note { margin: 0 0 var(--wdc-space-2); color: var(--wdc-text-3); font-size: 0.8rem; }
.svc-row {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr) auto auto;
  gap: var(--wdc-space-2-5);
  align-items: center;
  padding: var(--wdc-space-2-5) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.svc-row:last-child { border-bottom: 0; }
.svc-name { font-size: 0.93rem; font-weight: 500; }
.svc-status {
  color: var(--wdc-text-2);
  font-size: 0.82rem;
  font-variant-numeric: tabular-nums;
}
.svc-error {
  grid-column: 2 / -1;
  margin: calc(var(--wdc-space-1) * -1) 0 0;
  color: var(--wdc-status-error);
  font-size: 0.8rem;
  overflow-wrap: anywhere;
}

@media (max-width: 760px) {
  .svc-row { grid-template-columns: 16px minmax(0, 1fr) auto; }
  .svc-row :deep(.el-button) { grid-column: 2 / -1; justify-self: stretch; }
}
</style>
