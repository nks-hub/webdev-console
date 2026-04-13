<template>
  <div class="databases-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('databases.title') }}</h1>
        <span class="db-count" v-if="databases.length > 0">{{ databases.length }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="loadDatabases" :loading="loading">{{ $t('common.refresh') }}</el-button>
        <el-button type="primary" size="small" @click="showCreateDialog = true">{{ $t('databases.create') }}</el-button>
      </div>
    </div>

    <div class="page-body">
      <!-- Connection status -->
      <el-alert
        v-if="error"
        type="error"
        :title="error"
        :closable="true"
        @close="error = ''"
        show-icon
        style="margin-bottom: 16px"
      />

      <!-- Loading state -->
      <div v-if="loading && databases.length === 0" style="padding: 24px">
        <el-skeleton :rows="3" animated />
      </div>

      <!-- Database list -->
      <div class="db-grid" v-else-if="databases.length > 0">
        <div
          v-for="db in databases"
          :key="db"
          class="db-card"
          :class="{ active: selectedDb === db }"
          @click="selectDatabase(db)"
        >
          <div class="db-card-header">
            <span class="db-card-name">{{ db }}</span>
            <el-button
              size="small"
              type="danger"
              text
              @click.stop="confirmDrop(db)"
              title="Drop database"
            >
              Drop
            </el-button>
          </div>
          <div class="db-card-meta" v-if="dbTables[db]">
            <span class="meta-item">{{ dbTables[db].length }} tables</span>
            <span class="meta-item" v-if="dbSizes[db]">{{ dbSizes[db] }}</span>
          </div>
        </div>
      </div>

      <el-empty v-else-if="!loading" description="No user databases. Create one to get started." :image-size="64" />

      <!-- Selected database detail -->
      <div v-if="selectedDb" class="db-detail">
        <div class="detail-header">
          <h2 class="detail-title">{{ selectedDb }}</h2>
          <div class="detail-actions">
            <el-button size="small" @click="loadTables(selectedDb)">Refresh Tables</el-button>
            <el-button size="small" type="success" @click="triggerImport">Import SQL</el-button>
            <el-button size="small" @click="exportDb">Export SQL</el-button>
            <input ref="importFileRef" type="file" accept=".sql,.gz" style="display:none" @change="handleImportFile" />
          </div>
        </div>

        <!-- Tables list -->
        <div class="tables-section" v-if="currentTables.length > 0">
          <div class="section-label">Tables</div>
          <el-table :data="currentTables" size="small" stripe>
            <el-table-column prop="name" label="Table" min-width="200">
              <template #default="{ row }">
                <span class="table-name">{{ row.name || row }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="rows" label="Rows" width="100" align="right">
              <template #default="{ row }">
                <span class="mono">{{ row.rows ?? '—' }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="size" label="Size" width="100" align="right">
              <template #default="{ row }">
                <span class="mono">{{ row.size ?? '—' }}</span>
              </template>
            </el-table-column>
          </el-table>
        </div>

        <!-- SQL Query -->
        <div class="query-section">
          <div class="section-label">SQL Query</div>
          <el-input
            v-model="sqlQuery"
            type="textarea"
            :rows="3"
            placeholder="SELECT * FROM users LIMIT 10;"
            class="query-input"
          />
          <div class="query-actions">
            <el-button type="primary" size="small" @click="executeQuery" :loading="queryRunning" :disabled="!sqlQuery.trim()">
              Execute
            </el-button>
            <span class="query-time" v-if="queryTime">{{ queryTime }}ms</span>
          </div>
          <div v-if="queryResult" class="query-result">
            <pre class="query-output">{{ queryResult }}</pre>
          </div>
        </div>
      </div>
    </div>

    <!-- Create dialog -->
    <el-dialog v-model="showCreateDialog" title="New Database" width="400px">
      <el-form label-position="top" size="small">
        <el-form-item label="Database Name" required>
          <el-input v-model="newDbName" placeholder="my_database" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreateDialog = false">Cancel</el-button>
        <el-button type="primary" :loading="creating" @click="createDatabase" :disabled="!newDbName.trim()">
          Create
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, reactive } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'

const databases = ref<string[]>([])
const loading = ref(false)
const error = ref('')
const selectedDb = ref('')
const dbTables = reactive<Record<string, any[]>>({})
const dbSizes = reactive<Record<string, string>>({})
const showCreateDialog = ref(false)
const newDbName = ref('')
const creating = ref(false)
const sqlQuery = ref('')
const queryResult = ref('')
const queryRunning = ref(false)
const queryTime = ref<number | null>(null)

const importFileRef = ref<HTMLInputElement>()
const currentTables = computed(() => dbTables[selectedDb.value] ?? [])

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = (window as any).daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

function authHeaders(): Record<string, string> {
  const urlToken = new URLSearchParams(window.location.search).get('token')
  const token = (window as any).daemonApi?.getToken?.() || urlToken || ''
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  return headers
}

// Thrown error message extractor for non-2xx fetch responses. Reads the
// daemon's response body (usually a plain text error or a { error: "..." }
// JSON object) so users see the real reason instead of "HTTP 500".
async function httpError(r: Response): Promise<Error> {
  const text = await r.text().catch(() => '')
  if (!text) return new Error(`HTTP ${r.status}`)
  try {
    const obj = JSON.parse(text)
    if (obj && typeof obj === 'object' && 'error' in obj) return new Error(String(obj.error))
    if (obj && typeof obj === 'object' && 'detail' in obj) return new Error(String(obj.detail))
  } catch { /* not JSON */ }
  return new Error(text.length > 300 ? text.slice(0, 300) + '…' : text)
}

async function loadDatabases() {
  loading.value = true
  error.value = ''
  try {
    const r = await fetch(`${daemonBase()}/api/databases`, { headers: authHeaders() })
    if (!r.ok) throw await httpError(r)
    const data = await r.json()
    databases.value = data.databases ?? []
  } catch (e: any) {
    error.value = `Failed to load databases: ${e?.message || e}`
  } finally {
    loading.value = false
  }
}

async function selectDatabase(db: string) {
  selectedDb.value = db
  queryResult.value = ''
  sqlQuery.value = ''
  await loadTables(db)
}

async function loadTables(db: string) {
  try {
    const r = await fetch(`${daemonBase()}/api/databases/${db}/tables`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      dbTables[db] = (data.tables ?? data ?? []).map((t: any) =>
        typeof t === 'string' ? { name: t } : t
      )
    }
  } catch { /* optional */ }

  try {
    const r = await fetch(`${daemonBase()}/api/databases/${db}/size`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      dbSizes[db] = data.size ?? data.totalSize ?? ''
    }
  } catch { /* optional */ }
}

async function createDatabase() {
  if (!newDbName.value.trim()) return
  creating.value = true
  try {
    const r = await fetch(`${daemonBase()}/api/databases`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ name: newDbName.value }),
    })
    if (!r.ok) throw await httpError(r)
    ElMessage.success(`Database ${newDbName.value} created`)
    newDbName.value = ''
    showCreateDialog.value = false
    await loadDatabases()
  } catch (e: any) {
    ElMessage.error(`Create failed: ${e?.message || e}`)
  } finally {
    creating.value = false
  }
}

