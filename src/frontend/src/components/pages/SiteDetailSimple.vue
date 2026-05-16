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
        <el-button size="large" @click="openInBrowser">
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
          size="large"
          :type="apacheRunning ? 'warning' : 'success'"
          :loading="startStopLoading"
          @click="toggleApache"
        >
          {{ apacheRunning ? $t('sites.detail.simple.stop') : $t('sites.detail.simple.start') }}
        </el-button>
      </div>

      <el-divider />

      <!-- Document root -->
      <div class="sd-row sd-row-stack">
        <div class="sd-label-stack">
          <span class="sd-label">
            <el-icon class="sd-row-icon"><FolderOpened /></el-icon>
            {{ $t('sites.documentRoot') }}
          </span>
          <span class="sd-hint">{{ $t('sites.detail.simple.docRootHint') }}</span>
        </div>
        <div class="sd-control-wrap sd-control-wide">
          <el-input
            v-model="documentRoot"
            size="large"
            :placeholder="$t('sites.detail.simple.docRootPlaceholder')"
            @change="onDocRootChange"
          >
            <template #append>
              <el-button :title="$t('sites.edit.browseDocRoot')" @click="pickDocRoot">
                <el-icon><FolderOpened /></el-icon>
              </el-button>
            </template>
          </el-input>
          <Transition name="flash">
            <span v-if="savedDocRoot" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- PHP version -->
      <div class="sd-row sd-row-stack">
        <div class="sd-label-stack">
          <span class="sd-label">
            <el-icon class="sd-row-icon"><Cpu /></el-icon>
            {{ $t('sites.detail.simple.phpVersion') }}
          </span>
          <span class="sd-hint">{{ $t('sites.detail.simple.phpVersionHint') }}</span>
        </div>
        <div class="sd-control-wrap">
          <el-select
            v-model="phpVersion"
            size="large"
            class="sd-control-medium"
            @change="onPhpChange"
          >
            <el-option
              v-if="phpVersion && phpVersion !== 'none' && !phpVersions.includes(phpVersion)"
              :key="phpVersion"
              :label="$t('sites.detail.simple.phpNotInstalled', { v: phpVersion })"
              :value="phpVersion"
              disabled
            />
            <el-option
              v-for="v in phpVersions"
              :key="v"
              :label="v"
              :value="v"
            />
            <el-option :label="$t('sites.phpNone')" value="none" />
          </el-select>
          <Transition name="flash">
            <span v-if="savedPhp" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- SSL switch -->
      <div class="sd-row sd-row-stack">
        <div class="sd-label-stack">
          <span class="sd-label">
            <el-icon class="sd-row-icon"><Lock /></el-icon>
            {{ $t('sites.detail.simple.ssl') }}
          </span>
          <span class="sd-hint">{{ $t('sites.detail.simple.sslHint') }}</span>
        </div>
        <div class="sd-control-wrap">
          <el-switch v-model="sslEnabled" @change="onSslChange" />
          <Transition name="flash">
            <span v-if="savedSsl" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- Bind IP -->
      <div class="sd-row sd-row-stack">
        <div class="sd-label-stack">
          <span class="sd-label">
            <el-icon class="sd-row-icon"><Connection /></el-icon>
            {{ $t('sites.bindIp') }}
          </span>
          <span class="sd-hint">{{ $t('sites.detail.simple.bindIpHint') }}</span>
        </div>
        <div class="sd-control-wrap sd-bind-control">
          <el-select
            v-model="bindAddresses"
            class="bind-address-select"
            size="large"
            multiple
            clearable
            collapse-tags
            collapse-tags-tooltip
            filterable
            :loading="bindAddressOptionsLoading"
            :disabled="bindAddressOptionsLoading || bindAddressOptions.length === 0"
            @change="onBindAddressesChange"
          >
            <el-option
              v-for="opt in bindAddressOptions"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            >
              <span class="bind-opt-label">{{ opt.label }}</span>
              <span
                v-if="opt.wildcard || opt.loopback"
                class="bind-opt-badge"
              >{{ $t('sites.bindIpRecommended') }}</span>
            </el-option>
          </el-select>
          <Transition name="flash">
            <span v-if="savedBind" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- Cloudflare tunnel switch -->
      <div class="sd-row sd-row-stack">
        <div class="sd-label-stack">
          <span class="sd-label">
            <el-icon class="sd-row-icon"><Link /></el-icon>
            {{ $t('sites.detail.simple.tunnel') }}
          </span>
          <span class="sd-hint">{{ $t('sites.detail.simple.tunnelHint') }}</span>
        </div>
        <div class="sd-control-wrap">
          <el-switch v-model="tunnelEnabled" @change="onTunnelChange" />
          <Transition name="flash">
            <span v-if="savedTunnel" class="sd-saved">{{ $t('sites.detail.simple.saved') }}</span>
          </Transition>
        </div>
      </div>

      <!-- Service status — extracted to a self-contained card so this
           component stays focused on site-config editing. -->
      <SimpleSiteServicesCard :domain="domain" />

      <!-- Recent activity (errors + traffic) — extracted to a dedicated
           card component so the activity widget can be reused by other
           dashboards without duplicating its fetch logic. -->
      <SimpleSiteActivityCard :domain="domain" />


      <el-divider />

      <!-- Delete -->
      <div class="sd-danger-row">
        <el-button type="danger" size="large" :icon="WarningIcon" @click="confirmDelete">
          {{ $t('sites.detail.simple.delete') }}
        </el-button>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
