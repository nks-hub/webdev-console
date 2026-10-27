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
        <el-button size="small" :loading="loading" @click="load">{{ $t('hosts.reload') }}</el-button>
        <el-button size="small" @click="doBackup" :loading="backing">{{ $t('hosts.backup') }}</el-button>
        <el-button size="small" @click="triggerRestore">{{ $t('hosts.restore') }}</el-button>
        <el-button size="small" :loading="reapplying" @click="reapply">{{ $t('hosts.reapplyFromSites') }}</el-button>
        <el-button size="small" type="primary" :icon="Plus" @click="showAddDialog = true">{{ $t('hosts.addEntry') }}</el-button>
        <input ref="restoreInput" type="file" accept=".bak,.txt" style="display:none" @change="onRestoreFile" />
      </div>
    </div>

    <!-- Staging bar -->
    <div v-if="pendingCount > 0" class="staging-bar">
      <span class="staging-label">{{ $t('hosts.pendingChanges', { count: pendingCount }, pendingCount) }}</span>
      <el-button size="small" type="danger" plain @click="discardChanges">{{ $t('hosts.discardChanges') }}</el-button>
      <el-button size="small" type="primary" :loading="applying" @click="confirmApply">
        {{ $t('hosts.applyChanges') }}
      </el-button>
    </div>

    <!-- Body -->
    <div class="page-body">
      <!-- Initial load -->
      <LoadingState v-if="loading && rows.length === 0" :label="$t('hosts.loadingHosts')" />

      <!-- Search -->
      <div v-if="!loading || rows.length > 0" class="toolbar">
        <el-input
          v-model="search"
          :placeholder="$t('hosts.searchPlaceholder')"
          size="small"
          clearable
          style="max-width: 300px"
        />
        <span class="entry-count">{{ $t('hosts.entriesCount', { n: filteredRows.length }, filteredRows.length) }}</span>
      </div>
      <p v-if="!loading || rows.length > 0" class="readonly-hint">{{ $t('hosts.readOnlyHint') }}</p>

      <el-card v-if="!loading || rows.length > 0" shadow="never" class="table-card">
        <el-table
          v-if="filteredRows.length > 0"
          :data="filteredRows"
          size="small"
          style="width: 100%"
          :row-class-name="rowClass"
        >
          <!-- Enabled toggle -->
          <el-table-column :label="$t('hosts.tableEnabled')" width="80" align="center">
            <template #default="{ row }">
              <el-tooltip :disabled="isEditable(row)" :content="$t('hosts.readOnlyRow')" placement="right">
                <span>
                  <el-switch
                    :model-value="row.enabled"
                    :disabled="!isEditable(row)"
                    :aria-label="$t('hosts.enableEntryAria', { hostname: row.hostname })"
                    size="small"
                    @change="(v: boolean) => toggleEnabled(row, v)"
                  />
                </span>
              </el-tooltip>
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
              <span v-else-if="isEditable(row)" v-activate class="mono editable" @click="startEdit(row, 'ip')">{{ row.ip }}</span>
              <span v-else class="mono cell">{{ row.ip }}</span>
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
              <span v-else-if="!isEditable(row)" class="cell">{{ row.hostname }}</span>
              <span v-else v-activate class="editable" @click="startEdit(row, 'hostname')">
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
              <span v-else-if="!isEditable(row)" class="cell comment-text">{{ row.comment ?? '' }}</span>
              <span
                v-else
                v-activate
                class="editable comment-text"
                @click="startEdit(row, 'comment')"
              >{{ row.comment ?? '' }}</span>
            </template>
          </el-table-column>

          <!-- Actions -->
          <el-table-column :label="$t('hosts.tableActions')" width="100" align="center">
            <template #default="{ row }">
              <el-popconfirm
                v-if="isEditable(row)"
                :title="$t('hosts.confirmRemove')"
                :confirm-button-text="$t('hosts.removeEntry')"
                :cancel-button-text="$t('common.cancel')"
                @confirm="removeRow(row)"
              >
                <template #reference>
                  <el-button
                    size="small"
                    type="danger"
                    circle
                    plain
                    :icon="Close"
                    :aria-label="$t('hosts.removeEntryAria', { hostname: row.hostname })"
                  />
                </template>
              </el-popconfirm>
              <el-tooltip v-else :content="$t('hosts.readOnlyRow')" placement="left">
                <el-icon class="readonly-icon" :aria-label="$t('hosts.readOnlyRow')" role="img" tabindex="0"><Lock /></el-icon>
              </el-tooltip>
            </template>
          </el-table-column>
        </el-table>

        <!-- Load-failure state — the fetch threw (daemon unreachable /
             error), so we have NO knowledge of the file's real contents.
             Must NOT be reported as corruption: the file is very likely
             intact and the only safe action is to retry, not re-apply. -->
        <el-alert
          v-else-if="loadError"
          :title="$t('hosts.loadFailedTitle')"
          type="warning"
          :closable="false"
          show-icon
          style="margin-top: var(--wdc-space-4)"
        >
          <div>{{ $t('hosts.loadFailedBody') }}</div>
          <div style="margin-top: var(--wdc-space-2); display: flex; gap: var(--wdc-space-2)">
            <el-button size="small" :loading="loading" @click="load">
              {{ $t('hosts.retry') }}
            </el-button>
          </div>
        </el-alert>

        <!-- Hosts file empty corruption banner — only after a SUCCESSFUL
             load that returned 0 entries. Windows always ships with at
             least the localhost entries, so 0 parsed entries practically
             always means the file got truncated by an external process
             (e.g. a botched manual edit).
             Incident 2026-05-07: prior session left the file 0-byte
             after a failed Set-Content/revert; UI showed the bland
             "no entries" placeholder and gave the operator no idea
             anything was wrong. -->
        <el-alert
          v-else-if="isPlatformWindows"
          :title="$t('hosts.emptyHostsCorruptionTitle')"
          type="error"
          :closable="false"
          show-icon
          style="margin-top: var(--wdc-space-4)"
        >
          <div>{{ $t('hosts.emptyHostsCorruptionBody') }}</div>
          <div style="margin-top: var(--wdc-space-2); display: flex; gap: var(--wdc-space-2)">
            <el-button size="small" :loading="reapplying" @click="reapply">
              {{ $t('hosts.reapplyFromSites') }}
            </el-button>
          </div>
        </el-alert>
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
      <el-form :model="addForm" label-position="top" @submit.prevent="submitAdd">
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
import { openHostsFile } from '../../utils/hostsFile'
import { ref, computed, onMounted } from 'vue'
import LoadingState from '../shared/LoadingState.vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Close, Lock, Plus } from '@element-plus/icons-vue'
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
import { errorMessage } from '../../utils/errors'

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
// Distinguishes "the fetch failed" (daemon unreachable / error) from "the
// file genuinely parsed to 0 entries". Without this, any load failure left
// rows empty and the template fired the scary Windows "corrupted" banner —
// a false alarm that pushed operators toward a destructive re-apply when the
// real hosts file was intact (the daemon was just unreachable).
const loadError = ref(false)
// Windows always ships hosts with default localhost entries — 0 entries
// after parse on Windows is a corruption signal. On macOS/Linux the
// default file is also non-empty but the empty case is plausibly a
// fresh install, so we keep the gentle placeholder there.
const isPlatformWindows = navigator.userAgent.toLowerCase().includes('windows')
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

