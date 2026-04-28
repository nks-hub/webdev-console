<template>
  <div class="timeline-chart" v-if="buckets.length > 0">
    <div class="timeline-title muted">
      {{ t('mcpActivity.timeline.title', { hours: withinHours }) }}
      <span v-if="totalCalls === 0" class="empty-note">{{ t('mcpActivity.timeline.empty') }}</span>
    </div>
    <svg
      :viewBox="`0 0 ${viewWidth} ${viewHeight}`"
      preserveAspectRatio="none"
      class="chart-svg"
      role="img"
      :aria-label="t('mcpActivity.timeline.title', { hours: withinHours }) + ' — ' + totalCalls + ' calls'"
    >
      <!-- Y-axis baseline + 50% gridline so the chart reads as a chart
           instead of "scattered rectangles". Drawn first so bars overlay
           cleanly. -->
      <line x1="0" :y1="chartHeight" :x2="viewWidth" :y2="chartHeight" class="grid-line" />
      <line x1="0" :y1="chartHeight / 2" :x2="viewWidth" :y2="chartHeight / 2" class="grid-line dashed" />
      <g v-for="(b, idx) in buckets" :key="b.hour">
        <!-- Baseline tick — small line at chart bottom for every bucket so
             the timeline reads as a continuous span even when most
             buckets have no calls (sparse data is the common case for
             a dev workstation). Without this the chart looks broken. -->
        <rect
          :x="idx * barWidth + 1"
          :y="chartHeight - 4"
          :width="barWidth - 2"
          :height="4"
          class="bar-baseline"
        />
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
        <!-- Error indicator — small red triangle above the bar when any
             call in the bucket failed. Sits above the stacked bars in
             the small reserved gap so it stays visible regardless of
             stack height. -->
        <polygon
          v-if="b.errors > 0"
          :points="errorMarker(idx)"
          class="error-marker"
        >
          <title>{{ b.errors }} error{{ b.errors === 1 ? '' : 's' }} in {{ b.hour }}h</title>
        </polygon>
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

// viewBox aspect ratio is tuned to match the typical chart card the
// timeline lives in (~3:1). Earlier 800×60 viewBox combined with
// `preserveAspectRatio="none"` stretched hour labels and bars
// vertically by ~5× when the card was tall, making the chart look
// distorted. 800×240 keeps stretch under 1.5× in the same card.
const viewWidth = 800
const viewHeight = 240
const chartHeight = viewHeight - 24  // reserve 24px for hour labels

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

function errorMarker(idx: number): string {
  // Small downward-pointing triangle centered above the bar.
  const cx = idx * barWidth.value + barWidth.value / 2
  const top = 4
  const size = 8
  return `${cx - size},${top} ${cx + size},${top} ${cx},${top + size + 2}`
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
  padding: 10px 14px 6px;
  background: var(--el-fill-color-light);
  display: flex;
  flex-direction: column;
  /* Fill grid cell — was 60px-fixed which left huge empty space when
     placed in a tall card; now the SVG flexes with the parent. */
  height: 100%;
  min-height: 140px;
}
.timeline-title {
  display: flex; align-items: center; gap: 8px;
  font-size: 11px;
  margin-bottom: 6px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  flex: 0 0 auto;
}
.empty-note { color: var(--el-text-color-disabled); }
.chart-svg {
  width: 100%;
  flex: 1 1 auto;
  display: block;
  min-height: 100px;
}
.grid-line {
  stroke: var(--el-border-color-lighter);
  stroke-width: 1;
  vector-effect: non-scaling-stroke;
}
.grid-line.dashed { stroke-dasharray: 4 4; opacity: 0.6; }
.bar-baseline { fill: var(--el-border-color); }
.bar-read { fill: var(--el-color-info); opacity: 0.85; }
.bar-mutate { fill: var(--el-color-warning); }
.bar-destructive { fill: var(--el-color-danger); }
.error-marker { fill: var(--el-color-danger); }
.hour-label {
  font-size: 16px;
  fill: var(--el-text-color-secondary);
  font-family: ui-monospace, Consolas, monospace;
}
.muted { color: var(--el-text-color-secondary); }
</style>
