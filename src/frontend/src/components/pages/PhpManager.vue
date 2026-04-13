<template>
  <div class="php-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('php.title') }}</h1>
        <span class="version-count" v-if="versions.length">{{ versions.length }} {{ $t('binaries.installed') }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="loadVersions" :loading="loading">{{ $t('common.refresh') }}</el-button>
      </div>
    </div>

    <div class="page-body">
      <el-alert
        v-if="loadError"
        type="error"
        :title="loadError"
        :closable="true"
        @close="loadError = ''"
        show-icon
        style="margin-bottom: 16px"
      />

      <!-- Loading skeleton -->
      <div v-if="loading && versions.length === 0" style="padding: 24px">
        <el-skeleton :rows="3" animated />
      </div>

      <!-- Version cards -->
      <div class="version-grid" v-else-if="versions.length > 0">
        <div
          v-for="ver in versions"
          :key="ver.version"
          class="version-card"
          :class="{ active: selectedVersion === ver.version, default: ver.isDefault }"
          @click="selectVersion(ver)"
        >
          <div class="ver-header">
            <span class="ver-number">PHP {{ ver.majorMinor }}</span>
            <el-tag v-if="ver.isDefault" type="success" size="small" effect="plain">default</el-tag>
          </div>
          <div class="ver-full">{{ ver.version }}</div>
          <div class="ver-path mono">{{ ver.path }}</div>
          <div class="ver-actions" v-if="!ver.isDefault">
            <el-button size="small" text @click.stop="setDefault(ver.version)">Set Default</el-button>
          </div>
        </div>
      </div>

      <el-empty v-else-if="!loading" description="No PHP versions installed" :image-size="64" />

      <!-- Selected version detail -->
      <div v-if="selectedVersion && selectedConfig" class="php-detail">
        <div class="detail-header">
          <h2 class="detail-title">PHP {{ selectedVersion }}</h2>
          <div class="detail-actions">
            <el-button size="small" @click="loadConfig(selectedVersion)">Refresh Config</el-button>
          </div>
        </div>

        <!-- Extensions -->
        <div class="extensions-section" v-if="extensions.length > 0">
          <div class="section-label">Extensions ({{ enabledCount }}/{{ extensions.length }} enabled)</div>
          <div class="ext-grid">
            <div
              v-for="ext in extensions"
              :key="ext.name"
              class="ext-item"
              :class="{ enabled: ext.enabled }"
            >
              <span class="ext-name">{{ ext.name }}</span>
              <el-switch
                :model-value="ext.enabled"
                :loading="togglingExt === ext.name"
                size="small"
                inline-prompt
                active-text="ON"
                inactive-text="OFF"
                @change="(val: boolean) => toggleExtension(ext.name, val)"
              />
            </div>
          </div>
        </div>

        <!-- php.ini content -->
        <div class="config-section">
          <div class="section-label">php.ini</div>
          <pre class="config-pre">{{ selectedConfig }}</pre>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'

interface PhpVersion {
  version: string
  majorMinor: string
  path: string
  isDefault: boolean
  extensions?: string[]
}

interface ExtInfo {
  name: string
  enabled: boolean
}

const versions = ref<PhpVersion[]>([])
const loading = ref(false)
const loadError = ref('')
const selectedVersion = ref('')
const selectedConfig = ref('')
const extensions = ref<ExtInfo[]>([])
const togglingExt = ref<string>('')

const enabledCount = computed(() => extensions.value.filter(e => e.enabled).length)

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

async function loadVersions() {
  loading.value = true
  loadError.value = ''
  try {
    const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: authHeaders() })
    if (r.ok) {
      versions.value = await r.json()
    } else {
      loadError.value = `Failed to load PHP versions: HTTP ${r.status}`
    }
  } catch (e: any) {
    loadError.value = `Cannot connect to daemon: ${e?.message || e}`
  } finally { loading.value = false }
}

function selectVersion(ver: PhpVersion) {
  selectedVersion.value = ver.version
  void loadConfig(ver.version)
}

async function loadConfig(version: string) {
  selectedConfig.value = ''
  extensions.value = []
  try {
    // Load php.ini from config endpoint
    const r = await fetch(`${daemonBase()}/api/services/php/config`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      const file = data.files?.find((f: any) => f.name?.includes(version) || f.path?.includes(version))
      if (file) {
        selectedConfig.value = file.content
        // Parse extensions from php.ini
        parseExtensions(file.content)
      }
    }
  } catch { /* skip */ }
}

