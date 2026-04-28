<template>
  <div class="timeline-chart" v-if="buckets.length > 0">
    <div class="timeline-title muted">
      {{ t('mcpActivity.timeline.title', { hours: withinHours }) }}
      <span v-if="totalCalls === 0" class="empty-note">{{ t('mcpActivity.timeline.empty') }}</span>
    </div>
    <svg :viewBox="`0 0 ${viewWidth} ${viewHeight}`" preserveAspectRatio="none" class="chart-svg" :style="{ height: '60px' }">
      <g v-for="(b, idx) in buckets" :key="b.hour">
        <!-- Stacked bar: read (bottom), mutate, destructive (top) -->
        <rect
          v-if="b.reads > 0"
          :x="idx * barWidth + 1"
          :y="readY(b)"
          :width="barWidth - 2"
          :height="readH(b)"
          class="bar-read"
          :title="`${b.hour}: ${b.reads} reads`"
        />
        <rect
          v-if="b.mutates > 0"
          :x="idx * barWidth + 1"
          :y="mutateY(b)"
          :width="barWidth - 2"
          :height="mutateH(b)"
          class="bar-mutate"
        />
        <rect
          v-if="b.destructives > 0"
          :x="idx * barWidth + 1"
          :y="destructiveY(b)"
          :width="barWidth - 2"
          :height="destructiveH(b)"
          class="bar-destructive"
        />
        <!-- Hour label every 6th bar -->
        <text
          v-if="idx % 6 === 0"
          :x="idx * barWidth + barWidth / 2"
          :y="viewHeight - 1"
          text-anchor="middle"
          class="hour-label"
        >{{ shortHour(b.hour) }}</text>
        <!-- Tooltip target -->
        <rect
          :x="idx * barWidth"
          y="0"
          :width="barWidth"
          :height="viewHeight - 12"
          fill="transparent"
          @mouseenter="hover = idx"
          @mouseleave="hover = null"
        >
          <title>{{ tooltipFor(b) }}</title>
        </rect>
      </g>
    </svg>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  fetchMcpToolCallTimeline,
  subscribeEventsMap,
  type McpToolCallTimelineBucket,
} from '../../api/daemon'

const { t } = useI18n()

const buckets = ref<McpToolCallTimelineBucket[]>([])
const withinHours = 24
const hover = ref<number | null>(null)

const viewWidth = 800
const viewHeight = 60
const chartHeight = viewHeight - 12  // reserve 12px for hour labels

const barWidth = computed(() => viewWidth / Math.max(1, buckets.value.length))

const maxBucketTotal = computed(() => {
  return Math.max(1, ...buckets.value.map(b => b.total))
})

const totalCalls = computed(() =>
  buckets.value.reduce((sum, b) => sum + b.total, 0),
)

function scale(n: number): number {
  return (n / maxBucketTotal.value) * (chartHeight - 4)
}
function readY(b: McpToolCallTimelineBucket): number {
  return chartHeight - scale(b.reads)
}
function readH(b: McpToolCallTimelineBucket): number {
  return scale(b.reads)
}
function mutateY(b: McpToolCallTimelineBucket): number {
  return chartHeight - scale(b.reads + b.mutates)
}
function mutateH(b: McpToolCallTimelineBucket): number {
  return scale(b.mutates)
}
function destructiveY(b: McpToolCallTimelineBucket): number {
  return chartHeight - scale(b.reads + b.mutates + b.destructives)
}
function destructiveH(b: McpToolCallTimelineBucket): number {
  return scale(b.destructives)
}

function shortHour(hour: string): string {
  // 'YYYY-MM-DDTHH' → 'HH'
  return hour.slice(11, 13) + 'h'
}

function tooltipFor(b: McpToolCallTimelineBucket): string {
  return `${b.hour} — total: ${b.total}, read: ${b.reads}, mutate: ${b.mutates}, destructive: ${b.destructives}` +
    (b.errors > 0 ? `, errors: ${b.errors}` : '')
}

let unsubscribe: (() => void) | null = null
let pollTimer: ReturnType<typeof setInterval> | null = null

async function refresh(): Promise<void> {
  try {
    const data = await fetchMcpToolCallTimeline(withinHours)
    buckets.value = fillBuckets(data.buckets)
  } catch (err) {
    console.warn('[McpActivityTimeline] refresh failed:', err)
  }
}

// SQL only returns hours that HAVE data — fill in empty buckets so the
// chart shows a continuous 24h span (dead hours render as gaps).
function fillBuckets(raw: McpToolCallTimelineBucket[]): McpToolCallTimelineBucket[] {
  const map = new Map<string, McpToolCallTimelineBucket>()
  for (const b of raw) map.set(b.hour, b)
  const out: McpToolCallTimelineBucket[] = []
  const now = new Date()
  for (let i = withinHours - 1; i >= 0; i--) {
    const dt = new Date(now.getTime() - i * 60 * 60 * 1000)
    // Match SQLite strftime('%Y-%m-%dT%H', ...) format which uses UTC
    // when the column carries 'Z' suffix.
    const utcHour = `${dt.getUTCFullYear()}-${String(dt.getUTCMonth() + 1).padStart(2, '0')}-` +
      `${String(dt.getUTCDate()).padStart(2, '0')}T${String(dt.getUTCHours()).padStart(2, '0')}`
    out.push(map.get(utcHour) ?? {
      hour: utcHour, reads: 0, mutates: 0, destructives: 0, errors: 0, total: 0,
    })
  }
  return out
}

onMounted(() => {
  void refresh()
  unsubscribe = subscribeEventsMap({
    'mcp:tool-call': () => { void refresh() },
  })
  // Hour boundary slow tick — chart only changes meaningfully when an
  // hour rolls over.
  pollTimer = setInterval(() => { void refresh() }, 60_000)
})

onBeforeUnmount(() => {
  if (unsubscribe) unsubscribe()
  if (pollTimer) clearInterval(pollTimer)
})
</script>

<style scoped>
.timeline-chart {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 8px 12px;
  background: var(--el-fill-color-light);
}
.timeline-title {
  display: flex; align-items: center; gap: 8px;
  font-size: 11px;
  margin-bottom: 4px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
.empty-note { color: var(--el-text-color-disabled); }
.chart-svg {
  width: 100%;
  display: block;
}
.bar-read { fill: var(--el-color-info); opacity: 0.7; }
.bar-mutate { fill: var(--el-color-warning); }
.bar-destructive { fill: var(--el-color-danger); }
.hour-label {
  font-size: 9px;
  fill: var(--el-text-color-secondary);
  font-family: ui-monospace, Consolas, monospace;
}
.muted { color: var(--el-text-color-secondary); }
</style>
