<template>
  <el-drawer
    v-model="open"
    :title="t('mcpActivity.sessionDetail.title')"
    direction="rtl"
    size="60%"
    :destroy-on-close="false"
  >
    <div v-if="sessionId" class="session-detail">
      <!-- Header summary -->
      <div class="detail-summary">
        <div class="summary-row">
          <span class="muted">{{ t('mcpActivity.sessionDetail.sessionId') }}</span>
          <code class="mono">{{ sessionId }}</code>
        </div>
        <div v-if="entries.length > 0" class="summary-row">
          <span class="muted">{{ t('mcpActivity.sessionDetail.callCount') }}</span>
          <span>{{ entries.length }}</span>
        </div>
        <div v-if="totalDurationMs > 0" class="summary-row">
          <span class="muted">{{ t('mcpActivity.sessionDetail.totalDuration') }}</span>
          <span>{{ totalDurationMs }} ms</span>
        </div>
        <div v-if="errorCount > 0" class="summary-row error">
          <span class="muted">{{ t('mcpActivity.sessionDetail.errors') }}</span>
          <span>{{ errorCount }}</span>
        </div>
      </div>

      <!-- Per-tool breakdown -->
      <div v-if="byToolBreakdown.length > 0" class="by-tool">
        <div class="muted section-label">{{ t('mcpActivity.sessionDetail.byTool') }}</div>
        <div v-for="b in byToolBreakdown" :key="b.tool" class="tool-line">
          <code class="mono">{{ b.tool }}</code>
          <span class="muted">×{{ b.count }}</span>
          <span class="muted">avg {{ Math.round(b.avgMs) }}ms</span>
        </div>
      </div>

      <!-- Actions -->
      <div class="detail-actions">
        <el-button size="small" @click="copyJson">
          {{ t('mcpActivity.sessionDetail.copyJson') }}
        </el-button>
      </div>

      <!-- Full call list (no read-collapse, ALL entries) -->
      <div class="call-list">
        <div class="muted section-label">{{ t('mcpActivity.sessionDetail.allCalls') }}</div>
        <div v-for="e in entries" :key="e.id" class="full-call-row" :class="`danger-${e.dangerLevel}`">
          <span class="dot" :class="`dot-${e.dangerLevel}`" />
          <code class="mono tool">{{ e.toolName }}</code>
          <span class="muted dur">{{ e.durationMs }}ms</span>
          <el-tag v-if="e.resultCode !== 'ok'" type="danger" size="small">{{ e.resultCode }}</el-tag>
          <span class="muted at">{{ formatTime(e.calledAt) }}</span>
          <code v-if="e.argsSummary && e.argsSummary !== '{}'" class="mono args">{{ shortArgs(e.argsSummary) }}</code>
        </div>
      </div>
    </div>
  </el-drawer>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { fetchMcpToolCalls, type McpToolCallEntry } from '../../api/daemon'

const props = defineProps<{
  modelValue: boolean
  sessionId: string | null
}>()
const emit = defineEmits<{ (e: 'update:modelValue', value: boolean): void }>()

const { t } = useI18n()

const open = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

const entries = ref<McpToolCallEntry[]>([])

const totalDurationMs = computed(() =>
  entries.value.reduce((s, e) => s + e.durationMs, 0),
)
const errorCount = computed(() =>
  entries.value.filter(e => e.resultCode !== 'ok').length,
)

interface ToolBreakdown { tool: string; count: number; avgMs: number }
const byToolBreakdown = computed<ToolBreakdown[]>(() => {
  const map = new Map<string, { count: number; sum: number }>()
  for (const e of entries.value) {
    const cur = map.get(e.toolName) ?? { count: 0, sum: 0 }
    cur.count++
    cur.sum += e.durationMs
    map.set(e.toolName, cur)
  }
  return Array.from(map.entries())
    .map(([tool, v]) => ({ tool, count: v.count, avgMs: v.sum / v.count }))
    .sort((a, b) => b.count - a.count)
})

watch(
  () => [props.modelValue, props.sessionId],
  async ([visible, sid]) => {
    if (visible && sid) {
      try {
        // Pull up to 1000 entries for this session — well above the
        // typical session size (a long Claude Code conversation).
        const data = await fetchMcpToolCalls(1000, 0, null, null, sid as string)
        // Reverse so chronological (oldest first) — easier to follow.
        entries.value = [...data.entries].reverse()
      } catch (err) {
        console.warn('[McpSessionDetailDrawer] load failed:', err)
        entries.value = []
      }
    } else if (!visible) {
      // Clear on close so reopening on a different session re-fetches.
      entries.value = []
    }
  },
  { immediate: true },
)

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString()
  } catch { return iso }
}

function shortArgs(s: string): string {
  return s.length > 80 ? s.slice(0, 77) + '…' : s
}

async function copyJson(): Promise<void> {
  try {
    const json = JSON.stringify({ sessionId: props.sessionId, entries: entries.value }, null, 2)
    await navigator.clipboard.writeText(json)
    ElMessage.success(t('mcpActivity.sessionDetail.copied'))
  } catch (err) {
    console.warn('[McpSessionDetailDrawer] copy failed:', err)
  }
}
</script>

<style scoped>
.session-detail { display: flex; flex-direction: column; gap: 16px; padding: 0 8px; }
.detail-summary {
  background: var(--el-fill-color-light);
  border-radius: 6px;
  padding: 10px 12px;
  display: flex; flex-direction: column; gap: 4px;
}
.summary-row {
  display: flex; gap: 12px; align-items: baseline;
  font-size: 13px;
}
.summary-row.error { color: var(--el-color-danger); }
.section-label {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin-bottom: 6px;
}
.by-tool { display: flex; flex-direction: column; gap: 2px; }
.tool-line {
  display: flex; gap: 10px;
  padding: 4px 8px;
  background: var(--el-fill-color);
  border-radius: 3px;
  font-size: 12px;
}
.detail-actions { display: flex; gap: 8px; }
.call-list { display: flex; flex-direction: column; gap: 2px; }
.full-call-row {
  display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
  padding: 6px 10px;
  font-size: 12px;
  border-radius: 3px;
  background: var(--el-fill-color);
}
.full-call-row.danger-destructive { background: var(--el-color-danger-light-9); }
.full-call-row.danger-mutate { border-left: 2px solid var(--el-color-warning); }
.dot { display: inline-block; width: 7px; height: 7px; border-radius: 50%; flex-shrink: 0; }
.dot-read { background: var(--el-color-info); }
.dot-mutate { background: var(--el-color-warning); }
.dot-destructive { background: var(--el-color-danger); }
.tool { font-weight: 600; }
.args { color: var(--el-text-color-secondary); flex: 1 1 200px; }
.dur, .at { font-size: 11px; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.muted { color: var(--el-text-color-secondary); }
</style>
