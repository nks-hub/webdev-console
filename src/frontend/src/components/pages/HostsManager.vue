<template>
  <div class="hosts-manager">
    <!-- Header -->
    <div class="page-header">
      <div class="header-title-block">
        <h1 class="page-title">{{ $t('hosts.title') }}</h1>
        <p class="page-subtitle">{{ $t('hosts.subtitle') }}</p>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openHostsFile">{{ $t('hosts.openFile') }}</el-button>
        <el-button size="small" @click="doBackup" :loading="backing">{{ $t('hosts.backup') }}</el-button>
        <el-button size="small" @click="triggerRestore">{{ $t('hosts.restore') }}</el-button>
        <el-button size="small" :loading="reapplying" @click="reapply">{{ $t('hosts.reapplyFromSites') }}</el-button>
        <el-button size="small" type="primary" @click="showAddDialog = true">{{ $t('hosts.addEntry') }}</el-button>
        <input ref="restoreInput" type="file" accept=".bak,.txt" style="display:none" @change="onRestoreFile" />
      </div>
    </div>

    <!-- Staging bar -->
    <div v-if="pendingCount > 0" class="staging-bar">
      <span class="staging-label">{{ $t('hosts.pendingChanges', { count: pendingCount }) }}</span>
      <el-button size="small" type="danger" plain @click="discardChanges">{{ $t('hosts.discardChanges') }}</el-button>
      <el-button size="small" type="primary" :loading="applying" @click="confirmApply">
        {{ $t('hosts.applyChanges') }}
      </el-button>
    </div>

    <!-- Body -->
    <div class="page-body">
      <!-- Initial load -->
      <LoadingState v-if="loading && rows.length === 0" label="Parsing system hosts file…" />

      <!-- Search -->
      <div v-if="!loading || rows.length > 0" class="toolbar">
        <el-input
          v-model="search"
          :placeholder="$t('hosts.searchPlaceholder')"
          size="small"
          clearable
          style="max-width: 300px"
        />
        <span class="entry-count">{{ filteredRows.length }} entries</span>
      </div>

      <el-card v-if="!loading || rows.length > 0" shadow="never" class="table-card">
        <el-table
          v-if="filteredRows.length > 0"
          :data="filteredRows"
          size="small"
          style="width: 100%"
          :row-class-name="rowClass"
        >
          <!-- Enabled toggle -->
          <el-table-column :label="$t('hosts.tableEnabled')" width="62" align="center">
            <template #default="{ row }">
              <el-switch
                :model-value="row.enabled"
                size="small"
                @change="(v: boolean) => toggleEnabled(row, v)"
              />
            </template>
          </el-table-column>

          <!-- IP (inline edit) -->
          <el-table-column :label="$t('hosts.tableIp')" width="150">
            <template #default="{ row }">
              <el-input
                v-if="editingId === row._id && editField === 'ip'"
                v-model="editValue"
                size="small"
                @blur="commitEdit(row)"
                @keyup.enter="commitEdit(row)"
                @keyup.escape="cancelEdit"
                autofocus
              />
              <span v-else class="mono editable" @click="startEdit(row, 'ip')">{{ row.ip }}</span>
            </template>
          </el-table-column>

          <!-- Hostname (inline edit) -->
          <el-table-column :label="$t('hosts.tableHostname')" min-width="180">
            <template #default="{ row }">
              <el-input
                v-if="editingId === row._id && editField === 'hostname'"
                v-model="editValue"
                size="small"
                @blur="commitEdit(row)"
                @keyup.enter="commitEdit(row)"
                @keyup.escape="cancelEdit"
                autofocus
              />
              <span v-else class="editable" @click="startEdit(row, 'hostname')">
                {{ row.hostname }}
                <el-tooltip
                  v-if="row.source === 'wdc' && !siteExists(row.hostname)"
                  :content="$t('hosts.orphanWarning', { hostname: row.hostname })"
                  placement="top"
                >
                  <span class="orphan-icon">!</span>
                </el-tooltip>
              </span>
            </template>
          </el-table-column>

          <!-- Source tag -->
          <el-table-column :label="$t('hosts.tableSource')" width="100">
            <template #default="{ row }">
              <el-tag
                size="small"
                :type="sourceTagType(row.source)"
                effect="plain"
              >{{ $t(`hosts.source${capitalize(row.source)}`) }}</el-tag>
            </template>
          </el-table-column>

          <!-- Comment (inline edit) -->
          <el-table-column :label="$t('hosts.tableComment')" min-width="160">
            <template #default="{ row }">
              <el-input
                v-if="editingId === row._id && editField === 'comment'"
                v-model="editValue"
                size="small"
                @blur="commitEdit(row)"
                @keyup.enter="commitEdit(row)"
                @keyup.escape="cancelEdit"
                autofocus
              />
              <span
                v-else
                class="editable comment-text"
                @click="startEdit(row, 'comment')"
              >{{ row.comment ?? '' }}</span>
            </template>
          </el-table-column>

          <!-- Actions -->
          <el-table-column :label="$t('hosts.tableActions')" width="56" align="center">
            <template #default="{ row }">
              <el-popconfirm
                :title="$t('hosts.confirmRemove')"
                @confirm="removeRow(row)"
              >
                <template #reference>
                  <el-button size="small" type="danger" circle plain>
                    <span style="font-size:12px; line-height:1">✕</span>
                  </el-button>
                </template>
              </el-popconfirm>
            </template>
          </el-table-column>
        </el-table>

        <el-empty v-else :image-size="64" :description="$t('hosts.emptyTitle')" />
      </el-card>
    </div>

    <!-- Add entry dialog -->
    <el-dialog
      v-model="showAddDialog"
      :title="$t('hosts.addDialogTitle')"
      width="420px"
      :close-on-click-modal="false"
    >
      <el-form :model="addForm" label-width="120px" @submit.prevent="submitAdd">
        <el-form-item :label="$t('hosts.addDialogIp')" required>
          <el-input v-model="addForm.ip" placeholder="127.0.0.1" />
        </el-form-item>
        <el-form-item :label="$t('hosts.addDialogHostname')" required>
          <el-input v-model="addForm.hostname" placeholder="myapp.loc" />
        </el-form-item>
        <el-form-item :label="$t('hosts.addDialogComment')">
          <el-input v-model="addForm.comment" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showAddDialog = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" @click="submitAdd">{{ $t('hosts.addDialogSave') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import LoadingState from '../shared/LoadingState.vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { useSitesStore } from '../../stores/sites'
