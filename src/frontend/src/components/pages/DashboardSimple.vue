<template>
  <div class="simple-dashboard">
    <div class="simple-hero">
      <h1 class="hero-title">{{ t('dashboard.simple.welcomeTitle') }}</h1>
      <p class="hero-summary">
        {{ t('dashboard.simple.summary', {
          sites: sitesCount,
          running: runningServices,
          total: totalServices,
          hits: totalHits,
          errors: totalErrors,
        }) }}
      </p>
    </div>

    <div v-if="aggregatesLoading" v-loading="true" style="height: 80px; margin-bottom: 12px;" />

    <div class="simple-tiles">
      <SimpleMetricTile
        :label="t('dashboard.simple.tiles.sites')"
        :value="sitesCount"
        icon="Link"
        @click="router.push('/sites')"
      />
      <SimpleMetricTile
        :label="t('dashboard.simple.tiles.services')"
        :value="`${runningServices}/${totalServices}`"
        icon="Monitor"
        :variant="runningServices < totalServices ? 'warning' : 'success'"
        @click="router.push('/services')"
      />
      <SimpleMetricTile
        :label="t('dashboard.simple.tiles.hits')"
        :value="totalHits"
        icon="DataLine"
        @click="router.push('/sites')"
      />
      <SimpleMetricTile
        :label="t('dashboard.simple.tiles.errors')"
        :value="totalErrors"
        icon="WarningFilled"
        :variant="totalErrors > 0 ? 'danger' : 'default'"
        @click="router.push('/sites')"
      />
    </div>

    <div v-if="sitesCount > 0 && !simpleApacheRunning" class="simple-apache-banner">
      <el-icon class="simple-apache-banner-icon"><WarningFilled /></el-icon>
      <div class="simple-apache-banner-text">
        <strong>{{ t('dashboard.simple.apacheStopped.title') }}</strong>
        <span>{{ t('dashboard.simple.apacheStopped.subtitle') }}</span>
      </div>
      <el-button
        type="success"
        size="default"
        :loading="startingApache"
        @click="startApache"
      >
        {{ t('dashboard.simple.apacheStopped.startBtn') }}
      </el-button>
    </div>

    <!-- Plan §4/478 — readiness signal: no recent backup. Soft prompt
         when (a) zero backups stored or (b) newest backup > 7 days old.
         Shown only when user has sites — empty install doesn't need a
         backup nag. -->
    <div v-if="sitesCount > 0 && backupStale" class="simple-backup-banner">
      <el-icon class="simple-backup-banner-icon"><FolderChecked /></el-icon>
      <div class="simple-backup-banner-text">
        <strong>{{ t('dashboard.simple.backupStale.title') }}</strong>
        <span>
          {{ lastBackupAgeMs === null
            ? t('dashboard.simple.backupStale.never')
            : t('dashboard.simple.backupStale.olderThanDays', { days: 7 }) }}
        </span>
      </div>
      <el-button
        type="primary"
        size="default"
        @click="router.push('/backups')"
      >
        {{ t('dashboard.simple.backupStale.openBtn') }}
      </el-button>
    </div>

    <!-- Plan §4 (item 478): readiness signal — update available banner.
         Surfaces pending updates without forcing user to navigate to
         Settings → Aktualizace to discover them. -->
    <div v-if="updatesStore.hasUpdate" class="simple-update-banner">
      <el-icon class="simple-update-banner-icon"><Download /></el-icon>
      <div class="simple-update-banner-text">
        <strong>{{ t('dashboard.simple.updateAvailable.title') }}</strong>
        <span>{{ t('dashboard.simple.updateAvailable.subtitle', { version: updatesStore.latestVersion }) }}</span>
      </div>
      <el-button
        type="primary"
        size="default"
        @click="router.push('/settings?tab=update')"
      >
        {{ t('dashboard.simple.updateAvailable.openBtn') }}
      </el-button>
    </div>

    <div class="simple-quick-actions">
      <el-button type="primary" size="large" @click="router.push('/sites?create=1')">
        + {{ t('dashboard.simple.quickActions.newSite') }}
      </el-button>
      <el-button size="large" @click="openMailpit">
        {{ t('dashboard.simple.quickActions.mailpit') }}
      </el-button>
      <el-button size="large" @click="router.push('/settings?tab=backup')">
        {{ t('dashboard.simple.quickActions.backup') }}
      </el-button>
    </div>

    <!-- First-run empty state — guides users with zero sites toward the
         create-first-site flow rather than leaving the dashboard sparse. -->
    <div v-if="sitesCount === 0" class="simple-empty-card" role="button" tabindex="0"
      :aria-label="t('dashboard.simple.empty.cta')"
      @click="router.push('/sites?create=1')"
      @keydown.enter.prevent="router.push('/sites?create=1')"
      @keydown.space.prevent="router.push('/sites?create=1')"
    >
      <div class="simple-empty-icon">
        <el-icon><Plus /></el-icon>
      </div>
      <div class="simple-empty-body">
        <strong>{{ t('dashboard.simple.empty.title') }}</strong>
        <span>{{ t('dashboard.simple.empty.subtitle') }}</span>
      </div>
      <el-icon class="simple-empty-arrow"><ArrowRight /></el-icon>
    </div>

    <!-- Recent sites — capped at 5 in insertion order so the dashboard
         answers "what was I working on?" without a click through. -->
    <div v-if="recentSimpleSites.length > 0" class="simple-recent">
      <div class="simple-recent-header">
        <span class="simple-recent-title">{{ t('dashboard.simple.recent.title') }}</span>
        <el-button text size="default" @click="router.push('/sites')">
          {{ t('dashboard.simple.recent.viewAll') }} →
        </el-button>
      </div>
      <ul class="simple-recent-list">
        <li
          v-for="s in recentSimpleSites"
          :key="s.domain"
          class="simple-recent-item"
          role="button"
          tabindex="0"
          :aria-label="t('sites.card.openSiteAria', { domain: s.domain })"
          @click="router.push(`/sites/${encodeURIComponent(s.domain)}/edit`)"
          @keydown.enter.prevent="router.push(`/sites/${encodeURIComponent(s.domain)}/edit`)"
          @keydown.space.prevent="router.push(`/sites/${encodeURIComponent(s.domain)}/edit`)"
        >
          <HealthStatusDot
            :level="s.enabled === false ? 'muted' : 'ok'"
          />
          <span class="simple-recent-domain">{{ s.domain }}</span>
          <span class="simple-recent-meta mono">
            <template v-if="s.phpVersion && s.phpVersion !== 'none'">PHP {{ s.phpVersion }}</template>
            <template v-else>{{ t('sites.phpNone') }}</template>
          </span>
          <el-icon class="simple-recent-arrow"><ArrowRight /></el-icon>
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
// Extracted from the 1394-line Dashboard.vue. The Simple-mode hero +
// KPI tiles + apache banner + quick actions + recent-sites list lives
// here so the parent stays focused on the Advanced surface (services
// grid, command palette, runtime widgets). Each piece keeps the same
// behaviour, just routed via local state + an emit('open-mailpit')-free
// surface — the component handles all of its own actions.
//
// Aggregates (totalHits + totalErrors) are fetched here on mount,
// matching the original Dashboard.vue behaviour. Recent sites + tile
// figures are derived live from the shared sitesStore / daemonStore.

