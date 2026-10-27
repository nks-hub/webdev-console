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
      <SettingsShell
        v-model="activeTab"
        :sections="sections"
        :aria-label="$t('nav.backups')"
      >

        <!-- ═══ OVERVIEW ════════════════════════════════════════════════════ -->
        <section v-show="activeTab === 'overview'">
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
                :disabled="noContentSelected"
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
              style="margin-top: var(--wdc-space-4)"
              @close="backupResult = null"
            />
            <el-alert
              v-if="errorMsg"
              type="error"
              :title="errorMsg"
              closable
              show-icon
              style="margin-top: var(--wdc-space-4)"
              @close="errorMsg = ''"
            />
          </div>
        </section>

        <!-- ═══ SNAPSHOTS ═══════════════════════════════════════════════════ -->
        <section v-show="activeTab === 'snapshots'">
          <div class="tab-content">
            <div class="snapshots-header">
              <span class="snapshots-count" v-if="backups.length">{{ $t('backupsPage.snapshots.count', backups.length) }}</span>
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
                  <span class="snapshot-flags">{{ contentLabel(b.contentFlags) }}</span>
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
        </section>

        <!-- ═══ SCHEDULE ════════════════════════════════════════════════════ -->
        <section v-show="activeTab === 'schedule'">
          <div class="tab-content">
            <SettingsCard :title="$t('backupsPage.schedule.cardTitle')">
              <el-form label-position="left" label-width="290px" size="small">
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
            </SettingsCard>
          </div>
        </section>

        <!-- ═══ CONTENT ══════════════════════════════════════════════════════ -->
        <section v-show="activeTab === 'content'">
          <div class="tab-content">
            <SettingsCard :title="$t('backupsPage.content.cardTitle')">
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
              <div v-if="noContentSelected" class="form-hint content-empty-hint">{{ $t('backupsPage.content.noneSelected') }}</div>
              <el-button
                type="primary"
                size="small"
                :loading="saving"
                :disabled="noContentSelected"
                style="margin-top: var(--wdc-space-4)"
                @click="saveContent"
              >
                {{ $t('backupsPage.content.save') }}
              </el-button>
            </SettingsCard>
          </div>
        </section>

      </SettingsShell>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { errorMessage } from '../../utils/errors'
import { Loading } from '@element-plus/icons-vue'
import {
  daemonBaseUrl, daemonAuthHeaders, downloadBackup as downloadBackupFile,
  fetchSettings, saveSettings, restartDaemon, subscribeEventsMap,
} from '../../api/daemon'
import SettingsCard from '../settings/shared/SettingsCard.vue'
import SettingsShell, { type SettingsSection } from '../settings/SettingsShell.vue'

const { t, locale } = useI18n()

const sections = computed<SettingsSection[]>(() => [
  { name: 'overview', label: t('backupsPage.tabs.overview'), group: 'backups' },
  { name: 'snapshots', label: t('backupsPage.tabs.snapshots'), group: 'backups' },
  { name: 'schedule', label: t('backupsPage.tabs.schedule'), group: 'backups' },
  { name: 'content', label: t('backupsPage.tabs.content'), group: 'backups' },
])

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

const noContentSelected = computed(() => !Object.values(contentFlags.value).some(Boolean))

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

// Scheduled backups finish in the background; the daemon announces them
// over SSE so the list and the overview stay current without a reload.
let unsubscribeBackupEvents: (() => void) | null = null

onMounted(() => {
  void loadAll()
  void loadSettings()
  unsubscribeBackupEvents = subscribeEventsMap({
    'backup:completed': () => { void loadAll() },
  })
})

onBeforeUnmount(() => {
  unsubscribeBackupEvents?.()
  unsubscribeBackupEvents = null
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
    const s = await fetchSettings()
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
    // Send every flag, switched-off ones included: the daemon fills a
    // missing vhosts/pluginConfigs/ssl key with `true`, so dropping the
    // false entries put SSL private keys into backups the user had
    // excluded them from.
    const res = await fetch(`${daemonBaseUrl()}/api/backup`, {
      method: 'POST',
      headers: { ...daemonAuthHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ contentFlags: { ...contentFlags.value } }),
    })
    if (!res.ok) throw new Error(await responseError(res, t('backupsPage.snapshots.backupFailed')))
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
  // openExternal refuses file:// on purpose (ShellExecute would run
  // executables), so reveal the newest archive in the OS file manager.
  const newest = backups.value[0]?.path
  if (!newest) {
    ElMessage.info(t('backupsPage.overview.noBackupsYet'))
    return
  }
  void window.electronAPI?.revealInFolder?.(newest)
}