defineOptions({ name: 'SiteDetailSimple' })

import { computed, h, onMounted, ref, watch } from 'vue'
import SimpleSiteActivityCard from './SimpleSiteActivityCard.vue'
import SimpleSiteServicesCard from './SimpleSiteServicesCard.vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { FolderOpened, Cpu, Lock, Connection, Link } from '@element-plus/icons-vue'

const WarningIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, [h('path', { d: 'M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z' }), h('line', { x1: '12', y1: '9', x2: '12', y2: '13' }), h('line', { x1: '12', y1: '17', x2: '12.01', y2: '17' })]  ) }
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useI18n } from 'vue-i18n'
import { daemonBaseUrl, fetchBindAddressOptions, fetchPhpVersions, type BindAddressOption } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'

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
const sslEnabled = ref(true)
const bindAddresses = ref<string[]>(['*'])
const bindAddressOptions = ref<BindAddressOption[]>([])
const bindAddressOptionsLoading = ref(false)
const tunnelEnabled = ref(false)
const documentRoot = ref('')

const savedPhp = ref(false)
const savedSsl = ref(false)
const savedBind = ref(false)
const savedTunnel = ref(false)
const savedDocRoot = ref(false)

const startStopLoading = ref(false)

// Service status + recent activity live in dedicated cards:
//  - SimpleSiteServicesCard.vue (Apache/PHP/MySQL/Cloudflare rows)
//  - SimpleSiteActivityCard.vue (errors + traffic sparkline)
// Each owns its own data lifecycle so this component stays focused
// on editable site config.

const apacheRunning = computed(() => {
  const svc = daemonStore.services.find(s => s.id === 'apache' || s.id === 'httpd')
  return svc?.state === 2 || svc?.status === 'running'
})


watch(site, (s) => {
  if (!s) return
  phpVersion.value = s.phpVersion ?? ''
  sslEnabled.value = s.sslEnabled ?? false
  bindAddresses.value = normalizeBindAddresses(s.bindAddresses?.length ? s.bindAddresses : [s.bindAddress || '*'])
  tunnelEnabled.value = s.cloudflare?.enabled ?? false
  documentRoot.value = s.documentRoot ?? ''
}, { immediate: true })

async function loadPhpVersions() {
  try {
    const versions = await fetchPhpVersions()
    phpVersions.value = versions.map(v => v.majorMinor || v.version.split('.').slice(0, 2).join('.') || v.version)
  } catch {
    phpVersions.value = ['8.4', '8.3', '8.2']
  }
}

async function loadBindAddressOptions() {
  bindAddressOptionsLoading.value = true
  try {
    bindAddressOptions.value = await fetchBindAddressOptions()
    for (const current of bindAddresses.value) {
      if (!bindAddressOptions.value.some(o => o.value === current)) {
        bindAddressOptions.value.push({
          value: current,
          label: `${current} (saved, unavailable)`,
          description: 'Saved site value that is not present on this device right now.',
          wildcard: current === '*',
          loopback: false,
        })
      }
    }
  } catch {
    bindAddressOptions.value = []
  } finally {
    bindAddressOptionsLoading.value = false
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
  } catch (e) {
    ElMessage.error(t('siteEditToast.updateFailed', { err: errorMessage(e) }))
  }
}

