<template>
  <div class="metrics-chart" ref="chartContainer">
    <v-chart
      v-if="chartReady"
      :option="chartOption"
      :autoresize="false"
      class="chart"
      :style="{ width: '100%', height: chartHeight + 'px' }"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { useChartPalette, rgba } from '../../utils/themeColors'

use([CanvasRenderer, LineChart, GridComponent, TooltipComponent])

const props = defineProps<{
  data: number[]
  /** Series colour: a design token tone. */
  tone?: 'accent' | 'success'
  height?: number
  /** Unit shown on the axis and in the tooltip ("%", "MB"). */
  unit?: string
}>()

const { locale } = useI18n()

const palette = useChartPalette()

const chartHeight = computed(() => props.height ?? 80)
const chartContainer = ref<HTMLElement | null>(null)
const chartReady = ref(false)
let resizeObserver: ResizeObserver | null = null

onMounted(() => {
  const updateReady = () => {
    const el = chartContainer.value
    chartReady.value = !!el && el.clientWidth > 0 && el.clientHeight > 0
  }
  updateReady()
  resizeObserver = new ResizeObserver(updateReady)
  if (chartContainer.value) resizeObserver.observe(chartContainer.value)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
})

// Values under 10 keep a decimal ("0,5 %"), so a quiet CPU does not read as
// "1 / 1 / 0" on the axis; larger values are rounded.
function formatValue(v: number): string {
  const digits = v < 1 ? 2 : v < 10 ? 1 : 0
  const text = new Intl.NumberFormat(String(locale.value), { maximumFractionDigits: digits }).format(v)
  return props.unit ? `${text} ${props.unit}` : text
}

const chartOption = computed(() => {
  const p = palette.value
  const line = props.tone === 'success' ? p.success : p.accent
  // Guard: caller may pass undefined before the first metrics payload
  // lands (async load). Previously `.length` on undefined threw and
  // poisoned the computed, which Vue surfaced as a page-wide render
  // error + Sentry FRONT-4 / FRONT-8.
  const series = props.data ?? []
  const dataArr = series.length > 0 ? [...series] : [0]

  return {
    animation: false,
    tooltip: {
      trigger: 'axis',
      backgroundColor: rgba(p.elevated),
      borderColor: rgba(p.borderStrong),
      textStyle: { color: rgba(p.text), fontSize: 11 },
      formatter: (params: unknown) => {
        const val = (params as Array<{ value?: unknown }>)?.[0]?.value
        return val != null ? formatValue(Number(val)) : ''
      },
    },
    grid: { top: 6, right: 6, bottom: 2, left: 52 },
    xAxis: {
      type: 'category',
      show: false,
      data: dataArr.map((_, i) => i),
      boundaryGap: false,
    },
    yAxis: {
      type: 'value',
      min: 0,
      splitNumber: 2,
      axisLabel: {
        fontSize: 10,
        color: rgba(p.text3),
        formatter: (v: number) => formatValue(v),
      },
      splitLine: {
        lineStyle: { color: rgba(p.border), type: 'dashed' },
      },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series: [{
      type: 'line',
      data: dataArr,
      smooth: 0.3,
      symbol: 'none',
      lineStyle: { color: rgba(line), width: 1.5 },
      areaStyle: {
        color: {
          type: 'linear',
          x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [
            { offset: 0, color: rgba(line, 0.16) },
            { offset: 1, color: rgba(line, 0.01) },
          ],
        },
      },
    }],
  }
})
</script>

<style scoped>
.metrics-chart {
  width: 100%;
  /* Lock to the prop height so flex parents (Dashboard service cards) can't
     stretch this sparkline unpredictably — charts without a fixed height tend
     to grow to fill their container on the next ResizeObserver cycle and
     push layout around. */
  height: v-bind('chartHeight + "px"');
  min-height: v-bind('chartHeight + "px"');
  max-height: v-bind('chartHeight + "px"');
  overflow: hidden;
  flex-shrink: 0;
}
</style>
