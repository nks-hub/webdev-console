<template>
  <div class="php-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('php.title') }}</h1>
        <span class="version-count" v-if="versions.length">{{ $t('php.installedCount', { n: versions.length }, versions.length) }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="loadVersions" :loading="loading" :icon="Refresh">{{ $t('common.refresh') }}</el-button>
        <el-button size="small" type="primary" :icon="Plus" @click="addVersion">{{ $t('php.addVersion') }}</el-button>
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
        style="margin-bottom: var(--wdc-space-4)"
      />

      <!-- Loading skeleton -->
      <div v-if="loading && versions.length === 0" style="padding: var(--wdc-space-5)">
        <el-skeleton :rows="3" animated />
      </div>

      <!-- Version cards -->
      <div class="version-grid" v-else-if="versions.length > 0">
        <div
          v-for="ver in versions"
          :key="ver.version"
          v-activate
          class="version-card"
          :class="{ active: selectedVersion === ver.version, default: isActive(ver) }"
          :aria-pressed="selectedVersion === ver.version"
          @click="selectVersion(ver)"
        >
          <div class="ver-header">
            <span class="ver-number">PHP {{ ver.majorMinor }}</span>
            <el-tag v-if="isActive(ver)" type="success" size="small" effect="plain">{{ $t('php.default') }}</el-tag>
          </div>
          <div class="ver-full">{{ ver.version }}</div>
          <div class="ver-path mono" :title="ver.directory ?? ver.path">{{ ver.directory ?? ver.path }}</div>
          <div class="ver-actions" v-if="!isActive(ver)">
            <el-button size="small" text @click.stop="setDefault(ver.version)">{{ $t('php.setDefault') }}</el-button>
          </div>
        </div>
      </div>

      <el-empty v-else-if="!loading" :description="$t('php.noVersions')" :image-size="64">
        <el-button type="primary" @click="addVersion">{{ $t('php.addVersion') }}</el-button>
      </el-empty>

      <div v-if="selectedVersion && configLoading" class="php-detail">
        <el-skeleton :rows="4" animated />
      </div>
      <el-alert
        v-else-if="selectedVersion && configError"
        type="warning"
        :title="configError"
        :closable="false"
        show-icon
      />

      <!-- Selected version detail -->
      <div v-else-if="selectedVersion && selectedConfig" class="php-detail">
        <div class="detail-header">
          <h2 class="detail-title">PHP {{ selectedVersion }}</h2>
          <div class="detail-actions">
            <el-button size="small" @click="loadConfig(selectedVersion)">{{ $t('php.refreshConfig') }}</el-button>
          </div>
        </div>

        <!-- Extensions -->
        <div class="extensions-section" v-if="extensions.length > 0">
          <div class="section-label">{{ $t('php.extensionsCount', { enabled: enabledCount, total: extensions.length }) }}</div>
          <div class="ext-grid">
            <div
              v-for="ext in extensions"
              :key="ext.name"
              class="ext-item"
              :class="{ enabled: ext.enabled }"
            >
              <span class="ext-name">
                {{ ext.name }}
                <el-tag v-if="ext.zend" size="small" type="info" effect="plain" class="ext-zend">Zend</el-tag>
              </span>
              <el-switch
                :model-value="ext.enabled"
                :loading="togglingExt === ext.name"
                :aria-label="$t('php.extensionAria', { name: ext.name })"
                size="small"
                @change="(val: string | number | boolean) => toggleExtension(ext.name, Boolean(val))"
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
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders, fetchPhpVersions } from '../../api/daemon'
import type { PhpVersion } from '../../api/types'
import { errorMessage } from '../../utils/errors'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const router = useRouter()

interface ExtInfo {
  name: string
  enabled: boolean
  /** Loaded with zend_extension= (Xdebug, OPcache). */
  zend: boolean
}