// The daemon rewrites only the WDC block and appends new rows; lines outside
// the block are never changed, so the UI must not offer to change them.
function isEditable(row: Row): boolean {
  return row.source === 'wdc' || row._added
}

function siteExists(hostname: string): boolean {
  return sitesStore.sites.some(
    s => s.domain === hostname || (s.aliases ?? []).includes(hostname)
  )
}

// ── Load ───────────────────────────────────────────────────────────────

async function load() {
  loading.value = true
  loadError.value = false
  try {
    const entries = await fetchHosts()
    rows.value = entries.map(e => ({
      ...e,
      _id: makeId(),
      _dirty: false,
      _removed: false,
      _added: false,
    }))
  } catch (e) {
    // Mark the failure so the template shows a "could not load / retry"
    // state instead of misreading empty rows as a corrupted hosts file.
    loadError.value = true
    rows.value = []
    ElMessage.error(errorMessage(e))
  } finally {
    loading.value = false
  }
}

// ── Inline edit ────────────────────────────────────────────────────────

function startEdit(row: Row, field: 'ip' | 'hostname' | 'comment') {
  if (!isEditable(row)) return
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
  if (!isEditable(row)) return
  row.enabled = v
  row._dirty = true
}

function removeRow(row: Row) {
  if (!isEditable(row)) return
  // A row added in this session was never written; dropping it is enough.
  if (row._added) rows.value = rows.value.filter(r => r !== row)
  else row._removed = true
}

// ── Add entry ──────────────────────────────────────────────────────────

