<template>
  <div class="sites-simple">
    <div class="simple-header">
      <div class="simple-header-title">
        <h1 class="page-title">
          {{ $t('sites.title') }}
          <span v-if="sitesStore.sites.length > 0" class="page-title-count">
            {{ sitesStore.sites.length }}
          </span>
        </h1>
        <p v-if="sitesStore.sites.length > 0" class="page-subtitle">
          {{ $t('sites.simple.subtitle', {
            running: runningSites,
            total: sitesStore.sites.length,
          }) }}
        </p>
      </div>
      <el-button type="primary" size="small" @click="emit('create')">
        + {{ $t('sites.card.newSite') }}
      </el-button>
    </div>

    <div v-if="sitesStore.loading" v-loading="true" class="loading-wrap" />

    <!-- Fresh-install hero: replaces the default el-empty drawing with a
         branded welcome card. Operators landing on a clean install
         see what the app does, not a placeholder. -->
    <div v-else-if="sitesStore.sites.length === 0" class="welcome-hero">
      <div class="welcome-mark">
        <el-icon><Link /></el-icon>
      </div>
      <h1 class="welcome-title">{{ $t('sites.card.welcomeTitle') }}</h1>
      <p class="welcome-sub">{{ $t('sites.card.welcomeSubtext') }}</p>
      <div class="welcome-cta">
        <el-button type="primary" size="large" @click="emit('create')">
          + {{ $t('sites.card.newSite') }}
        </el-button>
      </div>
      <ul class="welcome-features">
        <li>
          <el-icon><Cpu /></el-icon>
          <span>{{ $t('sites.simple.welcomeFeaturePhp') }}</span>
        </li>
        <li>
          <el-icon><Lock /></el-icon>
          <span>{{ $t('sites.simple.welcomeFeatureSsl') }}</span>
        </li>
        <li>
          <el-icon><Connection /></el-icon>
          <span>{{ $t('sites.simple.welcomeFeatureBind') }}</span>
        </li>
      </ul>
    </div>

    <div v-else class="card-grid">
      <SimpleSiteCard
        v-for="site in sitesStore.sites"
        :key="site.domain"
        :site="site"
        :apache-running="apacheRunning"
        :toggling="toggling"
        :toggling-enabled="togglingEnabled"
        :restarting="restarting"
        :activity="activityMap[site.domain] ?? null"
        :relative-label="relativeTime(activityMap[site.domain]?.lastHitIso ?? null)"
        @navigate="(d) => navigateToSite(d)"
        @open="openSite"
        @start-apache="startApache"
        @stop-apache="stopApache"
        @toggle-enabled="toggleSiteEnabled"
        @command="(cmd, s) => handleCommand(cmd, s)"
      />
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
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessageBox, ElMessage } from 'element-plus'
import { Link, Cpu, Lock, Connection } from '@element-plus/icons-vue'
import SimpleSiteCard from './SimpleSiteCard.vue'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { startService, stopService, duplicateSite, daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import type { SiteInfo } from '../../api/types'

// Per-card icon set moved into SimpleSiteCard.vue.

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

// Number of enabled sites — used by the simple-header subtitle.
// A site is "running" when Apache is up AND the site itself is enabled
// (soft-disable via PATCH /enabled doesn't stop Apache but the vhost
// is removed from sites-enabled/, so the site doesn't actually serve).
const runningSites = computed(() => {
  if (!apacheRunning.value) return 0
  return sitesStore.sites.filter(s => s.enabled !== false).length
})

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
  // Stopping Apache from a per-site card is misleading — it actually takes
  // down EVERY site on the host. The confirm dialog forces an acknowledgment
  // so an absent-minded click doesn't break unrelated work.
  try {
    await ElMessageBox.confirm(
      $t('sites.card.stopApacheConfirm'),
      $t('sites.card.stopApacheTitle'),
      { type: 'warning', confirmButtonText: $t('sites.card.stop') }
    )
  } catch {
    return
  }
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
  gap: 16px;
  margin-bottom: 24px;
}

.simple-header-title {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.page-title {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 0;
  font-size: 1.4rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.page-title-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 28px;
  height: 22px;
  padding: 0 8px;
  font-size: 0.78rem;
  font-weight: 700;
  color: var(--wdc-accent);
  background: var(--wdc-accent-dim);
  border-radius: 999px;
}
.page-subtitle {
  margin: 0;
  font-size: 0.82rem;
  color: var(--wdc-text-2);
}

.loading-wrap {
  height: 200px;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

/* Per-card styles moved to SimpleSiteCard.vue. */

.mono {
  font-family: var(--el-font-family-mono, monospace);
}

/* Fresh-install welcome hero — replaces the default el-empty drawing
   with a branded splash. First impression for an operator who just
   installed the app sees the value proposition + a single big CTA. */
.welcome-hero {
  max-width: 560px;
  margin: 48px auto;
  padding: 40px 32px;
  text-align: center;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  box-shadow: var(--wdc-shadow-card);
}
.welcome-mark {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 64px;
  height: 64px;
  margin-bottom: 16px;
  border-radius: 50%;
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  font-size: 32px;
}
.welcome-title {
  margin: 0 0 8px;
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.welcome-sub {
  margin: 0 0 24px;
  color: var(--wdc-text-2);
  font-size: 0.95rem;
  line-height: 1.5;
}
.welcome-cta {
  margin-bottom: 28px;
}
.welcome-features {
  list-style: none;
  margin: 0;
  padding: 16px 0 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
  border-top: 1px solid var(--wdc-border);
  text-align: left;
}
.welcome-features li {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--wdc-text-2);
  font-size: 0.86rem;
}
.welcome-features li .el-icon {
  color: var(--wdc-accent);
  font-size: 18px;
  flex-shrink: 0;
}

.danger-item {
  color: var(--el-color-danger) !important;
}

@media (max-width: 820px) {
  .sites-simple {
    padding: 18px 16px;
  }

  .simple-header {
    align-items: stretch;
    flex-direction: column;
    margin-bottom: 16px;
  }

  .simple-header :deep(.el-button) {
    width: 100%;
    min-height: 38px;
  }

  .card-grid {
    grid-template-columns: minmax(0, 1fr);
  }

  .card-actions {
    display: grid;
    grid-template-columns: 1fr auto auto auto;
    width: 100%;
  }

  .card-actions :deep(.el-button:first-child) {
    width: 100%;
  }
}

@media (max-width: 460px) {
  .card-title-row {
    align-items: flex-start;
    flex-direction: column;
    gap: 4px;
  }

  .card-actions {
    grid-template-columns: 1fr 42px 46px 42px;
  }
}
</style>
