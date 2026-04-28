<template>
  <div class="mcp-activity-page" role="region" :aria-label="t('mcpActivity.stats.totalCalls')">
    <!-- Stats banner — moved to the very top so the six tiles (Celkem
         volání / Read / Mutating / Destruktivní / Chyby / Session) are
         the first thing the operator sees, before chart and KPIs.
         Tiles are clickable: clicking a danger-level tile filters the
         feed below to that level (or clears the filter when clicking
         the already-active one). -->
    <div v-if="stats" class="activity-stats">
      <div
        class="stat-tile clickable"
        :class="{ active: !dangerFilter }"
        role="button"
        tabindex="0"
        :aria-pressed="!dangerFilter"
        @click="setDangerFilter(null)"
        @keydown.enter.prevent="setDangerFilter(null)"
        @keydown.space.prevent="setDangerFilter(null)"
        :title="t('mcpActivity.stats.clickToClear')"
      >
        <div class="stat-num">{{ stats.total }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.totalCalls') }}</div>
      </div>
      <div
        class="stat-tile read clickable"
        :class="{ active: dangerFilter === 'read' }"
        role="button"
        tabindex="0"
        :aria-pressed="dangerFilter === 'read'"
        @click="setDangerFilter('read')"
        @keydown.enter.prevent="setDangerFilter('read')"
        @keydown.space.prevent="setDangerFilter('read')"
        :title="t('mcpActivity.stats.clickToFilter')"
      >
        <div class="stat-num">{{ stats.reads }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.reads') }}</div>
      </div>
      <div
        class="stat-tile mutate clickable"
        :class="{ active: dangerFilter === 'mutate' }"
        role="button"
        tabindex="0"
        :aria-pressed="dangerFilter === 'mutate'"
        @click="setDangerFilter('mutate')"
        @keydown.enter.prevent="setDangerFilter('mutate')"
        @keydown.space.prevent="setDangerFilter('mutate')"
        :title="t('mcpActivity.stats.clickToFilter')"
      >
        <div class="stat-num">{{ stats.mutates }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.mutates') }}</div>
      </div>
      <div
        class="stat-tile destructive clickable"
        :class="{ active: dangerFilter === 'destructive' }"
        role="button"
        tabindex="0"
        :aria-pressed="dangerFilter === 'destructive'"
        @click="setDangerFilter('destructive')"
        @keydown.enter.prevent="setDangerFilter('destructive')"
        @keydown.space.prevent="setDangerFilter('destructive')"
        :title="t('mcpActivity.stats.clickToFilter')"
      >
        <div class="stat-num">{{ stats.destructives }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.destructives') }}</div>
      </div>
      <div v-if="stats.errors > 0" class="stat-tile error">
        <div class="stat-num">{{ stats.errors }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.errors') }}</div>
      </div>
      <div class="stat-tile">
        <div class="stat-num">{{ stats.distinctSessions }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.sessions') }}</div>
      </div>
    </div>

    <!-- Visual analytics row — timeline + top tools (moved below tiles
         so primary KPIs are read first). -->
    <div class="analytics-row">
      <McpActivityTimeline class="analytics-timeline" />
      <McpTopToolsPanel
        class="analytics-toptools"
        :within-hours="24"
        :limit="10"
        @select="onToolSelect"
      />
    </div>

    <!-- Perf KPI strip — latency percentiles + throughput + error rate -->
    <div v-if="stats && stats.total > 0" class="perf-kpis">
      <div class="kpi" :title="t('mcpActivity.kpi.callsPerMinHint')">
        <span class="kpi-num">{{ formatThroughput(stats.callsPerMinute).num }}</span>
        <span class="kpi-unit">{{ formatThroughput(stats.callsPerMinute).unit }}</span>
        <span class="kpi-label">{{ t('mcpActivity.kpi.throughput') }}</span>
      </div>
      <div class="kpi">
        <span class="kpi-num">{{ stats.p50DurationMs }}</span>
        <span class="kpi-unit">ms</span>
        <span class="kpi-label">p50 {{ t('mcpActivity.kpi.latency') }}</span>
      </div>
      <div class="kpi" :class="{ warn: stats.p95DurationMs > 1000 }">
        <span class="kpi-num">{{ stats.p95DurationMs }}</span>
        <span class="kpi-unit">ms</span>
        <span class="kpi-label">p95 {{ t('mcpActivity.kpi.latency') }}</span>
      </div>
      <div class="kpi" :class="{ warn: stats.p99DurationMs > 5000 }">
        <span class="kpi-num">{{ stats.p99DurationMs }}</span>
        <span class="kpi-unit">ms</span>
        <span class="kpi-label">p99 {{ t('mcpActivity.kpi.latency') }}</span>
      </div>
      <div class="kpi" :class="{ warn: stats.errorRatePercent > 1, danger: stats.errorRatePercent > 5 }">
        <span class="kpi-num">{{ stats.errorRatePercent.toFixed(1) }}</span>
        <span class="kpi-unit">%</span>
        <span class="kpi-label">{{ t('mcpActivity.kpi.errorRate') }}</span>
      </div>
    </div>

    <!-- Filters row -->
    <div class="activity-filters">
      <el-select v-model="dangerFilter" size="small" :placeholder="t('mcpActivity.filter.allDanger')" clearable style="width: 160px">
        <el-option :label="t('mcpActivity.danger.read')" value="read" />
        <el-option :label="t('mcpActivity.danger.mutate')" value="mutate" />
        <el-option :label="t('mcpActivity.danger.destructive')" value="destructive" />
      </el-select>
      <el-input v-model="toolFilter" size="small" :placeholder="t('mcpActivity.filter.toolName')" clearable style="width: 200px" />
      <el-input v-model="searchQuery" size="small" :placeholder="t('mcpActivity.filter.search')" clearable style="width: 220px">
        <template #prefix>🔍</template>
      </el-input>
      <el-input v-if="sessionFilter" v-model="sessionFilter" size="small" disabled style="width: 180px">
        <template #prefix><el-icon><Lock /></el-icon></template>
        <template #append>
          <el-button @click="sessionFilter = ''">✕</el-button>
        </template>
      </el-input>
      <el-radio-group v-model="viewMode" size="small">
        <el-radio-button value="sessions">{{ t('mcpActivity.view.sessions') }}</el-radio-button>
        <el-radio-button value="flat">{{ t('mcpActivity.view.flat') }}</el-radio-button>
      </el-radio-group>
      <el-checkbox v-model="collapseReads" size="small">{{ t('mcpActivity.filter.collapseReads') }}</el-checkbox>
      <el-button size="small" :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> {{ t('mcpActivity.refresh') }}
      </el-button>
      <el-button size="small" @click="exportCsv">
        <el-icon><Download /></el-icon> {{ t('mcpActivity.exportCsv') }}
      </el-button>
      <span v-if="totalCount" class="muted total-count">
        {{ t('mcpActivity.totalShown', { n: entries.length, total: totalCount }) }}
      </span>
    </div>

    <!-- Empty state -->
    <el-empty v-if="!loading && entries.length === 0" :description="t('mcpActivity.empty')" />

    <!-- Session-grouped view -->
    <div v-else-if="viewMode === 'sessions'" class="sessions-list">
      <div
        v-for="session in sessionGroups"
        :key="session.key"
        class="session-card"
        :class="{ 'has-destructive': session.destructives > 0, 'has-error': session.errors > 0 }"
      >
        <div
          class="session-header"
          role="button"
          tabindex="0"
          :aria-expanded="!collapsedSessions.has(session.key)"
          @click="toggleSession(session.key)"
          @keydown.enter.prevent="toggleSession(session.key)"
          @keydown.space.prevent="toggleSession(session.key)"
        >
          <el-icon class="chev" :class="{ rotated: !collapsedSessions.has(session.key) }"><ArrowRight /></el-icon>
          <code class="mono caller-pill">{{ session.caller }}</code>
          <code
            v-if="session.sessionId"
            class="mono session-pill clickable"
            role="button"
            tabindex="0"
            :title="t('mcpActivity.filterBySession', { id: session.sessionId.slice(0, 8) })"
            @click.stop="sessionFilter = session.sessionId!"
            @keydown.enter.stop.prevent="sessionFilter = session.sessionId!"
            @keydown.space.stop.prevent="sessionFilter = session.sessionId!"
          >
            {{ session.sessionId.slice(0, 8) }}
          </code>
          <span class="muted at">{{ formatRelative(session.startedAt) }}</span>
          <span class="muted dur-range" v-if="session.entries.length > 1">
            ({{ formatDuration(session.startedAt, session.endedAt) }})
          </span>
          <div class="session-counts">
            <span class="count-pill total">{{ session.entries.length }}</span>
            <span v-if="session.reads > 0" class="count-pill read">{{ session.reads }}r</span>
            <span v-if="session.mutates > 0" class="count-pill mutate">{{ session.mutates }}m</span>
            <span v-if="session.destructives > 0" class="count-pill destructive">{{ session.destructives }}d</span>
            <span v-if="session.errors > 0" class="count-pill error">{{ session.errors }}!</span>
            <el-button
              v-if="session.sessionId"
              size="small"
              link
              @click.stop="openSessionDetail(session.sessionId)"
              :title="t('mcpActivity.sessionDetail.openHint')"
            >
              {{ t('mcpActivity.sessionDetail.open') }} →
            </el-button>
          </div>
        </div>
        <div v-if="!collapsedSessions.has(session.key)" class="session-body">
          <div v-for="row in groupReads(session.entries, session.key)" :key="row.key">
            <div v-if="row.kind === 'collapsed-reads'" class="collapsed-reads-row" @click="expandReads(row.key)">
              <el-icon class="chev"><ArrowRight /></el-icon>
              <span class="muted">{{ t('mcpActivity.readsCollapsed', { n: row.count }) }}</span>
              <span class="time-range muted">{{ formatRelative(row.lastAt) }}</span>
            </div>
            <template v-else>
              <div
                class="call-row clickable-row"
                :class="[`danger-${row.entry.dangerLevel}`, { expanded: expandedRows.has(row.entry.id) }]"
                @click="toggleRow(row.entry.id)"
              >
                <span class="dot" :class="`dot-${row.entry.dangerLevel}`" />
                <code class="mono tool-name">{{ row.entry.toolName }}</code>
                <el-tag v-if="row.entry.dangerLevel === 'destructive'" type="danger" size="small" effect="plain">
                  {{ t('mcpActivity.danger.destructive') }}
                </el-tag>
                <el-tag v-else-if="row.entry.dangerLevel === 'mutate'" type="warning" size="small" effect="plain">
                  {{ t('mcpActivity.danger.mutate') }}
                </el-tag>
                <el-tag v-if="row.entry.resultCode !== 'ok'" type="danger" size="small">{{ row.entry.resultCode }}</el-tag>
                <span class="muted dur">{{ row.entry.durationMs }}ms</span>
                <span class="muted at">{{ formatRelative(row.entry.calledAt) }}</span>
                <code v-if="row.entry.argsSummary && row.entry.argsSummary !== '{}'" class="mono args-preview">
                  {{ row.entry.argsSummary.length > 60 ? row.entry.argsSummary.slice(0, 57) + '…' : row.entry.argsSummary }}
                </code>
              </div>
              <div v-if="expandedRows.has(row.entry.id)" class="row-detail">
                <div class="detail-grid">
                  <div class="detail-label">id</div>
                  <code class="mono detail-value">{{ row.entry.id }}</code>
                  <div class="detail-label">{{ t('mcpActivity.detail.calledAt') }}</div>
                  <code class="mono detail-value">{{ row.entry.calledAt }}</code>
                  <div class="detail-label">{{ t('mcpActivity.detail.argsHash') }}</div>
                  <code class="mono detail-value">{{ row.entry.argsHash || '-' }}</code>
                  <div v-if="row.entry.intentId" class="detail-label">{{ t('mcpActivity.detail.intentId') }}</div>
                  <code v-if="row.entry.intentId" class="mono detail-value clickable" @click.stop="goToIntent(row.entry.intentId)">
                    {{ row.entry.intentId }} →
                  </code>
                  <div v-if="row.entry.errorMessage" class="detail-label">error</div>
                  <code v-if="row.entry.errorMessage" class="mono detail-value detail-error">{{ row.entry.errorMessage }}</code>
                </div>
                <div v-if="row.entry.argsSummary" class="detail-args">
                  <div class="detail-label">{{ t('mcpActivity.detail.args') }}</div>
                  <pre class="mono args-full">{{ row.entry.argsSummary }}</pre>
                  <el-button size="small" link @click.stop="copyArgs(row.entry.argsSummary!)">
                    {{ t('mcpActivity.detail.copy') }}
                  </el-button>
                </div>
              </div>
            </template>
          </div>
        </div>
      </div>
    </div>

    <!-- Flat view (legacy / debugging) -->
    <div v-else class="activity-timeline">
      <div v-for="row in flatView" :key="row.key" class="activity-group" :class="{ collapsed: row.kind === 'collapsed-reads' }">
        <div v-if="row.kind === 'collapsed-reads'" class="collapsed-reads-row" @click="expandReads(row.key)">
          <el-icon class="chev"><ArrowRight /></el-icon>
          <span class="muted">{{ t('mcpActivity.readsCollapsed', { n: row.count }) }}</span>
          <span class="time-range muted">{{ formatRelative(row.lastAt) }}</span>
        </div>
        <div v-else class="call-row" :class="`danger-${row.entry.dangerLevel}`">
          <span class="dot" :class="`dot-${row.entry.dangerLevel}`" />
          <code class="mono tool-name">{{ row.entry.toolName }}</code>
          <el-tag v-if="row.entry.dangerLevel === 'destructive'" type="danger" size="small" effect="plain">
            {{ t('mcpActivity.danger.destructive') }}
          </el-tag>
          <el-tag v-else-if="row.entry.dangerLevel === 'mutate'" type="warning" size="small" effect="plain">
            {{ t('mcpActivity.danger.mutate') }}
          </el-tag>
          <el-tag v-if="row.entry.resultCode !== 'ok'" type="danger" size="small">{{ row.entry.resultCode }}</el-tag>
          <span class="muted dur">{{ row.entry.durationMs }}ms</span>
          <span class="muted at">{{ formatRelative(row.entry.calledAt) }}</span>
          <code v-if="row.entry.argsSummary && row.entry.argsSummary !== '{}'" class="mono args-preview" :title="row.entry.argsSummary">
            {{ row.entry.argsSummary.length > 60 ? row.entry.argsSummary.slice(0, 57) + '…' : row.entry.argsSummary }}
          </code>
          <code v-if="row.entry.sessionId" class="mono session-pill" :title="row.entry.sessionId">
            {{ row.entry.sessionId.slice(0, 8) }}
          </code>
          <code class="mono caller-pill">{{ row.entry.caller }}</code>
        </div>
      </div>
    </div>

    <!-- Per-session detail drawer (right-side, 60% width). Loads up to
         1000 entries for the chosen session id, breakdown by tool,
         copy-to-clipboard JSON of the whole session. -->
    <McpSessionDetailDrawer
      v-model="detailDrawerOpen"
      :session-id="detailSessionId"
    />

    <!-- Pagination -->
    <div v-if="totalCount > pageSize" class="activity-pagination">
      <el-pagination
        v-model:current-page="currentPage"
        v-model:page-size="pageSize"
        :total="totalCount"
        :page-sizes="[25, 50, 100, 200]"
        layout="total, sizes, prev, pager, next, jumper"
        background
        small
        @current-change="refresh"
        @size-change="refresh"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Refresh, ArrowRight, Lock, Download } from '@element-plus/icons-vue'
import McpActivityTimeline from '../mcp/McpActivityTimeline.vue'
import McpTopToolsPanel from '../mcp/McpTopToolsPanel.vue'
import McpSessionDetailDrawer from '../mcp/McpSessionDetailDrawer.vue'
import {
  fetchMcpToolCalls,
  fetchMcpToolCallStats,
  subscribeEventsMap,
  type McpToolCallEntry,
  type McpToolCallStats,
} from '../../api/daemon'

const { t } = useI18n()

const entries = ref<McpToolCallEntry[]>([])
const totalCount = ref(0)
const stats = ref<McpToolCallStats | null>(null)
const loading = ref(false)

const currentPage = ref(1)
const pageSize = ref(50)
const dangerFilter = ref<string | null>(null)
const toolFilter = ref('')
const sessionFilter = ref('')
const searchQuery = ref('')
const collapseReads = ref(true)
const viewMode = ref<'sessions' | 'flat'>('sessions')

// Track which session cards are collapsed (default: all but first).
const collapsedSessions = ref<Set<string>>(new Set())

// Track which collapsed-read groups the operator has expanded.
const expandedReadGroups = ref<Set<string>>(new Set())

// Click-to-expand per call row to show full args + intent link + error.
const expandedRows = ref<Set<string>>(new Set())
function toggleRow(id: string): void {
  if (expandedRows.value.has(id)) expandedRows.value.delete(id)
  else expandedRows.value.add(id)
  expandedRows.value = new Set(expandedRows.value)
}
function goToIntent(intentId: string | null): void {
  if (!intentId) return
  window.location.hash = '/mcp/intents'
}
async function copyArgs(text: string): Promise<void> {
  try { await navigator.clipboard.writeText(text) } catch { /* ignore */ }
}

function expandReads(key: string): void {
  if (expandedReadGroups.value.has(key)) {
    expandedReadGroups.value.delete(key)
  } else {
    expandedReadGroups.value.add(key)
  }
  expandedReadGroups.value = new Set(expandedReadGroups.value)
}

function toggleSession(key: string): void {
  if (collapsedSessions.value.has(key)) {
    collapsedSessions.value.delete(key)
  } else {
    collapsedSessions.value.add(key)
  }
  collapsedSessions.value = new Set(collapsedSessions.value)
}

interface DisplayRow {
  key: string
  kind: 'call' | 'collapsed-reads'
  entry: McpToolCallEntry
  count: number
  lastAt: string
}

interface SessionGroup {
  key: string
  sessionId: string | null
  caller: string
  startedAt: string
  endedAt: string
  entries: McpToolCallEntry[]
  reads: number
  mutates: number
  destructives: number
  errors: number
}

// Group consecutive entries by sessionId (5 min gap = new group).
const sessionGroups = computed<SessionGroup[]>(() => {
  const groups: SessionGroup[] = []
  let current: SessionGroup | null = null
  for (const e of entries.value) {
    const breakHere = !current
      || e.sessionId !== current.sessionId
      || (current.endedAt && Math.abs(new Date(current.endedAt).getTime() - new Date(e.calledAt).getTime()) > 5 * 60 * 1000)
    if (breakHere) {
      current = {
        key: `${e.sessionId ?? 'none'}-${e.id}`,
        sessionId: e.sessionId,
        caller: e.caller,
        startedAt: e.calledAt,
        endedAt: e.calledAt,
        entries: [],
        reads: 0,
        mutates: 0,
        destructives: 0,
        errors: 0,
      }
      groups.push(current)
    }
    current!.entries.push(e)
    current!.endedAt = e.calledAt
    if (e.dangerLevel === 'read') current!.reads++
    else if (e.dangerLevel === 'mutate') current!.mutates++
    else if (e.dangerLevel === 'destructive') current!.destructives++
    if (e.resultCode !== 'ok') current!.errors++
  }
  return groups
})

// Group reads inside a session (when collapseReads is on).
function groupReads(sessionEntries: McpToolCallEntry[], _sessionKey: string): DisplayRow[] {
  const result: DisplayRow[] = []
  let i = 0
  while (i < sessionEntries.length) {
    const cur = sessionEntries[i]
    if (collapseReads.value && cur.dangerLevel === 'read') {
      let j = i + 1
      while (j < sessionEntries.length && sessionEntries[j].dangerLevel === 'read') {
        j++
      }
      const runLen = j - i
      if (runLen >= 3 && !expandedReadGroups.value.has(cur.id)) {
        result.push({ key: cur.id, kind: 'collapsed-reads', entry: cur, count: runLen, lastAt: cur.calledAt })
        i = j
        continue
      }
      result.push({ key: cur.id, kind: 'call', entry: cur, count: 1, lastAt: cur.calledAt })
      i++
    } else {
      result.push({ key: cur.id, kind: 'call', entry: cur, count: 1, lastAt: cur.calledAt })
      i++
    }
  }
  return result
}

// Flat-view grouping (legacy: all entries collapsed across sessions).
const flatView = computed<DisplayRow[]>(() => groupReads(entries.value, 'flat'))

// Auto-expand first session, collapse rest, on each refresh.
watch(sessionGroups, (groups) => {
  if (groups.length === 0) return
  const newCollapsed = new Set<string>()
  for (let idx = 1; idx < groups.length; idx++) newCollapsed.add(groups[idx].key)
  collapsedSessions.value = newCollapsed
})

function formatRelative(iso: string): string {
  try {
    const dt = new Date(iso).getTime()
    if (!Number.isFinite(dt)) return ''
    const deltaSec = Math.max(0, Math.round((Date.now() - dt) / 1000))
    if (deltaSec < 60) return t('mcpActivity.justNow')
    if (deltaSec < 3600) return t('mcpActivity.minutesAgo', { n: Math.floor(deltaSec / 60) })
    if (deltaSec < 86400) return t('mcpActivity.hoursAgo', { n: Math.floor(deltaSec / 3600) })
    return t('mcpActivity.daysAgo', { n: Math.floor(deltaSec / 86400) })
  } catch { return '' }
}

function formatDuration(startIso: string, endIso: string): string {
  try {
    const ms = Math.max(0, new Date(startIso).getTime() - new Date(endIso).getTime())
    if (ms < 1000) return `${ms}ms`
    const s = Math.round(ms / 1000)
    if (s < 60) return `${s}s`
    return `${Math.round(s / 60)}m`
  } catch { return '' }
}

function onToolSelect(toolName: string): void {
  // TopTools click → instant filter on the feed below
  toolFilter.value = toolName
}

// Pick a sensible unit for the throughput KPI so a low-traffic backend
// doesn't show "0.0/min" when ~10 calls/day is plenty visible. Switches
// to /h or /day when /min rounds toward zero.
function formatThroughput(perMinute: number): { num: string; unit: string } {
  if (perMinute >= 1) return { num: perMinute.toFixed(1), unit: '/min' }
  const perHour = perMinute * 60
  if (perHour >= 1) return { num: perHour.toFixed(1), unit: '/h' }
  const perDay = perHour * 24
  return { num: perDay.toFixed(0), unit: '/day' }
}

function setDangerFilter(level: 'read' | 'mutate' | 'destructive' | null): void {
  // Click on already-active level clears, click on inactive switches.
  if (dangerFilter.value === level) {
    dangerFilter.value = null
  } else {
    dangerFilter.value = level
  }
}

const detailDrawerOpen = ref(false)
const detailSessionId = ref<string | null>(null)
function openSessionDetail(sessionId: string | null): void {
  if (!sessionId) return
  detailSessionId.value = sessionId
  detailDrawerOpen.value = true
}

async function exportCsv(): Promise<void> {
  // Stream from daemon directly; preserves Content-Disposition so the
  // browser saves it as `mcp-audit-{date}.csv`. Filters in URL match
  // current view so operator gets exactly what they're seeing.
  const params = new URLSearchParams()
  if (dangerFilter.value) params.set('dangerLevel', dangerFilter.value)
  if (toolFilter.value) params.set('toolName', toolFilter.value)
  if (sessionFilter.value) params.set('sessionId', sessionFilter.value)
  const url = `/api/mcp/tool-calls/export.csv${params.toString() ? '?' + params.toString() : ''}`
  // Use the same baseUrl + auth pattern as the json() helper. Easier to
  // ask the api module for a URL we can fetch with a Bearer header and
  // turn into a Blob → object URL → click.
  try {
    const { daemonBaseUrl, daemonAuthHeaders } = await import('../../api/daemon')
    const res = await fetch(daemonBaseUrl() + url, { headers: daemonAuthHeaders() })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const blob = await res.blob()
    const objUrl = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = objUrl
    a.download = `mcp-audit-${new Date().toISOString().slice(0, 10)}.csv`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    setTimeout(() => URL.revokeObjectURL(objUrl), 5_000)
  } catch (err) {
    console.warn('[McpActivity] CSV export failed:', err)
  }
}

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const offset = (currentPage.value - 1) * pageSize.value
    const [list, s] = await Promise.all([
      fetchMcpToolCalls(pageSize.value, offset, dangerFilter.value, toolFilter.value || null, sessionFilter.value || null, searchQuery.value || null),
      fetchMcpToolCallStats(1440),
    ])
    entries.value = list.entries
    totalCount.value = list.total
    stats.value = s
  } catch (err) {
    console.warn('[McpActivity] refresh failed:', err)
  } finally {
    loading.value = false
  }
}

// Debounce search input by 300ms so typing doesn't burn requests.
let searchDebounce: ReturnType<typeof setTimeout> | null = null
watch(searchQuery, () => {
  if (searchDebounce) clearTimeout(searchDebounce)
  searchDebounce = setTimeout(() => {
    currentPage.value = 1
    void refresh()
  }, 300)
})

watch([dangerFilter, toolFilter, sessionFilter], () => {
  currentPage.value = 1
  void refresh()
})

let unsubscribe: (() => void) | null = null
let pollTimer: ReturnType<typeof setInterval> | null = null

// Throttle: SSE bursts during AI activity could fire 100+ events/s for
// chains of read calls. Coalesce to at most one refresh per 600ms to
// keep the page snappy without losing real-time feel.
let pendingRefresh: ReturnType<typeof setTimeout> | null = null
function throttledRefresh(): void {
  if (pendingRefresh) return
  pendingRefresh = setTimeout(() => {
    pendingRefresh = null
    void refresh()
  }, 600)
}

onMounted(() => {
  void refresh()
  unsubscribe = subscribeEventsMap({
    'mcp:tool-call': () => { throttledRefresh() },
    'mcp:intent-changed': () => { void refresh() },
    'mcp:confirm-request': () => { void refresh() },
  })
  // Slow safety-net poll (5min) in case SSE silently drops a connection
  // — far less aggressive than the original 30s tick now that real
  // updates flow through `mcp:tool-call`.
  pollTimer = setInterval(() => { void refresh() }, 300_000)
})

onBeforeUnmount(() => {
  if (unsubscribe) unsubscribe()
  if (pollTimer) clearInterval(pollTimer)
  // Clear pending timeouts so they don't fire after unmount and try to
  // mutate disposed reactive state.
  if (searchDebounce) clearTimeout(searchDebounce)
  if (pendingRefresh) clearTimeout(pendingRefresh)
})
</script>

<style scoped>
.mcp-activity-page { display: flex; flex-direction: column; gap: 12px; }
.analytics-row {
  display: grid;
  grid-template-columns: 2fr 1fr;
  gap: 12px;
}
@media (max-width: 900px) {
  .analytics-row { grid-template-columns: 1fr; }
}
.analytics-timeline, .analytics-toptools { min-width: 0; }
.perf-kpis {
  display: flex; flex-wrap: wrap; gap: 6px;
  padding: 6px 10px;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
}
.kpi {
  flex: 1 1 100px;
  min-width: 90px;
  padding: 4px 10px;
  border-left: 2px solid var(--el-border-color);
  display: flex;
  align-items: baseline;
  gap: 4px;
}
.kpi.warn { border-left-color: var(--el-color-warning); }
.kpi.danger { border-left-color: var(--el-color-danger); background: var(--el-color-danger-light-9); }
.kpi-num { font-size: 18px; font-weight: 600; }
.kpi-unit { font-size: 11px; color: var(--el-text-color-secondary); }
.kpi-label {
  margin-left: auto;
  font-size: 10px;
  color: var(--el-text-color-secondary);
  text-transform: uppercase;
  letter-spacing: 0.3px;
}
.activity-stats { display: flex; flex-wrap: wrap; gap: 12px; }
.stat-tile {
  flex: 1 1 120px; min-width: 100px;
  padding: 10px 14px;
  background: var(--el-fill-color-light);
  border-radius: 6px;
  border-left: 3px solid var(--el-border-color);
}
.stat-tile.read { border-left-color: var(--el-color-info); }
.stat-tile.mutate { border-left-color: var(--el-color-warning); }
.stat-tile.destructive { border-left-color: var(--el-color-danger); }
.stat-tile.error { border-left-color: var(--el-color-danger); background: var(--el-color-danger-light-9); }
.stat-tile.clickable {
  cursor: pointer;
  transition: transform 0.15s, box-shadow 0.15s;
  user-select: none;
}
.stat-tile.clickable:hover {
  transform: translateY(-1px);
  box-shadow: 0 2px 8px var(--el-fill-color-darker);
}
.stat-tile.clickable.active {
  background: var(--el-color-primary-light-9);
  outline: 2px solid var(--el-color-primary-light-5);
}
.stat-tile.clickable:focus-visible,
.session-header:focus-visible,
.session-pill.clickable:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}
.stat-num { font-size: 20px; font-weight: 600; }
.stat-label { font-size: 11px; color: var(--el-text-color-secondary); text-transform: uppercase; letter-spacing: 0.5px; }
.activity-filters {
  display: flex; flex-wrap: wrap; gap: 12px; align-items: center;
  padding: 8px 0;
}
.total-count { font-size: 12px; }

/* Sessions view — denser table-like layout instead of separate cards.
   Rule of thumb: a 13" laptop should fit at least ~12 collapsed
   sessions on screen. Each session is one compact row; only its
   highlight (left edge color, expanded body) sets it apart. */
.sessions-list {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  overflow: hidden;
}
.session-card {
  border-bottom: 1px solid var(--el-border-color-lighter);
  border-left: 3px solid transparent;
}
.session-card:last-child { border-bottom: none; }
.session-card.has-destructive { border-left-color: var(--el-color-danger); }
.session-card.has-error { border-left-color: var(--el-color-danger); background: var(--el-color-danger-light-9); }
.session-header {
  display: flex; align-items: center; gap: 10px;
  padding: 6px 12px;
  background: var(--el-fill-color-light);
  cursor: pointer;
  user-select: none;
  font-size: 12px;
}
.session-header:hover { background: var(--el-fill-color-darker); }
.session-header .chev { transition: transform 0.15s; font-size: 12px; flex-shrink: 0; }
.session-header .chev.rotated { transform: rotate(90deg); }
.session-header .at { flex-shrink: 0; }
.session-header .dur-range { flex-shrink: 0; }
.session-counts {
  margin-left: auto;
  display: flex; gap: 4px;
  align-items: center;
  flex-shrink: 0;
}
.count-pill {
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 11px;
  font-weight: 600;
  background: var(--el-fill-color-darker);
}
.count-pill.read { background: var(--el-color-info-light-7); color: var(--el-color-info); }
.count-pill.mutate { background: var(--el-color-warning-light-7); color: var(--el-color-warning); }
.count-pill.destructive { background: var(--el-color-danger-light-7); color: var(--el-color-danger); }
.count-pill.error { background: var(--el-color-danger); color: white; }
.session-body { background: var(--el-bg-color); }

/* Flat view & shared rows */
.activity-timeline {
  display: flex; flex-direction: column;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  overflow: hidden;
}
.activity-group { border-bottom: 1px solid var(--el-border-color-lighter); }
.activity-group:last-child { border-bottom: none; }
.collapsed-reads-row {
  display: flex; align-items: center; gap: 10px;
  padding: 6px 12px;
  background: var(--el-fill-color);
  cursor: pointer;
  font-size: 12px;
}
.collapsed-reads-row:hover { background: var(--el-fill-color-darker); }
.collapsed-reads-row .chev { font-size: 10px; }
.call-row {
  display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
  padding: 8px 12px;
  font-size: 12px;
  border-top: 1px solid var(--el-border-color-lighter);
}
.session-body .call-row:first-child { border-top: none; }
.call-row:hover { background: var(--el-fill-color-light); }
.call-row.danger-destructive { background: var(--el-color-danger-light-9); }
.call-row.clickable-row { cursor: pointer; }
.call-row.expanded { background: var(--el-fill-color-darker); }
.row-detail {
  padding: 8px 16px 12px 28px;
  background: var(--el-fill-color);
  border-top: 1px dashed var(--el-border-color-lighter);
  font-size: 12px;
}
.detail-grid {
  display: grid;
  grid-template-columns: 120px 1fr;
  gap: 4px 12px;
}
.detail-label {
  color: var(--el-text-color-secondary);
  text-transform: uppercase;
  font-size: 10px;
  letter-spacing: 0.5px;
  padding-top: 2px;
}
.detail-value { word-break: break-all; }
.detail-value.clickable { cursor: pointer; color: var(--el-color-primary); }
.detail-error { color: var(--el-color-danger); }
.detail-args { margin-top: 8px; }
.args-full {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
  padding: 8px;
  margin: 4px 0;
  font-size: 11px;
  max-height: 200px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
}
.dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
.dot-read { background: var(--el-color-info); }
.dot-mutate { background: var(--el-color-warning); }
.dot-destructive { background: var(--el-color-danger); }
.tool-name { font-weight: 600; }
.args-preview { color: var(--el-text-color-secondary); }
.session-pill, .caller-pill {
  padding: 1px 6px;
  background: var(--el-fill-color-darker);
  border-radius: 3px;
  font-size: 10px;
}
.session-pill.clickable {
  cursor: pointer;
  user-select: none;
  transition: background 0.15s;
}
.session-pill.clickable:hover {
  background: var(--el-color-primary-light-7);
  color: var(--el-color-primary);
}
.dur, .at, .dur-range { font-size: 11px; }
.activity-pagination { display: flex; justify-content: flex-end; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.muted { color: var(--el-text-color-secondary); }
.time-range { margin-left: auto; }
</style>
