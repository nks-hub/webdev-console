<template>
  <div class="simple-detail">
    <div v-if="loading" class="state-box">
      <el-skeleton :rows="6" animated />
    </div>

    <div v-else-if="!site" class="state-box">
      <el-empty :description="`Site '${domain}' not found.`" />
    </div>

    <template v-else>
      <!-- Header row -->
      <div class="sd-header">
        <div class="sd-domain">{{ domain }}</div>
        <el-button size="small" @click="openInBrowser">
          {{ $t('sites.detail.simple.open') }}
        </el-button>
      </div>

      <!-- Status row -->
      <div class="sd-row sd-status-row">
        <div class="sd-label-group">
          <span class="sd-label">{{ $t('sites.detail.simple.serverLabel') }}</span>
          <span class="sd-status-dot" :class="apacheRunning ? 'dot-running' : 'dot-stopped'" />
          <span class="sd-status-text" :class="apacheRunning ? 'text-running' : 'text-stopped'">
            {{ apacheRunning ? $t('sites.detail.simple.status.running') : $t('sites.detail.simple.status.stopped') }}
          </span>
        </div>
        <el-button
          size="small"
          :type="apacheRunning ? 'warning' : 'success'"
          :loading="startStopLoading"
          @click="toggleApache"
        >
          {{ apacheRunning ? $t('sites.detail.simple.stop') : $t('sites.detail.simple.start') }}
        </el-button>
      </div>

      <el-divider />

      <!-- PHP version -->
      <div class="sd-row">
        <span class="sd-label">{{ $t('sites.detail.simple.phpVersion') }}</span>
        <div class="sd-control-wrap">
          <el-select
            v-model="phpVersion"
            size="small"
            style="width: 110px"
            @change="onPhpChange"
          >
            <el-option v-for="v in phpVersions" :key="v" :label="v" :value="v" />
          </el-select>
          <Transition name="flash">
            <span v-if="savedPhp" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- SSL switch -->
      <div class="sd-row">
        <span class="sd-label">{{ $t('sites.detail.simple.ssl') }}</span>
        <div class="sd-control-wrap">
          <el-switch v-model="sslEnabled" @change="onSslChange" />
          <Transition name="flash">
            <span v-if="savedSsl" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- Cloudflare tunnel switch -->
      <div class="sd-row">
        <span class="sd-label">{{ $t('sites.detail.simple.tunnel') }}</span>
        <div class="sd-control-wrap">
          <el-switch v-model="tunnelEnabled" @change="onTunnelChange" />
          <Transition name="flash">
            <span v-if="savedTunnel" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- Service status -->
      <div class="sd-section">
        <h3 class="sd-section-title">{{ $t('sites.detail.simple.services.title') }}</h3>
        <div class="sd-service-row" v-for="svc in visibleServices" :key="svc.id">
          <span class="sd-status-dot" :class="stateClass(svc)" />
          <span class="sd-service-name">{{ svc.label }}</span>
          <span class="sd-service-uptime">{{ uptimeLabel(svc) }}</span>
          <el-button
            size="small"
            :loading="restartLoading[svc.id]"
            :disabled="isTransitioning(svc)"
            @click="restartService(svc)"
          >
            {{ isRunning(svc) ? $t('sites.detail.simple.services.restart') : $t('sites.detail.simple.services.start') }}
          </el-button>
        </div>
      </div>

      <el-divider />

      <!-- Delete -->
      <div class="sd-danger-row">
        <el-button type="danger" plain size="default" :icon="WarningIcon" @click="confirmDelete">
          {{ $t('sites.detail.simple.delete') }}
        </el-button>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
defineOptions({ name: 'SiteDetailSimple' })

import { computed, h, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'

const WarningIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, [h('path', { d: 'M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z' }), h('line', { x1: '12', y1: '9', x2: '12', y2: '13' }), h('line', { x1: '12', y1: '17', x2: '12.01', y2: '17' })]  ) }
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useI18n } from 'vue-i18n'

const props = defineProps<{ domain: string }>()

const { t } = useI18n()
const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()

const loading = ref(false)
const phpVersions = ref<string[]>([])

const site = computed(() => sitesStore.sites.find(s => s.domain === props.domain) ?? null)

const phpVersion = ref('')
const sslEnabled = ref(false)
const tunnelEnabled = ref(false)

const savedPhp = ref(false)
const savedSsl = ref(false)
const savedTunnel = ref(false)

const startStopLoading = ref(false)
const restartLoading = reactive<Record<string, boolean>>({})

