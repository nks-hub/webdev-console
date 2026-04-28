<template>
  <div class="mcp-activity-page">
    <!-- Activity stats banner — at-a-glance traffic overview for last 24h -->
    <div v-if="stats" class="activity-stats">
      <div class="stat-tile">
        <div class="stat-num">{{ stats.total }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.totalCalls') }}</div>
      </div>
      <div class="stat-tile read">
        <div class="stat-num">{{ stats.reads }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.reads') }}</div>
      </div>
      <div class="stat-tile mutate">
        <div class="stat-num">{{ stats.mutates }}</div>
        <div class="stat-label">{{ t('mcpActivity.stats.mutates') }}</div>
      </div>
      <div class="stat-tile destructive">
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

    <!-- Filters row -->
    <div class="activity-filters">
      <el-select v-model="dangerFilter" size="small" :placeholder="t('mcpActivity.filter.allDanger')" clearable style="width: 160px">
        <el-option :label="t('mcpActivity.danger.read')" value="read" />
        <el-option :label="t('mcpActivity.danger.mutate')" value="mutate" />
        <el-option :label="t('mcpActivity.danger.destructive')" value="destructive" />
      </el-select>
      <el-input v-model="toolFilter" size="small" :placeholder="t('mcpActivity.filter.toolName')" clearable style="width: 240px" />
      <el-checkbox v-model="collapseReads" size="small">{{ t('mcpActivity.filter.collapseReads') }}</el-checkbox>
      <el-button size="small" :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> {{ t('mcpActivity.refresh') }}
      </el-button>
      <span v-if="totalCount" class="muted total-count">
        {{ t('mcpActivity.totalShown', { n: groupedView.length, total: totalCount }) }}
      </span>
    </div>

    <!-- Empty state -->
    <el-empty v-if="!loading && entries.length === 0" :description="t('mcpActivity.empty')" />

    <!-- Grouped activity timeline -->
    <div v-else class="activity-timeline">
      <div v-for="group in groupedView" :key="group.key" class="activity-group" :class="{ collapsed: group.kind === 'collapsed-reads' }">
        <!-- Collapsed reads summary row -->
        <div v-if="group.kind === 'collapsed-reads'" class="collapsed-reads-row" @click="expandReads(group.key)">
          <el-icon class="chev"><ArrowRight /></el-icon>
          <span class="muted">{{ t('mcpActivity.readsCollapsed', { n: group.count }) }}</span>
          <span class="time-range muted">{{ formatRelative(group.lastAt) }}</span>
        </div>
        <!-- Standard call row -->
        <div v-else class="call-row" :class="`danger-${group.entry.dangerLevel}`">
          <span class="dot" :class="`dot-${group.entry.dangerLevel}`" />
          <code class="mono tool-name">{{ group.entry.toolName }}</code>
          <el-tag v-if="group.entry.dangerLevel === 'destructive'" type="danger" size="small" effect="plain">
            {{ t('mcpActivity.danger.destructive') }}
          </el-tag>
          <el-tag v-else-if="group.entry.dangerLevel === 'mutate'" type="warning" size="small" effect="plain">
            {{ t('mcpActivity.danger.mutate') }}
          </el-tag>
          <el-tag v-if="group.entry.resultCode !== 'ok'" type="danger" size="small">{{ group.entry.resultCode }}</el-tag>
          <span class="muted dur">{{ group.entry.durationMs }}ms</span>
          <span class="muted at">{{ formatRelative(group.entry.calledAt) }}</span>
          <code v-if="group.entry.argsSummary && group.entry.argsSummary !== '{}'" class="mono args-preview" :title="group.entry.argsSummary">
            {{ group.entry.argsSummary.length > 60 ? group.entry.argsSummary.slice(0, 57) + '…' : group.entry.argsSummary }}
          </code>
          <code v-if="group.entry.sessionId" class="mono session-pill" :title="group.entry.sessionId">
            {{ group.entry.sessionId.slice(0, 8) }}
          </code>
          <code class="mono caller-pill">{{ group.entry.caller }}</code>
        </div>
      </div>
    </div>

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
import { Refresh, ArrowRight } from '@element-plus/icons-vue'
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
const collapseReads = ref(true)

// Track which collapsed-read groups the operator has expanded — keyed
// by group key (first read entry id) so toggling persists during page
// session.
const expandedReadGroups = ref<Set<string>>(new Set())

function expandReads(key: string): void {
  if (expandedReadGroups.value.has(key)) {
    expandedReadGroups.value.delete(key)
  } else {
    expandedReadGroups.value.add(key)
  }
  // Force reactivity refresh
  expandedReadGroups.value = new Set(expandedReadGroups.value)
}

interface DisplayGroup {
  key: string
  kind: 'call' | 'collapsed-reads'
  entry: McpToolCallEntry
  count: number
  lastAt: string
}

// Group consecutive reads (same tool name, < 5min gap) into a single
// collapsed row. Mutate/destructive rows always render individually
// because each one matters.
const groupedView = computed<DisplayGroup[]>(() => {
  const result: DisplayGroup[] = []
  let i = 0
  const list = entries.value
  while (i < list.length) {
    const cur = list[i]
    if (collapseReads.value && cur.dangerLevel === 'read') {
      // Find run of consecutive read entries (any tool) within 5 min.
      let j = i + 1
      while (j < list.length && list[j].dangerLevel === 'read') {
        const gap = Math.abs(new Date(list[j - 1].calledAt).getTime() - new Date(list[j].calledAt).getTime())
        if (gap > 5 * 60 * 1000) break
        j++
      }
      const runLen = j - i
      if (runLen >= 3 && !expandedReadGroups.value.has(cur.id)) {
        result.push({
          key: cur.id,
          kind: 'collapsed-reads',
          entry: cur,
          count: runLen,
          lastAt: cur.calledAt,
        })
        i = j
        continue
      }
      // Run too short or operator expanded — render individually.
      result.push({ key: cur.id, kind: 'call', entry: cur, count: 1, lastAt: cur.calledAt })
      i++
    } else {
      result.push({ key: cur.id, kind: 'call', entry: cur, count: 1, lastAt: cur.calledAt })
      i++
    }
  }
  return result
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

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const offset = (currentPage.value - 1) * pageSize.value
    const [list, s] = await Promise.all([
      fetchMcpToolCalls(pageSize.value, offset, dangerFilter.value, toolFilter.value || null),
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

watch([dangerFilter, toolFilter], () => {
  currentPage.value = 1
  void refresh()
})

let unsubscribe: (() => void) | null = null
let pollTimer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  void refresh()
  // Refresh on relevant SSE events. Tool-call audit doesn't have its
  // own event yet — we piggyback on intent-changed (which fires for
  // destructive calls) plus a 30s poll for read traffic.
  unsubscribe = subscribeEventsMap({
    'mcp:intent-changed': () => { void refresh() },
    'mcp:confirm-request': () => { void refresh() },
  })
  pollTimer = setInterval(() => { void refresh() }, 30_000)
})

onBeforeUnmount(() => {
  if (unsubscribe) unsubscribe()
  if (pollTimer) clearInterval(pollTimer)
})
</script>

<style scoped>
.mcp-activity-page {
  display: flex; flex-direction: column; gap: 12px;
}
.activity-stats {
  display: flex; flex-wrap: wrap; gap: 12px;
}
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
.stat-num { font-size: 20px; font-weight: 600; }
.stat-label { font-size: 11px; color: var(--el-text-color-secondary); text-transform: uppercase; letter-spacing: 0.5px; }
.activity-filters {
  display: flex; flex-wrap: wrap; gap: 12px; align-items: center;
  padding: 8px 0;
}
.total-count { font-size: 12px; }
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
}
.call-row:hover { background: var(--el-fill-color-light); }
.call-row.danger-destructive { background: var(--el-color-danger-light-9); }
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
.dur, .at { font-size: 11px; }
.activity-pagination { display: flex; justify-content: flex-end; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.muted { color: var(--el-text-color-secondary); }
.time-range { margin-left: auto; }
</style>