import {
  fetchHosts,
  applyHosts,
  backupHosts,
  restoreHosts,
  daemonBaseUrl,
  type HostEntry,
  type HostApplyEntry,
} from '../../api/daemon'

const { t } = useI18n()
const sitesStore = useSitesStore()

// ── State ──────────────────────────────────────────────────────────────

interface Row extends HostEntry {
  _id: string
  _dirty: boolean
  _removed: boolean
  _added: boolean
}

let _idCounter = 0
function makeId() { return `row-${++_idCounter}` }

const rows = ref<Row[]>([])
const loading = ref(false)
const applying = ref(false)
const reapplying = ref(false)
const backing = ref(false)
const search = ref('')
const showAddDialog = ref(false)
const restoreInput = ref<HTMLInputElement | null>(null)

const addForm = ref({ ip: '127.0.0.1', hostname: '', comment: '' })

// Inline edit
const editingId = ref<string | null>(null)
const editField = ref<'ip' | 'hostname' | 'comment'>('ip')
const editValue = ref('')

// ── Derived ────────────────────────────────────────────────────────────

const activeRows = computed(() => rows.value.filter(r => !r._removed))

const filteredRows = computed(() => {
  const q = search.value.trim().toLowerCase()
  if (!q) return activeRows.value
  return activeRows.value.filter(r =>
    r.hostname.toLowerCase().includes(q) || r.ip.toLowerCase().includes(q)
  )
})