function parseExtensions(iniContent: string) {
  const exts: ExtInfo[] = []
  const lines = iniContent.split('\n')
  for (const line of lines) {
    const trimmed = line.trim()
    // extension=name or ;extension=name (disabled)
    const match = trimmed.match(/^(;?)extension=(.+)$/)
    if (match) {
      const enabled = match[1] !== ';'
      const name = match[2].replace(/\.dll$|\.so$/, '').trim()
      exts.push({ name, enabled })
    }
    // zend_extension=name
    const zendMatch = trimmed.match(/^(;?)zend_extension=(.+)$/)
    if (zendMatch) {
      const enabled = zendMatch[1] !== ';'
      const name = zendMatch[2].replace(/\.dll$|\.so$/, '').trim()
      exts.push({ name: `[zend] ${name}`, enabled })
    }
  }
  extensions.value = exts
}

async function toggleExtension(name: string, enabled: boolean) {
  if (!selectedVersion.value) return
  // Derive major.minor from the full version so the endpoint can locate the
  // installation (path is keyed by the full version directory, e.g. 8.4.20,
  // but the endpoint accepts the major.minor form and globs matching dirs).
  const majorMinor = selectedVersion.value.split('.').slice(0, 2).join('.')
  togglingExt.value = name
  try {
    const r = await fetch(
      `${daemonBase()}/api/php/${encodeURIComponent(majorMinor)}/extensions/${encodeURIComponent(name)}`,
      {
        method: 'POST',
        headers: authHeaders(),
        body: JSON.stringify({ enabled }),
      },
    )
    if (!r.ok) {
      const text = await r.text().catch(() => r.statusText)
      ElMessage.error(`Failed to toggle ${name}: ${text}`)
      return
    }
    // Optimistically update local state; daemon also restarted PHP so a
    // follow-up config reload picks up the real result.
    const ext = extensions.value.find((e) => e.name === name)
    if (ext) ext.enabled = enabled
    ElMessage.success(`${name} ${enabled ? 'enabled' : 'disabled'} — PHP restarted`)
    // Reload config after a brief pause so the ini content matches the new state.
    setTimeout(() => { void loadConfig(selectedVersion.value) }, 400)
  } catch (e: any) {
    ElMessage.error(`Toggle failed: ${e?.message || e}`)
  } finally {
    togglingExt.value = ''
  }
}

async function setDefault(version: string) {
  try {
    const r = await fetch(`${daemonBase()}/api/php/default`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify({ version }),
    })
    if (r.ok) {
      ElMessage.success(`PHP ${version} set as default`)
      await loadVersions()
    } else {
      // Surface the daemon's response body so the user can see WHY the
      // operation failed (permission denied, invalid version, file locked…)
      // instead of a generic "Failed to set default" dead-end.
      const text = await r.text().catch(() => r.statusText)
      ElMessage.error(`Failed to set default: ${text || `HTTP ${r.status}`}`)
    }
  } catch (e: any) {
    ElMessage.error(`Cannot set default: ${e?.message || e}`)
  }
}

onMounted(() => { void loadVersions() })
</script>

<style scoped>
.php-page {
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

.header-left { display: flex; align-items: center; gap: 10px; }
.header-actions { display: flex; align-items: center; gap: 8px; }

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.version-count {
  font-size: 0.72rem;
  font-weight: 600;
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  padding: 2px 8px;
  border-radius: 10px;
  font-family: 'JetBrains Mono', monospace;
}

.page-body { padding: 0 24px 24px; }

/* Version grid */
.version-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
  margin-bottom: 20px;
}

.version-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 14px 18px;
  cursor: pointer;
  transition: all 0.15s;
}

.version-card:hover { border-color: var(--wdc-border-strong); }
.version-card.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); }
.version-card.default { border-left: 3px solid var(--wdc-status-running); }

.ver-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 4px;
}

.ver-number {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.ver-full {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
  font-family: 'JetBrains Mono', monospace;
}

.ver-path {
  font-size: 0.7rem;
  color: var(--wdc-text-3);
  margin-top: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.ver-actions { margin-top: 8px; }

.mono { font-family: 'JetBrains Mono', monospace; }

/* Detail */
.php-detail {
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
}

.section-label {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--wdc-text-3);
  margin-bottom: 10px;
}

/* Extensions grid */
.extensions-section { margin-bottom: 20px; }

.ext-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
  gap: 4px;
}

.ext-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 10px;
  border-radius: var(--wdc-radius-sm);
  font-size: 0.78rem;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
}

.ext-item.enabled {
  border-color: rgba(34, 197, 94, 0.2);
}

.ext-name {
  color: var(--wdc-text);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.75rem;
}

.ext-status {
  font-size: 0.68rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.ext-item.enabled .ext-status { color: var(--wdc-status-running); }
.ext-item:not(.enabled) .ext-status { color: var(--wdc-text-3); }

/* Config */
.config-section { margin-top: 16px; }

.config-pre {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
  line-height: 1.5;
  color: var(--wdc-text-2);
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  padding: 14px;
  max-height: 500px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