async function onDocRootChange(v: string) {
  if (!site.value) return
  const trimmed = (v ?? '').trim()
  if (!trimmed || trimmed === site.value.documentRoot) return
  try {
    await sitesStore.update(props.domain, { ...site.value, documentRoot: trimmed })
    flashSaved(savedDocRoot)
  } catch (e) {
    ElMessage.error(t('siteEditToast.updateFailed', { err: errorMessage(e) }))
    documentRoot.value = site.value.documentRoot
  }
}

async function pickDocRoot() {
  const api = (window as unknown as {
    electronAPI?: { showOpenDialog: (o: unknown) => Promise<{ canceled: boolean; filePaths: string[] }> }
  }).electronAPI
  if (!api?.showOpenDialog) {
    ElMessage.info(t('siteEditToast.folderPickerDesktopOnly'))
    return
  }
  const result = await api.showOpenDialog({
    properties: ['openDirectory', 'createDirectory'],
    title: 'Select document root',
  })
  if (!result.canceled && result.filePaths[0]) {
    documentRoot.value = result.filePaths[0]
    await onDocRootChange(result.filePaths[0])
  }
}

async function onSslChange(v: boolean) {
  if (!site.value) return
  try {
    await sitesStore.update(props.domain, { ...site.value, sslEnabled: v })
    flashSaved(savedSsl)
  } catch (e) {
    ElMessage.error(t('siteEditToast.updateFailed', { err: errorMessage(e) }))
    sslEnabled.value = !v
  }
}

function normalizeBindAddresses(values: string[] | undefined): string[] {
  const selected = [...new Set((values ?? []).map(v => String(v).trim()).filter(Boolean))]
  if (selected.length === 0) return []
  if (selected.includes('*')) {
    return selected[selected.length - 1] === '*'
      ? ['*']
      : selected.filter(v => v !== '*')
  }
  return selected
}

async function onBindAddressesChange(v: string[]) {
  if (!site.value) return
  const normalized = normalizeBindAddresses(v)
  bindAddresses.value = normalized
  if (normalized.length === 0) {
    ElMessage.warning(t('siteEditToast.bindRequired'))
    return
  }
  try {
    await sitesStore.update(props.domain, {
      ...site.value,
      bindAddress: normalized[0] ?? '*',
      bindAddresses: normalized,
    })
    flashSaved(savedBind)
    // Surface daemon NIC sanity warnings — operator picking an IP that
    // isn't on any active interface gets a clear message instead of
    // discovering it at Apache restart time.
    for (const w of sitesStore.lastUpdateWarnings) {
      ElMessage.warning({ message: w, duration: 8000, showClose: true })
    }
  } catch (e) {
    ElMessage.error(t('siteEditToast.updateFailed', { err: errorMessage(e) }))
    bindAddresses.value = normalizeBindAddresses(site.value.bindAddresses?.length ? site.value.bindAddresses : [site.value.bindAddress || '*'])
  }
}

async function onTunnelChange(v: boolean) {
  if (!site.value) return
  const existing = site.value.cloudflare ?? { enabled: false, subdomain: '', zoneId: '', zoneName: '', localService: 'localhost:80', protocol: 'http' as const }
  try {
    await sitesStore.update(props.domain, { ...site.value, cloudflare: { ...existing, enabled: v } })
    flashSaved(savedTunnel)
  } catch (e) {
    ElMessage.error(t('siteEditToast.updateFailed', { err: errorMessage(e) }))
    tunnelEnabled.value = !v
  }
}

async function toggleApache() {
  const svc = daemonStore.services.find(s => s.id === 'apache' || s.id === 'httpd')
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
  if (window.electronAPI?.openExternal) {
    ;window.electronAPI.openExternal(url)
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
    await loadBindAddressOptions()
  } finally {
    loading.value = false
  }
})
</script>

