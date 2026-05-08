<template>
  <div class="databases-page">
    <!-- Top action bar (database-level operations) -->
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('databases.title') }}</h1>
        <el-tag size="small" type="info" effect="plain" v-if="databases.length > 0">
          {{ databases.length }} {{ $t('databases.tree.dbCount') }}
        </el-tag>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="loadDatabases" :loading="loading" :icon="Refresh">{{ $t('common.refresh') }}</el-button>
        <el-button size="small" type="primary" @click="showCreateDialog = true">
          <el-icon><Plus /></el-icon>
          <span style="margin-left: 4px">{{ $t('databases.create') }}</span>
        </el-button>
      </div>
    </div>

    <!-- Diagnostic banner (auth/port issues) -->
    <el-alert
      v-if="error"
      type="error"
      :title="error"
      :description="errorHint"
      :closable="true"
      @close="error = ''; errorHint = ''"
      show-icon
      class="page-alert"
    >
      <template #default>
        <div class="alert-body">
          <el-button v-if="suggestedPort" type="primary" size="small" :loading="fixing" @click="useAltPort">
            {{ $t('databases.useAltPort', { port: suggestedPort }) }}
          </el-button>
          <el-button v-if="isAuthError" size="small" @click="goResetPassword">
            <el-icon><Key /></el-icon>
            <span style="margin-left: 4px">{{ $t('databases.resetRootPassword') }}</span>
          </el-button>
        </div>
      </template>
    </el-alert>

    <!-- 3-pane layout (HeidiSQL-style) -->
    <div class="page-body" v-if="!error || databases.length > 0">
      <DatabaseTreePane
        class="left-pane"
        :databases="databases"
        :tables="dbTables"
        :loading-tables="loadingTables"
        :selected-db="selectedDb"
        :selected-table="selectedTable"
        :loading="loading"
        @refresh="loadDatabases"
        @expand="loadTables"
        @select="onTreeSelect"
      />

      <div class="right-pane">
        <!-- Tab nav -->
        <div class="right-tabs">
          <div class="tab-strip">
            <button
              v-for="tab in availableTabs"
              :key="tab.id"
              class="tab-btn"
              :class="{ active: activeTab === tab.id }"
              :disabled="tab.requiresTable && !selectedTable"
              @click="activeTab = tab.id"
            >
              <el-icon><component :is="tab.icon" /></el-icon>
              <span>{{ $t(tab.labelKey) }}</span>
            </button>
          </div>
          <div class="tab-actions" v-if="selectedDb && activeTab === 'overview'">
            <input ref="importFileRef" type="file" accept=".sql,.gz" style="display:none" @change="handleImportFile" />
            <el-button size="small" type="success" :loading="importing" @click="triggerImport">
              <el-icon><Upload /></el-icon>
              <span style="margin-left: 4px">{{ $t('databases.importSql') }}</span>
            </el-button>
            <el-button size="small" :loading="exporting" @click="exportDb">
              <el-icon><Download /></el-icon>
              <span style="margin-left: 4px">{{ $t('databases.exportSql') }}</span>
            </el-button>
            <el-button size="small" type="danger" plain @click="confirmDrop(selectedDb)">
              <el-icon><Delete /></el-icon>
              <span style="margin-left: 4px">{{ $t('databases.drop') }}</span>
            </el-button>
          </div>
        </div>

        <!-- Tab content -->
        <div class="tab-content">
          <!-- No selection -->
          <div v-if="!selectedDb" class="empty-state">
            <el-empty :image-size="80">
              <template #description>
                <p class="empty-title">{{ $t('databases.selectDb') }}</p>
                <p class="empty-hint">{{ $t('databases.selectDbHint') }}</p>
              </template>
            </el-empty>
          </div>

          <!-- Overview -->
          <div v-else-if="activeTab === 'overview'" class="overview-pane">
            <div class="overview-header">
              <h2 class="overview-title mono">{{ selectedDb }}</h2>
              <div class="overview-meta">
                <el-tag size="small" effect="plain" type="info">
                  {{ $t('databases.tree.tableCount', { count: (dbTables[selectedDb]?.length ?? 0) }) }}
                </el-tag>
                <el-tag v-if="dbCharsets[selectedDb]" size="small" effect="plain">
                  {{ dbCharsets[selectedDb] }}
                </el-tag>
                <el-tag v-if="dbSizes[selectedDb]" size="small" effect="plain">
                  {{ dbSizes[selectedDb] }}
                </el-tag>
              </div>
            </div>

            <el-table
              v-if="(dbTables[selectedDb]?.length ?? 0) > 0"
              :data="dbTables[selectedDb]"
              size="small"
              stripe
              border
              v-loading="loadingTables.has(selectedDb)"
              @row-click="(row: TableSummary) => onTreeSelect({ db: selectedDb, table: row.name })"
              class="overview-table"
            >
              <el-table-column prop="name" :label="$t('databases.tree.tableName')" min-width="200">
                <template #default="{ row }">
                  <el-icon><Grid /></el-icon>
                  <span class="mono" style="margin-left: 6px">{{ row.name }}</span>
                  <el-tag v-if="row.kind === 'view'" size="small" type="info" class="ml-1">VIEW</el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="rowsApprox" :label="$t('databases.tree.rows')" width="120" align="right">
                <template #default="{ row }">
                  <span class="mono">{{ row.rowsApprox?.toLocaleString() ?? '—' }}</span>
                </template>
              </el-table-column>
              <el-table-column prop="dataBytes" :label="$t('databases.tree.size')" width="120" align="right">
                <template #default="{ row }">
                  <span class="mono">{{ formatBytes((row.dataBytes ?? 0) + (row.indexBytes ?? 0)) }}</span>
                </template>
              </el-table-column>
              <el-table-column prop="engine" :label="$t('databases.tree.engine')" width="100">
                <template #default="{ row }"><span class="mono">{{ row.engine || '—' }}</span></template>
              </el-table-column>
              <el-table-column prop="comment" :label="$t('databases.tree.comment')" min-width="160" show-overflow-tooltip>
                <template #default="{ row }"><span class="muted">{{ row.comment || '—' }}</span></template>
              </el-table-column>
            </el-table>
            <el-empty v-else-if="!loadingTables.has(selectedDb)" :image-size="48">
              <template #description>
                <p>{{ $t('databases.noTables') }}</p>
              </template>
            </el-empty>
          </div>

          <!-- Browse data -->
          <TableBrowser
            v-else-if="activeTab === 'browse' && selectedTable"
            :database="selectedDb"
            :table="selectedTable"
            :columns="browseColumns"
            :rows="browseRows"
            :total-rows="browseTotalRows"
            :page="browsePage"
            :page-size="browsePageSize"
            :execution-time-ms="browseExecutionTimeMs"
            :order-by="browseOrderBy"
            :order-dir="browseOrderDir"
            :filter="browseFilter"
            :loading="browseLoading"
            :error="browseError"
            @page-change="onBrowsePage"
            @page-size-change="onBrowsePageSize"
            @sort="onBrowseSort"
            @filter="onBrowseFilter"
            @reload="loadTableData"
          />

          <!-- Structure -->
          <TableStructure
            v-else-if="activeTab === 'structure' && selectedTable"
            :database="selectedDb"
            :table="selectedTable"
            :columns="structureColumns"
            :indexes="structureIndexes"
            :loading="structureLoading"
            @reload="loadStructure"
          />

          <!-- SQL Console -->
          <SqlConsole
            v-else-if="activeTab === 'console'"
            :database="selectedDb"
            :results="consoleResults"
            :running="consoleRunning"
            :error="consoleError"
            :initial-sql="consoleSql"
            @execute="executeQuery"
          />
        </div>
      </div>
    </div>

    <!-- Create dialog -->
    <el-dialog v-model="showCreateDialog" :title="$t('databases.newDatabase')" width="400px">
      <el-form label-position="top" size="small">
        <el-form-item :label="$t('databases.nameLabel')" required>
          <el-input v-model="newDbName" :placeholder="$t('databases.namePlaceholder')" @keyup.enter="createDatabase" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreateDialog = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="creating" @click="createDatabase" :disabled="!newDbName.trim()">
          {{ $t('common.create') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, Plus, Key, Upload, Download, Delete, Grid, DataLine, List, Operation } from '@element-plus/icons-vue'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import DatabaseTreePane from './databases/DatabaseTreePane.vue'
import TableBrowser from './databases/TableBrowser.vue'
import TableStructure from './databases/TableStructure.vue'
import SqlConsole from './databases/SqlConsole.vue'
import type {
  DatabaseSummary, TableSummary, ColumnInfo, IndexInfo,
  DataColumn, BrowseResult, QueryExecutionResult, QueryResultSet, DetailView,
} from './databases/types'

const { t } = useI18n()
const router = useRouter()

// ── Diagnostic state (auth/port banner)
const isAuthError = computed(() => {
  const e = (error.value + ' ' + errorHint.value).toLowerCase()
  return /access denied|error\s*1045|authentication|wrong password/.test(e)
})

function goResetPassword() {
  void router.push({ path: '/plugins/mysql', query: { tab: 'root-password' } })
}

// ── Database list state
const databases = ref<DatabaseSummary[]>([])
const loading = ref(false)
const error = ref('')
const errorHint = ref('')
const suggestedPort = ref<number | null>(null)
const fixing = ref(false)

// ── Selection state
const selectedDb = ref('')
const selectedTable = ref('')
const activeTab = ref<DetailView>('overview')

interface TabDescriptor { id: DetailView; labelKey: string; icon: unknown; requiresTable: boolean }

const availableTabs: TabDescriptor[] = [
  { id: 'overview', labelKey: 'databases.tabs.overview', icon: List, requiresTable: false },
  { id: 'browse', labelKey: 'databases.tabs.browse', icon: Grid, requiresTable: true },
  { id: 'structure', labelKey: 'databases.tabs.structure', icon: DataLine, requiresTable: true },
  { id: 'console', labelKey: 'databases.tabs.console', icon: Operation, requiresTable: false },
]

// ── Per-database tables (lazy-loaded into dbTables[dbName])
const dbTables = reactive<Record<string, TableSummary[]>>({})
const dbCharsets = reactive<Record<string, string>>({})
const dbSizes = reactive<Record<string, string>>({})
const loadingTables = reactive(new Set<string>())

// ── Create / drop / import / export
const showCreateDialog = ref(false)
const newDbName = ref('')
const creating = ref(false)
const importFileRef = ref<HTMLInputElement>()
const importing = ref(false)
const exporting = ref(false)

// ── Browse state
const browseColumns = ref<DataColumn[]>([])
const browseRows = ref<unknown[][]>([])
const browseTotalRows = ref<number | null>(null)
const browsePage = ref(1)
const browsePageSize = ref(50)
const browseExecutionTimeMs = ref<number | null>(null)
const browseOrderBy = ref<string | null>(null)
const browseOrderDir = ref<'asc' | 'desc' | null>(null)
const browseFilter = ref('')
const browseLoading = ref(false)
const browseError = ref('')

// ── Structure state
const structureColumns = ref<ColumnInfo[]>([])
const structureIndexes = ref<IndexInfo[]>([])
const structureLoading = ref(false)

// ── Console state
const consoleSql = ref('')
const consoleResults = ref<QueryResultSet[]>([])
const consoleRunning = ref(false)
const consoleError = ref('')

// ── Helpers
async function httpError(r: Response): Promise<Error> {
  const text = await r.text().catch(() => '')
  if (!text) return new Error(`HTTP ${r.status}`)
  try {
    const obj = JSON.parse(text) as { error?: string; detail?: string }
    if (obj?.error) return new Error(String(obj.error))
    if (obj?.detail) return new Error(String(obj.detail))
  } catch { /* not JSON */ }
  return new Error(text.length > 300 ? text.slice(0, 300) + '…' : text)
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

// ── Database list
async function loadDatabases() {
  loading.value = true
  error.value = ''
  errorHint.value = ''
  suggestedPort.value = null
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/v2`, { headers: authHeaders() })
    if (!r.ok) throw await httpError(r)
    const data = await r.json() as {
      error?: string; hint?: string; suggestedPort?: number;
      databases?: DatabaseSummary[];
    }
    if (data.error) {
      error.value = data.error
      errorHint.value = data.hint ?? ''
      suggestedPort.value = data.suggestedPort ?? null
      databases.value = []
      return
    }
    databases.value = data.databases ?? []
    for (const d of databases.value) {
      if (d.charset) dbCharsets[d.name] = d.charset
      if (d.sizeBytes != null) dbSizes[d.name] = formatBytes(d.sizeBytes)
    }
  } catch (e) {
    error.value = `${t('databases.loadFailed')}: ${errorMessage(e)}`
  } finally {
    loading.value = false
  }
}

async function useAltPort() {
  if (!suggestedPort.value) return
  fixing.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/use-alt-port`, {
      method: 'POST',
      headers: { ...authHeaders(), 'content-type': 'application/json' },
      body: JSON.stringify({ port: suggestedPort.value }),
    })
    if (!r.ok) throw await httpError(r)
    await new Promise(res => setTimeout(res, 1200))
    await loadDatabases()
  } catch (e) {
    error.value = `${t('databases.portSwitchFailed')}: ${errorMessage(e)}`
  } finally {
    fixing.value = false
  }
}