const visibleServices = computed(() => {
  const svcs = daemonStore.services
  const out: Array<{ id: string; label: string; state: number | string; startedAt?: string }> = []
  const add = (id: string, label: string) => {
    const s = svcs.find((x: any) => x.id === id)
    if (s) out.push({ id: s.id, label, state: s.state ?? 0, startedAt: s.startedAt })
  }
  add('apache', 'Apache')
  if (site.value?.phpVersion) {
    const phpSvc = svcs.find((x: any) => x.id === `php-${site.value!.phpVersion}`) ?? svcs.find((x: any) => x.id === 'php')
    if (phpSvc) out.push({ id: phpSvc.id, label: `PHP ${site.value!.phpVersion}`, state: phpSvc.state ?? 0, startedAt: phpSvc.startedAt })
  }
  const mysqlSvc = svcs.find((x: any) => x.id === 'mysql') ?? svcs.find((x: any) => x.id === 'mariadb')
  if (mysqlSvc) out.push({ id: mysqlSvc.id, label: mysqlSvc.id === 'mysql' ? 'MySQL' : 'MariaDB', state: mysqlSvc.state ?? 0, startedAt: mysqlSvc.startedAt })
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
  if (!isRunning(svc) || !svc.startedAt) return t('common.stopped')
  const ms = Date.now() - new Date(svc.startedAt).getTime()
  const min = Math.floor(ms / 60_000)
  const h = Math.floor(min / 60)
  const d = Math.floor(h / 24)
  if (d > 0) return `${d}d ${h % 24}h`
  if (h > 0) return `${h}h ${min % 60}m`
  if (min > 0) return `${min}m`
  return `<1m`
}

async function restartService(svc: { id: string; label: string; state: number | string }) {
  restartLoading[svc.id] = true
  try {
    if (isRunning(svc)) {
      await servicesStore.restart(svc.id)
      ElMessage.success(t('sites.detail.simple.services.restarted', { name: svc.label }))
    } else {
      await servicesStore.start(svc.id)
      ElMessage.success(t('sites.detail.simple.services.started', { name: svc.label }))
    }
  } catch (e: any) {
    ElMessage.error(e?.message || String(e))
  } finally {
    restartLoading[svc.id] = false
  }
}

const apacheRunning = computed(() => {
  const svc = daemonStore.services.find((s: any) => s.id === 'apache' || s.id === 'httpd')
  return svc?.state === 2 || svc?.status === 'running'
})

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  if (urlPort && /^\d+$/.test(urlPort)) return `http://localhost:${urlPort}`
  const p = (window as any).daemonApi?.getPort?.()
  return `http://localhost:${typeof p === 'number' ? p : 5199}`
}

watch(site, (s) => {
  if (!s) return
  phpVersion.value = s.phpVersion ?? ''
  sslEnabled.value = s.sslEnabled ?? false
  tunnelEnabled.value = s.cloudflare?.enabled ?? false
}, { immediate: true })

async function loadPhpVersions() {
  try {
    const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: sitesStore.authHeaders() })
    if (r.ok) {
      const versions = await r.json()
      phpVersions.value = versions.map((v: any) => v.majorMinor || v.version?.split('.').slice(0, 2).join('.') || v.version)
    }
  } catch {
    phpVersions.value = ['8.4', '8.3', '8.2']
  }
}

function flashSaved(flag: { value: boolean }) {
  flag.value = true
  setTimeout(() => { flag.value = false }, 1500)
}

async function onPhpChange(v: string) {
  if (!site.value) return
  try {
    await sitesStore.update(props.domain, { ...site.value, phpVersion: v })
    flashSaved(savedPhp)
  } catch (e: any) {
    ElMessage.error(`Update failed: ${e?.message || e}`)
  }
}

async function onSslChange(v: boolean) {
  if (!site.value) return
  try {
    await sitesStore.update(props.domain, { ...site.value, sslEnabled: v })
    flashSaved(savedSsl)
  } catch (e: any) {
    ElMessage.error(`Update failed: ${e?.message || e}`)
    sslEnabled.value = !v
  }
}

async function onTunnelChange(v: boolean) {
  if (!site.value) return
  const existing = site.value.cloudflare ?? { enabled: false, subdomain: '', zoneId: '', zoneName: '', localService: 'localhost:80', protocol: 'http' as const }
  try {
    await sitesStore.update(props.domain, { ...site.value, cloudflare: { ...existing, enabled: v } })
    flashSaved(savedTunnel)
  } catch (e: any) {
    ElMessage.error(`Update failed: ${e?.message || e}`)
    tunnelEnabled.value = !v
  }
}

