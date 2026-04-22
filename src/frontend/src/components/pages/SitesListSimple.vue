<template>
  <div class="sites-simple">
    <div class="simple-header">
      <h1 class="page-title">{{ $t('sites.title') }}</h1>
      <el-button type="primary" size="small" @click="emit('create')">{{ $t('sites.card.newSite') }}</el-button>
    </div>

    <div v-if="sitesStore.loading" v-loading="true" class="loading-wrap" />

    <el-empty
      v-else-if="sitesStore.sites.length === 0"
      :description="$t('sites.card.welcomeSubtext')"
      :image-size="80"
    >
      <template #description>
        <p class="empty-title">{{ $t('sites.card.welcomeTitle') }}</p>
        <p class="empty-sub">{{ $t('sites.card.welcomeSubtext') }}</p>
      </template>
      <el-button type="primary" size="large" @click="emit('create')">{{ $t('sites.create') }}</el-button>
    </el-empty>

    <div v-else class="card-grid">
      <el-card
        v-for="site in sitesStore.sites"
        :key="site.domain"
        class="site-card"
        shadow="hover"
      >
        <div class="card-body" @click="navigateToSite(site.domain)">
          <div class="card-title">{{ site.domain }}</div>

          <div class="card-status">
            <span class="status-dot" :class="apacheRunning ? 'dot-green' : 'dot-red'" />
            <span class="status-text">{{
              apacheRunning ? $t('sites.card.running') : $t('sites.card.stopped')
            }}</span>
          </div>

          <div class="card-badges">
            <el-tag
              v-if="site.phpVersion && site.phpVersion !== 'none'"
              size="small"
              effect="dark"
              class="badge-php"
            >PHP {{ site.phpVersion }}</el-tag>
            <el-tag
              v-if="site.sslEnabled"
              size="small"
              type="success"
              effect="dark"
            >HTTPS</el-tag>
            <el-tag
              v-if="site.cloudflare?.enabled"
              size="small"
              type="warning"
              effect="dark"
            >{{ $t('sites.simple.cloudflareTunnel') }}</el-tag>
          </div>

          <div v-if="activityMap[site.domain]" class="card-activity">
            <MiniSparkline :values="activityMap[site.domain].hourlyHits" :width="120" :height="24" />
            <span class="card-hits mono">{{ activityMap[site.domain].totalHits }} hits</span>
            <span v-if="activityMap[site.domain].errorCount > 0" class="card-errors mono">
              · {{ activityMap[site.domain].errorCount }} err
            </span>
          </div>
          <div class="card-lasthit">{{ relativeTime(activityMap[site.domain]?.lastHitIso ?? null) }}</div>
        </div>

        <div class="card-actions" @click.stop>
          <el-button size="small" type="primary" :icon="ExternalLinkIcon" @click="openSite(site)">{{ $t('sites.card.open') }}</el-button>

          <el-button
            v-if="apacheRunning"
            size="small"
            circle
            :icon="StopIcon"
            :loading="toggling"
            :title="$t('sites.card.stop')"
            @click="stopApache"
          />
          <el-button
            v-else
            size="small"
            circle
            type="success"
            :icon="PlayIcon"
            :loading="toggling"
            :title="$t('sites.card.start')"
            @click="startApache"
          />

          <!-- Task 01: enable/disable toggle. Rests next to the three-dots
               menu so the user gets one-click on/off without the menu. -->
          <el-tooltip
            :content="site.enabled === false ? 'Site disabled — vhost removed, config preserved' : 'Site enabled'"
            placement="top"
          >
            <el-switch
              :model-value="site.enabled !== false"
              :loading="togglingEnabled === site.domain"
              size="small"
              @change="toggleSiteEnabled(site, $event)"
            />
          </el-tooltip>

          <!-- Task 01: teleported=true + preventOverflow popper options so
               the dropdown never clips at viewport edges (previously the
               menu opened off-screen on narrow viewports / mobile). -->
          <el-dropdown
            trigger="click"
            :teleported="true"
            :popper-options="{ modifiers: [{ name: 'preventOverflow', options: { boundary: 'viewport', padding: 8 } }] }"
            @command="(cmd: string) => handleCommand(cmd, site)"
          >
            <el-button size="small" circle :aria-label="$t('sites.card.moreActions', { domain: site.domain })"><el-icon><MoreFilled /></el-icon></el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="reveal">
                  <el-icon><FolderOpened /></el-icon> {{ $t('sites.card.revealFolder') }}
                </el-dropdown-item>
                <el-dropdown-item command="duplicate">
                  <el-icon><CopyDocument /></el-icon> {{ $t('sites.card.duplicate') }}
                </el-dropdown-item>
                <el-dropdown-item command="restart" :disabled="restarting">
                  <el-icon v-if="restarting" class="is-loading"><RefreshRight /></el-icon>
                  <el-icon v-else><RefreshRight /></el-icon>
                  {{ $t('sites.card.restart') }}
                </el-dropdown-item>
                <el-dropdown-item command="delete" divided class="danger-item">{{ $t('sites.card.delete') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-card>
    </div>
  </div>

  <!-- Duplicate dialog -->
  <el-dialog
    v-model="duplicateDialog.visible"
    :title="$t('sites.card.duplicateTitle')"
    width="480"
  >
    <el-form label-position="top" size="small">
      <el-form-item :label="$t('sites.card.duplicateNewDomain')">
        <el-input v-model="duplicateDialog.newDomain" />
      </el-form-item>
      <el-form-item :label="$t('sites.card.duplicateCopyFiles')">
        <el-radio-group v-model="duplicateDialog.copyFiles">
          <el-radio value="all">{{ $t('sites.card.copyFilesAll') }}</el-radio>
          <el-radio value="top">{{ $t('sites.card.copyFilesTop') }}</el-radio>
          <el-radio value="empty">{{ $t('sites.card.copyFilesEmpty') }}</el-radio>
        </el-radio-group>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button :disabled="duplicating" @click="duplicateDialog.visible = false">{{ $t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="duplicating" @click="confirmDuplicate">{{ $t('sites.card.duplicate') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, h, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessageBox, ElMessage } from 'element-plus'
import { MoreFilled, FolderOpened, CopyDocument, RefreshRight } from '@element-plus/icons-vue'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { startService, stopService, duplicateSite, daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import type { SiteInfo } from '../../api/types'
import MiniSparkline from '../common/MiniSparkline.vue'

const ExternalLinkIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, [h('path', { d: 'M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6' }), h('polyline', { points: '15 3 21 3 21 9' }), h('line', { x1: '10', y1: '14', x2: '21', y2: '3' })]) }
const PlayIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'currentColor' }, [h('polygon', { points: '5 3 19 12 5 21 5 3' })]) }
const StopIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'currentColor' }, [h('rect', { x: '3', y: '3', width: '18', height: '18', rx: '2' })]) }

