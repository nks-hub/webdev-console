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
            </el-form>
          </div>
        </el-tab-pane>

        <!-- General tab -->
        <el-tab-pane label="General" name="general">
          <div class="tab-content">
            <p class="tab-desc">Application behavior and startup preferences.</p>
            <el-form label-position="left" label-width="180px" size="small" style="max-width: 440px">
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
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { useThemeStore, type ThemeMode } from '../../stores/theme'

const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'
const themeStore = useThemeStore()

const activeTab = ref('ports')
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
})

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
  } catch { /* daemon not reachable — keep defaults */ }
}

onMounted(async () => {
  void loadSettings()
  void loadDatabases()
  void loadPhpVersions()
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
</style>
