<template>
  <div class="backups-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">
          {{ $t('nav.backups') }}
          <span v-if="stats.count > 0" class="page-title-count">{{ stats.count }}</span>
        </h1>
        <p class="page-subtitle">{{ $t('backupsPage.subtitle') }}</p>
      </div>
    </div>

    <div class="page-body">
      <el-tabs v-model="activeTab" class="backup-tabs">

        <!-- ═══ OVERVIEW ════════════════════════════════════════════════════ -->
        <el-tab-pane :label="$t('backupsPage.tabs.overview')" name="overview">
          <div class="tab-content">
            <div class="overview-grid">
              <div class="stat-card">
                <div class="stat-label">{{ $t('backupsPage.overview.totalSize') }}</div>
                <div class="stat-value">{{ formatSize(stats.totalSize) }}</div>
              </div>
              <div class="stat-card">
                <div class="stat-label">{{ $t('backupsPage.overview.count') }}</div>
                <div class="stat-value">{{ stats.count }}</div>
              </div>
              <div class="stat-card">
                <div class="stat-label">{{ $t('backupsPage.overview.lastCreated') }}</div>
                <div class="stat-value">{{ stats.lastCreatedUtc ? formatDate(stats.lastCreatedUtc) : $t('backupsPage.overview.none') }}</div>
              </div>
              <div class="stat-card">
                <div class="stat-label">{{ $t('backupsPage.overview.next') }}</div>
                <div class="stat-value">{{ nextScheduledLabel }}</div>
              </div>
            </div>

            <div class="overview-actions">
              <el-button
                type="primary"
                :loading="backingUp"
                @click="runBackupNow"
              >
                {{ $t('backupsPage.overview.backupNow') }}
              </el-button>
              <el-button @click="openBackupFolder">
                {{ $t('backupsPage.overview.openFolder') }}
              </el-button>
              <el-button @click="loadAll" :loading="loading">
                {{ $t('backupsPage.overview.refresh') }}
              </el-button>
            </div>

            <el-alert
              v-if="backupResult"
              type="success"
              :title="$t('backupsPage.overview.createdToast', { files: backupResult.files, size: formatSize(backupResult.size) })"
              :description="backupResult.path"
              closable
              show-icon
              style="margin-top: 16px"
              @close="backupResult = null"
            />
            <el-alert
              v-if="errorMsg"
              type="error"
              :title="errorMsg"
              closable
              show-icon
              style="margin-top: 16px"
              @close="errorMsg = ''"
            />
          </div>
        </el-tab-pane>

        <!-- ═══ SNAPSHOTS ═══════════════════════════════════════════════════ -->
        <el-tab-pane :label="$t('backupsPage.tabs.snapshots')" name="snapshots">
          <div class="tab-content">
            <div class="snapshots-header">
              <span class="snapshots-count" v-if="backups.length">{{ $t('backupsPage.snapshots.count', { n: backups.length }) }}</span>
              <el-button size="small" @click="loadAll" :loading="loading">{{ $t('backupsPage.overview.refresh') }}</el-button>
            </div>

            <div v-if="loading && backups.length === 0" class="empty-state">
              <el-icon class="is-loading"><Loading /></el-icon>
              <span>{{ $t('backupsPage.snapshots.loading') }}</span>
            </div>

            <div v-else-if="backups.length === 0" class="empty-state">
              {{ $t('backupsPage.snapshots.empty') }}
            </div>

            <div v-else class="snapshots-list">
              <div
                v-for="b in backups"
                :key="b.path"
                class="snapshot-row"
              >
                <div class="snapshot-info">
                  <span class="snapshot-date">{{ formatDate(b.createdUtc) }}</span>
                  <span class="snapshot-size">{{ formatSize(b.size) }}</span>
                  <span class="snapshot-flags">{{ b.contentFlags }}</span>
                </div>
                <div class="snapshot-name mono">{{ fileName(b.path) }}</div>
                <div class="snapshot-actions">
                  <el-button
                    size="small"
                    @click="downloadBackup(b.path)"
                  >{{ $t('backupsPage.snapshots.download') }}</el-button>
                  <el-button
                    size="small"
                    type="warning"
                    @click="restoreBackup(b)"
                  >{{ $t('backupsPage.snapshots.restore') }}</el-button>
                  <el-button
                    size="small"
                    type="danger"
                    @click="deleteBackup(b)"
                  >{{ $t('backupsPage.snapshots.delete') }}</el-button>
                </div>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- ═══ SCHEDULE ════════════════════════════════════════════════════ -->
        <el-tab-pane :label="$t('backupsPage.tabs.schedule')" name="schedule">
          <div class="tab-content">
            <div class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">{{ $t('backupsPage.schedule.cardTitle') }}</span>
              </header>
              <div class="settings-card-body">
                <el-form label-position="left" label-width="220px" size="small" style="max-width: 500px">
                  <el-form-item :label="$t('backupsPage.schedule.intervalLabel')">
                    <el-input-number
                      v-model="scheduleHours"
                      :min="0"
                      :max="168"
                      style="width: 160px"
                    />
                    <span class="form-hint" v-if="scheduleHours === 0">{{ $t('backupsPage.schedule.hintOff') }}</span>
                    <span class="form-hint" v-else>{{ $t('backupsPage.schedule.hintEvery', { h: scheduleHours }) }}</span>
                  </el-form-item>
                  <el-form-item :label="$t('backupsPage.schedule.retainLabel')">
                    <el-input-number
                      v-model="retainCount"
                      :min="1"
                      :max="100"
                      style="width: 160px"
                    />
                  </el-form-item>
                </el-form>
                <el-button type="primary" size="small" @click="saveSchedule" :loading="saving">
                  {{ $t('backupsPage.schedule.save') }}
                </el-button>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- ═══ CONTENT ══════════════════════════════════════════════════════ -->
        <el-tab-pane :label="$t('backupsPage.tabs.content')" name="content">
          <div class="tab-content">
            <div class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">{{ $t('backupsPage.content.cardTitle') }}</span>
              </header>
              <div class="settings-card-body">
                <div class="content-flags">
                  <div v-for="flag in contentFlagDefs" :key="flag.key" class="flag-row">
                    <el-switch v-model="contentFlags[flag.key]" />
                    <div class="flag-copy">
                      <span class="flag-name">{{ $t(flag.labelKey) }}</span>
                      <span class="flag-desc">{{ $t(flag.descKey) }}</span>
                      <el-tag v-if="flag.default" size="small" type="success">{{ $t('backupsPage.content.tagDefault') }}</el-tag>
                      <el-tag v-else size="small" type="info">{{ $t('backupsPage.content.tagOptional') }}</el-tag>
                    </div>
                  </div>
                </div>
                <el-button type="primary" size="small" @click="saveContent" :loading="saving" style="margin-top: 16px">
                  {{ $t('backupsPage.content.save') }}
                </el-button>
              </div>
            </div>
          </div>
        </el-tab-pane>

      </el-tabs>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Loading } from '@element-plus/icons-vue'