const { t: $t } = useI18n()

const emit = defineEmits<{ (e: 'create'): void }>()

const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()

const toggling = ref(false)
const duplicating = ref(false)
const restarting = ref(false)
// Task 01: per-row enable/disable toggle — track which domain is
// currently switching so the other rows' switches stay interactive.
const togglingEnabled = ref<string | null>(null)

async function toggleSiteEnabled(site: { domain: string; enabled?: boolean }, value: boolean | string | number) {
  const enabled = value === true
  togglingEnabled.value = site.domain
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(site.domain)}/enabled`, {
      method: 'PATCH',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    ElMessage.success(enabled ? `Site ${site.domain} enabled` : `Site ${site.domain} disabled`)
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(`Toggle failed: ${e instanceof Error ? e.message : e}`)
  } finally {
    togglingEnabled.value = null
  }
}

const apacheRunning = computed(() =>
  daemonStore.services.some(
    s => s.id === 'apache' && (s.state === 2 || s.status === 'running')
  )
)

// ── Activity / sparkline ─────────────────────────────────────────────

interface SiteActivity {
  hourlyHits: number[]
  totalHits: number
  errorCount: number
  lastHitIso: string | null
  loadedAt: number
}

const activityMap = ref<Record<string, SiteActivity>>({})


async function loadActivityForSite(domain: string) {
  const existing = activityMap.value[domain]
  if (existing && Date.now() - existing.loadedAt < 5 * 60_000) return

  try {
    const [metricsR, errorsR] = await Promise.allSettled([
      fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/metrics/history?minutes=1440&limit=24`, { headers: sitesStore.authHeaders() }),
      fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/logs/errors?limit=100`, { headers: sitesStore.authHeaders() }),
    ])

    let hourlyHits: number[] = []
    let totalHits = 0
    let lastHitIso: string | null = null

    type MetricSample = { requests?: number; hits?: number; count?: number; timestamp?: string }
    if (metricsR.status === 'fulfilled' && metricsR.value.ok) {
      const data: unknown = await metricsR.value.json()
      const samples: MetricSample[] = Array.isArray(data)
        ? data
        : ((data as { samples?: MetricSample[] })?.samples ?? [])
      hourlyHits = samples.map(s => s.requests ?? s.hits ?? s.count ?? 0)
      totalHits = hourlyHits.reduce((a, b) => a + b, 0)
      for (let i = samples.length - 1; i >= 0; i--) {
        const hits = samples[i].requests ?? samples[i].hits ?? 0
        if (hits > 0) { lastHitIso = samples[i].timestamp ?? null; break }
      }
    }

    let errorCount = 0
    if (errorsR.status === 'fulfilled' && errorsR.value.ok) {
      const data: unknown = await errorsR.value.json()
      const entries: Array<{ timestamp?: string }> = Array.isArray(data)
        ? data
        : ((data as { entries?: Array<{ timestamp?: string }> })?.entries ?? [])
      const cutoff = Date.now() - 24 * 60 * 60 * 1000
      errorCount = entries.filter(e => {
        if (!e.timestamp) return true
        const t = new Date(e.timestamp).getTime()
        return !isNaN(t) && t > cutoff
      }).length
    }

    activityMap.value[domain] = { hourlyHits, totalHits, errorCount, lastHitIso, loadedAt: Date.now() }
  } catch {
    // silent — empty activity shown
  }
}

function relativeTime(iso: string | null): string {
  if (!iso) return $t('sites.card.neverVisited')
  const diff = Date.now() - new Date(iso).getTime()
  if (isNaN(diff)) return $t('sites.card.neverVisited')
  const min = Math.floor(diff / 60_000)
  if (min < 1) return $t('sites.card.justNow')
  if (min < 60) return $t('sites.card.minutesAgo', { n: min })
  const h = Math.floor(min / 60)
  if (h < 24) return $t('sites.card.hoursAgo', { n: h })
  const d = Math.floor(h / 24)
  return $t('sites.card.daysAgo', { n: d })
}

watch(() => sitesStore.sites, (list) => {
  Promise.allSettled(list.map(s => loadActivityForSite(s.domain)))
}, { immediate: true })

// ── Duplicate dialog ──────────────────────────────────────────────────

const duplicateDialog = ref<{
  visible: boolean
  sourceDomain: string
  newDomain: string
  copyFiles: 'all' | 'top' | 'empty'
}>({
  visible: false,
  sourceDomain: '',
  newDomain: '',
  copyFiles: 'all',
})

function openDuplicateDialog(domain: string) {
  duplicateDialog.value = {
    visible: true,
    sourceDomain: domain,
    newDomain: `copy-of-${domain}`,
    copyFiles: 'all',
  }
}

async function confirmDuplicate() {
  const { sourceDomain, newDomain, copyFiles } = duplicateDialog.value
  duplicating.value = true
  try {
    ElMessage.info($t('sites.card.duplicating'))
    await duplicateSite(sourceDomain, newDomain, copyFiles)
    ElMessage.success($t('sites.card.duplicated', { name: newDomain }))
    duplicateDialog.value.visible = false
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(errorMessage(e))
  } finally {
    duplicating.value = false
  }
}

// ── File reveal ───────────────────────────────────────────────────────

function revealInFolder(docroot: string) {
  if (window.electronAPI?.revealInFolder) {
    window.electronAPI.revealInFolder(docroot)
  } else {
    // Browser dev fallback — packaged Electron always has the preload
    // surface, so this only fires when the renderer is loaded outside
    // an Electron BrowserWindow (e.g. `vite dev` in Chrome).
    window.open(`file://${docroot}`)
  }
}

