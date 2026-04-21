<template>
  <div class="backups-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">Zálohy</h1>
        <p class="page-subtitle">Správa záloh konfigurace a dat</p>
      </div>
    </div>

    <div class="page-body">
      <el-tabs v-model="activeTab" class="backup-tabs">

        <!-- ═══ OVERVIEW ════════════════════════════════════════════════════ -->
        <el-tab-pane label="Přehled" name="overview">
          <div class="tab-content">
            <div class="overview-grid">
              <div class="stat-card">
                <div class="stat-label">Celková velikost</div>
                <div class="stat-value">{{ formatSize(stats.totalSize) }}</div>
              </div>
              <div class="stat-card">
                <div class="stat-label">Počet záloh</div>
                <div class="stat-value">{{ stats.count }}</div>
              </div>
              <div class="stat-card">
                <div class="stat-label">Poslední záloha</div>
                <div class="stat-value">{{ stats.lastCreatedUtc ? formatDate(stats.lastCreatedUtc) : 'Žádná' }}</div>
              </div>
              <div class="stat-card">
                <div class="stat-label">Příští záloha</div>
                <div class="stat-value">{{ nextScheduledLabel }}</div>
              </div>
            </div>

            <div class="overview-actions">
              <el-button
                type="primary"
                :loading="backingUp"
                @click="runBackupNow"
              >
                Zálohovat nyní
              </el-button>
              <el-button @click="openBackupFolder">
                Otevřít složku záloh
              </el-button>
              <el-button @click="loadAll" :loading="loading">
                Obnovit
              </el-button>
            </div>

            <el-alert
              v-if="backupResult"
              type="success"
              :title="`Záloha vytvořena: ${backupResult.files} souborů, ${formatSize(backupResult.size)}`"
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
        <el-tab-pane label="Snímky" name="snapshots">
          <div class="tab-content">
            <div class="snapshots-header">
              <span class="snapshots-count" v-if="backups.length">{{ backups.length }} záloh</span>
              <el-button size="small" @click="loadAll" :loading="loading">Obnovit</el-button>
            </div>

            <div v-if="loading && backups.length === 0" class="empty-state">
              <el-icon class="is-loading"><Loading /></el-icon>
              <span>Načítám…</span>
            </div>

            <div v-else-if="backups.length === 0" class="empty-state">
              Žádné zálohy. Spusťte první zálohu přes záložku Přehled.
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
                  >Stáhnout</el-button>
                  <el-button
                    size="small"
                    type="warning"
                    @click="restoreBackup(b)"
                  >Obnovit</el-button>
                  <el-button
                    size="small"
                    type="danger"
                    @click="deleteBackup(b)"
                  >Smazat</el-button>
                </div>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- ═══ SCHEDULE ════════════════════════════════════════════════════ -->
        <el-tab-pane label="Plánování" name="schedule">
          <div class="tab-content">
            <div class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Interval automatické zálohy</span>
              </header>
              <div class="settings-card-body">
                <el-form label-position="left" label-width="220px" size="small" style="max-width: 500px">
                  <el-form-item label="Interval (hodiny, 0 = vypnuto)">
                    <el-input-number
                      v-model="scheduleHours"
                      :min="0"
                      :max="168"
                      style="width: 160px"
                    />
                    <span class="form-hint" v-if="scheduleHours === 0">Automatická záloha je vypnuta</span>
                    <span class="form-hint" v-else>Záloha každých {{ scheduleHours }} h</span>
                  </el-form-item>
                  <el-form-item label="Počet uchovávaných záloh">
                    <el-input-number
                      v-model="retainCount"
                      :min="1"
                      :max="100"
                      style="width: 160px"
                    />
                  </el-form-item>
                </el-form>
                <el-button type="primary" size="small" @click="saveSchedule" :loading="saving">
                  Uložit plánování
                </el-button>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- ═══ CONTENT ══════════════════════════════════════════════════════ -->
        <el-tab-pane label="Obsah" name="content">
          <div class="tab-content">
            <div class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Co zahrnout do zálohy</span>
              </header>
              <div class="settings-card-body">
                <div class="content-flags">
                  <div v-for="flag in contentFlagDefs" :key="flag.key" class="flag-row">
                    <el-switch v-model="contentFlags[flag.key]" />
                    <div class="flag-copy">
                      <span class="flag-name">{{ flag.label }}</span>
                      <span class="flag-desc">{{ flag.desc }}</span>
                      <el-tag v-if="flag.default" size="small" type="success">Výchozí</el-tag>
                      <el-tag v-else size="small" type="info">Volitelné</el-tag>
                    </div>
                  </div>
                </div>
                <el-button type="primary" size="small" @click="saveContent" :loading="saving" style="margin-top: 16px">
                  Uložit výběr obsahu
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
import { ElMessage, ElMessageBox } from 'element-plus'
import { Loading } from '@element-plus/icons-vue'
import { daemonBaseUrl, daemonAuthHeaders } from '../../api/daemon'

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
  { key: 'vhosts',       label: 'Vhosts (konfigurace stránek)', desc: 'TOML soubory stránek z ~/.wdc/sites/', default: true },
  { key: 'pluginConfigs',label: 'Konfigurace pluginů',          desc: 'Caddy config, plugin config.json soubory', default: true },
  { key: 'ssl',          label: 'SSL certifikáty',              desc: 'mkcert certifikáty z ~/.wdc/ssl/sites/', default: true },
  { key: 'databases',    label: 'Databáze (mysqldump)',         desc: 'Dump všech MySQL databází — může být velký', default: false },
  { key: 'docroots',     label: 'Document roots (soubory stránek)', desc: 'Fyzické soubory webu — může být velmi velký', default: false },
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
  if (scheduleHours.value === 0) return 'Vypnuto'
  if (!stats.value.lastCreatedUtc) return 'Brzy'
  const last = new Date(stats.value.lastCreatedUtc).getTime()
  const nextMs = last + scheduleHours.value * 3600 * 1000
  const diff = nextMs - Date.now()
  if (diff <= 0) return 'Brzy'
  const hours = Math.floor(diff / 3600000)
  const mins = Math.floor((diff % 3600000) / 60000)
  return hours > 0 ? `za ${hours} h ${mins} min` : `za ${mins} min`
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
    errorMsg.value = `Chyba načítání: ${e instanceof Error ? e.message : String(e)}`
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
      throw new Error(err.title ?? err.error ?? 'Záloha selhala')
    }
    backupResult.value = await res.json()
    await loadAll()
    ElMessage.success('Záloha dokončena')
  } catch (e) {
    errorMsg.value = e instanceof Error ? e.message : String(e)
    ElMessage.error(errorMsg.value)
  } finally {
    backingUp.value = false
  }
}

