<template>
  <div class="site-metrics">
    <div class="metrics-controls">
      <label class="control">
        <span class="control-label">{{ $t('metrics.historical.dateLabel') }}</span>
        <el-date-picker
          v-model="date"
          type="date"
          size="small"
          :disabled-date="isFutureDate"
          value-format="YYYY-MM-DD"
          format="YYYY-MM-DD"
          :clearable="false"
          class="date-picker"
          @change="load"
        />
      </label>
      <label class="control">
        <span class="control-label">{{ $t('metrics.historical.granularityLabel') }}</span>
        <el-select v-model="granularity" size="small" class="granularity" @change="load">
          <el-option v-for="g in ['1m', '5m', '15m', '1h']" :key="g" :label="g" :value="g" />
        </el-select>
      </label>
      <el-button size="small" :loading="loading" :icon="Refresh" @click="load">{{ $t('metrics.historical.refresh') }}</el-button>
      <div class="series-toggles" role="group" :aria-label="$t('siteEdit.metrics')">
        <el-check-tag
          v-for="s in seriesOptions"
          :key="s.key"
          :checked="activeSeries.includes(s.key)"
          class="series-toggle"
          @change="(checked: boolean) => toggleSeries(s.key, checked)"
        >{{ $t(s.labelKey) }}</el-check-tag>
      </div>
    </div>

    <div class="chart-wrap">
      <div v-if="loading" class="chart-overlay">{{ $t('metrics.historical.loading') }}</div>
      <div v-else-if="isEmptyDay" class="chart-overlay">
        <strong>{{ $t('metrics.historical.emptyDay') }}</strong>
        <span>{{ $t('siteEdit.metricsEmpty') }}</span>
      </div>
      <v-chart v-if="active" :option="chartOption" :autoresize="true" class="chart" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, DataZoomComponent } from 'echarts/components'
import VChart from 'vue-echarts'
import { getHistoricalMetrics } from '../../api/daemon'
import type { HistoricalMetrics } from '../../api/types'
import { errorMessage } from '../../utils/errors'
import { resolveToken, rgba, useChartPalette } from '../../utils/themeColors'

use([CanvasRenderer, LineChart, GridComponent, TooltipComponent, DataZoomComponent])

/**
 * Traffic chart of one site for a chosen day. Loaded when the tab is shown and
 * on demand ("Refresh"); there is no background polling.
 */
const props = defineProps<{ domain: string; active: boolean }>()
const { t } = useI18n()
const palette = useChartPalette()

const pad = (n: number) => String(n).padStart(2, '0')
const today = () => { const d = new Date(); return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` }

const date = ref(today())
const granularity = ref('5m')
const loading = ref(false)
const data = ref<HistoricalMetrics | null>(null)

const seriesOptions = [
  { key: 'requests', labelKey: 'metrics.historical.seriesRequests' },
  { key: 'bytes', labelKey: 'metrics.historical.seriesBytes' },
  { key: 'errors', labelKey: 'metrics.historical.seriesErrors' },
] as const
type SeriesKey = typeof seriesOptions[number]['key']
const activeSeries = ref<SeriesKey[]>(['requests'])

function toggleSeries(key: SeriesKey, checked: boolean) {
  activeSeries.value = checked
    ? (activeSeries.value.includes(key) ? activeSeries.value : [...activeSeries.value, key])
    : activeSeries.value.filter(k => k !== key)
}

const isFutureDate = (d: Date) => d > new Date(new Date().setHours(23, 59, 59, 999))

async function load() {
  loading.value = true
  try {
    data.value = await getHistoricalMetrics(props.domain, { date: date.value, granularity: granularity.value })
  } catch (e) {
    ElMessage.error(t('siteEditToast.historicalMetricsFailed', { err: errorMessage(e) }))
    data.value = null
  } finally {
    loading.value = false
  }
}

const isEmptyDay = computed(() => !!data.value && data.value.series.every(s => s.data.every(p => p.value === 0)))

// Loads when the tab is first shown and again for another site.
watch(() => props.active, (on) => { if (on && !data.value) void load() })
watch(() => props.domain, () => { data.value = null; if (props.active) void load() })
onMounted(() => { if (props.active) void load() })

const chartOption = computed(() => {
  const p = palette.value
  const seriesColor: Record<SeriesKey, string> = {
    requests: rgba(p.accent),
    bytes: rgba(p.success),
    errors: rgba(resolveToken('--wdc-status-error')),
  }
  const first = data.value?.series[0]
  const labels = first ? first.data.map(pt => {
    const d = new Date(pt.ts)
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`
  }) : []
  const series = seriesOptions
    .filter(s => activeSeries.value.includes(s.key))
    .map(s => {
      const found = data.value?.series.find(sr => sr.name === s.key)
      const color = seriesColor[s.key]
      return {
        name: s.key,
        type: 'line' as const,
        data: found ? found.data.map(pt => pt.value) : labels.map(() => 0),
        smooth: 0.2,
        symbol: 'none',
        lineStyle: { color, width: 1.75 },
      }
    })
  const axisText = rgba(p.text3)
  return {
    animation: false,
    tooltip: {
      trigger: 'axis',
      backgroundColor: rgba(p.elevated),
      borderColor: rgba(p.borderStrong),
      textStyle: { color: rgba(p.text), fontSize: 12 },
    },
    grid: { top: 12, right: 16, bottom: 44, left: 48 },
    dataZoom: [
      { type: 'inside', start: 0, end: 100 },
      { type: 'slider', height: 18, bottom: 4, borderColor: rgba(p.border), fillerColor: rgba(p.accent, 0.14), textStyle: { color: axisText } },
    ],
    xAxis: {
      type: 'category',
      data: labels,
      boundaryGap: false,
      axisLabel: { fontSize: 11, color: axisText, interval: Math.max(0, Math.floor(labels.length / 12) - 1) },
      axisLine: { lineStyle: { color: rgba(p.borderStrong) } },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value',
      min: 0,
      splitNumber: 3,
      axisLabel: { fontSize: 11, color: axisText, formatter: (v: number) => (v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(Math.round(v))) },
      splitLine: { lineStyle: { color: rgba(p.border), type: 'dashed' } },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series,
  }
})
</script>

<style scoped>
.site-metrics { display: grid; gap: var(--wdc-space-4); }
.metrics-controls { display: flex; flex-wrap: wrap; align-items: flex-end; gap: var(--wdc-space-4); }
.control { display: grid; gap: var(--wdc-space-1); }
.control-label { color: var(--wdc-text-2); font-size: 0.8rem; }
.date-picker { width: 150px; }
.granularity { width: 90px; }
.series-toggles { display: flex; gap: var(--wdc-space-2); margin-left: auto; }
.chart-wrap {
  position: relative;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  background: var(--wdc-surface);
  padding: var(--wdc-space-3);
}
.chart { width: 100%; height: 260px; }
.chart-overlay {
  position: absolute;
  inset: 0;
  z-index: 1;
  display: grid;
  place-content: center;
  gap: var(--wdc-space-1);
  padding: var(--wdc-space-4);
  text-align: center;
  color: var(--wdc-text-2);
  font-size: 0.87rem;
  pointer-events: none;
}
.chart-overlay strong { color: var(--wdc-text); font-family: var(--wdc-font-display); }
</style>
