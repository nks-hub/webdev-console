<template>
  <div class="settings-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">Settings</h1>
        <p class="page-subtitle">Configure NKS WDC daemon and UI preferences</p>
      </div>
    </div>

    <div class="page-body">
      <el-tabs v-model="activeTab" class="settings-tabs">
        <!-- Ports tab -->
        <el-tab-pane label="Ports" name="ports">
          <div class="tab-content">
            <p class="tab-desc">Configure default service ports. Restart services after changing.</p>
            <el-form label-position="left" label-width="160px" size="small" style="max-width: 400px">
              <el-form-item label="HTTP Port">
                <el-input-number v-model="ports.http" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item label="HTTPS Port">
                <el-input-number v-model="ports.https" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item label="MySQL Port">
                <el-input-number v-model="ports.mysql" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item label="Redis Port">
                <el-input-number v-model="ports.redis" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item label="Mailpit SMTP">
                <el-input-number v-model="ports.mailpitSmtp" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item label="Mailpit HTTP">
                <el-input-number v-model="ports.mailpitHttp" :min="1" :max="65535" style="width: 100%" />
              </el-form-item>
              <el-form-item label="PHP-FPM base port">
                <el-input-number v-model="phpFpmBasePort" :min="9000" :max="9999" style="width: 100%" />
                <div class="hint">
                  Per-version ports are derived as base + major×10 + minor.
                  E.g. base 9000 + PHP 8.4 → port 9084.
                </div>
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- General tab -->
        <el-tab-pane label="General" name="general">
          <div class="tab-content">
            <p class="tab-desc">Application behavior and startup preferences.</p>
            <el-form label-position="left" label-width="180px" size="small" style="max-width: 500px">
              <el-form-item label="Language">
                <el-select
                  :model-value="locale"
                  @update:model-value="(v: string) => { locale = v; localStorage.setItem('wdc-locale', v) }"
                  style="width: 160px"
                >
                  <el-option label="English" value="en" />
                  <el-option label="Čeština" value="cs" />
                </el-select>
              </el-form-item>
              <el-form-item label="Theme">
                <el-radio-group
                  :model-value="themeStore.mode"
                  @update:model-value="themeStore.setMode($event as ThemeMode)"
                >
                  <el-radio-button value="dark">Dark</el-radio-button>
                  <el-radio-button value="light">Light</el-radio-button>
                  <el-radio-button value="system">System</el-radio-button>
                </el-radio-group>
              </el-form-item>
              <el-form-item label="Run on startup">
                <el-switch v-model="runOnStartup" />
              </el-form-item>
              <el-form-item label="Auto-start services">
                <el-switch v-model="autoStart" />
              </el-form-item>
              <el-form-item label="Default PHP version">
                <el-select v-model="defaultPhp" style="width: 160px" placeholder="Select">
                  <el-option v-for="v in phpVersions" :key="v" :label="'PHP ' + v" :value="v" />
                </el-select>
              </el-form-item>
              <el-form-item label="DNS Cache">
                <el-button size="small" @click="flushDns" :loading="flushingDns">
                  Flush DNS Cache
                </el-button>
              </el-form-item>

              <el-divider />

              <el-form-item label="Telemetry">
                <el-switch v-model="telemetryEnabled" />
                <div class="hint">
                  Send anonymous usage statistics to help improve NKS WDC.
                  No personal data, no site names, no code — just feature usage counts.
                </div>
              </el-form-item>
              <el-form-item label="Crash reports" v-if="telemetryEnabled">
                <el-switch v-model="telemetryCrashReports" />
                <div class="hint">
                  Send crash stack traces via Sentry when a daemon exception occurs.
                  Disabled when telemetry is off.
                </div>
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- Paths tab -->
        <el-tab-pane label="Paths" name="paths">
          <div class="tab-content">
            <p class="tab-desc">Override binary paths. Leave blank to use auto-detected defaults.</p>
            <el-form label-position="top" size="small" style="max-width: 500px">
              <el-form-item label="Apache httpd.exe">
                <el-input v-model="paths.apache" placeholder="C:\nks-wdc\binaries\apache\2.4\bin\httpd.exe" />
              </el-form-item>
              <el-form-item label="MySQL mysqld.exe">
                <el-input v-model="paths.mysql" placeholder="C:\nks-wdc\binaries\mysql\8.0\bin\mysqld.exe" />
              </el-form-item>
              <el-form-item label="PHP executable">
                <el-input v-model="paths.php" placeholder="C:\nks-wdc\binaries\php\8.4\php.exe" />
              </el-form-item>
              <el-form-item label="Redis redis-server.exe">
                <el-input v-model="paths.redis" placeholder="C:\nks-wdc\binaries\redis\7.2\redis-server.exe" />
              </el-form-item>
              <el-form-item label="Sites config directory">
                <el-input v-model="paths.sitesDir" placeholder="C:\nks-wdc\conf\vhosts" />
              </el-form-item>
              <el-form-item label="Hosts file">
                <el-input v-model="paths.hostsFile" placeholder="C:\Windows\System32\drivers\etc\hosts" />
                <div class="hint">
                  Path to the system hosts file for local domain resolution.
                  Leave blank for the OS default.
                </div>
              </el-form-item>

              <el-divider />

              <el-form-item label="Data directory">
                <el-input
                  :model-value="systemInfo?.os?.machine ? `${systemInfo?.daemon?.pid ? '~/.wdc' : '~/.wdc'}` : '~/.wdc'"
                  disabled
                  class="mono-input"
                />
                <div class="hint">
                  Root for all daemon state: sites, binaries, SSL certs, backups, configs.
                  Override with <code>WDC_DATA_DIR</code> environment variable or
                  <code>portable.txt</code> next to the executable.
                </div>
              </el-form-item>
              <el-form-item label="Backup directory">
                <el-input v-model="backupDir" placeholder="~/.wdc/backups" />
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- Databases tab -->
        <el-tab-pane label="Databases" name="databases">
          <div class="tab-content">
            <p class="tab-desc">MySQL databases managed by NKS WDC.</p>
            <div class="db-list" v-if="databases.length > 0">
              <div class="db-row" v-for="db in databases" :key="db">
                <span class="db-name">{{ db }}</span>
                <el-button size="small" type="danger" text @click="dropDatabase(db)">Drop</el-button>
              </div>
            </div>
            <el-empty v-else description="No user databases" :image-size="48" />
            <div class="db-create">
              <el-input v-model="newDbName" placeholder="new_database" size="small" style="width: 200px" />
              <el-button size="small" type="primary" @click="createDatabase" :disabled="!newDbName">Create</el-button>
            </div>
          </div>
        </el-tab-pane>

        <!-- Advanced tab — integration endpoints -->
        <el-tab-pane label="Advanced" name="advanced">
          <div class="tab-content">
            <p class="tab-desc">
              External services the daemon talks to. Leave blank to use built-in defaults.
              Changes take effect after restart or a catalog refresh.
            </p>
            <el-form label-position="top" size="small" style="max-width: 560px">
              <el-form-item label="Catalog API URL">
                <el-input
                  v-model="catalogUrl"
                  placeholder="http://127.0.0.1:8765"
                  class="mono-input"
                >
                  <template #append>
                    <el-button :loading="refreshingCatalog" @click="refreshCatalog">
                      Refresh
                    </el-button>
                  </template>
                </el-input>
                <div class="hint">
                  URL of the NKS WDC catalog-api service (see
                  <code>services/catalog-api/</code>). Electron auto-spawns
                  a local sidecar on <code>127.0.0.1:8765</code> in dev mode.
                  Point at your self-hosted deployment for team-wide binary
                  versions or leave blank for the default.
                </div>
                <div class="hint" v-if="catalogStatus">
                  <span :class="['status-dot', catalogStatus.ok ? 'ok' : 'err']"></span>
                  {{ catalogStatus.message }}
                </div>
              </el-form-item>
              <el-form-item label="Binary releases">
                <el-button size="small" @click="testCatalogReachable" :loading="testingCatalog">
                  Test connection
                </el-button>
                <el-button
                  size="small"
                  type="primary"
                  @click="openCatalogAdmin"
                  :disabled="!catalogUrl"
                >
                  Open admin UI
                </el-button>
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- Account & Devices tab -->
        <el-tab-pane label="Account" name="account">
          <div class="tab-content">
            <!-- Not logged in -->
            <template v-if="!accountToken">
              <section class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">Sign in to NKS WDC Cloud</span>
                </header>
                <div class="settings-card-body">
                  <p class="tab-desc">
                    Sign in to sync settings across devices, manage your fleet,
                    and push configs between machines.
                  </p>
                  <el-form label-position="top" size="small" style="max-width: 360px" @submit.prevent="doLogin">
                    <el-form-item label="Email">
                      <el-input v-model="authEmail" placeholder="you@example.com" />
                    </el-form-item>
                    <el-form-item label="Password">
                      <el-input v-model="authPassword" type="password" show-password />
                    </el-form-item>
                    <div class="sync-actions">
                      <el-button type="primary" size="small" :loading="authLoading" @click="doLogin">Sign in</el-button>
                      <el-button size="small" :loading="authLoading" @click="doRegister">Register</el-button>
                    </div>
                    <div class="hint" v-if="authError" style="color: var(--wdc-status-error); margin-top: 8px;">
                      {{ authError }}
                    </div>
                  </el-form>
                </div>
              </section>
            </template>

            <!-- Logged in -->
            <template v-else>
              <section class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">Account</span>
                  <span style="font-size: 0.78rem; color: var(--wdc-text-2);">{{ accountEmail }}</span>
                </header>
                <div class="settings-card-body">
                  <div class="sync-actions">
                    <el-button size="small" @click="loadDevicesAccount" :loading="devicesLoading">Refresh devices</el-button>
                    <el-button size="small" type="danger" plain @click="doLogout">Sign out</el-button>
                  </div>
                </div>
              </section>

              <section class="settings-card">
                <header class="settings-card-header">
                  <span class="settings-card-title">My Devices</span>
                  <span style="font-size: 0.72rem; color: var(--wdc-text-3)">{{ accountDevices.length }} registered</span>
                </header>
                <div class="settings-card-body">
                  <el-table v-if="accountDevices.length > 0" :data="accountDevices" size="small" stripe>
                    <el-table-column label="Name" min-width="150">
                      <template #default="{ row }">
                        <span class="mono" :style="row.device_id === deviceId ? 'font-weight: 700' : ''">
                          {{ row.name || row.device_id.slice(0, 12) + '…' }}
                        </span>
                        <el-tag v-if="row.device_id === deviceId" size="small" type="success" effect="dark" style="margin-left: 6px">this</el-tag>
                      </template>
                    </el-table-column>
                    <el-table-column label="OS" width="120">
                      <template #default="{ row }">
                        <span class="mono">{{ (row.os ?? '') + '/' + (row.arch ?? '') }}</span>
                      </template>
                    </el-table-column>
                    <el-table-column label="Sites" width="70" align="center">
                      <template #default="{ row }">{{ row.site_count ?? '—' }}</template>
                    </el-table-column>
                    <el-table-column label="Status" width="90">
                      <template #default="{ row }">
                        <el-tag size="small" :type="row.online ? 'success' : 'info'" effect="dark">
                          {{ row.online ? 'Online' : 'Offline' }}
                        </el-tag>
                      </template>
                    </el-table-column>
                    <el-table-column label="Last sync" width="150">
                      <template #default="{ row }">
                        <span style="font-size: 0.72rem; color: var(--wdc-text-3);">
                          {{ row.last_seen_at ? new Date(row.last_seen_at).toLocaleString() : '—' }}
                        </span>
                      </template>
                    </el-table-column>
                    <el-table-column label="" width="110" align="right">
                      <template #default="{ row }">
                        <el-button
                          v-if="row.device_id !== deviceId"
                          size="small"
                          type="primary"
                          plain
                          @click="pushMyConfigTo(row.device_id)"
                          :loading="pushingTo === row.device_id"
                        >Push here</el-button>
                      </template>
                    </el-table-column>
                  </el-table>
                  <el-empty v-else description="No devices registered yet. Push settings first." :image-size="48" />
                </div>
              </section>
            </template>
          </div>
        </el-tab-pane>

        <!-- Sync tab — cloud config sync + export/import -->
        <el-tab-pane label="Sync" name="sync">
          <div class="tab-content">
            <p class="tab-desc">
              Synchronize your NKS WDC configuration with the cloud catalog
              service, or export/import settings as a file for offline transfer.
            </p>

            <!-- Device identity -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Device</span>
              </header>
              <div class="settings-card-body">
                <el-form label-position="left" label-width="140px" size="small" style="max-width: 500px">
                  <el-form-item label="Device ID">
                    <el-input :model-value="deviceId" disabled class="mono-input">
                      <template #append>
                        <el-button @click="copyDeviceId" title="Copy to clipboard">Copy</el-button>
                      </template>
                    </el-input>
                    <div class="hint">
                      Auto-generated unique identifier for this machine.
                      Used as the key for cloud sync.
                    </div>
                  </el-form-item>
                  <el-form-item label="Device name">
                    <el-input v-model="deviceName" placeholder="e.g. Work Laptop" />
                    <div class="hint">Optional label to identify this device in the sync UI.</div>
                  </el-form-item>
                </el-form>
              </div>
            </section>

            <!-- Cloud sync -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Cloud sync</span>
                <span v-if="syncStatus" :class="['sync-badge', syncStatus.ok ? 'sync-ok' : 'sync-err']">
                  {{ syncStatus.message }}
                </span>
              </header>
              <div class="settings-card-body">
                <p class="tab-desc" style="margin-bottom: 12px;">
                  Push your current settings (sites, services, ports, paths, plugin
                  toggles) to the catalog-api service so a fresh install can
                  restore them with one click.
                </p>
                <div class="sync-actions">
                  <el-button
                    type="primary"
                    size="small"
                    :loading="syncing"
                    :disabled="!catalogUrl && !deviceId"
                    @click="pushToCloud"
                  >
                    <el-icon><Upload /></el-icon>
                    <span>Push to cloud</span>
                  </el-button>
                  <el-button
                    size="small"
                    :loading="pulling"
                    :disabled="!catalogUrl && !deviceId"
                    @click="pullFromCloud"
                  >
                    <el-icon><Download /></el-icon>
                    <span>Pull from cloud</span>
                  </el-button>
                  <el-button
                    size="small"
                    :disabled="!catalogUrl && !deviceId"
                    @click="checkCloudExists"
                    :loading="checkingCloud"
                  >
                    Check status
                  </el-button>
                </div>
                <div class="hint" v-if="lastSyncTime">
                  Last synced: {{ lastSyncTime }}
                </div>
              </div>
            </section>

            <!-- File export / import -->
            <section class="settings-card">
              <header class="settings-card-header">
                <span class="settings-card-title">Export / Import</span>
              </header>
              <div class="settings-card-body">
                <p class="tab-desc" style="margin-bottom: 12px;">
                  Download a JSON file of all settings, or import from a
                  previously exported file. Useful for offline transfers or
                  version-controlled team configs.
                </p>
                <div class="sync-actions">
                  <el-button size="small" @click="exportSettings">
                    <el-icon><Download /></el-icon>
                    <span>Export to file</span>
                  </el-button>
                  <el-button size="small" @click="triggerImport">
                    <el-icon><Upload /></el-icon>
                    <span>Import from file</span>
                  </el-button>
                  <input
                    ref="importFileInput"
                    type="file"
                    accept=".json"
                    style="display: none"
                    @change="importSettings"
                  />
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- About tab -->
        <el-tab-pane label="About" name="about">
          <div class="tab-content">
            <div class="about-card">
              <div class="about-logo">NKS WDC</div>
              <div class="about-version">v{{ appVersion }}</div>
              <div class="about-subtitle">Local development server manager</div>
              <div class="about-desc">
                A modern replacement for MAMP PRO, built with Electron + Vue 3 + Element Plus.
                Manages Apache, MySQL, PHP, Redis, Mailpit and SSL certificates for local development.
              </div>

              <div class="about-links">
                <a href="https://github.com/nks-hub/webdev-console" target="_blank" class="about-link">GitHub</a>
                <a href="https://wdc.nks-hub.cz" target="_blank" class="about-link">Website</a>
              </div>

              <div class="about-stack">
                <el-tag size="small" effect="plain">Electron 34</el-tag>
                <el-tag size="small" effect="plain">Vue 3.5</el-tag>
                <el-tag size="small" effect="plain">Element Plus 2.9</el-tag>
                <el-tag size="small" effect="plain">.NET 9</el-tag>
              </div>

              <div v-if="systemInfo" class="about-system">
                <div class="about-sys-title">Runtime</div>
                <div class="about-sys-row">
                  <span class="sys-label">Services</span>
                  <span class="sys-value">{{ systemInfo.services?.running }}/{{ systemInfo.services?.total }} running</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Sites</span>
                  <span class="sys-value">{{ systemInfo.sites }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Plugins</span>
                  <span class="sys-value">{{ systemInfo.plugins }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">Binaries</span>
                  <span class="sys-value">{{ systemInfo.binaries }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">OS</span>
                  <span class="sys-value">{{ systemInfo.os?.version }}</span>
                </div>
                <div class="about-sys-row">
                  <span class="sys-label">.NET</span>
                  <span class="sys-value">{{ systemInfo.runtime?.dotnet }} ({{ systemInfo.runtime?.arch }})</span>
                </div>
              </div>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>

      <!-- Save button (not shown on About) -->
      <div class="settings-footer" v-if="activeTab !== 'about'">
        <el-button type="primary" size="small" :loading="saving" @click="save">
          Save Settings
        </el-button>
        <el-button size="small" @click="loadSettings">Reset</el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Upload, Download } from '@element-plus/icons-vue'
import { useThemeStore, type ThemeMode } from '../../stores/theme'
import {
  catalogRegister, catalogLogin, fetchDevices, pushConfigToDevice,
  type DeviceInfo as CatalogDeviceInfo,
} from '../../api/daemon'

const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'
const themeStore = useThemeStore()
const { locale } = useI18n()

const activeTab = ref('general')
const saving = ref(false)
const databases = ref<string[]>([])
const newDbName = ref('')
const systemInfo = ref<any>(null)

const ports = reactive({
  http: 80,
  https: 443,
  mysql: 3306,
  redis: 6379,
  mailpitSmtp: 1025,
  mailpitHttp: 8025,
})

const runOnStartup = ref(false)
const autoStart = ref(true)
const defaultPhp = ref('8.4')
const phpVersions = ref<string[]>(['8.4', '8.3', '7.4'])
const flushingDns = ref(false)

const paths = reactive({
  apache: '',
  mysql: '',
  php: '',
  redis: '',
  sitesDir: '',
  hostsFile: '',
})

// ── Additional settings from SPEC ─────────────────────────────────────
const phpFpmBasePort = ref(9000)
const telemetryEnabled = ref(false)
const telemetryCrashReports = ref(false)
const backupDir = ref('')
const backupScheduleHours = ref(0)

// ── Catalog API integration (Advanced tab) ────────────────────────────
const catalogUrl = ref('')
const refreshingCatalog = ref(false)
const testingCatalog = ref(false)
const catalogStatus = ref<{ ok: boolean; message: string } | null>(null)

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = window.daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

function authHeaders(): Record<string, string> {
  const urlToken = new URLSearchParams(window.location.search).get('token')
  const token = window.daemonApi?.getToken?.() || urlToken || ''
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  return headers
}

async function loadDatabases() {
  try {
    const r = await fetch(`${daemonBase()}/api/databases`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      databases.value = data.databases ?? []
    }
  } catch { /* not connected */ }
}

async function createDatabase() {
  if (!newDbName.value) return
  try {
    const r = await fetch(`${daemonBase()}/api/databases`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ name: newDbName.value }),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    ElMessage.success(`Database ${newDbName.value} created`)
    newDbName.value = ''
    await loadDatabases()
  } catch (e: any) { ElMessage.error(`Create failed: ${e.message}`) }
}

async function dropDatabase(name: string) {
  try {
    const r = await fetch(`${daemonBase()}/api/databases/${name}`, { method: 'DELETE', headers: authHeaders() })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    ElMessage.success(`Database ${name} dropped`)
    await loadDatabases()
  } catch (e: any) { ElMessage.error(`Drop failed: ${e.message}`) }
}

async function flushDns() {
  flushingDns.value = true
  try {
    const r = await fetch(`${daemonBase()}/api/dns/flush`, { method: 'POST', headers: authHeaders() })
    if (r.ok) ElMessage.success('DNS cache flushed')
    else ElMessage.warning('DNS flush may require admin privileges')
  } catch {
    ElMessage.warning('DNS flush endpoint not available')
  } finally {
    flushingDns.value = false
  }
}

async function loadPhpVersions() {
  try {
    const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      phpVersions.value = data.map((v: any) => v.majorMinor || v.version)
    }
  } catch { /* keep defaults */ }
}

