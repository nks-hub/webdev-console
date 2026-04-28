<template>
  <div v-if="rows.length > 0" class="top-tools-panel">
    <div class="panel-title muted">
      {{ t('mcpActivity.topTools.title') }}
    </div>
    <div class="tool-rows">
      <div
        v-for="r in rows"
        :key="r.toolName"
        class="tool-row"
        :class="`danger-${r.dangerLevel}`"
        role="button"
        tabindex="0"
        :aria-label="t('mcpActivity.topTools.clickHint', { tool: r.toolName }) + ' — ' + r.count + ' calls'"
        @click="onToolClick(r.toolName)"
        @keydown.enter.prevent="onToolClick(r.toolName)"
        @keydown.space.prevent="onToolClick(r.toolName)"
        :title="t('mcpActivity.topTools.clickHint', { tool: r.toolName })"
      >
        <code class="mono tool-name">{{ r.toolName }}</code>
        <div class="bar-track">
          <div class="bar-fill" :class="`fill-${r.dangerLevel}`" :style="{ width: barPct(r) + '%' }" />
        </div>
        <span class="count">{{ r.count }}</span>
        <span class="muted avg">{{ r.avgDurationMs }}ms</span>
        <span v-if="r.errors > 0" class="errors">{{ r.errors }}!</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  fetchMcpToolCallsByTool,
  subscribeEventsMap,
  type McpToolCallByToolRow,
} from '../../api/daemon'

const props = defineProps<{ withinHours?: number; limit?: number }>()
const emit = defineEmits<{ (e: 'select', toolName: string): void }>()
const { t } = useI18n()

const rows = ref<McpToolCallByToolRow[]>([])
const maxCount = computed(() => Math.max(1, ...rows.value.map(r => r.count)))

function barPct(r: McpToolCallByToolRow): number {
  return Math.round((r.count / maxCount.value) * 100)
}

function onToolClick(toolName: string): void {
  emit('select', toolName)
}

let unsubscribe: (() => void) | null = null
let pollTimer: ReturnType<typeof setInterval> | null = null

async function refresh(): Promise<void> {
  try {
    const data = await fetchMcpToolCallsByTool(props.withinHours ?? 24, props.limit ?? 10)
    rows.value = data.rows
  } catch (err) {
    console.warn('[McpTopToolsPanel] refresh failed:', err)
  }
}

let pendingRefresh: ReturnType<typeof setTimeout> | null = null
function throttledRefresh(): void {
  if (pendingRefresh) return
  pendingRefresh = setTimeout(() => {
    pendingRefresh = null
    void refresh()
  }, 1500)
}

onMounted(() => {
  void refresh()
  unsubscribe = subscribeEventsMap({
    'mcp:tool-call': () => { throttledRefresh() },
  })
  pollTimer = setInterval(() => { void refresh() }, 60_000)
})

onBeforeUnmount(() => {
  if (unsubscribe) unsubscribe()
  if (pollTimer) clearInterval(pollTimer)
})
</script>

<style scoped>
.top-tools-panel {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 10px 12px;
  background: var(--el-fill-color-light);
}
.panel-title {
  font-size: 11px;
  margin-bottom: 8px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
.tool-rows { display: flex; flex-direction: column; gap: 4px; }
.tool-row {
  display: grid;
  grid-template-columns: minmax(160px, 1fr) 2fr auto auto auto;
  gap: 10px;
  align-items: center;
  padding: 4px 6px;
  border-radius: 3px;
  cursor: pointer;
  font-size: 12px;
  transition: background 0.15s;
}
.tool-row:hover { background: var(--el-fill-color-darker); }
.tool-row:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}
.tool-name { font-weight: 600; font-size: 11px; }
.bar-track {
  height: 8px;
  background: var(--el-fill-color);
  border-radius: 4px;
  overflow: hidden;
}
.bar-fill {
  height: 100%;
  border-radius: 4px;
  transition: width 0.3s;
}
.fill-read { background: var(--el-color-info); opacity: 0.7; }
.fill-mutate { background: var(--el-color-warning); }
.fill-destructive { background: var(--el-color-danger); }
.count { font-weight: 600; min-width: 32px; text-align: right; }
.avg { font-size: 11px; min-width: 48px; text-align: right; }
.errors {
  background: var(--el-color-danger);
  color: white;
  padding: 1px 6px;
  border-radius: 10px;
  font-size: 10px;
  font-weight: 600;
}
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.muted { color: var(--el-text-color-secondary); }
</style>