import { daemonBaseUrl, daemonAuthHeaders } from '../../api/daemon'

const { t } = useI18n()

// ── types ─────────────────────────────────────────────────────────────────

interface BackupEntry {
  path: string
  size: number
  createdUtc: string
  contentFlags: string
}

interface BackupStats {
  count: number
  totalSize: number
  lastCreatedUtc: string | null
}

interface BackupResult {
  path: string
  files: number
  size: number
  contentFlags: string
}

// ── state ─────────────────────────────────────────────────────────────────

const activeTab = ref('overview')
const loading = ref(false)
const saving = ref(false)
const backingUp = ref(false)
const errorMsg = ref('')
const backupResult = ref<BackupResult | null>(null)

const backups = ref<BackupEntry[]>([])
const stats = ref<BackupStats>({ count: 0, totalSize: 0, lastCreatedUtc: null })

const scheduleHours = ref(24)
const retainCount = ref(10)

const contentFlagDefs = [
  { key: 'vhosts',        labelKey: 'backupsPage.content.flags.vhostsLabel',        descKey: 'backupsPage.content.flags.vhostsDesc',        default: true },
  { key: 'pluginConfigs', labelKey: 'backupsPage.content.flags.pluginConfigsLabel', descKey: 'backupsPage.content.flags.pluginConfigsDesc', default: true },
  { key: 'ssl',           labelKey: 'backupsPage.content.flags.sslLabel',           descKey: 'backupsPage.content.flags.sslDesc',           default: true },
  { key: 'databases',     labelKey: 'backupsPage.content.flags.databasesLabel',     descKey: 'backupsPage.content.flags.databasesDesc',     default: false },
  { key: 'docroots',      labelKey: 'backupsPage.content.flags.docrootsLabel',      descKey: 'backupsPage.content.flags.docrootsDesc',      default: false },
]

const contentFlags = ref<Record<string, boolean>>({
  vhosts: true,
  pluginConfigs: true,
  ssl: true,
  databases: false,
  docroots: false,
})

// ── computed ───────────────────────────────────────────────────────────────

