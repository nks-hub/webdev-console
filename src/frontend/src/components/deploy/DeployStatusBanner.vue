<template>
  <!--
    Phase 7.5+++ Deploy detail redesign — top-of-page status banner.
    Replaces the silent gap between page header and host cards with an
    instant-overview dashboard: aggregate counts, success rate, last
    deploy age + a per-host mini-strip. Designed for operators who open
    the tab to answer "what's the state right now?" without reading the
    full history table.

    Hidden when entries.length === 0 — empty state still shows the
    wizard CTA from DeploySiteTab. Once history exists, this is the
    primary at-a-glance widget.
  -->
  <div v-if="entries.length > 0" class="deploy-status-banner" role="region"
       :aria-label="t('deploy.statusBanner.aria')">
    <!-- Top row: 4 hero metrics -->
    <div class="status-metrics">
      <div class="metric">
        <div class="metric-value">{{ entries.length }}</div>
        <div class="metric-label">{{ t('deploy.statusBanner.totalDeploys') }}</div>
      </div>
      <div class="metric" :class="successRateClass">
        <div class="metric-value">{{ successRatePct }}%</div>
        <div class="metric-label">{{ t('deploy.statusBanner.successRate') }}</div>
      </div>
      <div class="metric">
        <div class="metric-value">{{ uniqueHosts.length }}</div>
        <div class="metric-label">{{ t('deploy.statusBanner.hosts') }}</div>
      </div>
      <div class="metric">
        <div class="metric-value">{{ lastDeployAge }}</div>
        <div class="metric-label">{{ t('deploy.statusBanner.lastDeploy') }}</div>
      </div>
    </div>

    <!-- Per-host strip: success/total + last release -->
    <div v-if="hostStrip.length > 0" class="status-host-strip">
      <div v-for="h in hostStrip" :key="h.host" class="status-host-pill"
           :class="{ 'pill-failed': h.lastFailed }">
        <span class="pill-host">{{ h.host }}</span>
        <span class="pill-rate muted">
          {{ h.successes }}/{{ h.total }}
          <span v-if="h.lastReleaseId" class="pill-release mono">· {{ h.lastReleaseId }}</span>
        </span>
      </div>
    </div>

    <!-- triggeredBy mix (mcp vs gui vs cli) — surfaces when AI activity is high -->
    <div v-if="triggeredByMix.length > 1" class="status-triggered-mix muted">
      {{ t('deploy.statusBanner.triggeredByLabel') }}
      <span v-for="(t, i) in triggeredByMix" :key="t.source">
        <strong>{{ t.count }}</strong> {{ t.source }}<span v-if="i < triggeredByMix.length - 1">, </span>
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { DeployHistoryEntryDto } from '../../api/deploy'

const { t } = useI18n()
const props = defineProps<{
  entries: DeployHistoryEntryDto[]
}>()

const successRatePct = computed(() => {
  if (props.entries.length === 0) return 0
  const ok = props.entries.filter((e) => e.finalPhase === 'Done').length
  return Math.round((ok / props.entries.length) * 100)
})

const successRateClass = computed(() => {
  const pct = successRatePct.value
  if (pct >= 95) return 'metric-success'
  if (pct >= 80) return 'metric-warning'
  return 'metric-danger'
})

const uniqueHosts = computed<string[]>(() =>
  Array.from(new Set(props.entries.map((e) => e.host))).sort())

const lastDeployAge = computed(() => {
  if (props.entries.length === 0) return '—'
  // Endpoint returns newest-first; first entry is the most recent.
  const iso = props.entries[0].startedAt
  try {
    const ms = Date.now() - new Date(iso).getTime()
    if (ms < 60_000) return t('deploy.statusBanner.justNow')
    if (ms < 3_600_000) return t('deploy.statusBanner.minutesAgo', { n: Math.floor(ms / 60_000) })
    if (ms < 86_400_000) return t('deploy.statusBanner.hoursAgo', { n: Math.floor(ms / 3_600_000) })
    return t('deploy.statusBanner.daysAgo', { n: Math.floor(ms / 86_400_000) })
  } catch { return '—' }
})

interface HostPill {
  host: string
  total: number
  successes: number
  lastFailed: boolean
  lastReleaseId: string | null
}

const hostStrip = computed<HostPill[]>(() => {
  const m = new Map<string, { total: number; successes: number; lastFailed: boolean; lastReleaseId: string | null }>()
  // entries are newest-first → first encounter per host wins for "last" fields.
  for (const e of props.entries) {
    const cur = m.get(e.host)
    if (!cur) {
      m.set(e.host, {
        total: 1,
        successes: e.finalPhase === 'Done' ? 1 : 0,
        lastFailed: e.finalPhase !== 'Done',
        lastReleaseId: e.releaseId,
      })
    } else {
      cur.total++
      if (e.finalPhase === 'Done') cur.successes++
    }
  }
  return Array.from(m.entries())
    .map(([host, s]) => ({ host, ...s }))
    .sort((a, b) => b.total - a.total)
})

interface TriggeredByCount { source: string; count: number }
const triggeredByMix = computed<TriggeredByCount[]>(() => {
  const m = new Map<string, number>()
  for (const e of props.entries) {
    if (e.triggeredBy) m.set(e.triggeredBy, (m.get(e.triggeredBy) ?? 0) + 1)
  }
  return Array.from(m.entries())
    .map(([source, count]) => ({ source, count }))
    .sort((a, b) => b.count - a.count)
})
</script>

<style scoped>
.deploy-status-banner {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px 20px;
  background: linear-gradient(to right,
    var(--el-fill-color-light),
    var(--el-fill-color-lighter));
  border-radius: 8px;
  border-left: 4px solid var(--el-color-primary);
  margin-bottom: 8px;
}
.status-metrics {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}
@container (max-width: 600px) {
  .status-metrics { grid-template-columns: repeat(2, 1fr); }
}
.metric {
  display: flex; flex-direction: column; align-items: center;
  padding: 8px 4px;
}
.metric-value {
  font-size: 28px; font-weight: 700; line-height: 1.1;
  color: var(--el-text-color-primary);
}
.metric-label {
  font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em;
  color: var(--el-text-color-secondary); margin-top: 2px;
}
.metric-success .metric-value { color: var(--el-color-success); }
.metric-warning .metric-value { color: var(--el-color-warning); }
.metric-danger  .metric-value { color: var(--el-color-danger); }

.status-host-strip {
  display: flex; flex-wrap: wrap; gap: 8px;
}
.status-host-pill {
  display: inline-flex; align-items: center; gap: 8px;
  padding: 4px 10px;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 14px;
  font-size: 13px;
}
.pill-failed {
  border-color: var(--el-color-danger-light-5);
  background: var(--el-color-danger-light-9);
}
.pill-host { font-weight: 600; }
.pill-rate { font-size: 12px; }
.pill-release {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.status-triggered-mix {
  font-size: 12px;
}
.muted { color: var(--el-text-color-secondary); }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
</style>