async function confirmDrop(db: string) {
  try {
    await ElMessageBox.confirm(`Drop database "${db}"? This will delete ALL data.`, 'Warning', {
      type: 'warning',
      confirmButtonText: 'Drop',
      confirmButtonClass: 'el-button--danger',
    })
    const r = await fetch(`${daemonBase()}/api/databases/${db}`, {
      method: 'DELETE',
      headers: authHeaders(),
    })
    if (!r.ok) {
      // Only surface an error toast if this wasn't a user-cancel. The
      // outer try/catch swallows ElMessageBox.confirm rejection which
      // is normal, but a DELETE HTTP failure deserves a visible error.
      const err = await httpError(r)
      ElMessage.error(`Drop failed: ${err.message}`)
      return
    }
    ElMessage.success(`Database ${db} dropped`)
    if (selectedDb.value === db) selectedDb.value = ''
    await loadDatabases()
  } catch { /* cancelled */ }
}

async function executeQuery() {
  if (!selectedDb.value) { ElMessage.warning('Select a database first'); return }
  if (!sqlQuery.value.trim()) return
  queryRunning.value = true
  queryResult.value = ''
  queryTime.value = null
  const start = Date.now()
  try {
    const r = await fetch(`${daemonBase()}/api/databases/${selectedDb.value}/query`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ sql: sqlQuery.value }),
    })
    queryTime.value = Date.now() - start
    if (r.ok) {
      const data = await r.json()
      queryResult.value = JSON.stringify(data, null, 2)
    } else {
      const text = await r.text()
      queryResult.value = `Error: ${text}`
    }
  } catch (e: any) {
    queryTime.value = Date.now() - start
    queryResult.value = `Error: ${e?.message || e}`
  } finally {
    queryRunning.value = false
  }
}