function submitAdd() {
  const ip = addForm.value.ip.trim()
  const hostname = addForm.value.hostname.trim()
  if (!ip || !hostname) {
    ElMessage.warning(t('hosts.ipHostnameRequired'))
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
      t('hosts.confirmApply', { count: pendingCount.value }, pendingCount.value),
      t('hosts.applyChanges'),
      { type: 'warning', confirmButtonText: t('hosts.applyChanges'), cancelButtonText: t('common.cancel') }
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
      lineNumber: r._added ? null : r.lineNumber,
    }))
    const res = await applyHosts(payload)
    // The daemon never rewrites lines outside the WDC block; anything it
    // could not write is reported instead of a success.
    if (res?.notApplied) {
      ElMessage.warning({ message: t('hosts.externalNotApplied', { count: res.notApplied }, res.notApplied), duration: 8000, showClose: true })
    } else {
      ElMessage.success(t('hosts.applySuccess'))
    }
    await load()
  } catch (e) {
    const msg: string = errorMessage(e)
    if (msg.includes('administrator') || msg.includes('403')) {
      // Surface the admin-required failure with an inline "Restart as
      // admin" CTA — clicking it raises a UAC prompt and re-spawns the
      // daemon elevated, after which the renderer reconnects to the new
      // port. Without this users were stuck on a toast they couldn't act
      // on (incident 2026-05-07: hosts file empty, daemon non-elevated,
      // no path forward in the UI).
      ElMessageBox.confirm(
        t('hosts.adminRequiredPrompt'),
        t('hosts.adminRequired'),
        {
          confirmButtonText: t('hosts.restartAsAdmin'),
          cancelButtonText: t('common.cancel'),
          type: 'warning',
        }
      ).then(async () => {
        const api = (window as unknown as {
          electronAPI?: { elevateDaemon?: () => Promise<{ ok: boolean; error?: string }> }
        }).electronAPI
        if (!api?.elevateDaemon) {
          ElMessage.error(t('hosts.elevateUnavailable'))
          return
        }
        const r = await api.elevateDaemon()
        if (r.ok) {
          ElMessage.success(t('hosts.elevateDispatched'))
        } else {
          ElMessage.error(t('hosts.elevateFailed') + ': ' + (r.error ?? 'unknown'))
        }
      }).catch(() => { /* user dismissed */ })
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
    ElMessage.success(t('hosts.reapplied'))
    await load()
  } catch (e) {
    ElMessage.error(errorMessage(e))
  } finally {
    reapplying.value = false
  }
}

// ── Backup / Restore ───────────────────────────────────────────────────

async function doBackup() {
  backing.value = true
  try {
    const { path } = await backupHosts()
    ElMessage.success(t('hosts.backedUpTo', { path }))
  } catch (e) {
    ElMessage.error(errorMessage(e))
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
      t('hosts.restoreConfirm', { file: file.name }),
      t('hosts.restore'),
      { type: 'warning', confirmButtonText: t('hosts.restoreConfirmBtn'), cancelButtonText: t('common.cancel') }
    )
  } catch {
    return
  }
  try {
    const content = await file.text()
    await restoreHosts({ content })
    ElMessage.success(t('hosts.restored'))
    await load()
  } catch (e) {
    const msg: string = errorMessage(e)
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
  padding: 0 0 var(--wdc-space-4);
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
  flex-shrink: 0;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
}

.header-title-block {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1);
}

.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0;
  letter-spacing: -0.02em;
}

.page-subtitle {
  font-size: 0.85rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.header-actions {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: center;
  flex-wrap: wrap;
}

.staging-bar {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2-5);
  padding: var(--wdc-space-2) 0;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.staging-label {
  flex: 1;
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--wdc-warning);
}

.readonly-hint {
  margin: 0;
  font-size: 0.8rem;
  color: var(--wdc-text-3);
}

.readonly-icon {
  color: var(--wdc-text-3);
  cursor: help;
}

.page-body {
  flex: 1;
  overflow-y: auto;
  padding: var(--wdc-space-4-5) 0;
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3);
}

.toolbar {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
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

.cell {
  display: inline-block;
  padding: var(--wdc-space-0-5) var(--wdc-space-1-5);
}

.editable {
  cursor: text;
  display: inline-block;
  min-width: 40px;
  padding: var(--wdc-space-0-5) var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  transition: background 0.15s, box-shadow 0.15s;
}

.editable:hover {
  background: var(--wdc-surface-2);
  box-shadow: inset 0 0 0 1px var(--wdc-border);
}

.comment-text {
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}

.orphan-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: var(--el-color-warning);
  color: var(--wdc-on-accent);
  font-size: 10px;
  font-weight: 700;
  margin-left: var(--wdc-space-1);
  cursor: help;
  vertical-align: middle;
}

:deep(.row-dirty td) {
  background: var(--wdc-accent-dim) !important;
}

:deep(.row-added td) {
  background: var(--el-color-success-light-9) !important;
}

:deep(.row-disabled td) {
  opacity: 0.5;
}
</style>
