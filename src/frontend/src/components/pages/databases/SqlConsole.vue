<template>
  <div class="sql-console">
    <div class="console-toolbar">
      <span class="toolbar-title">
        <el-icon><Operation /></el-icon>
        <span>{{ t('databases.console.title') }}</span>
        <el-tag size="small" type="info" effect="plain" class="db-tag">{{ database || t('databases.console.noDb') }}</el-tag>
      </span>
      <div class="toolbar-actions">
        <el-button size="small" type="primary" :loading="running" :disabled="!database || !sql.trim()" @click="run">
          <el-icon><VideoPlay /></el-icon>
          <span style="margin-left: 4px">{{ t('databases.console.execute') }}</span>
          <span class="kbd">Ctrl+↵</span>
        </el-button>
        <el-button size="small" @click="clearSql" :disabled="running">
          <el-icon><Delete /></el-icon>
        </el-button>
        <el-dropdown size="small" trigger="click" @command="loadHistory">
          <el-button size="small" :disabled="history.length === 0">
            <el-icon><Clock /></el-icon>
            <span style="margin-left: 4px">{{ t('databases.console.history', { count: history.length }) }}</span>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item v-for="(h, i) in history" :key="i" :command="i">
                <span class="mono history-item">{{ truncate(h, 80) }}</span>
              </el-dropdown-item>
              <el-dropdown-item v-if="history.length > 0" divided command="-1">
                <el-icon><Delete /></el-icon>
                <span style="margin-left: 4px">{{ t('databases.console.clearHistory') }}</span>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </div>

    <div class="console-editor">
      <MonacoEditor
        v-model="sql"
        language="sql"
        height="100%"
        @ready="onEditorReady"
      />
    </div>

    <div class="console-results">
      <el-alert v-if="error" type="error" :title="error" :closable="false" show-icon />

      <div v-else-if="results.length === 0 && !running" class="results-empty">
        <span>{{ t('databases.console.runHint') }}</span>
      </div>

      <el-tabs v-else v-model="activeResult" class="result-tabs" type="card">
        <el-tab-pane
          v-for="(r, i) in results"
          :key="i"
          :name="String(i)"
        >
          <template #label>
            <span class="result-label">
              <el-icon v-if="r.columns.length > 0"><Grid /></el-icon>
              <el-icon v-else><Check /></el-icon>
              <span>{{ resultLabel(r, i) }}</span>
            </span>
          </template>
          <div class="result-meta">
            <span class="meta-stmt mono">{{ r.statementText }}</span>
            <span class="meta-stat">{{ r.rows.length }} rows</span>
            <span class="meta-stat" v-if="r.rowsAffected > 0">{{ r.rowsAffected }} affected</span>
            <span class="meta-stat">{{ r.executionTimeMs }} ms</span>
          </div>
          <el-table
            v-if="r.columns.length > 0"
            :data="rowsForResult(r)"
            size="small"
            stripe
            border
            height="100%"
          >
            <el-table-column
              v-for="col in r.columns"
              :key="col.name"
              :prop="col.name"
              :label="col.name"
              min-width="120"
              show-overflow-tooltip
            >
              <template #header>
                <div class="col-header">
                  <span class="col-name">{{ col.name }}</span>
                  <span class="col-type">{{ col.type }}</span>
                </div>
              </template>
              <template #default="{ row }">
                <span v-if="row[col.name] === null" class="cell-null">NULL</span>
                <span v-else class="cell-value">{{ row[col.name] }}</span>
              </template>
            </el-table-column>
          </el-table>
          <div v-else class="result-affected">
            <el-icon><Check /></el-icon>
            <span>{{ t('databases.console.affected', { count: r.rowsAffected }) }}</span>
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Operation, VideoPlay, Clock, Delete, Grid, Check } from '@element-plus/icons-vue'
import MonacoEditor from '../../shared/MonacoEditor.vue'
import type { QueryResultSet } from './types'
import type { editor as MonacoEditorTypes } from 'monaco-editor'

const HISTORY_KEY = 'wdc.dbconsole.history'
const HISTORY_MAX = 30

const props = defineProps<{
  database: string
  results: QueryResultSet[]
  running: boolean
  error: string
  initialSql?: string
}>()

const emit = defineEmits<{
  (e: 'execute', sql: string): void
}>()

const { t } = useI18n()

const sql = ref(props.initialSql ?? '')
watch(() => props.initialSql, v => { if (v != null) sql.value = v })

const activeResult = ref('0')
watch(() => props.results, () => { activeResult.value = '0' })