const pendingCount = computed(() =>
  rows.value.filter(r => r._dirty || r._removed || r._added).length
)

function siteExists(hostname: string): boolean {
  return sitesStore.sites.some(
    s => s.domain === hostname || (s.aliases ?? []).includes(hostname)
  )
}

// ── Load ───────────────────────────────────────────────────────────────

async function load() {
  loading.value = true
  try {
    const entries = await fetchHosts()
    rows.value = entries.map(e => ({
      ...e,
      _id: makeId(),
      _dirty: false,
      _removed: false,
      _added: false,
    }))
  } catch (e: any) {
    ElMessage.error(e?.message ?? String(e))
  } finally {
    loading.value = false
  }
}

// ── Inline edit ────────────────────────────────────────────────────────

function startEdit(row: Row, field: 'ip' | 'hostname' | 'comment') {
  if (row.source === 'external') return
  editingId.value = row._id
  editField.value = field
  editValue.value = (row[field] as string | null | undefined) ?? ''
}

function commitEdit(row: Row) {
  if (editingId.value !== row._id) return
  const field = editField.value
  const val = editValue.value.trim()

  if (field === 'ip' && val && val !== row.ip) {
    row.ip = val
    row._dirty = true
  } else if (field === 'hostname' && val && val !== row.hostname) {
    row.hostname = val
    row._dirty = true
  } else if (field === 'comment' && val !== (row.comment ?? '')) {
    row.comment = val || null
    row._dirty = true
  }

  editingId.value = null
}

function cancelEdit() {
  editingId.value = null
}

function toggleEnabled(row: Row, v: boolean) {
  row.enabled = v
  row._dirty = true
}

function removeRow(row: Row) {
  row._removed = true
}

// ── Add entry ──────────────────────────────────────────────────────────

function submitAdd() {
  const ip = addForm.value.ip.trim()
  const hostname = addForm.value.hostname.trim()
  if (!ip || !hostname) {
    ElMessage.warning('IP and hostname are required')
    return
  }
  const newRow: Row = {
    enabled: true,
    ip,
    hostname,
    source: 'custom',
    comment: addForm.value.comment.trim() || null,
    lineNumber: 0,
    _id: makeId(),
    _dirty: false,
    _removed: false,
    _added: true,
  }
  rows.value.push(newRow)
  showAddDialog.value = false
  addForm.value = { ip: '127.0.0.1', hostname: '', comment: '' }
}

// ── Apply ──────────────────────────────────────────────────────────────

async function confirmApply() {
  try {
    await ElMessageBox.confirm(
      t('hosts.confirmApply', { count: pendingCount.value }),
      t('hosts.applyChanges'),
      { type: 'warning', confirmButtonText: t('hosts.applyChanges'), cancelButtonText: t('hosts.discardChanges') }
    )
  } catch {
    return
  }
  applying.value = true
  try {
    const payload: HostApplyEntry[] = activeRows.value.map(r => ({
      enabled: r.enabled,
      ip: r.ip,
      hostname: r.hostname,
      source: r.source,
      comment: r.comment ?? null,
    }))
    await applyHosts(payload)
    ElMessage.success(t('hosts.applySuccess'))
    await load()
  } catch (e: any) {
    const msg: string = e?.message ?? String(e)
    if (msg.includes('administrator') || msg.includes('403')) {
      ElMessage.error(t('hosts.adminRequired'))
    } else {
      ElMessage.error(t('hosts.applyError') + ': ' + msg)
    }
  } finally {
    applying.value = false
  }
}

function discardChanges() {
  void load()
}

// ── Re-apply from sites ────────────────────────────────────────────────

async function reapply() {
  reapplying.value = true
  try {
    const res = await fetch(
      `${daemonBaseUrl()}/api/sites/reapply-all`,
      { method: 'POST', headers: sitesStore.authHeaders() }
    )
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    ElMessage.success(t('hosts.reapplyFromSites'))
    await load()
  } catch (e: any) {
    ElMessage.error(e?.message ?? String(e))
  } finally {
    reapplying.value = false
  }
}