async function toggleApache() {
  const svc = daemonStore.services.find((s: any) => s.id === 'apache' || s.id === 'httpd')
  if (!svc) return
  startStopLoading.value = true
  try {
    if (apacheRunning.value) {
      await servicesStore.stop(svc.id)
    } else {
      await servicesStore.start(svc.id)
    }
  } finally {
    startStopLoading.value = false
  }
}

function openInBrowser() {
  if (!site.value) return
  const s = site.value
  const proto = s.sslEnabled ? 'https' : 'http'
  const port = s.sslEnabled ? (s.httpsPort || 443) : (s.httpPort || 80)
  const portSuffix = (s.sslEnabled && port === 443) || (!s.sslEnabled && port === 80) ? '' : `:${port}`
  const url = `${proto}://${s.domain}${portSuffix}`
  if ((window as any).electronAPI?.openExternal) {
    ;(window as any).electronAPI.openExternal(url)
  } else {
    window.open(url, '_blank')
  }
}

async function confirmDelete() {
  if (!site.value) return
  try {
    await ElMessageBox.confirm(
      t('sites.detail.simple.deleteConfirm', { domain: props.domain }),
      t('sites.detail.simple.delete'),
      { type: 'warning', confirmButtonText: t('common.delete'), confirmButtonClass: 'el-button--danger' }
    )
    await sitesStore.remove(props.domain)
    ElMessage.success(t('common.delete'))
    router.push('/sites')
  } catch { /* cancelled */ }
}

onMounted(async () => {
  loading.value = true
  try {
    await sitesStore.load()
    await loadPhpVersions()
  } finally {
    loading.value = false
  }
})
</script>

<style scoped>
.simple-detail {
  max-width: 520px;
  margin: 32px auto;
  padding: 28px 32px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: 16px;
  box-shadow: 0 6px 24px rgba(0, 0, 0, 0.18);
}

.state-box {
  padding: 40px 0;
}

.sd-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 20px;
}

.sd-domain {
  font-size: 20px;
  font-weight: 600;
  word-break: break-all;
}

.sd-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 0;
}

.sd-status-row {
  padding: 12px 0;
}

.sd-label-group {
  display: flex;
  align-items: center;
  gap: 8px;
}

.sd-label {
  color: var(--el-text-color-secondary);
  font-size: 14px;
  min-width: 140px;
}

.sd-status-row .sd-label {
  min-width: 80px;
}

.sd-status-row .sd-label-group {
  flex: 1;
  min-width: 0;
}

.text-running {
  color: var(--el-color-success);
  font-weight: 500;
}

.text-stopped {
  color: var(--el-text-color-regular);
}

.sd-control-wrap {
  display: flex;
  align-items: center;
  gap: 10px;
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

.dot-stopped {
  background: var(--el-color-info);
}

.sd-status-text {
  font-size: 14px;
}

.sd-saved {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  font-weight: 600;
  color: var(--el-color-success);
}

.sd-saved::before {
  content: '\2713';
  font-size: 11px;
}

.sd-danger-row {
  display: flex;
  justify-content: flex-start;
  padding-top: 8px;
}

.sd-section { margin-top: 20px; padding-top: 16px; border-top: 1px solid var(--wdc-border); }
.sd-section-title { margin: 0 0 12px; font-size: 14px; font-weight: 600; color: var(--wdc-text-2); text-transform: uppercase; letter-spacing: 0.04em; }
.sd-service-row {
  display: grid;
  grid-template-columns: 16px 1fr auto auto;
  gap: 10px;
  align-items: center;
  padding: 8px 0;
}
.sd-service-name { font-size: 14px; font-weight: 500; }
.sd-service-uptime { color: var(--el-text-color-secondary); font-size: 12px; font-variant-numeric: tabular-nums; }
.dot-transition { background: var(--el-color-info); animation: dot-pulse 1.2s ease-in-out infinite; }
@keyframes dot-pulse { 0%,100% { opacity: 0.5 } 50% { opacity: 1 } }

.flash-enter-active {
  transition: opacity 0.2s ease, transform 0.2s ease;
}
.flash-leave-active {
  transition: opacity 0.4s ease, transform 0.4s ease;
}

.flash-enter-from {
  opacity: 0;
  transform: translateY(6px);
}
.flash-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
