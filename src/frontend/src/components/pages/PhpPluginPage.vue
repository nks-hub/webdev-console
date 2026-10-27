<template>
  <div class="wdc-page">
    <ServicePageHead
      service-id="php"
      plugin-id="nks.wdc.php"
      :title="t('phpPlugin.title')"
      :subtitle="t('phpPlugin.subtitle')"
    >
      <template #facts>
        <div><dt>{{ t('phpPlugin.defaultVersion') }}</dt><dd class="mono">{{ defaultVersion || t('phpPlugin.noDefault') }}</dd></div>
        <div><dt>{{ t('phpPlugin.installedVersions') }}</dt><dd>{{ t('phpPlugin.versionsCount', { n: versions.length }, versions.length) }}</dd></div>
      </template>
    </ServicePageHead>

    <el-alert v-if="loadError" type="error" :title="loadError" :closable="true" show-icon @close="loadError = ''" />

    <el-tabs v-if="installed" v-model="activeTab">
      <el-tab-pane name="versions" :label="t('phpPlugin.tabVersions')">
        <div v-if="loading && versions.length === 0" class="pad">
          <el-skeleton :rows="3" animated />
        </div>
        <div v-else-if="versions.length > 0" class="version-grid">
          <div
            v-for="ver in versions"
            :key="ver.version"
            class="version-card"
            :class="{ active: selectedVersion === ver.version }"
          >
            <span class="ver-header">
              <button
                type="button"
                class="ver-select"
                :aria-current="selectedVersion === ver.version ? 'true' : undefined"
                @click="selectVersion(ver)"
              >PHP {{ ver.majorMinor }}</button>
              <el-tag v-if="ver.isDefault" type="success" size="small" effect="plain">{{ t('php.default') }}</el-tag>
            </span>
            <span class="ver-full mono">{{ ver.version }}</span>
            <span class="ver-path mono">{{ ver.path }}</span>
            <el-button v-if="!ver.isDefault" size="small" text :loading="settingDefault === ver.version" @click="setDefault(ver.version)">
              {{ t('php.setDefault') }}
            </el-button>
          </div>
        </div>
        <el-empty v-else-if="!loading" :description="t('php.noVersions')" :image-size="64" />
      </el-tab-pane>

      <el-tab-pane name="extensions" :label="t('phpPlugin.tabExtensions')" lazy>
        <p class="hint">{{ selectedVersion ? `PHP ${selectedVersion}` : t('phpPlugin.selectVersionHint') }}</p>
        <p v-if="selectedVersion && extensions.length === 0" class="hint">{{ t('phpPlugin.noExtensions') }}</p>
        <div v-else-if="selectedVersion" class="ext-grid">
          <div v-for="ext in extensions" :key="ext.name" class="ext-item" :class="{ enabled: ext.enabled }">
            <span class="ext-name">{{ ext.name }}</span>
            <el-switch
              :model-value="ext.enabled"
              :loading="togglingExt === ext.name"
              :aria-label="ext.name"
              size="small"
              @change="(val: string | number | boolean) => toggleExtension(ext.name, Boolean(val))"
            />
          </div>
        </div>
      </el-tab-pane>

      <el-tab-pane name="ini" label="php.ini" lazy>
        <p v-if="!selectedConfig" class="hint">{{ t('phpPlugin.selectVersionHint') }}</p>
        <pre v-else class="config-pre mono">{{ selectedConfig }}</pre>
      </el-tab-pane>

      <el-tab-pane name="logs" :label="t('phpPlugin.tabLogs')" lazy>
        <LogViewer service-id="php" class="tab-log" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onActivated, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders, fetchPhpVersions } from '../../api/daemon'
import type { PhpVersion } from '../../api/types'
import { useDaemonStore } from '../../stores/daemon'
import { errorMessage } from '../../utils/errors'
import { stateOf } from '../../utils/services'
import LogViewer from '../shared/LogViewer.vue'
import ServicePageHead from '../shared/ServicePageHead.vue'

defineOptions({ name: 'PhpPluginPage' })

interface ExtInfo { name: string; enabled: boolean }

const { t } = useI18n()
const daemonStore = useDaemonStore()
const activeTab = ref<'versions' | 'extensions' | 'ini' | 'logs'>('versions')
const versions = ref<PhpVersion[]>([])
const loading = ref(false)
const loadError = ref('')
const selectedVersion = ref('')
const selectedConfig = ref('')
const extensions = ref<ExtInfo[]>([])
const togglingExt = ref('')
const settingDefault = ref('')

const installed = computed(() => stateOf(daemonStore.services.find(s => s.id === 'php')) !== 'notInstalled')
const defaultVersion = computed(() => versions.value.find(v => v.isDefault)?.version ?? '')

