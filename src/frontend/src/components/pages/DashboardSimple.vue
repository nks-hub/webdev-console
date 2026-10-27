<template>
  <div class="simple-dashboard">
    <!-- Simple density below the shared attention list + stack table:
         today's traffic, the three most common actions, recent sites. -->
    <dl class="simple-figures" :aria-busy="aggregatesLoading">
      <div class="simple-figure">
        <dt>{{ t('dashboard.simple.tiles.hits') }}</dt>
        <dd class="mono">{{ aggregatesLoading ? '…' : totalHits }}</dd>
      </div>
      <div class="simple-figure" :class="{ 'has-errors': totalErrors > 0 }">
        <dt>{{ t('dashboard.simple.tiles.errors') }}</dt>
        <dd class="mono">{{ aggregatesLoading ? '…' : totalErrors }}</dd>
      </div>
    </dl>

    <div class="simple-quick-actions">
      <el-button @click="openMailpit">
        {{ t('dashboard.simple.quickActions.mailpit') }}
      </el-button>
      <el-button @click="router.push('/backups')">
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
        <li v-for="s in recentSimpleSites" :key="s.domain">
          <router-link
            class="simple-recent-item"
            :to="`/sites/${encodeURIComponent(s.domain)}/edit`"
            :aria-label="t('sites.card.openSiteAria', { domain: s.domain })"
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
          </router-link>
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
import { ArrowRight, Plus } from '@element-plus/icons-vue'
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import { useSitesStore } from '../../stores/sites'
import { daemonBaseUrl } from '../../api/daemon'

defineOptions({ name: 'DashboardSimple' })

const { t } = useI18n()
const router = useRouter()
const sitesStore = useSitesStore()

const sitesCount = computed(() => sitesStore.sites.length)

const totalHits = ref(0)
const totalErrors = ref(0)
const aggregatesLoading = ref(false)

const recentSimpleSites = computed(() => sitesStore.sites.slice(0, 5))

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


onMounted(() => {
  void loadAggregates()
})

defineExpose({ reload: loadAggregates })
</script>

<style scoped>
.simple-dashboard {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-5);
  width: 100%;
}
.simple-figures {
  display: flex;
  margin: 0;
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
}
.simple-figure {
  display: grid;
  gap: var(--wdc-space-1);
  padding: var(--wdc-space-3) var(--wdc-space-6) var(--wdc-space-3) 0;
}
.simple-figure + .simple-figure { padding-left: var(--wdc-space-6); border-left: 1px solid var(--wdc-border); }
.simple-figure dt { font-family: var(--wdc-font-display); font-size: 0.82rem; color: var(--wdc-text-3); }
.simple-figure dd { margin: 0; font-size: 1.2rem; color: var(--wdc-text); }
.simple-figure.has-errors dd { color: var(--wdc-status-error); }
.simple-quick-actions {
  display: flex;
  gap: var(--wdc-space-2);
  flex-wrap: wrap;
}
.simple-quick-actions :deep(.el-button + .el-button) { margin-left: 0; }

.simple-recent {
  width: 100%;
}

.simple-empty-card {
  max-width: none;
  width: 100%;
  padding: var(--wdc-space-5);
  border: 1px dashed color-mix(in oklab, var(--wdc-accent) 40%, var(--wdc-border));
  border-radius: var(--wdc-radius);
  background: color-mix(in oklab, var(--wdc-accent) 6%, var(--wdc-surface));
  display: flex;
  align-items: center;
  gap: var(--wdc-space-4);
  cursor: pointer;
  transition: border-color var(--wdc-dur-micro) var(--wdc-ease-out), background-color var(--wdc-dur-micro) var(--wdc-ease-out);
}
.simple-empty-card:hover,
.simple-empty-card:focus-visible {
  border-color: var(--wdc-accent);
  background: color-mix(in oklab, var(--wdc-accent) 10%, var(--wdc-surface));
  outline: none;
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
  gap: var(--wdc-space-1);
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
  margin-bottom: var(--wdc-space-2);
}
.simple-recent-title {
  font-family: var(--wdc-font-display);
  font-size: 1.2rem;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--wdc-text);
}
.simple-recent-list {
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--wdc-border);
}
.simple-recent-item {
  color: inherit;
  text-decoration: none;
  display: grid;
  grid-template-columns: 10px minmax(0, 1fr) auto 16px;
  gap: var(--wdc-space-2-5);
  align-items: center;
  padding: var(--wdc-space-3) var(--wdc-space-1-5);
  min-height: 48px;
  border-bottom: 1px solid var(--wdc-border);
  cursor: pointer;
  transition: background 0.12s;
}
.simple-recent-item:hover { background: var(--wdc-hover); }
.simple-recent-item:focus-visible {
  outline: 2px solid var(--wdc-accent);
  outline-offset: -2px;
}
/* Per-site dot now uses HealthStatusDot shared primitive; the old
   .simple-recent-dot rules are removed. */
.simple-recent-domain {
  font-family: var(--wdc-font-display);
  font-weight: 500;
  color: var(--wdc-text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.simple-recent-meta { color: var(--wdc-text-2); font-size: 0.78rem; }
.simple-recent-arrow { color: var(--wdc-text-2); font-size: 14px; }
</style>
