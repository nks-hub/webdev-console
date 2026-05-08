<template>
  <div class="table-browser">
    <div class="browser-toolbar">
      <div class="toolbar-left">
        <span class="toolbar-title">
          <el-icon><Grid /></el-icon>
          <span>{{ database }}.{{ table }}</span>
        </span>
        <el-tag size="small" type="info" effect="plain" v-if="totalRows != null">
          {{ t('databases.browse.totalRows', { count: totalRows.toLocaleString() }) }}
        </el-tag>
      </div>
      <div class="toolbar-right">
        <el-button size="small" :loading="loading" @click="$emit('reload')">
          <el-icon><Refresh /></el-icon>
          <span style="margin-left: 4px">{{ t('common.refresh') }}</span>
        </el-button>
      </div>
    </div>

    <div class="browser-filter">
      <el-input
        v-model="localFilter"
        size="small"
        :placeholder="t('databases.browse.filterPlaceholder')"
        @keyup.enter="applyFilter"
        clearable
        @clear="clearFilter"
      >
        <template #prefix><el-icon><Filter /></el-icon></template>
        <template #append>
          <el-button size="small" :loading="loading" @click="applyFilter">
            {{ t('databases.browse.applyFilter') }}
          </el-button>
        </template>
      </el-input>
      <span class="filter-hint">{{ t('databases.browse.filterHint') }}</span>
    </div>

    <el-alert v-if="error" type="error" :closable="false" :title="error" show-icon />

    <!--
      Browser-grid uses position:relative + the el-table is absolutely
      pinned to it. This is the only way to keep height="100%" stable in
      a flex chain — without it, an empty result set lets the el-table
      empty placeholder grow unboundedly past the pager. The absolute
      anchoring also lets el-table's body-wrapper own its own
      horizontal/vertical scroll.
    -->
    <div class="browser-grid">
      <el-table
        v-if="columns.length > 0"
        :data="tableRows"
        size="small"
        stripe
        border
        height="100%"
        v-loading="loading"
        :empty-text="loading ? t('common.loading') : t('databases.browse.empty')"
        @sort-change="onSortChange"
        class="browse-grid-table"
      >
        <el-table-column
          v-for="col in columns"
          :key="col.name"
          :prop="col.name"
          :label="col.name"
          :sortable="'custom'"
          :sort-orders="['ascending', 'descending', null]"
          :width="columnWidth(col)"
          show-overflow-tooltip
        >
          <template #header>
            <div class="col-header">
              <span class="col-name" :class="{ pk: col.isPrimaryKey }">{{ col.name }}</span>
              <span class="col-type">{{ col.type }}</span>
              <el-icon v-if="col.isPrimaryKey" class="pk-icon" :title="t('databases.structure.pkBadge')"><Key /></el-icon>
            </div>
          </template>
          <template #default="{ row }">
            <span v-if="row[col.name] === null" class="cell-null">NULL</span>
            <span v-else-if="typeof row[col.name] === 'string' && row[col.name].startsWith('0x')" class="cell-blob">{{ formatBlob(row[col.name]) }}</span>
            <span v-else class="cell-value">{{ row[col.name] }}</span>
          </template>
        </el-table-column>
      </el-table>
      <div v-else-if="!loading && !error" class="empty-pane">
        <el-empty :image-size="48">
          <template #description>
            <p>{{ t('databases.browse.empty') }}</p>
          </template>
        </el-empty>
      </div>
    </div>

    <div class="browser-pager">
      <span class="pager-time" v-if="executionTimeMs != null">
        {{ t('databases.browse.queryTime', { ms: executionTimeMs }) }}
      </span>
      <el-pagination
        :current-page="page"
        :page-size="pageSize"
        :total="totalRows ?? 0"
        :page-sizes="[25, 50, 100, 200, 500]"
        layout="sizes, prev, pager, next, jumper"
        size="small"
        @current-change="$emit('page-change', $event)"
        @size-change="$emit('page-size-change', $event)"
        background
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Refresh, Filter, Grid, Key } from '@element-plus/icons-vue'
import type { DataColumn } from './types'

const props = defineProps<{
  database: string
  table: string
  columns: DataColumn[]
  rows: unknown[][]
  totalRows: number | null
  page: number
  pageSize: number
  executionTimeMs: number | null
  orderBy: string | null
  orderDir: 'asc' | 'desc' | null
  filter: string
  loading: boolean
  error: string
}>()