import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ArrowRight, WarningFilled, Plus, Download, FolderChecked } from '@element-plus/icons-vue'
import SimpleMetricTile from '../common/SimpleMetricTile.vue'
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useUpdatesStore } from '../../stores/updates'
import { daemonBaseUrl } from '../../api/daemon'

defineOptions({ name: 'DashboardSimple' })

const { t } = useI18n()
const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const updatesStore = useUpdatesStore()

// Tiles + summary
const sitesCount = computed(() => sitesStore.sites.length)
const services = computed(() => daemonStore.services)
const runningServices = computed(() => services.value.filter(s => s.state === 2).length)
const totalServices = computed(() => services.value.length)

const totalHits = ref(0)
const totalErrors = ref(0)
const aggregatesLoading = ref(false)

const recentSimpleSites = computed(() => sitesStore.sites.slice(0, 5))

const simpleApacheRunning = computed(() => {
  const apache = services.value.find(s => s.id === 'apache' || s.id === 'httpd')
  return apache?.state === 2 || apache?.status === 'running'
})
const startingApache = ref(false)

async function startApache() {
  const apache = services.value.find(s => s.id === 'apache' || s.id === 'httpd')
  if (!apache) return
  startingApache.value = true
  try {
    await servicesStore.start(apache.id)
  } finally {
    startingApache.value = false
  }
}

function openMailpit() {
  const mailpitUrl = 'http://127.0.0.1:8025'
  const electronApi = (window as unknown as {
    electronAPI?: { openExternal?: (url: string) => void }
  }).electronAPI
  if (electronApi?.openExternal) electronApi.openExternal(mailpitUrl)
  else window.open(mailpitUrl, '_blank')
}