async function downloadBackup(path: string) {
  try {
    await downloadBackupFile(path)
  } catch (e) {
    ElMessage.error(t('backupsPage.snapshots.downloadFailed', { error: errorMessage(e) }))
  }
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
    if (!res.ok) throw new Error(await responseError(res, t('backupsPage.snapshots.restoreFailed')))
    const data = await res.json()
    ElMessage.success({
      message: t('backupsPage.toast.restored', { files: data.restored, safety: fileName(data.safetyBackup) }),
      duration: 3500,
    })
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
    return
  }
  // The restore replaced state.db and the site files under a running
  // daemon, which still holds the old sites and settings in memory.
  // Restart it and reload the window, the same way the settings reset does.
  try { await restartDaemon() } catch { /* the daemon exits mid-response */ }
  await new Promise(resolve => setTimeout(resolve, 3500))
  if (window.electronAPI?.restartRenderer) await window.electronAPI.restartRenderer()
  else window.location.reload()
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
    if (!res.ok) throw new Error(await responseError(res, t('backupsPage.snapshots.deleteFailed')))
    ElMessage.success(t('backupsPage.toast.deleted'))
    await loadAll()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function saveSchedule() {
  await persist({
    'backup.scheduleHours': String(scheduleHours.value),
    'backup.retainCount': String(retainCount.value),
  }, t('backupsPage.toast.scheduleSaved'))
}

async function saveContent() {
  await persist(Object.fromEntries(
    contentFlagDefs.map(f => [`backup.content.${f.key}`, contentFlags.value[f.key] ? 'true' : 'false']),
  ), t('backupsPage.toast.contentSaved'))
}

// One PUT with flat "category.key" names — the only body shape the
// settings endpoint accepts.
async function persist(patch: Record<string, string>, successText: string) {
  saving.value = true
  try {
    await saveSettings(patch)
    ElMessage.success(successText)
  } catch (e) {
    ElMessage.error(t('backupsPage.toast.settingsSaveFailed', { err: errorMessage(e) }))
  } finally {
    saving.value = false
  }
}

// ── utils ──────────────────────────────────────────────────────────────────

// Daemon errors come as { error } or as ProblemDetails, whose `title` is a
// generic English sentence — the actual cause sits in `detail`.
async function responseError(res: Response, fallback: string): Promise<string> {
  const body = await res.json().catch(() => null) as { detail?: string; error?: string; title?: string } | null
  return body?.detail || body?.error || body?.title || fallback
}

function fileName(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() ?? path
}

// BackupContentFlags.ToString() on the daemon: "Vhosts, Ssl", "Default", "All, Binaries"…
const FLAG_KEYS: Record<string, string[]> = {
  Vhosts: ['vhosts'],
  PluginConfigs: ['pluginConfigs'],
  Ssl: ['ssl'],
  Databases: ['databases'],
  Docroots: ['docroots'],
  Default: ['vhosts', 'pluginConfigs', 'ssl'],
  All: ['vhosts', 'pluginConfigs', 'ssl', 'databases', 'docroots'],
}

function contentLabel(flags: string): string {
  return flags.split(',')
    .flatMap(f => FLAG_KEYS[f.trim()] ?? [])
    .map(k => t(`backupsPage.content.short.${k}`))
    .join(', ')
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(locale.value, {
    day: '2-digit', month: '2-digit', year: 'numeric',
    hour: '2-digit', minute: '2-digit',
  })
}
</script>

<style scoped>
.backups-page { width: 100%; }

.page-header {
  margin-bottom: var(--wdc-space-5);
  padding: 0 0 var(--wdc-space-4);
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
}
.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0 0 var(--wdc-space-1);
  letter-spacing: -0.02em;
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-2-5);
}
.page-title-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 28px;
  height: 22px;
  padding: 0 var(--wdc-space-2);
  border-radius: var(--wdc-radius-full);
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

.tab-content {
  padding: var(--wdc-space-1) 0;
}

/* ── Overview ── */
.overview-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: var(--wdc-space-3);
  margin-bottom: var(--wdc-space-4-5);
}

.stat-card {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-4) var(--wdc-space-4-5);
}
.stat-label {
  font-size: 0.82rem;
  font-weight: 500;
  color: var(--wdc-text-3);
  margin-bottom: var(--wdc-space-1-5);
  font-family: var(--wdc-font-display);
}
.stat-value {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.overview-actions {
  display: flex;
  gap: var(--wdc-space-2-5);
  flex-wrap: wrap;
}

/* ── Snapshots ── */
.snapshots-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: var(--wdc-space-3);
}
.snapshots-count {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
}

.empty-state {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2-5);
  padding: var(--wdc-space-6) 0;
  color: var(--wdc-text-3);
  font-size: 0.9rem;
}

.snapshots-list {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2);
}

.snapshot-row {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-3) var(--wdc-space-3-5);
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  flex-wrap: wrap;
}
.snapshot-info {
  display: flex;
  gap: var(--wdc-space-3);
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
  gap: var(--wdc-space-1-5);
}

/* Settings cards moved to settings/shared/SettingsCard.vue primitive. */

.form-hint {
  margin-left: var(--wdc-space-2-5);
  font-size: 0.8rem;
  color: var(--wdc-text-3);
}
.content-empty-hint {
  margin: var(--wdc-space-3) 0 0;
  color: var(--wdc-status-error);
}

/* ── Content flags ── */
.content-flags {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3-5);
}
.flag-row {
  display: flex;
  align-items: flex-start;
  gap: var(--wdc-space-3-5);
}
.flag-copy {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1);
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