// ── Tables (per-db)
async function loadTables(db: string) {
  loadingTables.add(db)
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/v2/${db}/tables`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json() as { tables: TableSummary[] }
      dbTables[db] = data.tables ?? []
    } else {
      const err = await httpError(r)
      ElMessage.error(`${t('databases.loadTablesFailed')}: ${err.message}`)
    }
  } catch (e) {
    ElMessage.error(`${t('databases.loadTablesFailed')}: ${errorMessage(e)}`)
  } finally {
    loadingTables.delete(db)
  }
}

// ── Selection
function onTreeSelect({ db, table }: { db: string; table?: string }) {
  if (selectedDb.value !== db) {
    selectedDb.value = db
    if (!dbTables[db]) void loadTables(db)
  }
  selectedTable.value = table ?? ''
  if (!table) {
    activeTab.value = 'overview'
    return
  }
  // First click on a table → browse data; if user is already on structure or console
  // we keep that view to avoid an unwanted navigation jump.
  if (activeTab.value !== 'structure' && activeTab.value !== 'console') {
    activeTab.value = 'browse'
    void loadTableData()
  } else if (activeTab.value === 'structure') {
    void loadStructure()
  }
}

// ── Browse data
async function loadTableData() {
  if (!selectedDb.value || !selectedTable.value) return
  browseLoading.value = true
  browseError.value = ''
  try {
    const params = new URLSearchParams()
    params.set('page', String(browsePage.value))
    params.set('pageSize', String(browsePageSize.value))
    if (browseOrderBy.value) params.set('orderBy', browseOrderBy.value)
    if (browseOrderDir.value) params.set('orderDir', browseOrderDir.value)
    if (browseFilter.value) params.set('where', browseFilter.value)

    const r = await fetch(
      `${daemonBaseUrl()}/api/databases/v2/${selectedDb.value}/tables/${selectedTable.value}/data?${params.toString()}`,
      { headers: authHeaders() })
    if (!r.ok) throw await httpError(r)
    const data = await r.json() as BrowseResult & { error?: string }
    if (data.error) {
      browseError.value = data.error
      return
    }
    browseColumns.value = data.columns
    browseRows.value = data.rows
    browseTotalRows.value = data.totalRows
    browsePage.value = data.page
    browsePageSize.value = data.pageSize
    browseExecutionTimeMs.value = data.executionTimeMs
    browseOrderBy.value = data.appliedOrderBy ?? null
    browseOrderDir.value = (data.appliedOrderDir as 'asc' | 'desc' | null) ?? null
  } catch (e) {
    browseError.value = errorMessage(e)
  } finally {
    browseLoading.value = false
  }
}

function onBrowsePage(page: number) { browsePage.value = page; void loadTableData() }
function onBrowsePageSize(size: number) { browsePageSize.value = size; browsePage.value = 1; void loadTableData() }
function onBrowseSort(payload: { orderBy: string | null; orderDir: 'asc' | 'desc' | null }) {
  browseOrderBy.value = payload.orderBy
  browseOrderDir.value = payload.orderDir
  browsePage.value = 1
  void loadTableData()
}
function onBrowseFilter(value: string) {
  browseFilter.value = value
  browsePage.value = 1
  void loadTableData()
}

// ── Structure
async function loadStructure() {
  if (!selectedDb.value || !selectedTable.value) return
  structureLoading.value = true
  try {
    const [colsRes, idxRes] = await Promise.all([
      fetch(`${daemonBaseUrl()}/api/databases/v2/${selectedDb.value}/tables/${selectedTable.value}/columns`, { headers: authHeaders() }),
      fetch(`${daemonBaseUrl()}/api/databases/v2/${selectedDb.value}/tables/${selectedTable.value}/indexes`, { headers: authHeaders() }),
    ])
    if (colsRes.ok) {
      const data = await colsRes.json() as { columns: ColumnInfo[] }
      structureColumns.value = data.columns
    }
    if (idxRes.ok) {
      const data = await idxRes.json() as { indexes: IndexInfo[] }
      structureIndexes.value = data.indexes
    }
  } catch (e) {
    ElMessage.error(`${t('databases.loadStructureFailed')}: ${errorMessage(e)}`)
  } finally {
    structureLoading.value = false
  }
}

// ── SQL console
async function executeQuery(sql: string) {
  if (!selectedDb.value) {
    ElMessage.warning(t('databases.selectFirst'))
    return
  }
  consoleRunning.value = true
  consoleError.value = ''
  consoleResults.value = []
  consoleSql.value = sql
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/v2/${selectedDb.value}/query`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ sql }),
    })
    if (!r.ok) {
      const err = await httpError(r)
      consoleError.value = err.message
      return
    }
    const data = await r.json() as QueryExecutionResult & { error?: string }
    if (data.error) {
      consoleError.value = data.error
      return
    }
    consoleResults.value = data.results ?? []
  } catch (e) {
    consoleError.value = errorMessage(e)
  } finally {
    consoleRunning.value = false
  }
}

