<template>
  <div class="cf-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('phpPlugin.title') }}</h1>
        <span class="page-subtitle">{{ $t('phpPlugin.subtitle') }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="loadVersions" :loading="loading">{{ $t('common.refresh') }}</el-button>
      </div>
    </div>
    <div class="page-autostart-row">
      <PluginAutostartSwitch plugin-id="nks.wdc.php" />
    </div>

    <div class="status-strip">
      <div class="status-card">
        <el-icon class="status-icon"><Cpu /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ defaultVersion || '—' }}</div>
          <div class="status-meta">{{ $t('phpPlugin.defaultVersion') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><List /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ versions.length }} {{ $t('phpPlugin.versionsInstalled') }}</div>
          <div class="status-meta">{{ $t('phpPlugin.versionsMeta') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Grid /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ enabledExtCount }} {{ $t('phpPlugin.extensions') }}</div>
          <div class="status-meta">{{ $t('phpPlugin.extensionsMeta') }}</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- Overview -->
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('phpPlugin.tabOverview') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('phpPlugin.tabOverview') }}</span>
            </header>
            <div class="edit-card-body">
              <el-descriptions :column="2" border size="small">
                <el-descriptions-item :label="$t('phpPlugin.defaultVersion')">
                  <span class="mono">{{ defaultVersion || '—' }}</span>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('phpPlugin.versionsInstalled')">{{ versions.length }}</el-descriptions-item>
                <el-descriptions-item :label="$t('phpPlugin.fpmStatus')">
                  <el-tag size="small" type="info" effect="plain">{{ $t('phpPlugin.fpmUnknown') }}</el-tag>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('phpPlugin.extensionsLoaded')">{{ enabledExtCount }}</el-descriptions-item>
              </el-descriptions>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Versions -->
      <el-tab-pane name="versions">
        <template #label>
          <span class="tab-label"><el-icon><Cpu /></el-icon> {{ $t('phpPlugin.tabVersions') }}</span>
        </template>
        <div class="tab-content">
          <el-alert v-if="loadError" type="error" :title="loadError" :closable="true" @close="loadError = ''" show-icon />
          <div v-if="loading && versions.length === 0" style="padding: 24px">
            <el-skeleton :rows="3" animated />
          </div>
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
            </div>
          </div>
          <el-empty v-else-if="!loading" :description="$t('php.noVersions')" :image-size="64" />
        </div>
      </el-tab-pane>

      <!-- Extensions -->
      <el-tab-pane name="extensions">
        <template #label>
          <span class="tab-label"><el-icon><Grid /></el-icon> {{ $t('phpPlugin.tabExtensions') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('phpPlugin.tabExtensions') }}</span>
              <span class="edit-card-hint">{{ selectedVersion ? `PHP ${selectedVersion}` : $t('phpPlugin.selectVersion') }}</span>
            </header>
            <div class="edit-card-body">
              <div v-if="!selectedVersion" class="hint">{{ $t('phpPlugin.selectVersionHint') }}</div>
              <div v-else-if="extensions.length === 0" class="hint">{{ $t('phpPlugin.noExtensions') }}</div>
              <div v-else class="ext-grid">
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
          </section>
        </div>
      </el-tab-pane>

      <!-- INI -->
      <el-tab-pane name="ini">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('phpPlugin.tabIni') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">php.ini</span>
              <span class="edit-card-hint">{{ selectedVersion ? `PHP ${selectedVersion}` : $t('phpPlugin.selectVersion') }}</span>
            </header>
            <div class="edit-card-body" style="padding: 0">
              <div v-if="!selectedConfig" class="hint" style="padding: 18px 20px">{{ $t('phpPlugin.selectVersionHint') }}</div>
              <pre v-else class="config-pre">{{ selectedConfig }}</pre>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Logs -->
      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Files /></el-icon> {{ $t('phpPlugin.tabLogs') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('phpPlugin.tabLogs') }}</span>
            </header>
            <div class="edit-card-body" style="padding: 0">
              <LogViewer :service-id="'php'" />
            </div>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Cpu, List, Grid, Monitor, Document, Files } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders, fetchPhpVersions } from '../../api/daemon'
import type { PhpVersion } from '../../api/types'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'

defineOptions({ name: 'PhpPluginPage' })

interface ExtInfo { name: string; enabled: boolean }

const activeTab = ref<'overview' | 'versions' | 'extensions' | 'ini' | 'logs'>('overview')
const versions = ref<PhpVersion[]>([])
const loading = ref(false)
const loadError = ref('')
const selectedVersion = ref('')
const selectedConfig = ref('')
const extensions = ref<ExtInfo[]>([])
const togglingExt = ref<string>('')

const defaultVersion = computed(() => versions.value.find(v => v.isDefault)?.version ?? '')
const enabledExtCount = computed(() => extensions.value.filter(e => e.enabled).length)

async function loadVersions() {
  loading.value = true
  loadError.value = ''
  try {
    versions.value = await fetchPhpVersions()
    const def = versions.value.find(v => v.isDefault)
    if (def && !selectedVersion.value) selectVersion(def)
  } catch (e) {
    loadError.value = `Cannot connect to daemon: ${errorMessage(e)}`
  } finally {
    loading.value = false
  }
}

