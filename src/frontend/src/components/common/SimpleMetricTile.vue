<template>
  <div
    class="metric-tile"
    :class="[`tile-${variant}`, { 'tile-static': !interactive }]"
    :role="interactive ? 'button' : undefined"
    :tabindex="interactive ? 0 : undefined"
    :aria-label="`${label}: ${value}`"
    @click="interactive && $emit('click')"
    @keydown.enter.prevent="interactive && $emit('click')"
    @keydown.space.prevent="interactive && $emit('click')"
  >
    <div class="tile-header">
      <el-icon v-if="iconComponent" class="tile-icon"><component :is="iconComponent" /></el-icon>
      <span class="tile-label">{{ label }}</span>
    </div>
    <div class="tile-value">{{ value }}</div>
  </div>
</template>

<script setup lang="ts">
import { computed, type Component } from 'vue'
import * as EPIcons from '@element-plus/icons-vue'

// Element Plus icons are re-exported as a namespace object whose keys
// are icon names. TypeScript treats namespace imports as a closed
// `{ foo: …, bar: … }` type — dynamic lookup by arbitrary string needs
// a Record widening. Using `Record<string, Component>` instead of `any`
// preserves the return type so callers still see the correct Component
// at the call site.
const ICONS = EPIcons as unknown as Record<string, Component>

const props = withDefaults(defineProps<{
  label: string
  value: string | number
  icon?: string
  variant?: 'default' | 'success' | 'warning' | 'danger'
  /** false renders a read-only tile: no button role, focus stop or click. */
  interactive?: boolean
}>(), {
  icon: '',
  variant: 'default',
  interactive: true,
})

defineEmits<{ (e: 'click'): void }>()

const iconComponent = computed(() => {
  if (!props.icon) return null
  return ICONS[props.icon] ?? null
})
</script>

<style scoped>
.metric-tile {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-md);
  padding: var(--wdc-space-4-5) var(--wdc-space-4-5);
  cursor: pointer;
  transition: all 0.15s;
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2-5);
  min-height: 104px;
}
.metric-tile:hover {
  transform: translateY(-2px);
  box-shadow: var(--wdc-shadow-card);
  border-color: var(--wdc-accent-glow);
}
.metric-tile.tile-static {
  cursor: default;
}
.metric-tile.tile-static:hover {
  transform: none;
  box-shadow: none;
  border-color: var(--wdc-border);
}

.metric-tile:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}
.tile-header {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
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
