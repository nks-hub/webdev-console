<template>
  <div class="db-tree">
    <div class="tree-header">
      <span class="tree-title">{{ t('databases.tree.title') }}</span>
      <el-button size="small" text :loading="loading" @click="$emit('refresh')">
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
.db-tree {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--wdc-surface);
  border-right: 1px solid var(--wdc-border);
}

.tree-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.tree-title {
  font-size: 0.7rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
}

.tree-search {
  padding: 8px 10px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.tree-body {
  flex: 1;
  overflow-y: auto;
  padding: 4px 0;
}

.tree-empty {
  padding: 20px 14px;
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
  gap: 6px;
  padding: 5px 12px;
  cursor: pointer;
  transition: background 0.1s;
  font-size: 0.82rem;
}
.tree-row:hover { background: var(--wdc-bg); }
.tree-db.selected > .tree-row { background: var(--wdc-accent-dim); color: var(--wdc-accent); }

.caret { transition: transform 0.15s; color: var(--wdc-text-3); font-size: 0.8rem; }
.tree-db.expanded .caret { transform: rotate(90deg); }

.kind-icon { color: var(--wdc-text-3); font-size: 0.95rem; }
.db-name { font-family: 'JetBrains Mono', monospace; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.db-meta {
  font-size: 0.66rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}

.table-list {
  background: var(--wdc-bg);
  border-top: 1px solid var(--wdc-border);
  border-bottom: 1px solid var(--wdc-border);
}

.tree-table {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 12px 4px 32px;
  cursor: pointer;
  font-size: 0.78rem;
  transition: background 0.1s;
}
.tree-table:hover { background: var(--wdc-surface); }
.tree-table.selected { background: var(--wdc-accent-dim); color: var(--wdc-accent); }

.table-name { font-family: 'JetBrains Mono', monospace; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.table-rows {
  font-size: 0.66rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}

.tree-loading {
  padding: 6px 12px 6px 32px;
  font-size: 0.74rem;
  color: var(--wdc-text-3);
  display: flex;
  align-items: center;
  gap: 6px;
}
.tree-loading.muted { font-style: italic; }
</style>