function triggerImport() {
  importFileRef.value?.click()
}

async function handleImportFile(e: Event) {
  const file = (e.target as HTMLInputElement).files?.[0]
  if (!file || !selectedDb.value) return
  const formData = new FormData()
  formData.append('file', file)
  try {
    ElMessage.info(`Importing ${file.name} into ${selectedDb.value}...`)
    const r = await fetch(`${daemonBase()}/api/databases/${selectedDb.value}/import`, {
      method: 'POST',
      headers: { Authorization: authHeaders()['Authorization'] },
      body: formData,
    })
    if (!r.ok) throw await httpError(r)
    ElMessage.success(`Imported ${file.name} successfully`)
    await loadTables(selectedDb.value)
  } catch (e: any) {
    ElMessage.error(`Import failed: ${e?.message || e}`)
  }
  if (importFileRef.value) importFileRef.value.value = ''
}

async function exportDb() {
  if (!selectedDb.value) { ElMessage.warning('Select a database first'); return }
  try {
    const r = await fetch(`${daemonBase()}/api/databases/${selectedDb.value}/export`, {
      headers: authHeaders(),
    })
    if (!r.ok) throw await httpError(r)
    const blob = await r.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${selectedDb.value}.sql`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success(`Exported ${selectedDb.value}.sql`)
  } catch (e: any) {
    ElMessage.error(`Export failed: ${e?.message || e}`)
  }
}

onMounted(() => { void loadDatabases() })
</script>

<style scoped>
.databases-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 24px 24px 0;
  margin-bottom: 20px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.db-count {
  font-size: 0.72rem;
  font-weight: 600;
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  padding: 2px 8px;
  border-radius: 10px;
  font-family: 'JetBrains Mono', monospace;
}

.header-actions { display: flex; align-items: center; gap: 8px; }

.page-body { padding: 0 24px 24px; }

/* DB grid */
.db-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 10px;
  margin-bottom: 20px;
}

.db-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 12px 16px;
  cursor: pointer;
  transition: all 0.15s;
}

.db-card:hover { border-color: var(--wdc-border-strong); }
.db-card.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); }

.db-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.db-card-name {
  font-size: 0.88rem;
  font-weight: 600;
  color: var(--wdc-text);
  font-family: 'JetBrains Mono', monospace;
}

.db-card-meta {
  display: flex;
  gap: 12px;
  margin-top: 6px;
}

.meta-item {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}

/* Detail */
.db-detail {
  border-top: 1px solid var(--wdc-border);
  padding-top: 20px;
}

.detail-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
}

.detail-title {
  font-size: 1rem;
  font-weight: 600;
  color: var(--wdc-text);
  font-family: 'JetBrains Mono', monospace;
}

.section-label {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--wdc-text-3);
  margin-bottom: 8px;
}

.tables-section { margin-bottom: 20px; }

.table-name {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
}

.mono {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
  color: var(--wdc-text-2);
}

/* Query */
.query-section { margin-top: 16px; }

.query-input {
  margin-bottom: 8px;
}

.query-input :deep(textarea) {
  font-family: 'JetBrains Mono', monospace !important;
  font-size: 0.82rem;
}

.query-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.query-time {
  font-size: 0.75rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}

.query-result {
  margin-top: 8px;
}

.query-output {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
  line-height: 1.5;
  color: var(--wdc-text-2);
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  padding: 12px;
  max-height: 400px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