// ── Backup / Restore ───────────────────────────────────────────────────

async function doBackup() {
  backing.value = true
  try {
    const { path } = await backupHosts()
    ElMessage.success(`Backed up to ${path}`)
  } catch (e: any) {
    ElMessage.error(e?.message ?? String(e))
  } finally {
    backing.value = false
  }
}

function triggerRestore() {
  restoreInput.value?.click()
}

async function onRestoreFile(evt: Event) {
  const file = (evt.target as HTMLInputElement).files?.[0]
  if (!file) return
  try {
    await ElMessageBox.confirm(
      `Restore hosts file from "${file.name}"? This will overwrite the current file.`,
      t('hosts.restore'),
      { type: 'warning' }
    )
  } catch {
    return
  }
  try {
    const content = await file.text()
    await restoreHosts({ content })
    ElMessage.success('Hosts file restored')
    await load()
  } catch (e: any) {
    const msg: string = e?.message ?? String(e)
    if (msg.includes('administrator') || msg.includes('403')) {
      ElMessage.error(t('hosts.adminRequired'))
    } else {
      ElMessage.error(msg)
    }
  } finally {
    if (restoreInput.value) restoreInput.value.value = ''
  }
}

// ── Helpers ────────────────────────────────────────────────────────────

function openHostsFile() {
  const hostsPath = 'C:\\Windows\\System32\\drivers\\etc\\hosts'
  window.open(`vscode://file/${hostsPath}`, '_self')
}

function sourceTagType(source: string): '' | 'success' | 'info' | 'warning' | 'danger' {
  if (source === 'wdc') return 'success'
  if (source === 'custom') return 'warning'
  return 'info'
}

function capitalize(s: string) {
  return s ? s[0].toUpperCase() + s.slice(1) : s
}

function rowClass({ row }: { row: Row }) {
  if (row._added) return 'row-added'
  if (row._dirty) return 'row-dirty'
  if (!row.enabled) return 'row-disabled'
  return ''
}

// ── Mount ──────────────────────────────────────────────────────────────

onMounted(async () => {
  if (!sitesStore.sites.length) await sitesStore.load()
  await load()
})
</script>

<style scoped>
.hosts-manager {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.page-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  padding: 20px 24px 16px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
  gap: 12px;
  flex-wrap: wrap;
}

.header-title-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.page-title {
  font-size: 1.35rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0;
}

.page-subtitle {
  font-size: 0.85rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.header-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}

.staging-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 24px;
  background: var(--el-color-warning-light-9, #fdf6ec);
  border-bottom: 1px solid var(--el-color-warning-light-5, #f5dab1);
  flex-shrink: 0;
}

.staging-label {
  flex: 1;
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--el-color-warning-dark-2, #b88230);
}

.page-body {
  flex: 1;
  overflow-y: auto;
  padding: 20px 24px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
}

.entry-count {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}

.table-card {
  flex: 1;
}

.mono {
  font-family: monospace;
  font-size: 0.88rem;
}

.editable {
  cursor: text;
  display: inline-block;
  min-width: 40px;
  padding: 1px 4px;
  border-radius: 3px;
  transition: background 0.15s;
}

.editable:hover {
  background: var(--el-fill-color-light, #f5f7fa);
}

.comment-text {
  color: var(--wdc-text-3);
  font-size: 0.85rem;
  font-style: italic;
}

.orphan-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: var(--el-color-warning);
  color: #fff;
  font-size: 10px;
  font-weight: 700;
  margin-left: 4px;
  cursor: help;
  vertical-align: middle;
}

:deep(.row-dirty td) {
  background: var(--el-color-primary-light-9, #ecf5ff) !important;
}

:deep(.row-added td) {
  background: var(--el-color-success-light-9, #f0f9eb) !important;
}

:deep(.row-disabled td) {
  opacity: 0.5;
}
</style>
