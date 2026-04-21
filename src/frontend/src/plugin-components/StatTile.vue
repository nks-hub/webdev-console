<template>
  <!-- F91.6: generic stat tile with optional route navigation + icon.
       Plugins use this for Dashboard contributions like "Node.js processes:
       3" without shipping their own Vue component. -->
  <div class="stat-card" :class="{ 'stat-clickable': route }" @click="onClick">
    <el-icon class="stat-icon" :class="{ 'stat-icon-running': highlight }"><component :is="iconComponent" /></el-icon>
    <div class="stat-content">
      <div class="stat-value mono">{{ value }}</div>
      <div class="stat-label">{{ label }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, markRaw } from 'vue'
import { useRouter } from 'vue-router'
import { Link, Lock, Download, Box, Monitor, Timer, ChromeFilled, Cpu, Grid, VideoPlay } from '@element-plus/icons-vue'

const props = defineProps<{
  label: string
  value: string | number
  icon?: string
  route?: string
  highlight?: boolean
}>()

const router = useRouter()
const ICONS: Record<string, unknown> = markRaw({ Link, Lock, Download, Box, Monitor, Timer, ChromeFilled, Cpu, Grid, VideoPlay })
const iconComponent = computed(() => (props.icon && ICONS[props.icon]) || Grid)

function onClick() {
  if (props.route) void router.push(props.route)
}
</script>

<style scoped>
.stat-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
}
.stat-clickable { cursor: pointer; transition: background 0.12s; }
.stat-clickable:hover { background: var(--wdc-hover); }
.stat-icon {
  width: 36px;
  height: 36px;
  padding: 8px;
  border-radius: var(--wdc-radius-sm);
  background: var(--wdc-surface);
  color: var(--wdc-text-2);
  font-size: 20px;
}
.stat-icon-running { color: var(--wdc-status-running); }
.stat-content { min-width: 0; }
.stat-value { font-size: 1.4rem; font-weight: 700; color: var(--wdc-text); }
.stat-label { font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.08em; color: var(--wdc-text-3); }
</style>