// Watch tab switches that need lazy fetches
import { watch } from 'vue'
watch(activeTab, async (v) => {
  if (v === 'structure' && selectedTable.value && structureColumns.value.length === 0) {
    await loadStructure()
  } else if (v === 'browse' && selectedTable.value && browseColumns.value.length === 0) {
    await loadTableData()
  }
})

// Reset browse/structure state when the table changes
watch(selectedTable, () => {
  browseColumns.value = []
  browseRows.value = []
  browseTotalRows.value = null
  browsePage.value = 1
  browseFilter.value = ''
  browseOrderBy.value = null
  browseOrderDir.value = null
  browseExecutionTimeMs.value = null
  browseError.value = ''
  structureColumns.value = []
  structureIndexes.value = []
})

// ── Create / drop / import / export
async function createDatabase() {
  if (!newDbName.value.trim()) return
  creating.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ name: newDbName.value }),
    })
    if (!r.ok) throw await httpError(r)
    ElMessage.success(t('databases.createdToast', { name: newDbName.value }))
    newDbName.value = ''
    showCreateDialog.value = false
    await loadDatabases()
  } catch (e) {
    ElMessage.error(`${t('databases.createFailed')}: ${errorMessage(e)}`)
  } finally {
    creating.value = false
  }
}

async function confirmDrop(db: string) {
  try {
    await ElMessageBox.confirm(
      t('databases.dropConfirm', { name: db }),
      t('common.warning'),
      {
        type: 'warning',
        confirmButtonText: t('databases.drop'),
        confirmButtonClass: 'el-button--danger',
      })
  } catch { return }
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${db}`, { method: 'DELETE', headers: authHeaders() })
    if (!r.ok) {
      const err = await httpError(r)
      ElMessage.error(`${t('databases.dropFailed')}: ${err.message}`)
      return
    }
    ElMessage.success(t('databases.droppedToast', { name: db }))
    if (selectedDb.value === db) {
      selectedDb.value = ''
      selectedTable.value = ''
      activeTab.value = 'overview'
    }
    await loadDatabases()
  } catch (e) {
    ElMessage.error(`${t('databases.dropFailed')}: ${errorMessage(e)}`)
  }
}

function triggerImport() { importFileRef.value?.click() }

async function handleImportFile(e: Event) {
  const file = (e.target as HTMLInputElement).files?.[0]
  if (!file || !selectedDb.value) return
  const formData = new FormData()
  formData.append('file', file)
  importing.value = true
  try {
    ElMessage.info(t('databases.importingToast', { file: file.name, db: selectedDb.value }))
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${selectedDb.value}/import`, {
      method: 'POST',
      headers: { Authorization: authHeaders()['Authorization'] },
      body: formData,
    })
    if (!r.ok) throw await httpError(r)
    ElMessage.success(t('databases.importedToast', { file: file.name }))
    await loadTables(selectedDb.value)
  } catch (err) {
    ElMessage.error(`${t('databases.importFailed')}: ${errorMessage(err)}`)
  } finally {
    importing.value = false
    if (importFileRef.value) importFileRef.value.value = ''
  }
}