async function loadAggregates() {
  aggregatesLoading.value = true
  const domains = sitesStore.sites.map(s => s.domain)
  const results = await Promise.allSettled(domains.map(async (domain: string) => {
    try {
      const [metricsR, errorsR] = await Promise.allSettled([
        fetch(
          `${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/metrics/history?minutes=1440&limit=24`,
          { headers: sitesStore.authHeaders() },
        ),
        fetch(
          `${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/logs/errors?limit=100`,
          { headers: sitesStore.authHeaders() },
        ),
      ])
      let hits = 0
      let errs = 0
      if (metricsR.status === 'fulfilled' && metricsR.value.ok) {
        const data: unknown = await metricsR.value.json()
        const samples: Array<{ requests?: number; hits?: number }> = Array.isArray(data)
          ? data
          : ((data as { samples?: Array<{ requests?: number; hits?: number }> })?.samples ?? [])
        hits = samples.reduce((sum, s) => sum + (s.requests ?? s.hits ?? 0), 0)
      }
      if (errorsR.status === 'fulfilled' && errorsR.value.ok) {
        const data: unknown = await errorsR.value.json()
        const entries: Array<{ timestamp?: string }> = Array.isArray(data)
          ? data
          : ((data as { entries?: Array<{ timestamp?: string }> })?.entries ?? [])
        const cutoff = Date.now() - 24 * 60 * 60 * 1000
        errs = entries.filter(e => {
          if (!e.timestamp) return true
          const ts = new Date(e.timestamp).getTime()
          return !isNaN(ts) && ts > cutoff
        }).length
      }
      return { hits, errs }
    } catch { return { hits: 0, errs: 0 } }
  }))
  totalHits.value = results.reduce(
    (sum, r) => r.status === 'fulfilled' ? sum + r.value.hits : sum, 0,
  )
  totalErrors.value = results.reduce(
    (sum, r) => r.status === 'fulfilled' ? sum + r.value.errs : sum, 0,
  )
  aggregatesLoading.value = false
}

// Plan §4/478 — backup age readiness signal. Surfaces a "no recent
// backup" banner when (a) backups feature is set up but nothing is
// stored, or (b) the newest backup is older than 7 days. Soft warning
// — informational, not alarmist.
const BACKUP_STALE_THRESHOLD_MS = 7 * 24 * 60 * 60 * 1000
const lastBackupAgeMs = ref<number | null>(null)
const backupStale = computed(() => {
  if (lastBackupAgeMs.value === null) return true // never backed up
  return lastBackupAgeMs.value > BACKUP_STALE_THRESHOLD_MS
})
async function loadLastBackupAge() {
  try {
    const { fetchBackups } = await import('../../api/daemon')
    const data = await fetchBackups()
    if (data.backups.length > 0) {
      lastBackupAgeMs.value = Date.now() - new Date(data.backups[0].createdUtc).getTime()
    } else {
      lastBackupAgeMs.value = null
    }
  } catch {
    // Daemon offline / endpoint missing — silence; the banner just won't show.
  }
}

onMounted(() => {
  void loadAggregates()
  void loadLastBackupAge()
})

defineExpose({ reload: loadAggregates })
</script>

<style scoped>
.simple-dashboard {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 20px;
  width: 100%;
  margin: 0 auto;
}
.simple-hero {
  text-align: center;
  padding: 24px 0 8px;
}
.hero-title {
  margin: 0;
  font-size: 1.8rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}
.hero-summary {
  margin: 8px 0 0;
  color: var(--wdc-text-2);
  font-size: 0.95rem;
}
.simple-tiles {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 14px;
  max-width: none;
  width: 100%;
  margin: 0 auto;
}
.simple-apache-banner {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 14px 18px;
  margin: 4px auto 0;
  max-width: none;
  width: 100%;
  background: color-mix(in srgb, var(--el-color-warning) 14%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-warning) 40%, transparent);
  border-radius: var(--wdc-radius);
}
.simple-apache-banner-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.simple-apache-banner-text strong { color: var(--wdc-text); font-size: 0.95rem; }
.simple-apache-banner-text span { color: var(--wdc-text-2); font-size: 0.84rem; }

/* Update-available banner — same shape as apache-banner but uses the
   accent color so it reads as "informational nudge" rather than
   "something is broken right now". */
