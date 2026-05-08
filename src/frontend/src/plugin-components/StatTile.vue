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
  gap: 14px;
  padding: 18px 20px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  box-shadow: var(--wdc-shadow-sm);
  transition: transform 0.15s, box-shadow 0.15s;
}
.stat-clickable { cursor: pointer; }
.stat-clickable:hover {
  transform: translateY(-2px);
  box-shadow: var(--wdc-shadow-card);
  border-color: var(--wdc-accent-glow);
}
.stat-icon {
  width: 40px;
  height: 40px;
  padding: 10px;
  border-radius: var(--wdc-radius);
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  font-size: 20px;
}
.stat-icon-running { color: var(--wdc-status-running); background: rgba(52, 211, 153, 0.14); }
.stat-content { min-width: 0; }
.stat-value { font-size: 1.6rem; font-weight: 800; color: var(--wdc-text); letter-spacing: -0.02em; }
.stat-label { font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.10em; color: var(--wdc-text-3); font-weight: 600; }
</style>