async function exportDb() {
  if (!selectedDb.value) {
    ElMessage.warning(t('databases.selectFirst'))
    return
  }
  exporting.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/databases/${selectedDb.value}/export`, { headers: authHeaders() })
    if (!r.ok) throw await httpError(r)
    const blob = await r.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${selectedDb.value}.sql`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success(t('databases.exportedToast', { name: selectedDb.value }))
  } catch (e) {
    ElMessage.error(`${t('databases.exportFailed')}: ${errorMessage(e)}`)
  } finally {
    exporting.value = false
  }
}

onMounted(() => { void loadDatabases() })
</script>

<style scoped>
/*
  Databases page — colorful redesign. Hero header with big title,
  accent-tinted page-header strip, content area in card with
  generous padding.
*/
.databases-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: transparent;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px;
  flex-shrink: 0;
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}

.header-left { display: flex; align-items: center; gap: 16px; }
.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0;
  letter-spacing: -0.02em;
}
.header-actions { display: flex; align-items: center; gap: 8px; }

.page-alert { margin: 12px 20px 0; }
.alert-body { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin-top: 6px; }

/*
  3-pane layout: tree (surface) | right pane (bg). The two-step bg
  contrast (surface vs bg) plus a strong vertical separator makes
  the panes visually independent without needing shadows or a card
  outline. Border-bottom of the page-header anchors the whole grid.
*/
.page-body {
  flex: 1;
  display: grid;
  grid-template-columns: 280px 1fr;
  gap: 0;
  min-height: 0;
  background: var(--wdc-bg);
}

