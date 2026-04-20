<template>
  <div class="error-logs-wrap">
    <!-- Toolbar -->
    <div class="logs-toolbar">
      <el-button
        size="small"
        :loading="loading"
        :icon="Refresh"
        :aria-label="t('common.refresh')"
        @click="fetchLogs"
      >
        {{ $t('logs.errors.refresh') }}
      </el-button>

      <div class="toolbar-group">
        <span class="toolbar-label">{{ $t('logs.errors.lines') }}</span>
        <el-select
          v-model="selectedLines"
          size="small"
          style="width: 90px"
          @change="fetchLogs"
        >
          <el-option v-for="n in LINE_OPTIONS" :key="n" :label="String(n)" :value="n" />
        </el-select>
      </div>

      <div class="toolbar-group">
        <span class="toolbar-label">{{ $t('logs.errors.since') }}</span>
        <el-date-picker
          v-model="sinceDate"
          type="datetime"
          size="small"
          style="width: 200px"
          clearable
          :placeholder="$t('logs.errors.since')"
          @change="fetchLogs"
        />
      </div>

      <div class="toolbar-group">
        <span class="toolbar-label">{{ $t('logs.errors.autoRefresh') }}</span>
        <el-switch v-model="autoRefresh" size="small" @change="onAutoRefreshToggle" />
      </div>
    </div>

    <!-- Loading skeleton -->
    <el-skeleton v-if="loading && entries.length === 0" :rows="8" animated style="padding: 16px 0" />

    <!-- Empty state -->
    <el-empty
      v-else-if="!loading && entries.length === 0"
      :description="$t('logs.errors.empty')"
      :image-size="64"
    />

    <!-- Log table -->
    <div v-else class="table-responsive">
    <el-table
      :data="entries"
      size="small"
      stripe
      class="logs-table"
      :row-class-name="rowClassName"
    >
      <el-table-column
        :label="$t('logs.errors.timestamp')"
        prop="timestamp"
        width="180"
        fixed
      >
        <template #default="{ row }">
          <span class="mono">{{ formatTimestamp(row.timestamp) }}</span>
        </template>
      </el-table-column>

      <el-table-column
        :label="$t('logs.errors.severity')"
        prop="severity"
        width="110"
      >
        <template #default="{ row }">
          <el-tag
            :type="severityType(row.severity)"
            :color="severityColor(row.severity)"
            :style="severityStyle(row.severity)"
            size="small"
            effect="dark"
            class="severity-tag"
          >
            {{ row.severity }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column
        :label="$t('logs.errors.source')"
        prop="source"
        width="160"
      >
        <template #default="{ row }">
          <el-tag type="info" size="small" effect="plain" class="mono">{{ row.source }}</el-tag>
        </template>
      </el-table-column>

      <el-table-column
        :label="$t('logs.errors.message')"
        prop="message"
        min-width="320"
      >
        <template #default="{ row }">
          <div class="message-cell">
            <span
              v-if="!expandedRows.has(row._idx)"
              class="message-truncated"
              role="button"
              tabindex="0"
              @click="toggleExpand(row._idx)"
              @keydown.enter="toggleExpand(row._idx)"
              @keydown.space.prevent="toggleExpand(row._idx)"
            >
              {{ row.message }}
            </span>
            <span
              v-else
              class="message-expanded"
              role="button"
              tabindex="0"
              @click="toggleExpand(row._idx)"
              @keydown.enter="toggleExpand(row._idx)"
              @keydown.space.prevent="toggleExpand(row._idx)"
            >
              {{ row.message }}
            </span>
          </div>
        </template>
      </el-table-column>

      <el-table-column
        :label="$t('logs.errors.pid')"
        prop="pid"
        width="80"
        align="right"
      >
        <template #default="{ row }">
          <span class="mono dim">{{ row.pid ?? '—' }}</span>
        </template>
      </el-table-column>

      <el-table-column
        :label="$t('logs.errors.client')"
        prop="client"
        width="140"
      >
        <template #default="{ row }">
          <span class="mono dim">{{ row.client ?? '—' }}</span>
        </template>
      </el-table-column>
    </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { getErrorLogs } from '../../api/daemon'
import type { SiteErrorLogEntry } from '../../api/types'
import { errorMessage } from '../../utils/errors'

const props = defineProps<{ domain: string }>()

const { t } = useI18n()

const LINE_OPTIONS = [50, 100, 200, 500, 1000] as const

const entries = ref<(SiteErrorLogEntry & { _idx: number })[]>([])
const loading = ref(false)
const selectedLines = ref<number>(100)
function defaultSinceDate(): Date {
  const d = new Date()
  d.setHours(d.getHours() - 24)
  return d
}
const sinceDate = ref<Date | null>(defaultSinceDate())
const autoRefresh = ref(false)
const expandedRows = ref(new Set<number>())

let refreshTimer: ReturnType<typeof setInterval> | null = null

const sinceIso = computed<string | undefined>(() => {
  if (!sinceDate.value) return undefined
  return sinceDate.value instanceof Date
    ? sinceDate.value.toISOString()
    : new Date(sinceDate.value).toISOString()
})

async function fetchLogs(): Promise<void> {
  loading.value = true
  try {
    const raw = await getErrorLogs(props.domain, {
      lines: selectedLines.value,
      since: sinceIso.value,
    })
    entries.value = raw.map((e, i) => ({ ...e, _idx: i }))
    expandedRows.value.clear()
  } catch (e) {
    ElMessage.error(`${t('logs.errors.refresh')}: ${errorMessage(e)}`)
  } finally {
    loading.value = false
  }
}

function onAutoRefreshToggle(on: boolean | string | number): void {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer)
    refreshTimer = null
  }
  if (on) {
    refreshTimer = setInterval(() => void fetchLogs(), 5000)
  }
}