async function loadSettings() {
  try {
    const r = await fetch(`${daemonBase()}/api/settings`, { headers: authHeaders() })
    if (!r.ok) return
    const data = await r.json() as Record<string, string>
    if (data['ports.http'])        ports.http = parseInt(data['ports.http'])
    if (data['ports.https'])       ports.https = parseInt(data['ports.https'])
    if (data['ports.mysql'])       ports.mysql = parseInt(data['ports.mysql'])
    if (data['ports.redis'])       ports.redis = parseInt(data['ports.redis'])
    if (data['ports.mailpitSmtp']) ports.mailpitSmtp = parseInt(data['ports.mailpitSmtp'])
    if (data['ports.mailpitHttp']) ports.mailpitHttp = parseInt(data['ports.mailpitHttp'])
    if (data['general.runOnStartup']) runOnStartup.value = data['general.runOnStartup'] === 'true'
    if (data['general.autoStart'])    autoStart.value = data['general.autoStart'] === 'true'
    if (data['paths.apache'])    paths.apache = data['paths.apache']
    if (data['paths.mysql'])     paths.mysql = data['paths.mysql']
    if (data['paths.php'])       paths.php = data['paths.php']
    if (data['paths.redis'])     paths.redis = data['paths.redis']
    if (data['paths.sitesDir'])  paths.sitesDir = data['paths.sitesDir']
    if (data['paths.hostsFile']) paths.hostsFile = data['paths.hostsFile']
    if (data['ports.phpFpmBase']) phpFpmBasePort.value = parseInt(data['ports.phpFpmBase'])
    if (data['daemon.catalogUrl']) catalogUrl.value = data['daemon.catalogUrl']
    if (data['telemetry.enabled']) telemetryEnabled.value = data['telemetry.enabled'] === 'true'
    if (data['telemetry.crashReports']) telemetryCrashReports.value = data['telemetry.crashReports'] === 'true'
    if (data['backup.dir']) backupDir.value = data['backup.dir']
    if (data['backup.scheduleHours']) backupScheduleHours.value = parseInt(data['backup.scheduleHours'])
  } catch { /* daemon not reachable — keep defaults */ }
}