function selectVersion(ver: PhpVersion) {
  selectedVersion.value = ver.version
  void loadConfig(ver.version)
}

async function loadConfig(version: string) {
  selectedConfig.value = ''
  extensions.value = []
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/services/php/config`, { headers: authHeaders() })
    if (r.ok) {
      const data: { files?: Array<{ name?: string; path?: string; content: string }> } = await r.json()
      const file = data.files?.find(f => f.name?.includes(version) || f.path?.includes(version))
      if (file) {
        selectedConfig.value = file.content
        parseExtensions(file.content)
      }
    }
  } catch { /* skip */ }
}

function parseExtensions(iniContent: string) {
  const exts: ExtInfo[] = []
  for (const line of iniContent.split('\n')) {
    const trimmed = line.trim()
    const match = trimmed.match(/^(;?)extension=(.+)$/)
    if (match) exts.push({ name: match[2].replace(/\.dll$|\.so$/, '').trim(), enabled: match[1] !== ';' })
    const zend = trimmed.match(/^(;?)zend_extension=(.+)$/)
    if (zend) exts.push({ name: `[zend] ${zend[2].replace(/\.dll$|\.so$/, '').trim()}`, enabled: zend[1] !== ';' })
  }
  extensions.value = exts
}

async function toggleExtension(name: string, enabled: boolean) {
  if (!selectedVersion.value) return
  togglingExt.value = name
  try {
    const cleanName = name.replace('[zend] ', '')
    const r = await fetch(`${daemonBaseUrl()}/api/php/${selectedVersion.value}/extensions/${cleanName}`, {
      method: 'PUT',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    const ext = extensions.value.find(e => e.name === name)
    if (ext) ext.enabled = enabled
    ElMessage.success(`${cleanName} ${enabled ? 'enabled' : 'disabled'}`)
  } catch (e) {
    ElMessage.error(`Toggle failed: ${errorMessage(e)}`)
  } finally {
    togglingExt.value = ''
  }
}

onMounted(() => { void loadVersions() })
</script>

<style scoped>
.cf-page { min-height: 100%; background: var(--wdc-bg); padding: 0; }
.page-header { display: flex; align-items: center; justify-content: space-between; padding: 20px 24px 14px; border-bottom: 1px solid var(--wdc-border); }
.page-autostart-row { padding: 10px 24px 0; max-width: 720px; }
.header-left { display: flex; flex-direction: column; gap: 2px; }
.page-title { font-size: 1.25rem; font-weight: 800; color: var(--wdc-text); margin: 0; }
.page-subtitle { font-size: 0.78rem; color: var(--wdc-text-3); }
.header-actions { display: flex; gap: 8px; }
.status-strip { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; padding: 18px 24px 4px; }
.status-card { display: flex; align-items: center; gap: 12px; padding: 14px 16px; background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); }
.status-icon { font-size: 1.4rem; width: 30px; text-align: center; color: var(--wdc-text-3); }
.status-body { display: flex; flex-direction: column; min-width: 0; }
.status-title { font-size: 0.92rem; font-weight: 700; color: var(--wdc-text); }
.status-meta { font-size: 0.72rem; color: var(--wdc-text-3); }
.cf-tabs { padding: 16px 24px; }
.tab-content { display: flex; flex-direction: column; gap: 16px; }
.edit-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); overflow: hidden; }
.edit-card-header { padding: 14px 20px; background: var(--wdc-surface-2); border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: baseline; }
.edit-card-title { font-size: 0.78rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: var(--wdc-text); }
.edit-card-hint { font-size: 0.75rem; color: var(--wdc-text-3); }
.edit-card-body { padding: 18px 20px; }
.hint { margin-top: 6px; font-size: 0.78rem; color: var(--wdc-text-3); }
.mono { font-family: 'JetBrains Mono', monospace; }
.version-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 12px; }
.version-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); padding: 14px 16px; cursor: pointer; transition: border-color 0.15s; }
.version-card:hover { border-color: var(--wdc-accent); }
.version-card.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); }
.ver-header { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
.ver-number { font-weight: 700; font-size: 0.95rem; color: var(--wdc-text); }
.ver-full { font-size: 0.78rem; color: var(--wdc-text-2); }
.ver-path { font-size: 0.68rem; color: var(--wdc-text-3); margin-top: 4px; word-break: break-all; }
.ext-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 8px; }
.ext-item { display: flex; align-items: center; justify-content: space-between; padding: 8px 12px; background: var(--wdc-surface-2); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius-sm); }
.ext-item.enabled { border-color: var(--wdc-accent); }
.ext-name { font-size: 0.82rem; font-family: 'JetBrains Mono', monospace; color: var(--wdc-text); }
.config-pre { margin: 0; padding: 18px 20px; font-family: 'JetBrains Mono', monospace; font-size: 0.78rem; color: var(--wdc-text-2); background: var(--wdc-surface-2); overflow: auto; max-height: 500px; white-space: pre-wrap; word-break: break-all; }
</style>