.simple-update-banner {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 14px 18px;
  margin: 4px auto 0;
  max-width: none;
  width: 100%;
  background: color-mix(in srgb, var(--el-color-primary) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-primary) 40%, transparent);
  border-radius: var(--wdc-radius);
}
.simple-update-banner-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.simple-update-banner-text strong { color: var(--wdc-text); font-size: 0.95rem; }
.simple-update-banner-text span { color: var(--wdc-text-2); font-size: 0.84rem; }
.simple-update-banner-icon {
  color: var(--el-color-primary);
  font-size: 28px;
  flex-shrink: 0;
}

/* Stale-backup banner — soft warning (info color, not warning) so it
   reads as "you should do this" rather than "something is broken". */
.simple-backup-banner {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 14px 18px;
  margin: 4px auto 0;
  max-width: none;
  width: 100%;
  background: color-mix(in srgb, var(--el-color-info) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-info) 40%, transparent);
  border-radius: var(--wdc-radius);
}
.simple-backup-banner-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.simple-backup-banner-text strong { color: var(--wdc-text); font-size: 0.95rem; }
.simple-backup-banner-text span { color: var(--wdc-text-2); font-size: 0.84rem; }
.simple-backup-banner-icon {
  color: var(--el-color-info);
  font-size: 28px;
  flex-shrink: 0;
}

/* Pulsing warning icon — draws the eye so the user notices that the
   web server is down and they should act, rather than treating the
   banner as decorative chrome. */
.simple-apache-banner-icon {
  color: var(--el-color-warning);
  font-size: 28px;
  flex-shrink: 0;
  animation: wdc-apache-banner-pulse 1.8s ease-in-out infinite;
}
@keyframes wdc-apache-banner-pulse {
  0%, 100% { opacity: 1; transform: scale(1); }
  50%       { opacity: 0.6; transform: scale(1.1); }
}
.simple-quick-actions {
  display: flex;
  gap: 12px;
  justify-content: center;
  flex-wrap: wrap;
  padding-top: 8px;
}

.simple-recent {
  max-width: none;
  width: 100%;
  margin: 18px auto 0;
  padding: 16px 18px;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface-2);
}

.simple-empty-card {
  max-width: none;
  width: 100%;
  margin: 18px auto 0;
  padding: 24px 22px;
  border: 1.5px dashed color-mix(in oklab, var(--wdc-accent) 40%, var(--wdc-border));
  border-radius: var(--wdc-radius);
  background: color-mix(in oklab, var(--wdc-accent) 6%, var(--wdc-surface));
  display: flex;
  align-items: center;
  gap: 16px;
  cursor: pointer;
  transition: all 0.15s ease;
}
.simple-empty-card:hover,
.simple-empty-card:focus-visible {
  border-color: var(--wdc-accent);
  background: color-mix(in oklab, var(--wdc-accent) 10%, var(--wdc-surface));
  outline: none;
  transform: translateY(-1px);
}
.simple-empty-icon {
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: color-mix(in oklab, var(--wdc-accent) 20%, transparent);
  color: var(--wdc-accent);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 22px;
  flex-shrink: 0;
}
.simple-empty-body {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.simple-empty-body strong {
  font-size: 1rem;
  color: var(--wdc-text);
}
.simple-empty-body span {
  font-size: 0.85rem;
  color: var(--wdc-text-2);
}
.simple-empty-arrow {
  color: var(--wdc-accent);
  font-size: 20px;
  flex-shrink: 0;
}
.simple-recent-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}
.simple-recent-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--wdc-text-2);
}
.simple-recent-list {
  list-style: none;
  margin: 0;
  padding: 0;
}
.simple-recent-item {
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr) auto 16px;
  gap: 10px;
  align-items: center;
  padding: 12px 6px;
  min-height: 48px;
  border-bottom: 1px solid var(--wdc-border);
  cursor: pointer;
  transition: background 0.12s;
}
.simple-recent-item:last-child { border-bottom: 0; }
.simple-recent-item:hover { background: var(--wdc-surface); }
.simple-recent-item:focus-visible {
  outline: 2px solid var(--wdc-accent);
  outline-offset: -2px;
}
/* Per-site dot now uses HealthStatusDot shared primitive; the old
   .simple-recent-dot rules are removed. */
.simple-recent-domain {
  font-weight: 600;
  color: var(--wdc-text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.simple-recent-meta { color: var(--wdc-text-2); font-size: 0.78rem; }
.simple-recent-arrow { color: var(--wdc-text-2); font-size: 14px; }
</style>
