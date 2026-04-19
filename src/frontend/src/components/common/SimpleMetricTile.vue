<template>
  <div class="metric-tile" :class="`tile-${variant}`" @click="$emit('click')" role="button" tabindex="0">
    <div class="tile-header">
      <el-icon v-if="iconComponent" class="tile-icon"><component :is="iconComponent" /></el-icon>
      <span class="tile-label">{{ label }}</span>
    </div>
    <div class="tile-value">{{ value }}</div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import * as EPIcons from '@element-plus/icons-vue'

const props = withDefaults(defineProps<{
  label: string
  value: string | number
  icon?: string
  variant?: 'default' | 'success' | 'warning' | 'danger'
}>(), {
  icon: '',
  variant: 'default',
})

defineEmits<{ (e: 'click'): void }>()

const iconComponent = computed(() => {
  if (!props.icon) return null
  return (EPIcons as any)[props.icon] ?? null
})
</script>

<style scoped>
.metric-tile {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: 12px;
  padding: 18px 20px;
  cursor: pointer;
  transition: all 0.15s;
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 104px;
}
.metric-tile:hover {
  transform: translateY(-2px);
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.25);
  border-color: var(--el-color-primary-light-3);
}
.metric-tile:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}
.tile-header {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--el-text-color-secondary);
}
.tile-icon { font-size: 16px; }
.tile-label {
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  font-weight: 500;
}
.tile-value {
  font-size: 28px;
  font-weight: 700;
  color: var(--wdc-text);
  font-variant-numeric: tabular-nums;
}
.tile-success .tile-value { color: var(--el-color-success); }
.tile-warning .tile-value { color: var(--el-color-warning); }
.tile-danger .tile-value { color: var(--el-color-danger); }
</style>
