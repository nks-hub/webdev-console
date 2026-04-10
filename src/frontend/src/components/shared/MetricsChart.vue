<template>
  <div class="metrics-chart">
    <v-chart :option="chartOption" :autoresize="true" class="chart" />
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
  title?: string
  data: number[]
  color?: string
}>()

const chartOption = computed(() => {
  const color = props.color ?? '#6366f1'
  const dataArr = props.data.length > 0 ? props.data : [0]

  return {
    animation: true,
    animationDuration: 300,
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#1c1e2a',
      borderColor: 'rgba(255,255,255,0.12)',
      textStyle: { color: '#eceef6', fontSize: 12 },
      formatter: (params: any) => {
        const val = params?.[0]?.value
        return val != null ? `${Number(val).toFixed(1)}` : ''
      },
    },
    grid: { top: 6, right: 6, bottom: 4, left: 32 },
    xAxis: {
      type: 'category',
      show: false,
      data: dataArr.map((_, i) => i),
      boundaryGap: false,
    },
    yAxis: {
      type: 'value',
      min: 0,
      splitNumber: 3,
      axisLabel: {
        fontSize: 10,
        color: '#8b8fa8',
        formatter: (v: number) => v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(Math.round(v)),
      },
      splitLine: {
        lineStyle: { color: 'rgba(255,255,255,0.06)', type: 'dashed' },
      },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series: [{
      type: 'line',
      data: dataArr,
      smooth: 0.3,
      symbol: 'none',
      sampling: 'lttb',
      lineStyle: { color, width: 1.5 },
      areaStyle: {
        color: {
          type: 'linear',
          x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [
            { offset: 0, color: color + '30' },
            { offset: 1, color: color + '05' },
          ],
        },
      },
    }],
  }
})
</script>

<style scoped>
.metrics-chart { width: 100%; height: 100%; }
.chart { width: 100%; min-height: 100px; }
</style>