const history = ref<string[]>(loadHistoryStore())

function loadHistoryStore(): string[] {
  try {
    const raw = localStorage.getItem(HISTORY_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter((s: unknown): s is string => typeof s === 'string') : []
  } catch { return [] }
}

function persistHistory() {
  try { localStorage.setItem(HISTORY_KEY, JSON.stringify(history.value)) } catch { /* quota */ }
}

function pushHistory(s: string) {
  const trimmed = s.trim()
  if (!trimmed) return
  history.value = [trimmed, ...history.value.filter(h => h !== trimmed)].slice(0, HISTORY_MAX)
  persistHistory()
}

function loadHistory(idx: number | string) {
  const i = Number(idx)
  if (i === -1) {
    history.value = []
    persistHistory()
    return
  }
  if (history.value[i]) sql.value = history.value[i]
}

function run() {
  const v = sql.value.trim()
  if (!v) return
  pushHistory(v)
  emit('execute', v)
}

function clearSql() { sql.value = '' }

function onEditorReady(ed: MonacoEditorTypes.IStandaloneCodeEditor) {
  ed.addAction({
    id: 'wdc-run-query',
    label: 'Run query',
    keybindings: [2048 | 3], // CtrlCmd + Enter
    run: () => { run() },
  })
}

function resultLabel(r: QueryResultSet, i: number): string {
  if (r.columns.length === 0) {
    return t('databases.console.tabAffected', { n: i + 1, count: r.rowsAffected })
  }
  return t('databases.console.tabRows', { n: i + 1, count: r.rows.length })
}

function rowsForResult(r: QueryResultSet) {
  return r.rows.map(arr => {
    const out: Record<string, unknown> = {}
    for (let i = 0; i < r.columns.length && i < arr.length; i++) {
      out[r.columns[i].name] = arr[i]
    }
    return out
  })
}

function truncate(s: string, n: number) {
  return s.length <= n ? s : s.slice(0, n) + '…'
}

const _unused = computed(() => props.database)
void _unused
</script>

<style scoped>
.sql-console {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--wdc-bg);
}

.console-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 20px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  flex-shrink: 0;
  gap: 12px;
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}

.toolbar-title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-weight: 700;
  font-size: 1.05rem;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}
.toolbar-title :deep(.el-icon) { color: var(--wdc-accent); }

.db-tag { font-family: 'JetBrains Mono', monospace; }

.toolbar-actions { display: flex; align-items: center; gap: 6px; }

.kbd {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.66rem;
  margin-left: 6px;
  color: var(--wdc-text-3);
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  padding: 1px 6px;
  box-shadow: 0 1px 0 var(--wdc-border-strong);
}

.console-editor {
  height: 35%;
  min-height: 180px;
  flex-shrink: 0;
  padding: 8px 14px 0;
}

.console-results {
  flex: 1;
  min-height: 0;
  padding: 8px 14px 14px;
  display: flex;
  flex-direction: column;
}

.results-empty {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
  border: 1px dashed var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  margin-top: 8px;
}

.result-tabs { flex: 1; display: flex; flex-direction: column; min-height: 0; }
.result-tabs :deep(.el-tabs__content) { flex: 1; min-height: 0; display: flex; flex-direction: column; }
.result-tabs :deep(.el-tab-pane) { flex: 1; display: flex; flex-direction: column; min-height: 0; }

.result-label { display: flex; align-items: center; gap: 4px; }

.result-meta {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 6px 0 8px;
  border-bottom: 1px solid var(--wdc-border);
  margin-bottom: 8px;
  flex-shrink: 0;
}

.meta-stmt {
  font-size: 0.74rem;
  color: var(--wdc-text-3);
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta-stat {
  font-size: 0.72rem;
  color: var(--wdc-text-2);
  font-family: 'JetBrains Mono', monospace;
}

.col-header { display: flex; align-items: center; gap: 4px; }
.col-name { font-weight: 600; font-family: 'JetBrains Mono', monospace; }
.col-type { font-size: 0.66rem; color: var(--wdc-text-3); font-family: 'JetBrains Mono', monospace; }

.cell-null { color: var(--wdc-text-3); font-style: italic; font-size: 0.78rem; }
.cell-value { font-family: 'JetBrains Mono', monospace; font-size: 0.78rem; }

.result-affected {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--el-color-success);
  font-size: 0.9rem;
}

.history-item {
  display: inline-block;
  max-width: 480px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mono { font-family: 'JetBrains Mono', monospace; }
</style>