async function loadVersions() {
  loading.value = true
  loadError.value = ''
  try {
    versions.value = await fetchPhpVersions()
    // Keep the user's pick; otherwise the default, or the only one there is.
    const keep = versions.value.find(v => v.version === selectedVersion.value)
    const pick = keep ?? versions.value.find(v => v.isDefault) ?? versions.value[0]
    if (pick) selectVersion(pick)
  } catch (e) {
    loadError.value = t('phpPlugin.loadFailed', { err: errorMessage(e) })
  } finally {
    loading.value = false
  }
}

function selectVersion(ver: PhpVersion) {
  selectedVersion.value = ver.version
  void loadConfig(ver.version)
}

async function loadConfig(version: string) {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/services/php/config`, { headers: authHeaders() })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    const data: { files?: Array<{ name?: string; path?: string; content: string }> } = await r.json()
    const file = data.files?.find(f => f.name?.includes(version) || f.path?.includes(version))
    selectedConfig.value = file?.content ?? ''
    extensions.value = file ? parseExtensions(file.content) : []
  } catch (e) {
    selectedConfig.value = ''
    extensions.value = []
    loadError.value = t('phpPlugin.configFailed', { err: errorMessage(e) })
  }
}

function parseExtensions(iniContent: string): ExtInfo[] {
  const exts: ExtInfo[] = []
  for (const line of iniContent.split('\n')) {
    const trimmed = line.trim()
    const match = trimmed.match(/^(;?)extension=(.+)$/)
    if (match) exts.push({ name: match[2].replace(/\.dll$|\.so$/, '').trim(), enabled: match[1] !== ';' })
    const zend = trimmed.match(/^(;?)zend_extension=(.+)$/)
    if (zend) exts.push({ name: `[zend] ${zend[2].replace(/\.dll$|\.so$/, '').trim()}`, enabled: zend[1] !== ';' })
  }
  return exts
}

async function toggleExtension(name: string, enabled: boolean) {
  if (!selectedVersion.value) return
  // The endpoint takes major.minor and finds the install directory itself.
  const majorMinor = selectedVersion.value.split('.').slice(0, 2).join('.')
  const cleanName = name.replace('[zend] ', '')
  togglingExt.value = name
  try {
    const r = await fetch(
      `${daemonBaseUrl()}/api/php/${encodeURIComponent(majorMinor)}/extensions/${encodeURIComponent(cleanName)}`,
      { method: 'POST', headers: authHeaders(), body: JSON.stringify({ enabled }) },
    )
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    const ext = extensions.value.find(e => e.name === name)
    if (ext) ext.enabled = enabled
    ElMessage.success(t(enabled ? 'toast.extEnabledRestarted' : 'toast.extDisabledRestarted', { name: cleanName }))
    void loadConfig(selectedVersion.value)
  } catch (e) {
    ElMessage.error(t('toast.extToggleFailed', { name: cleanName, err: errorMessage(e) }))
  } finally {
    togglingExt.value = ''
  }
}

async function setDefault(version: string) {
  settingDefault.value = version
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/php/default`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify({ version }),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    ElMessage.success(t('toast.phpDefaultSet', { version }))
    await loadVersions()
  } catch (e) {
    ElMessage.error(t('toast.setDefaultFailed', { err: errorMessage(e) }))
  } finally {
    settingDefault.value = ''
  }
}

// Also runs on first mount: every page lives inside <keep-alive>, and a
// version installed meanwhile shows up when the user comes back.
onActivated(() => { void loadVersions() })
</script>

<style scoped>
.pad { padding: var(--wdc-space-5); }
.hint { margin: 0 0 var(--wdc-space-3); font-size: 0.82rem; color: var(--wdc-text-3); }
.tab-log { height: 460px; }
.version-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: var(--wdc-space-3); }
.version-card {
  display: grid;
  gap: var(--wdc-space-1);
  justify-items: start;
  padding: var(--wdc-space-3) var(--wdc-space-4);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface);
}
.version-card.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); }
.ver-header { display: flex; align-items: center; gap: var(--wdc-space-2); }
.ver-select {
  padding: 0;
  border: 0;
  background: none;
  color: var(--wdc-text);
  font: inherit;
  font-family: var(--wdc-font-display);
  font-weight: 600;
  cursor: pointer;
}
@media (hover: hover) { .ver-select:hover { color: var(--wdc-accent); } }
.ver-full { font-size: 0.78rem; color: var(--wdc-text-2); }
.ver-path { font-size: 0.7rem; color: var(--wdc-text-3); word-break: break-all; }
.version-card :deep(.el-button) { padding-left: 0; }
.ext-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: var(--wdc-space-2); }
.ext-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.ext-item.enabled { border-color: var(--wdc-accent); }
.ext-name { font-size: 0.82rem; font-family: var(--wdc-font-mono); }
.config-pre {
  margin: 0;
  max-height: 520px;
  overflow: auto;
  padding: var(--wdc-space-4);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-log-bg);
  color: var(--wdc-log-text);
  font-size: 0.78rem;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