function toggleExpand(idx: number): void {
  if (expandedRows.value.has(idx)) {
    expandedRows.value.delete(idx)
  } else {
    expandedRows.value.add(idx)
  }
}

function formatTimestamp(iso: string): string {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

const SEVERITY_MAP: Record<string, { color: string; type: '' | 'success' | 'warning' | 'danger' | 'info' }> = {
  emerg:   { color: '#7f0000', type: 'danger' },
  alert:   { color: '#7f0000', type: 'danger' },
  crit:    { color: '#7f0000', type: 'danger' },
  error:   { color: '#ef4444', type: 'danger' },
  err:     { color: '#ef4444', type: 'danger' },
  warn:    { color: '#f59e0b', type: 'warning' },
  warning: { color: '#f59e0b', type: 'warning' },
  notice:  { color: '#3b82f6', type: 'info' },
  info:    { color: '#6b7280', type: 'info' },
  debug:   { color: '#6b7280', type: 'info' },
}

function severityEntry(sev: string) {
  return SEVERITY_MAP[sev.toLowerCase()] ?? { color: '#6b7280', type: '' as const }
}

function severityType(sev: string): '' | 'success' | 'warning' | 'danger' | 'info' {
  return severityEntry(sev).type
}

function severityColor(sev: string): string {
  return severityEntry(sev).color
}

function severityStyle(sev: string): Record<string, string> {
  const entry = severityEntry(sev)
  if (entry.type === 'danger') {
    return { background: entry.color, borderColor: entry.color, color: '#fff' }
  }
  return {}
}

function rowClassName({ row }: { row: SiteErrorLogEntry }): string {
  const sev = row.severity.toLowerCase()
  if (['emerg', 'alert', 'crit'].includes(sev)) return 'row-crit'
  if (['error', 'err'].includes(sev)) return 'row-error'
  if (['warn', 'warning'].includes(sev)) return 'row-warn'
  return ''
}

onMounted(() => void fetchLogs())
onBeforeUnmount(() => {
  if (refreshTimer !== null) clearInterval(refreshTimer)
})
</script>

<style scoped>
.error-logs-wrap {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.logs-toolbar {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
}

.toolbar-group {
  display: flex;
  align-items: center;
  gap: 6px;
}

.toolbar-label {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  white-space: nowrap;
}

.table-responsive {
  overflow-x: auto;
  width: 100%;
}

.logs-table {
  width: 100%;
  font-size: 0.82rem;
}

.mono {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
}

.dim {
  color: var(--wdc-text-3);
}

.severity-tag {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.03em;
}

.message-cell {
  line-height: 1.5;
}

.message-truncated {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  cursor: pointer;
  color: var(--wdc-text);
  word-break: break-all;
}

.message-expanded {
  cursor: pointer;
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--wdc-text);
}

.logs-table :deep(.row-crit td) {
  background: rgba(127, 0, 0, 0.07) !important;
}

.logs-table :deep(.row-error td) {
  background: rgba(239, 68, 68, 0.05) !important;
}

.logs-table :deep(.row-warn td) {
  background: rgba(245, 158, 11, 0.05) !important;
}
</style>
