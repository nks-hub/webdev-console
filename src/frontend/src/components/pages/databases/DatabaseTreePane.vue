<template>
  <div class="db-tree">
    <div class="tree-header">
      <span class="tree-title">{{ t('databases.tree.title') }}</span>
      <el-button
        size="small"
        text
        :loading="loading"
        :aria-label="t('common.refresh')"
        :title="t('common.refresh')"
        @click="$emit('refresh')"
      >
        <el-icon><Refresh /></el-icon>
      </el-button>
    </div>

    <div class="tree-search">
      <el-input
        v-model="filter"
        size="small"
        :placeholder="t('databases.tree.filterPlaceholder')"
        clearable
      >
        <template #prefix><el-icon><Search /></el-icon></template>
      </el-input>
    </div>

    <div class="tree-body">
      <div v-if="loading && databases.length === 0" class="tree-empty">
        <el-icon class="is-loading"><Loading /></el-icon>
        <span>{{ t('databases.tree.loading') }}</span>
      </div>

      <div v-else-if="filteredDatabases.length === 0" class="tree-empty">
        <span>{{ t('databases.tree.empty') }}</span>
      </div>

      <ul v-else class="tree-list">
        <li
          v-for="db in filteredDatabases"
          :key="db.name"
          class="tree-db"
          :class="{ expanded: expanded.has(db.name), selected: selectedDb === db.name && !selectedTable }"
        >
          <div class="tree-row" @click="toggleDb(db.name)">
            <el-icon class="caret"><CaretRight /></el-icon>
            <el-icon class="kind-icon"><Coin /></el-icon>
            <span class="db-name">{{ db.name }}</span>
            <span v-if="db.sizeBytes != null" class="db-meta">{{ formatBytes(db.sizeBytes) }}</span>
          </div>

          <ul v-if="expanded.has(db.name)" class="table-list">
            <li v-if="loadingTables.has(db.name)" class="tree-loading">
              <el-icon class="is-loading"><Loading /></el-icon>
              <span>{{ t('databases.tree.loadingTables') }}</span>
            </li>
            <li
              v-for="t in (tables[db.name] ?? [])"
              :key="t.name"
              class="tree-table"
              :class="{ selected: selectedDb === db.name && selectedTable === t.name }"
              @click.stop="$emit('select', { db: db.name, table: t.name })"
            >
              <el-icon class="kind-icon"><Grid /></el-icon>
              <span class="table-name">{{ t.name }}</span>
              <span v-if="t.rowsApprox != null" class="table-rows">{{ t.rowsApprox.toLocaleString() }}</span>
            </li>
            <li v-if="!loadingTables.has(db.name) && (tables[db.name]?.length ?? 0) === 0" class="tree-loading muted">
              {{ t('databases.tree.noTables') }}
            </li>
          </ul>
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Refresh, Search, Loading, CaretRight, Coin, Grid } from '@element-plus/icons-vue'
import type { DatabaseSummary, TableSummary } from './types'

const props = defineProps<{
  databases: DatabaseSummary[]
  tables: Record<string, TableSummary[]>
  loadingTables: Set<string>
  selectedDb: string
  selectedTable: string
  loading: boolean
}>()

const emit = defineEmits<{
  (e: 'refresh'): void
  (e: 'expand', db: string): void
  (e: 'select', payload: { db: string; table?: string }): void
}>()

const { t } = useI18n()

const filter = ref('')
const expanded = ref(new Set<string>())

const filteredDatabases = computed(() => {
  const f = filter.value.trim().toLowerCase()
  if (!f) return props.databases
  return props.databases.filter(d => d.name.toLowerCase().includes(f))
})

function toggleDb(name: string) {
  if (expanded.value.has(name)) {
    expanded.value.delete(name)
  } else {
    expanded.value.add(name)
    if (!props.tables[name]) emit('expand', name)
  }
  emit('select', { db: name })
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}
</script>

<style scoped>
/*
  Tree pane sits on --wdc-surface, one luminance step above the page
  bg. The right pane sits on --wdc-bg, so the vertical seam between
  them is always visible without needing a heavy shadow or bevel.
*/
.db-tree {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--wdc-surface);
}

.tree-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  flex-shrink: 0;
  background: linear-gradient(180deg, var(--wdc-accent-dim), var(--wdc-surface-2));
}

.tree-title {
  font-size: 0.74rem;
  font-weight: 800;
  text-transform: uppercase;
  letter-spacing: 0.14em;
  color: var(--wdc-accent);
}

.tree-search {
  padding: 10px 12px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.tree-body {
  flex: 1;
  overflow-y: auto;
  padding: 4px 0;
}

.tree-empty {
  padding: 24px 14px;
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--wdc-text-3);
  font-size: 0.78rem;
  justify-content: center;
}

.tree-list, .table-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.tree-db {
  user-select: none;
}

.tree-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 12px 7px 14px;
  cursor: pointer;
  transition: background 0.1s, border-color 0.1s;
  font-size: 0.84rem;
  border-left: 3px solid transparent;
}
.tree-row:hover {
  background: var(--wdc-elevated);
  color: var(--wdc-text);
}
/*
  Selected row gets the accent left bar + accent-dim fill so it pops
  without inverting fg/bg (which made adjacent rows look "missing").
*/
.tree-db.selected > .tree-row {
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  border-left-color: var(--wdc-accent);
  box-shadow: inset 0 0 0 1px var(--wdc-accent-glow), 0 2px 12px var(--wdc-accent-glow);
}
.tree-db.selected > .tree-row .db-name { color: var(--wdc-accent); font-weight: 700; }

.caret { transition: transform 0.15s; color: var(--wdc-text-3); font-size: 0.8rem; }
.tree-db.expanded .caret { transform: rotate(90deg); color: var(--wdc-text-2); }

.kind-icon { color: var(--wdc-text-3); font-size: 0.95rem; }
.db-name {
  font-family: 'JetBrains Mono', monospace;
  font-weight: 500;
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--wdc-text);
}
.db-meta {
  font-size: 0.68rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
  font-weight: 500;
}

/*
  Nested table list visually inset by sitting on --wdc-bg (one step
  darker than the parent surface), creating an obvious "drawer
  expanded" look.
*/
.table-list {
  background: var(--wdc-bg);
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
}

.tree-table {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 12px 5px 36px;
  cursor: pointer;
  font-size: 0.79rem;
  transition: background 0.1s, border-color 0.1s;
  border-left: 3px solid transparent;
}
.tree-table:hover {
  background: var(--wdc-surface);
  color: var(--wdc-text);
}
.tree-table.selected {
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  border-left-color: var(--wdc-accent);
}
.tree-table.selected .table-name { color: var(--wdc-accent); font-weight: 600; }

.table-name {
  font-family: 'JetBrains Mono', monospace;
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--wdc-text-2);
}
.table-rows {
  font-size: 0.68rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}

.tree-loading {
  padding: 7px 12px 7px 36px;
  font-size: 0.74rem;
  color: var(--wdc-text-3);
  display: flex;
  align-items: center;
  gap: 6px;
}
.tree-loading.muted { font-style: italic; }
</style>
