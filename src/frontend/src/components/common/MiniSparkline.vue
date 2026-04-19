<template>
  <svg :width="width" :height="height" :viewBox="`0 0 ${width} ${height}`" class="mini-sparkline">
    <rect
      v-for="(v, i) in normalized"
      :key="i"
      :x="i * barWidth"
      :y="height - v * height"
      :width="barWidth - 1"
      :height="v * height"
      :fill="color"
      rx="1"
    />
  </svg>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{
  values: number[]
  width?: number
  height?: number
  color?: string
}>(), {
  width: 140,
  height: 28,
  color: 'var(--el-color-primary)',
})

const barWidth = computed(() => props.values.length > 0 ? props.width / props.values.length : 0)
const normalized = computed(() => {
  if (props.values.length === 0) return []
  const max = Math.max(...props.values, 1)
  return props.values.map(v => v / max)
})
</script>

<style scoped>
.mini-sparkline { display: inline-block; vertical-align: middle; }
</style>