<style scoped>
.simple-detail {
  width: min(1500px, calc(100% - 48px));
  margin: 24px auto;
  padding: 22px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  box-shadow: none;
}

.state-box {
  padding: 40px 0;
}

.sd-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 20px;
}

.sd-domain {
  font-size: 20px;
  font-weight: 600;
  word-break: break-all;
}

.sd-row {
  display: grid;
  grid-template-columns: minmax(170px, 0.45fr) minmax(0, 1fr);
  align-items: center;
  gap: 16px;
  padding: 12px 0;
  border-top: 1px solid var(--wdc-border);
}

.sd-row-stack {
  align-items: start;
}

.sd-status-row {
  padding: 14px;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface-2);
}

.sd-label-group {
  display: flex;
  align-items: center;
  gap: 8px;
}

.sd-label {
  color: var(--el-text-color-secondary);
  font-size: 14px;
  min-width: 0;
}

.sd-label-stack {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.sd-label-stack .sd-label {
  color: var(--wdc-text);
  font-weight: 600;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

/* Settings-row icon — tinted to the accent color so each row gets a
   little visual anchor and the 6 rows don't read as a uniform list. */
.sd-row-icon {
  color: var(--wdc-accent);
  font-size: 16px;
  flex-shrink: 0;
}

.sd-hint {
  color: var(--wdc-text-2);
  font-size: 12px;
  line-height: 1.45;
  max-width: 56ch;
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
  min-width: 0;
  justify-content: flex-end;
}

/* All edit controls share a single width budget so the column lines up
   across PHP/SSL/Bind IP/Cloudflare/docroot rows. Was a mix of 110px
   (PHP) and 460px (bind IP) — visually ragged. */
.sd-control-medium {
  width: min(320px, 100%);
}

.sd-control-wide {
  width: 100%;
}

.sd-control-wide :deep(.el-input) {
  width: min(360px, 100%);
}

.sd-bind-control {
  width: 100%;
}

.sd-bind-control :deep(.el-select) {
  width: min(360px, 100%);
}

.sd-bind-control :deep(.bind-address-select .el-tag) {
  background: var(--wdc-accent) !important;
  border-color: var(--wdc-accent) !important;
  color: var(--wdc-bg) !important;
  font-weight: 700;
  min-height: 32px;
}

.sd-bind-control :deep(.bind-address-select .el-tag__close) {
  color: var(--wdc-bg) !important;
  min-width: 32px;
  min-height: 32px;
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

.sd-section {
  margin-top: 18px;
  padding: 16px;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface-2);
}
.sd-section-title { margin: 0 0 12px; font-size: 14px; font-weight: 600; color: var(--wdc-text-2); text-transform: uppercase; letter-spacing: 0.04em; }
/* Service-row + dot-transition styles moved to SimpleSiteServicesCard.vue.
   .sd-status-dot + .dot-running/.dot-stopped stay here because the page
   header status row above still uses them for the Apache pill. */

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

/* Recent activity styles moved to SimpleSiteActivityCard.vue. */

@media (min-width: 900px) {
  .simple-detail {
    display: grid;
    grid-template-columns: minmax(0, 0.95fr) minmax(340px, 0.65fr);
    gap: 0 18px;
  }

  .simple-detail > .sd-header,
  .simple-detail > .sd-status-row,
  .simple-detail > .el-divider,
  .simple-detail > .sd-row,
  .simple-detail > .sd-danger-row {
    grid-column: 1;
  }

  .simple-detail > .sd-section {
    grid-column: 2;
  }

  .simple-detail > .sd-section:first-of-type {
    grid-row: 1 / span 5;
    margin-top: 0;
    position: sticky;
    top: 18px;
    align-self: start;
  }
}

@media (max-width: 760px) {
  .simple-detail {
    width: calc(100% - 32px);
    margin: 18px auto;
    padding: 16px;
  }

  .sd-header {
    align-items: stretch;
    flex-direction: column;
  }

  .sd-header :deep(.el-button) {
    width: 100%;
    min-height: 36px;
  }

  .sd-row {
    grid-template-columns: minmax(0, 1fr);
    gap: 8px;
  }

  .sd-control-wrap {
    justify-content: flex-start;
  }
}
</style>
