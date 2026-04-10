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

const chartOption = computed(() => ({
  tooltip: { trigger: 'axis' },
  grid: { top: 8, right: 8, bottom: 24, left: 36 },
  xAxis: { type: 'category', show: false, data: props.data.map((_, i) => i) },
  yAxis: { type: 'value', splitLine: { lineStyle: { color: '#333' } } },
  series: [{
    type: 'line',
    data: props.data,
    smooth: true,
    symbol: 'none',
    lineStyle: { color: props.color ?? '#67C23A', width: 2 },
    areaStyle: { color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [
      { offset: 0, color: (props.color ?? '#67C23A') + '40' },
      { offset: 1, color: (props.color ?? '#67C23A') + '05' }
    ]}}
  }]
}))
</script>

<style scoped>
.metrics-chart { width: 100%; height: 100%; }
.chart { width: 100%; min-height: 120px; }
</style>