async function refreshCatalog() {
  refreshingCatalog.value = true
  catalogStatus.value = null
  try {
    // Save URL first so the daemon's CatalogClient picks it up, then
    // trigger a manual refresh so the new source takes effect without
    // restarting the daemon.
    await fetch(`${daemonBase()}/api/settings`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify({ 'daemon.catalogUrl': catalogUrl.value || '' }),
    })
    const r = await fetch(`${daemonBase()}/api/binaries/catalog/refresh`, {
      method: 'POST',
      headers: authHeaders(),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    const result = await r.json()
    catalogStatus.value = {
      ok: true,
      message: `Refreshed: ${result.count ?? 0} releases · ${result.lastFetch ?? 'now'}`,
    }
    ElMessage.success(`Catalog refreshed: ${result.count ?? 0} releases`)
  } catch (e: any) {
    catalogStatus.value = { ok: false, message: `Refresh failed: ${e.message}` }
    ElMessage.error(`Refresh failed: ${e.message}`)
  } finally {
    refreshingCatalog.value = false
  }
}

async function testCatalogReachable() {
  testingCatalog.value = true
  catalogStatus.value = null
  const url = catalogUrl.value || 'http://127.0.0.1:8765'
  try {
    const r = await fetch(`${url.replace(/\/$/, '')}/healthz`)
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    const body = await r.json()
    catalogStatus.value = {
      ok: true,
      message: `✓ Reachable: ${body.service ?? 'catalog-api'} v${body.version ?? '?'}`,
    }
  } catch (e: any) {
    catalogStatus.value = {
      ok: false,
      message: `✗ Unreachable: ${e.message}. Is the sidecar running?`,
    }
  } finally {
    testingCatalog.value = false
  }
}

function openCatalogAdmin() {
  const url = catalogUrl.value || 'http://127.0.0.1:8765'
  window.open(url.replace(/\/$/, '') + '/admin', '_blank')
}

// ── Account & Devices tab ─────────────────────────────────────────────
const accountToken = ref(localStorage.getItem('nks-wdc-catalog-jwt') || '')
const accountEmail = ref(localStorage.getItem('nks-wdc-catalog-email') || '')
const authEmail = ref('')
const authPassword = ref('')
const authLoading = ref(false)
const authError = ref('')
const accountDevices = ref<CatalogDeviceInfo[]>([])
const devicesLoading = ref(false)
const pushingTo = ref<string | null>(null)

function getCatalogUrl(): string {
  return (catalogUrl.value || 'http://127.0.0.1:8765').replace(/\/$/, '')
}

async function doLogin() {
  authLoading.value = true
  authError.value = ''
  try {
    const result = await catalogLogin(getCatalogUrl(), authEmail.value, authPassword.value)
    accountToken.value = result.token
    accountEmail.value = result.email
    localStorage.setItem('nks-wdc-catalog-jwt', result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    authPassword.value = ''
    ElMessage.success(`Signed in as ${result.email}`)
    void loadDevicesAccount()
  } catch (e: any) {
    authError.value = e.message
  } finally {
    authLoading.value = false
  }
}

async function doRegister() {
  authLoading.value = true
  authError.value = ''
  try {
    const result = await catalogRegister(getCatalogUrl(), authEmail.value, authPassword.value)
    accountToken.value = result.token
    accountEmail.value = result.email
    localStorage.setItem('nks-wdc-catalog-jwt', result.token)
    localStorage.setItem('nks-wdc-catalog-email', result.email)
    authPassword.value = ''
    ElMessage.success(`Account created: ${result.email}`)
  } catch (e: any) {
    authError.value = e.message
  } finally {
    authLoading.value = false
  }
}

function doLogout() {
  accountToken.value = ''
  accountEmail.value = ''
  accountDevices.value = []
  localStorage.removeItem('nks-wdc-catalog-jwt')
  localStorage.removeItem('nks-wdc-catalog-email')
  ElMessage.success('Signed out')
}

async function loadDevicesAccount() {
  if (!accountToken.value) return
  devicesLoading.value = true
  try {
    accountDevices.value = await fetchDevices(getCatalogUrl(), accountToken.value)
  } catch (e: any) {
    ElMessage.error(`Load devices failed: ${e.message}`)
    if (e.message.includes('401')) doLogout()
  } finally {
    devicesLoading.value = false
  }
}

async function pushMyConfigTo(targetDeviceId: string) {
  if (!accountToken.value || !deviceId.value) return
  pushingTo.value = targetDeviceId
  try {
    await pushConfigToDevice(getCatalogUrl(), accountToken.value, targetDeviceId, deviceId.value)
    ElMessage.success(`Config pushed to device ${targetDeviceId.slice(0, 8)}…`)
  } catch (e: any) {
    ElMessage.error(`Push failed: ${e.message}`)
  } finally {
    pushingTo.value = null
  }
}

// ── Sync tab state ────────────────────────────────────────────────────
const deviceId = ref('')
const deviceName = ref('')
const syncing = ref(false)
const pulling = ref(false)
const checkingCloud = ref(false)
const syncStatus = ref<{ ok: boolean; message: string } | null>(null)
const lastSyncTime = ref<string | null>(null)
const importFileInput = ref<HTMLInputElement | null>(null)

async function loadDeviceId() {
  // Device ID is persisted in daemon settings; generate if missing
  try {
    const r = await fetch(`${daemonBase()}/api/settings`, { headers: authHeaders() })
    if (!r.ok) return
    const data = await r.json() as Record<string, string>
    if (data['sync.deviceId']) {
      deviceId.value = data['sync.deviceId']
    } else {
      // First run: generate a UUID and persist it
      const id = crypto.randomUUID()
      deviceId.value = id
      await fetch(`${daemonBase()}/api/settings`, {
        method: 'PUT',
        headers: authHeaders(),
        body: JSON.stringify({ 'sync.deviceId': id }),
      })
    }
    if (data['sync.deviceName']) deviceName.value = data['sync.deviceName']
    if (data['sync.lastSyncTime']) lastSyncTime.value = data['sync.lastSyncTime']
  } catch { /* daemon not reachable */ }
}

function copyDeviceId() {
  navigator.clipboard.writeText(deviceId.value)
    .then(() => ElMessage.success('Device ID copied'))
    .catch(() => ElMessage.warning('Cannot access clipboard'))
}

async function buildSyncPayload(): Promise<Record<string, any>> {
  // Collect all daemon settings + sites + service states into one object
  const [settingsRes, sitesRes] = await Promise.all([
    fetch(`${daemonBase()}/api/settings`, { headers: authHeaders() }),
    fetch(`${daemonBase()}/api/sites`, { headers: authHeaders() }),
  ])
  const settings = settingsRes.ok ? await settingsRes.json() : {}
  const sites = sitesRes.ok ? await sitesRes.json() : []
  return {
    exportedAt: new Date().toISOString(),
    version: appVersion,
    deviceId: deviceId.value,
    deviceName: deviceName.value,
    settings,
    sites,
  }
}

async function pushToCloud() {
  syncing.value = true
  syncStatus.value = null
  try {
    // Save device name first
    await fetch(`${daemonBase()}/api/settings`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify({
        'sync.deviceName': deviceName.value,
        'sync.lastSyncTime': new Date().toISOString(),
      }),
    })

    const payload = await buildSyncPayload()
    const url = getCatalogUrl()
    const pushHeaders: Record<string, string> = { 'Content-Type': 'application/json' }
    if (accountToken.value) pushHeaders['Authorization'] = `Bearer ${accountToken.value}`
    const r = await fetch(`${url}/api/v1/sync/config`, {
      method: 'POST',
      headers: pushHeaders,
      body: JSON.stringify({ device_id: deviceId.value, payload }),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    lastSyncTime.value = new Date().toLocaleString()
    syncStatus.value = { ok: true, message: 'Pushed successfully' }
    ElMessage.success('Configuration pushed to cloud')
  } catch (e: any) {
    syncStatus.value = { ok: false, message: `Push failed: ${e.message}` }
    ElMessage.error(`Push failed: ${e.message}`)
  } finally {
    syncing.value = false
  }
}

// ── Sync field classification (Strategy D) ────────────────────────────
// Each settings key is tagged as "sync" (portable across devices) or
// "local" (machine-specific paths/ports that must stay untouched on
// pull). The classification lives here so adding a new settings key
// forces the developer to decide "is this sync or local?" at definition
// time. Site fields follow the same principle: domain/phpVersion/ssl/
// aliases/framework/cloudflare are sync; documentRoot/ports are local.
const SYNC_SETTINGS_PREFIXES = [
  'general.', 'telemetry.', 'backup.scheduleHours', 'daemon.catalogUrl',
  'sync.',
]
const LOCAL_SETTINGS_PREFIXES = [
  'paths.', 'ports.', 'backup.dir',
]

function isSettingSyncable(key: string): boolean {
  if (SYNC_SETTINGS_PREFIXES.some(p => key.startsWith(p))) return true
  if (LOCAL_SETTINGS_PREFIXES.some(p => key.startsWith(p))) return false
  return true // unknown keys default to sync
}

const SITE_SYNC_FIELDS = new Set([
  'domain', 'phpVersion', 'sslEnabled', 'aliases', 'framework',
  'environment', 'cloudflare',
])
const SITE_LOCAL_FIELDS = new Set([
  'documentRoot', 'httpPort', 'httpsPort',
])

async function pullFromCloud() {
  pulling.value = true
  syncStatus.value = null
  try {
    const url = (catalogUrl.value || 'http://127.0.0.1:8765').replace(/\/$/, '')
    const r = await fetch(`${url}/api/v1/sync/config/${deviceId.value}`)
    if (!r.ok) {
      if (r.status === 404) {
        syncStatus.value = { ok: false, message: 'No cloud snapshot found for this device' }
        ElMessage.info('No cloud snapshot found — push first')
        return
      }
      throw new Error(`HTTP ${r.status}`)
    }
    const data = await r.json()
    const payload = data.payload

    // ── Merge settings with local overrides ──────────────────────────
    // Only apply sync-classified keys from the remote snapshot. Local
    // keys (paths, ports, backup dir) stay untouched so pulling another
    // device's snapshot doesn't overwrite C:\work\htdocs with /home/user.
    if (payload?.settings && typeof payload.settings === 'object') {
      const localSettings = await fetch(`${daemonBase()}/api/settings`, { headers: authHeaders() })
        .then(r => r.ok ? r.json() : {}) as Record<string, string>

      const merged: Record<string, string> = {}
      for (const [key, value] of Object.entries(payload.settings as Record<string, string>)) {
        if (isSettingSyncable(key)) {
          merged[key] = value
        }
        // Local keys: keep existing local value (skip remote)
      }

      if (Object.keys(merged).length > 0) {
        await fetch(`${daemonBase()}/api/settings`, {
          method: 'PUT',
          headers: authHeaders(),
          body: JSON.stringify(merged),
        })
      }
    }

    // ── Merge sites ──────────────────────────────────────────────────
    // Match by domain. Existing sites: merge sync fields, keep local.
    // New sites: create with sync fields + empty documentRoot (user
    // must set it via SiteEdit before the vhost is generated).
    if (Array.isArray(payload?.sites)) {
      const localSites = await fetch(`${daemonBase()}/api/sites`, { headers: authHeaders() })
        .then(r => r.ok ? r.json() : []) as any[]
      const localByDomain = new Map(localSites.map((s: any) => [s.domain, s]))

      let newSiteCount = 0
      for (const remoteSite of payload.sites) {
        const domain = remoteSite.domain
        if (!domain) continue
        const local = localByDomain.get(domain)

        if (local) {
          // Existing site: merge sync fields only
          const update: any = { ...local }
          for (const field of SITE_SYNC_FIELDS) {
            if (field in remoteSite) update[field] = remoteSite[field]
          }
          await fetch(`${daemonBase()}/api/sites/${domain}`, {
            method: 'PUT',
            headers: authHeaders(),
            body: JSON.stringify(update),
          })
        } else {
          // New site: create with sync fields. DocumentRoot can't be empty
          // (SiteManager.ValidateDocumentRoot throws) so we use a clear
          // placeholder path that passes validation. The user replaces it
          // in SiteEdit → General → Document Root before the vhost works.
          const placeholder = navigator.platform?.startsWith('Win')
            ? `C:\\pending-sync\\${domain}`
            : `/tmp/pending-sync/${domain}`
          const newSite: any = { domain, documentRoot: placeholder }
          for (const field of SITE_SYNC_FIELDS) {
            if (field in remoteSite) newSite[field] = remoteSite[field]
          }
          try {
            await fetch(`${daemonBase()}/api/sites`, {
              method: 'POST',
              headers: authHeaders(),
              body: JSON.stringify(newSite),
            })
            newSiteCount++
          } catch { /* domain validation may reject invalid entries */ }
        }
      }

      if (newSiteCount > 0) {
        ElMessage.info(`${newSiteCount} new site(s) imported — set their document root in Sites`)
      }
    }

    syncStatus.value = { ok: true, message: `Pulled from cloud (${data.updated_at ?? 'unknown'})` }
    ElMessage.success('Configuration synced from cloud')
    await loadSettings()
  } catch (e: any) {
    syncStatus.value = { ok: false, message: `Pull failed: ${e.message}` }
    ElMessage.error(`Pull failed: ${e.message}`)
  } finally {
    pulling.value = false
  }
}

async function checkCloudExists() {
  checkingCloud.value = true
  syncStatus.value = null
  try {
    const url = (catalogUrl.value || 'http://127.0.0.1:8765').replace(/\/$/, '')
    const r = await fetch(`${url}/api/v1/sync/config/${deviceId.value}/exists`)
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    const data = await r.json()
    syncStatus.value = {
      ok: data.has_config,
      message: data.has_config
        ? `Cloud snapshot exists (updated ${data.updated_at ?? 'unknown'})`
        : 'No cloud snapshot for this device',
    }
  } catch (e: any) {
    syncStatus.value = { ok: false, message: `Check failed: ${e.message}` }
  } finally {
    checkingCloud.value = false
  }
}

async function exportSettings() {
  try {
    const payload = await buildSyncPayload()
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `nks-wdc-settings-${new Date().toISOString().slice(0, 10)}.json`
    a.click()
    URL.revokeObjectURL(url)
    ElMessage.success('Settings exported')
  } catch (e: any) {
    ElMessage.error(`Export failed: ${e.message}`)
  }
}

function triggerImport() {
  importFileInput.value?.click()
}

async function importSettings(event: Event) {
  const file = (event.target as HTMLInputElement)?.files?.[0]
  if (!file) return
  try {
    const text = await file.text()
    const data = JSON.parse(text)
    if (!data.settings || typeof data.settings !== 'object') {
      throw new Error('Invalid settings file — missing "settings" object')
    }
    const fromDevice = data.deviceId ?? 'unknown'
    const fromDate = data.exportedAt ? new Date(data.exportedAt).toLocaleString() : 'unknown'
    await ElMessageBox.confirm(
      `Import settings from "${file.name}"?\n\n`
      + `Source device: ${fromDevice}\n`
      + `Exported: ${fromDate}\n\n`
      + `Sync-classified settings (preferences, telemetry) will be applied.\n`
      + `Local settings (paths, ports) will be kept unchanged.\n`
      + (data.sites?.length ? `${data.sites.length} site(s) will be merged by domain.` : ''),
      'Import settings',
      { confirmButtonText: 'Import', type: 'warning' },
    )

    // Use the same merge logic as pullFromCloud — sync fields only
    const merged: Record<string, string> = {}
    for (const [key, value] of Object.entries(data.settings as Record<string, string>)) {
      if (isSettingSyncable(key)) merged[key] = value
    }
    if (Object.keys(merged).length > 0) {
      await fetch(`${daemonBase()}/api/settings`, {
        method: 'PUT',
        headers: authHeaders(),
        body: JSON.stringify(merged),
      })
    }
    ElMessage.success(`Imported ${Object.keys(merged).length} sync settings from ${file.name}`)
    await loadSettings()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error(`Import failed: ${e.message}`)
  }
  if (importFileInput.value) importFileInput.value.value = ''
}

onMounted(async () => {
  void loadSettings()
  void loadDatabases()
  void loadPhpVersions()
  void loadDeviceId()
  if (accountToken.value) void loadDevicesAccount()
  try {
    const r = await fetch(`${daemonBase()}/api/system`, { headers: authHeaders() })
    if (r.ok) systemInfo.value = await r.json()
  } catch { /* not connected */ }
})

async function save() {
  saving.value = true
  try {
    const payload: Record<string, string> = {
      'ports.http':          String(ports.http),
      'ports.https':         String(ports.https),
      'ports.mysql':         String(ports.mysql),
      'ports.redis':         String(ports.redis),
      'ports.mailpitSmtp':   String(ports.mailpitSmtp),
      'ports.mailpitHttp':   String(ports.mailpitHttp),
      'general.runOnStartup': String(runOnStartup.value),
      'general.autoStart':    String(autoStart.value),
      'paths.apache':   paths.apache,
      'paths.mysql':    paths.mysql,
      'paths.php':      paths.php,
      'paths.redis':    paths.redis,
      'paths.sitesDir': paths.sitesDir,
      'paths.hostsFile': paths.hostsFile,
      'ports.phpFpmBase': String(phpFpmBasePort.value),
      'daemon.catalogUrl': catalogUrl.value,
      'telemetry.enabled': String(telemetryEnabled.value),
      'telemetry.crashReports': String(telemetryCrashReports.value),
      'backup.dir': backupDir.value,
      'backup.scheduleHours': String(backupScheduleHours.value),
    }
    const r = await fetch(`${daemonBase()}/api/settings`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify(payload),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    ElMessage.success('Settings saved')
  } catch (e: any) {
    ElMessage.error(`Failed to save: ${e.message}`)
  } finally {
    saving.value = false
  }
}
</script>

<style scoped>
.settings-page {
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

.page-title {
  font-size: 1.25rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.page-subtitle {
  font-size: 0.82rem;
  color: var(--wdc-text-2);
  margin-top: 2px;
}

.page-body {
  padding: 0 24px 24px;
}

.settings-tabs {
  --el-tabs-header-height: 40px;
}

.tab-content {
  padding: 20px 0;
}

.tab-desc {
  font-size: 0.82rem;
  color: var(--el-text-color-secondary);
  margin-bottom: 20px;
  line-height: 1.5;
}

.mono-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
}

.hint {
  margin-top: 8px;
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  line-height: 1.5;
}
.hint code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface-2);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--wdc-accent);
  font-size: 0.74rem;
}
.status-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  margin-right: 6px;
  vertical-align: middle;
}
.status-dot.ok { background: var(--wdc-status-running); }
.status-dot.err { background: var(--wdc-status-error); }

.settings-footer {
  margin-top: 24px;
  padding-top: 16px;
  border-top: 1px solid var(--el-border-color);
  display: flex;
  gap: 8px;
}

.about-card {
  max-width: 420px;
  background: var(--wdc-surface);
  border: 1px solid var(--el-border-color);
  border-radius: 12px;
  padding: 28px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.about-logo {
  font-size: 1.5rem;
  font-weight: 800;
  letter-spacing: 0.06em;
  background: linear-gradient(135deg, #6366f1, #8b5cf6);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

.about-version {
  font-family: monospace;
  font-size: 0.85rem;
  color: var(--el-text-color-secondary);
}

.about-subtitle {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.about-desc {
  font-size: 0.82rem;
  color: var(--el-text-color-secondary);
  line-height: 1.6;
  margin-top: 4px;
}

.about-stack {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.about-system { margin-top: 16px; padding-top: 16px; border-top: 1px solid var(--wdc-border); }
.about-sys-title { font-size: 0.78rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; color: var(--wdc-text-3); margin-bottom: 8px; }
.about-sys-row { display: flex; justify-content: space-between; padding: 4px 0; font-size: 0.85rem; }
.sys-label { color: var(--wdc-text-2); }
.sys-value { color: var(--wdc-text); font-family: 'JetBrains Mono', monospace; }

.about-links { display: flex; gap: 12px; margin-top: 12px; }
.about-link {
  color: var(--wdc-accent);
  text-decoration: none;
  font-size: 0.85rem;
}
.about-link:hover { text-decoration: underline; }

.db-list { margin-bottom: 16px; }
.db-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-bottom: 1px solid var(--wdc-border);
}
.db-row:last-child { border-bottom: none; }
.db-name { font-family: 'JetBrains Mono', monospace; font-size: 0.88rem; color: var(--wdc-text); }
.db-create { display: flex; gap: 8px; margin-top: 12px; }

/* Sync tab */
.settings-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  margin-bottom: 16px;
  overflow: hidden;
}
.settings-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
}
.settings-card-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}
.settings-card-body { padding: 18px; }
.sync-actions { display: flex; gap: 8px; flex-wrap: wrap; }
.sync-badge {
  font-size: 0.72rem;
  font-weight: 600;
  padding: 2px 10px;
  border-radius: 10px;
}
.sync-ok { background: rgba(34, 197, 94, 0.15); color: var(--wdc-status-running); }
.sync-err { background: rgba(255, 107, 107, 0.15); color: var(--wdc-status-error); }
</style>