function openBackupFolder() {
  window.electronAPI?.openPath?.(
    `${(window as any).__WDC_ROOT__ ?? '~'}/.wdc/backups`
  )
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
      `Obnovit zálohu z ${formatDate(b.createdUtc)}? Aktuální stav bude před obnovením automaticky uložen jako bezpečnostní záloha.`,
      'Obnovit zálohu',
      { type: 'warning', confirmButtonText: 'Obnovit', cancelButtonText: 'Zrušit' }
    )
  } catch { return }

  try {
    const name = fileName(b.path)
    const res = await fetch(`${daemonBaseUrl()}/api/backup/${encodeURIComponent(name)}/restore`, {
      method: 'POST',
      headers: daemonAuthHeaders(),
    })
    if (!res.ok) {
      const err = await res.json().catch(() => ({ title: 'Obnovení selhalo' }))
      throw new Error(err.title ?? err.error ?? 'Obnovení selhalo')
    }
    const data = await res.json()
    ElMessage.success(`Obnoveno ${data.restored} souborů. Bezpečnostní záloha: ${fileName(data.safetyBackup)}`)
    await loadAll()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function deleteBackup(b: BackupEntry) {
  try {
    await ElMessageBox.confirm(
      `Smazat zálohu ${fileName(b.path)}?`,
      'Smazat zálohu',
      { type: 'warning', confirmButtonText: 'Smazat', cancelButtonText: 'Zrušit' }
    )
  } catch { return }

  try {
    const name = fileName(b.path)
    const res = await fetch(`${daemonBaseUrl()}/api/backup/${encodeURIComponent(name)}`, {
      method: 'DELETE',
      headers: daemonAuthHeaders(),
    })
    if (!res.ok) throw new Error('Smazání selhalo')
    ElMessage.success('Záloha smazána')
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
    ElMessage.success('Plánování uloženo')
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
    ElMessage.success('Výběr obsahu uložen')
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
  if (!res.ok) throw new Error(`Uložení nastavení selhalo: ${category}.${key}`)
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
.backups-page { max-width: 900px; margin: 0 auto; padding: 0 24px; }

.page-header {
  margin-bottom: 24px;
}
.page-title {
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0 0 4px;
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
  background: rgba(255,255,255,0.03);
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