const nextScheduledLabel = computed(() => {
  if (scheduleHours.value === 0) return t('backupsPage.overview.off')
  if (!stats.value.lastCreatedUtc) return t('backupsPage.overview.soon')
  const last = new Date(stats.value.lastCreatedUtc).getTime()
  const nextMs = last + scheduleHours.value * 3600 * 1000
  const diff = nextMs - Date.now()
  if (diff <= 0) return t('backupsPage.overview.soon')
  const hours = Math.floor(diff / 3600000)
  const mins = Math.floor((diff % 3600000) / 60000)
  return hours > 0 ? t('backupsPage.overview.inHours', { h: hours, m: mins }) : t('backupsPage.overview.inMinutes', { m: mins })
})

// ── lifecycle ──────────────────────────────────────────────────────────────

onMounted(() => {
  void loadAll()
  void loadSettings()
})

// ── data loading ───────────────────────────────────────────────────────────

async function loadAll() {
  loading.value = true
  errorMsg.value = ''
  try {
    const [listRes, statsRes] = await Promise.all([
      fetch(`${daemonBaseUrl()}/api/backup/list`, { headers: daemonAuthHeaders() }),
      fetch(`${daemonBaseUrl()}/api/backup/stats`, { headers: daemonAuthHeaders() }),
    ])
    if (listRes.ok) {
      const data = await listRes.json()
      backups.value = data.backups ?? []
    }
    if (statsRes.ok) {
      stats.value = await statsRes.json()
    }
  } catch (e) {
    errorMsg.value = t('backupsPage.toast.loadError', { err: e instanceof Error ? e.message : String(e) })
  } finally {
    loading.value = false
  }
}

async function loadSettings() {
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/settings`, { headers: daemonAuthHeaders() })
    if (!res.ok) return
    const s: Record<string, string> = await res.json()
    if (s['backup.scheduleHours'] !== undefined)
      scheduleHours.value = parseInt(s['backup.scheduleHours']) || 24
    if (s['backup.retainCount'] !== undefined)
      retainCount.value = parseInt(s['backup.retainCount']) || 10
    for (const flag of contentFlagDefs) {
      const key = `backup.content.${flag.key}`
      if (s[key] !== undefined)
        contentFlags.value[flag.key] = s[key] === 'true' || s[key] === '1'
    }
  } catch { /* non-critical */ }
}

// ── actions ────────────────────────────────────────────────────────────────

async function runBackupNow() {
  backingUp.value = true
  backupResult.value = null
  errorMsg.value = ''
  try {
    const flags = Object.fromEntries(
      Object.entries(contentFlags.value).filter(([, v]) => v)
    )
    const res = await fetch(`${daemonBaseUrl()}/api/backup`, {
      method: 'POST',
      headers: { ...daemonAuthHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ contentFlags: flags }),
    })
    if (!res.ok) {
      const err = await res.json().catch(() => ({ title: 'Záloha selhala' }))
      throw new Error(err.title ?? err.error ?? t('backupsPage.snapshots.backupFailed'))
    }
    backupResult.value = await res.json()
    await loadAll()
    ElMessage.success(t('backupsPage.toast.completed'))
  } catch (e) {
    errorMsg.value = e instanceof Error ? e.message : String(e)
    ElMessage.error(errorMsg.value)
  } finally {
    backingUp.value = false
  }
}

function openBackupFolder() {
  // electronAPI doesn't expose openPath (local file), only openExternal
  // (URLs). Use file:// URL which Electron's openExternal happily opens
  // via the OS default handler (Explorer on Win, Finder on macOS).
  const root = (window as unknown as { __WDC_ROOT__?: string }).__WDC_ROOT__
  const path = `${root ?? ''}/.wdc/backups`
  const url = `file://${path.replace(/\\/g, '/')}`
  window.electronAPI?.openExternal?.(url)
}

function downloadBackup(path: string) {
  const name = fileName(path)
  const url = `${daemonBaseUrl()}/api/backup/download?path=${encodeURIComponent(path)}`
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
}