const emit = defineEmits<{
  (e: 'page-change', page: number): void
  (e: 'page-size-change', size: number): void
  (e: 'sort', payload: { orderBy: string | null; orderDir: 'asc' | 'desc' | null }): void
  (e: 'filter', value: string): void
  (e: 'reload'): void
}>()

const { t } = useI18n()

const localFilter = ref(props.filter)
watch(() => props.filter, v => { localFilter.value = v })

const tableRows = computed(() => {
  return props.rows.map(rowArr => {
    const out: Record<string, unknown> = {}
    for (let i = 0; i < props.columns.length && i < rowArr.length; i++) {
      out[props.columns[i].name] = rowArr[i]
    }
    return out
  })
})

function applyFilter() {
  emit('filter', localFilter.value.trim())
}

function clearFilter() {
  localFilter.value = ''
  emit('filter', '')
}

function onSortChange(payload: { prop: string | null; order: 'ascending' | 'descending' | null }) {
  emit('sort', {
    orderBy: payload.order ? payload.prop : null,
    orderDir: payload.order === 'descending' ? 'desc' : (payload.order === 'ascending' ? 'asc' : null),
  })
}

function formatBlob(hex: string): string {
  // Show 0x then up to 16 nibbles for a quick BLOB peek; full value lives in tooltip.
  if (hex.length <= 18) return hex
  return hex.slice(0, 18) + '…'
}

/*
  Pick a fixed pixel width per column so the table's total width can
  exceed the visible body-wrapper, which is what makes el-table emit a
  horizontal scrollbar instead of squeezing every column to ~70px and
  hiding everything behind ellipsis. The width is heuristic — wider for
  text/blob types, narrower for ints/booleans — and never below 120 so
  the column header (name + type tag) stays legible.
*/
function columnWidth(col: DataColumn): number {
  const t = (col.type || '').toLowerCase()
  if (t.includes('text') || t.includes('blob') || t.includes('json')) return 280
  if (t.includes('varchar') || t.includes('char')) return 220
  if (t.includes('datetime') || t.includes('timestamp')) return 180
  if (t.includes('date') || t.includes('time')) return 140
  if (t.includes('decimal') || t.includes('double') || t.includes('float')) return 140
  if (t.includes('int') || t.includes('bool') || t.includes('bit')) return 120
  return 160
}
</script>

<style scoped>
.table-browser {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--wdc-bg);
}

.browser-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 20px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  flex-shrink: 0;
  gap: 12px;
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}

.toolbar-left, .toolbar-right { display: flex; align-items: center; gap: 12px; }

.toolbar-title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-family: 'JetBrains Mono', monospace;
  font-weight: 700;
  font-size: 1.05rem;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}
.toolbar-title :deep(.el-icon) { color: var(--wdc-accent); }

.browser-filter {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 16px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
  background: var(--wdc-surface-2);
}

.filter-hint {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}

.browser-grid {
  flex: 1 1 0;
  min-height: 0;
  position: relative;
  overflow: hidden;
}

/*
  Pin the el-table to the relative parent. Without absolute positioning
  the body-wrapper grows past the visible viewport when the result set
  is empty (el-table's "no data" placeholder is auto-height in a fluid
  flex parent), and horizontal scroll is suppressed because the table
  silently widens its parent. inset:0 + width/height 100% confines the
  table so its own body-wrapper owns both scroll axes.
*/
.browse-grid-table {
  position: absolute !important;
  inset: 0;
  width: 100% !important;
}

/*
  Force horizontal scroll on the table body and ensure the empty state
  fills its row instead of expanding the table. Element Plus emits
  overflow-x:auto by default but it gets overridden by our scoped
  parent's overflow:hidden in some flex layouts.
*/
.browser-grid :deep(.el-table__body-wrapper) {
  overflow-x: auto !important;
  overflow-y: auto !important;
}
.browser-grid :deep(.el-table__empty-block) {
  min-height: 80px;
}

.empty-pane {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
}

.browser-pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 16px;
  border-top: 1px solid var(--wdc-border);
  flex-shrink: 0;
  background: var(--wdc-surface);
}

.pager-time {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}

.col-header {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: nowrap;
}
.col-name { font-weight: 600; font-family: 'JetBrains Mono', monospace; }
.col-name.pk { color: var(--wdc-accent); }
.col-type {
  font-size: 0.66rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}
.pk-icon { color: var(--wdc-accent); font-size: 0.85rem; }

.cell-null { color: var(--wdc-text-3); font-style: italic; font-size: 0.78rem; }
.cell-blob {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.74rem;
  color: var(--el-color-warning);
}
.cell-value {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
}
</style>