const versions = ref<PhpVersion[]>([])
const loading = ref(false)
const loadError = ref('')
const selectedVersion = ref('')
const selectedConfig = ref('')
const extensions = ref<ExtInfo[]>([])
const togglingExt = ref<string>('')
const configLoading = ref(false)
const configError = ref('')

// The daemon marks the version new sites get as `isActive`.
function isActive(ver: PhpVersion): boolean {
  return !!(ver.isActive ?? ver.isDefault)
}

function addVersion() {
  void router.push({ path: '/binaries', query: { app: 'php' } })
}

const enabledCount = computed(() => extensions.value.filter(e => e.enabled).length)


async function loadVersions() {
  loading.value = true
  loadError.value = ''
  try {
    versions.value = await fetchPhpVersions()
    // Open the version new sites use, so extensions and php.ini are visible
    // without first guessing that the card is clickable.
    const initial = versions.value.find(isActive) ?? versions.value[0]
    if (!selectedVersion.value && initial) selectVersion(initial)
  } catch (e) {
    loadError.value = t('php.daemonUnreachable', { err: errorMessage(e) })
  } finally { loading.value = false }
}

function selectVersion(ver: PhpVersion) {
  selectedVersion.value = ver.version
  void loadConfig(ver.version)
}

async function loadConfig(version: string) {
  selectedConfig.value = ''
  extensions.value = []
  configError.value = ''
  configLoading.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/services/php/config`, { headers: authHeaders() })
    if (!r.ok) {
      configError.value = t('php.configLoadFailed', { err: `HTTP ${r.status}` })
      return
    }
    const data: { files?: Array<{ name?: string; path?: string; content: string }> } = await r.json()
    // Each version keeps its php.ini in its own install folder, named by the full version.
    const file = data.files?.find(f => (f.path ?? '').split(/[\\/]/).includes(version))
      ?? data.files?.find(f => f.name?.includes(version) || f.path?.includes(version))
    if (!file) {
      configError.value = t('php.configNotFound', { version })
      return
    }
    selectedConfig.value = file.content
    extensions.value = [...parseExtensions(file.content), ...await availableNotInIni(version, file.content)]
  } catch (e) {
    configError.value = t('php.configLoadFailed', { err: errorMessage(e) })
  } finally {
    configLoading.value = false
  }
}

/**
 * Extension files in the version's ext folder that php.ini does not mention
 * yet, offered switched off so they can be enabled from here. Built-in
 * modules (no file) are left out: there is nothing to toggle.
 */
async function availableNotInIni(version: string, iniContent: string): Promise<ExtInfo[]> {
  const inIni = new Set(parseExtensions(iniContent).map(e => e.name.toLowerCase()))
  const majorMinor = version.split('.').slice(0, 2).join('.')
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/php/${encodeURIComponent(majorMinor)}/extensions`, { headers: authHeaders() })
    if (!r.ok) return []
    const list = await r.json() as Array<{ name: string; soFile?: string | null }>
    return list
      .filter(e => e.soFile && /^[a-z0-9_]{1,64}$/i.test(e.name) && !inIni.has(e.name.toLowerCase()))
      .map(e => ({ name: e.name, enabled: false, zend: e.name === 'xdebug' || e.name === 'opcache' }))
  } catch {
    return []
  }
}