async function restoreBackup(b: BackupEntry) {
  try {
    await ElMessageBox.confirm(
      t('backupsPage.snapshots.restoreConfirmMessage', { date: formatDate(b.createdUtc) }),
      t('backupsPage.snapshots.restoreConfirmTitle'),
      { type: 'warning', confirmButtonText: t('backupsPage.snapshots.restore'), cancelButtonText: t('common.cancel') }
    )
  } catch { return }

  try {
    const name = fileName(b.path)
    const res = await fetch(`${daemonBaseUrl()}/api/backup/${encodeURIComponent(name)}/restore`, {
      method: 'POST',
      headers: daemonAuthHeaders(),
    })
    if (!res.ok) {
      const err = await res.json().catch(() => ({ title: t('backupsPage.snapshots.restoreFailed') }))
      throw new Error(err.title ?? err.error ?? t('backupsPage.snapshots.restoreFailed'))
    }
    const data = await res.json()
    ElMessage.success(t('backupsPage.toast.restored', { files: data.restored, safety: fileName(data.safetyBackup) }))
    await loadAll()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function deleteBackup(b: BackupEntry) {
  try {
    await ElMessageBox.confirm(
      t('backupsPage.snapshots.deleteConfirmMessage', { name: fileName(b.path) }),
      t('backupsPage.snapshots.deleteConfirmTitle'),
      { type: 'warning', confirmButtonText: t('backupsPage.snapshots.delete'), cancelButtonText: t('common.cancel') }
    )
  } catch { return }

  try {
    const name = fileName(b.path)
    const res = await fetch(`${daemonBaseUrl()}/api/backup/${encodeURIComponent(name)}`, {
      method: 'DELETE',
      headers: daemonAuthHeaders(),
    })
    if (!res.ok) throw new Error(t('backupsPage.snapshots.deleteFailed'))
    ElMessage.success(t('backupsPage.toast.deleted'))
    await loadAll()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function saveSchedule() {
  saving.value = true
  try {
    await Promise.all([
      putSetting('backup', 'scheduleHours', String(scheduleHours.value)),
      putSetting('backup', 'retainCount', String(retainCount.value)),
    ])
    ElMessage.success(t('backupsPage.toast.scheduleSaved'))
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    saving.value = false
  }
}

async function saveContent() {
  saving.value = true
  try {
    await Promise.all(
      contentFlagDefs.map(f =>
        putSetting('backup', `content.${f.key}`, contentFlags.value[f.key] ? 'true' : 'false')
      )
    )
    ElMessage.success(t('backupsPage.toast.contentSaved'))
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    saving.value = false
  }
}

async function putSetting(category: string, key: string, value: string) {
  const res = await fetch(`${daemonBaseUrl()}/api/settings`, {
    method: 'PUT',
    headers: { ...daemonAuthHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ category, key, value }),
  })
  if (!res.ok) throw new Error(t('backupsPage.toast.settingsSaveFailed', { key: `${category}.${key}` }))
}

// ── utils ──────────────────────────────────────────────────────────────────

function fileName(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() ?? path
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString('cs-CZ', {
    day: '2-digit', month: '2-digit', year: 'numeric',
    hour: '2-digit', minute: '2-digit',
  })
}
</script>

<style scoped>
.backups-page { max-width: 1100px; margin: 0 auto; padding: 0; }

.page-header {
  margin-bottom: 24px;
  padding: 20px 24px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}
.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0 0 4px;
  letter-spacing: -0.02em;
  display: inline-flex;
  align-items: center;
  gap: 10px;
}
.page-title-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 28px;
  height: 22px;
  padding: 0 8px;
  border-radius: 999px;
  background: color-mix(in oklab, var(--wdc-accent) 18%, transparent);
  color: var(--wdc-accent);
  font-size: 0.78rem;
  font-weight: 700;
}
.page-subtitle {
  color: var(--wdc-text-3);
  font-size: 0.88rem;
  margin: 0;
}

.backup-tabs :deep(.el-tabs__header) {
  margin-bottom: 20px;
}

.tab-content {
  padding: 4px 0;
}

/* ── Overview ── */
.overview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 12px;
  margin-bottom: 20px;
}

.stat-card {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 16px 18px;
}
.stat-label {
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
  margin-bottom: 6px;
}
.stat-value {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.overview-actions {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

/* ── Snapshots ── */
.snapshots-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}
.snapshots-count {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}

.empty-state {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 32px 0;
  color: var(--wdc-text-3);
  font-size: 0.9rem;
}

.snapshots-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.snapshot-row {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 12px 14px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  flex-wrap: wrap;
}
.snapshot-info {
  display: flex;
  gap: 12px;
  align-items: center;
  flex: 1;
  flex-wrap: wrap;
}
.snapshot-date {
  font-size: 0.88rem;
  font-weight: 600;
  color: var(--wdc-text);
}
.snapshot-size {
  font-size: 0.8rem;
  color: var(--wdc-text-2);
}
.snapshot-flags {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}
.snapshot-name {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  flex: 1;
  min-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.snapshot-actions {
  display: flex;
  gap: 6px;
}

/* ── Settings cards ── */
.settings-card {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}
.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface-2);
}
.settings-card-title {
  font-size: 0.88rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.settings-card-body {
  padding: 16px;
}

.form-hint {
  margin-left: 10px;
  font-size: 0.8rem;
  color: var(--wdc-text-3);
}

/* ── Content flags ── */
.content-flags {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.flag-row {
  display: flex;
  align-items: flex-start;
  gap: 14px;
}
.flag-copy {
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.flag-name {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--wdc-text);
}
.flag-desc {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}

.mono { font-family: 'JetBrains Mono', monospace; }
</style>