.left-pane {
  min-height: 0;
  overflow: hidden;
  border-right: 1px solid var(--wdc-border-strong);
}

.right-pane {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--wdc-bg);
}

/*
  Tab strip — flat surface bar. Active tab paints itself onto the
  page bg below it (so it visually merges with its content panel),
  while inactive tabs sit on the surface-2 chip strip. The 2px
  accent under-bar reinforces selection without competing with text.
*/
.right-tabs {
  display: flex;
  align-items: center;
  justify-content: space-between;
  background: linear-gradient(180deg, var(--wdc-accent-dim), var(--wdc-surface-2));
  border-bottom: 2px solid var(--wdc-accent-glow);
  flex-shrink: 0;
  padding: 0 16px 0 12px;
  height: 52px;
}

.tab-strip {
  display: flex;
  align-items: stretch;
  height: 100%;
}

.tab-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 20px;
  border: none;
  background: transparent;
  cursor: pointer;
  color: var(--wdc-text-2);
  font-size: 0.86rem;
  font-weight: 600;
  border-bottom: 3px solid transparent;
  transition: all 0.12s;
  font-family: inherit;
  position: relative;
  margin-bottom: -2px;
}
.tab-btn:hover:not(:disabled) {
  color: var(--wdc-accent);
}
.tab-btn.active {
  color: var(--wdc-accent);
  border-bottom-color: var(--wdc-accent);
  background: var(--wdc-accent-dim);
  font-weight: 700;
  box-shadow: inset 0 -1px 0 var(--wdc-accent), 0 -2px 12px var(--wdc-accent-glow);
}
.tab-btn:disabled { opacity: 0.4; cursor: not-allowed; }

.tab-actions { display: flex; gap: 6px; padding-right: 6px; }

.tab-content {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.empty-state, .overview-pane {
  flex: 1;
  padding: 24px;
  overflow: auto;
  background: var(--wdc-bg);
}

.empty-state { display: flex; align-items: center; justify-content: center; }

.empty-title { font-size: 1rem; color: var(--wdc-text); margin: 0 0 6px; font-weight: 600; }
.empty-hint { font-size: 0.85rem; color: var(--wdc-text-3); margin: 0; max-width: 480px; }

.overview-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
  padding: 14px 18px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
}
.overview-title {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0;
  letter-spacing: -0.005em;
}
.overview-meta { display: flex; gap: 6px; flex-wrap: wrap; }

.overview-table { cursor: pointer; }
.overview-table :deep(.el-table__row):hover { background: var(--wdc-accent-dim) !important; }

.mono { font-family: 'JetBrains Mono', monospace; }
.muted { color: var(--wdc-text-3); }
.ml-1 { margin-left: 4px; }
</style>