/** extension= / zend_extension= lines of php.ini, by bare name (no path, php_ prefix or suffix). */
function parseExtensions(iniContent: string): ExtInfo[] {
  const exts: ExtInfo[] = []
  for (const line of iniContent.split('\n')) {
    const match = line.trim().match(/^(;?)\s*(zend_extension|extension)\s*=\s*"?([^"]+?)"?\s*$/)
    if (!match) continue
    const name = (match[3].split(/[\\/]/).pop() ?? '')
      .replace(/^php_/i, '')
      .replace(/\.(dll|so)$/i, '')
      .trim()
    if (!/^[a-z0-9_]{1,64}$/i.test(name) || exts.some(e => e.name === name)) continue
    exts.push({ name, enabled: match[1] !== ';', zend: match[2] === 'zend_extension' })
  }
  return exts
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
      `${daemonBaseUrl()}/api/php/${encodeURIComponent(majorMinor)}/extensions/${encodeURIComponent(name)}`,
      {
        method: 'POST',
        headers: authHeaders(),
        body: JSON.stringify({ enabled }),
      },
    )
    if (!r.ok) {
      const body = await r.json().catch(() => ({})) as { error?: string }
      ElMessage.error(t('toast.extToggleFailed', { name, err: body.error || `HTTP ${r.status}` }))
      return
    }
    // Optimistically update local state; daemon also restarted PHP so a
    // follow-up config reload picks up the real result.
    const ext = extensions.value.find((e) => e.name === name)
    if (ext) ext.enabled = enabled
    ElMessage.success(t(enabled ? 'toast.extEnabledRestarted' : 'toast.extDisabledRestarted', { name }))
    // Reload config after a brief pause so the ini content matches the new state.
    setTimeout(() => { void loadConfig(selectedVersion.value) }, 400)
  } catch (e) {
    ElMessage.error(t('toast.toggleFailed', { err: errorMessage(e) }))
  } finally {
    togglingExt.value = ''
  }
}

async function setDefault(version: string) {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/php/default`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify({ version }),
    })
    if (r.ok) {
      ElMessage.success(t('toast.phpDefaultSet', { version }))
      await loadVersions()
    } else {
      // Surface the daemon's reason (version not installed, plugin missing)
      // instead of a generic "Failed to set default" dead-end.
      const body = await r.json().catch(() => ({})) as { error?: string }
      ElMessage.error(t('toast.setDefaultFailed', { err: body.error || `HTTP ${r.status}` }))
    }
  } catch (e) {
    ElMessage.error(t('toast.setDefaultFailed', { err: errorMessage(e) }))
  }
}

onMounted(() => { void loadVersions() })
</script>

<style scoped>
.php-page {
  min-height: 100%;
  background: transparent;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 0 var(--wdc-space-4);
  margin-bottom: var(--wdc-space-4-5);
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
}

.header-left { display: flex; align-items: center; gap: 12px; }
.header-actions { display: flex; align-items: center; gap: 8px; }

.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  letter-spacing: -0.02em;
  margin: 0;
}

.version-count {
  font-size: 0.76rem;
  color: var(--wdc-text-3);
}

.page-body { padding: 0 0 24px; }

/* Version grid */
.version-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: var(--wdc-space-2-5);
  margin-bottom: var(--wdc-space-4-5);
}

.version-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-3-5) var(--wdc-space-4-5);
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
  margin-bottom: var(--wdc-space-1);
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
  margin-top: var(--wdc-space-1);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.ver-actions { margin-top: 8px; }

.mono { font-family: 'JetBrains Mono', monospace; }

/* Detail */
.php-detail {
  border-top: 1px solid var(--wdc-border);
  padding-top: var(--wdc-space-4-5);
}

.detail-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: var(--wdc-space-4);
}

.detail-title {
  font-size: 1rem;
  font-weight: 600;
  color: var(--wdc-text);
}

.section-label {
  font-size: 0.82rem;
  font-weight: 500;
  color: var(--wdc-text-3);
  margin-bottom: var(--wdc-space-2-5);
  font-family: var(--wdc-font-display);
}

/* Extensions grid */
.extensions-section { margin-bottom: 20px; }

.ext-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
  gap: var(--wdc-space-1);
}

.ext-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--wdc-space-1-5) var(--wdc-space-2-5);
  border-radius: var(--wdc-radius-sm);
  font-size: 0.78rem;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
}

.ext-item.enabled {
  border-color: color-mix(in oklab, var(--wdc-status-running) 35%, var(--wdc-border));
}

.ext-zend { margin-left: var(--wdc-space-1); }

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
  padding: var(--wdc-space-3-5);
  max-height: 500px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