// ── Mount / navigation / site actions ────────────────────────────────

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.load()
  }
})

function navigateToSite(domain: string) {
  void router.push(`/sites/${encodeURIComponent(domain)}/edit`)
}

function openSite(site: SiteInfo) {
  const proto = site.sslEnabled ? 'https' : 'http'
  const port = site.sslEnabled ? (site.httpsPort || 443) : (site.httpPort || 80)
  const portSuffix = (site.sslEnabled && port === 443) || (!site.sslEnabled && port === 80) ? '' : `:${port}`
  window.open(`${proto}://${site.domain}${portSuffix}`, '_blank')
}

async function startApache() {
  toggling.value = true
  try {
    await startService('apache')
  } catch (e) {
    ElMessage.error(`Start failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

async function stopApache() {
  toggling.value = true
  try {
    await stopService('apache')
  } catch (e) {
    ElMessage.error(`Stop failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

async function handleCommand(cmd: string, site: SiteInfo) {
  if (cmd === 'reveal') {
    revealInFolder(site.documentRoot)
    return
  }

  if (cmd === 'duplicate') {
    openDuplicateDialog(site.domain)
    return
  }

  if (cmd === 'restart') {
    try {
      await ElMessageBox.confirm(
        $t('sites.card.restartConfirm'),
        $t('sites.card.restart'),
        { type: 'warning', confirmButtonText: $t('sites.card.restart') }
      )
    } catch {
      return
    }
    restarting.value = true
    try {
      await stopService('apache')
      await startService('apache')
      ElMessage.success($t('sites.card.restarted'))
    } catch (e) {
      ElMessage.error(`Restart failed: ${errorMessage(e)}`)
    } finally {
      restarting.value = false
    }
    return
  }

  if (cmd === 'delete') {
    try {
      await ElMessageBox.confirm(
        $t('sites.card.deleteConfirm', { domain: site.domain }),
        $t('sites.card.delete'),
        { type: 'warning', confirmButtonText: $t('sites.card.delete'), confirmButtonClass: 'el-button--danger' }
      )
    } catch {
      return
    }
    try {
      await sitesStore.remove(site.domain)
      ElMessage.success(`${site.domain} deleted`)
    } catch (e) {
      ElMessage.error(`Delete failed: ${errorMessage(e)}`)
    }
  }
}
</script>

<style scoped>
.sites-simple {
  padding: 24px;
  min-height: 100%;
  background: var(--wdc-bg);
}

.simple-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 24px;
}

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.loading-wrap {
  height: 200px;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 16px;
}

.site-card {
  cursor: default;
  border-radius: 12px !important;
  transition: box-shadow 0.2s ease, transform 0.2s ease;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

.site-card:hover {
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.28);
  transform: scale(1.02);
}

:deep(.el-card__body) {
  padding: 20px;
}

.card-body {
  cursor: pointer;
  padding-bottom: 12px;
}

.card-title {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin-bottom: 8px;
  word-break: break-all;
}

.card-status {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 10px;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.dot-green {
  background: #22c55e;
  animation: pulse-green 2s ease-in-out infinite;
}
.dot-red { background: #ef4444; }

@keyframes pulse-green {
  0%, 100% { box-shadow: 0 0 0 0 rgba(34, 197, 94, 0.6); }
  50%       { box-shadow: 0 0 0 5px rgba(34, 197, 94, 0); }
}

.status-text {
  font-size: 0.8rem;
  color: var(--wdc-text-2);
}

.card-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.badge-php {
  background: #4f5b93 !important;
  border-color: #4f5b93 !important;
  color: #fff !important;
  font-weight: 700 !important;
  font-size: 0.68rem !important;
}

.card-activity {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  padding: 6px 0;
}

.card-hits {
  color: var(--el-text-color-primary);
  font-weight: 500;
}

.card-errors {
  color: var(--el-color-danger);
}

.card-lasthit {
  font-size: 11px;
  color: var(--el-text-color-secondary);
  margin-bottom: 8px;
}

.mono {
  font-family: var(--el-font-family-mono, monospace);
}

.card-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  padding-top: 12px;
  border-top: 1px solid var(--wdc-border);
  margin-top: 4px;
}

.empty-title {
  font-size: 1rem;
  font-weight: 600;
  color: var(--wdc-text);
  margin-bottom: 4px;
}

.empty-sub {
  font-size: 0.85rem;
  color: var(--wdc-text-2);
  margin-bottom: 12px;
}

.danger-item {
  color: var(--el-color-danger) !important;
}
</style>
