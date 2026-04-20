<template>
  <div class="metrics-chart" ref="chartContainer">
    <v-chart
      :option="chartOption"
      :autoresize="false"
      class="chart"
      :style="{ width: '100%', height: chartHeight + 'px' }"
    />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import VChart from 'vue-echarts'

use([CanvasRenderer, LineChart, GridComponent, TooltipComponent])

const props = defineProps<{
  data: number[]
  color?: string
  height?: number
}>()

const chartHeight = computed(() => props.height ?? 80)

const chartOption = computed(() => {
  const color = props.color ?? '#6366f1'
  const dataArr = props.data.length > 0 ? [...props.data] : [0]

  return {
    animation: false,
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#1c1e2a',
      borderColor: 'rgba(255,255,255,0.12)',
      textStyle: { color: '#eceef6', fontSize: 11 },
      formatter: (params: unknown) => {
        const val = (params as Array<{ value?: unknown }>)?.[0]?.value
        return val != null ? `${Number(val).toFixed(1)}` : ''
      },
    },
    grid: { top: 4, right: 4, bottom: 2, left: 30 },
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
        fontSize: 9,
        color: '#8b8fa8',
        formatter: (v: number) => v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(Math.round(v)),
      },
      splitLine: {
        lineStyle: { color: 'rgba(255,255,255,0.05)', type: 'dashed' },
      },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series: [{
      type: 'line',
      data: dataArr,
      smooth: 0.3,
      symbol: 'none',
      lineStyle: { color, width: 1.5 },
      areaStyle: {
        color: {
          type: 'linear',
          x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [
            { offset: 0, color: color + '25' },
            { offset: 1, color: color + '03' },
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
