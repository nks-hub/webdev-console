<template>
  <div class="wdc-page svc-config-page">
    <header class="page-head">
      <div>
        <el-button size="small" text class="back-link" @click="goBack">
          <el-icon aria-hidden="true"><ArrowLeft /></el-icon>
          <span>{{ t('services.back') }}</span>
        </el-button>
        <h1 class="page-title">{{ displayName }}</h1>
        <p class="page-sub">{{ t('services.title') }}</p>
      </div>
      <div v-if="files.length > 0" class="header-actions">
        <el-button size="small" :loading="loading" @click="reload">
          <el-icon aria-hidden="true"><Refresh /></el-icon>
          <span>{{ t('services.reload') }}</span>
        </el-button>
        <el-button
          type="primary"
          size="small"
          :loading="saving"
          :disabled="!dirty || !activeFile"
          @click="saveCurrent"
        >
          {{ t('serviceConfig.saveApply') }}
        </el-button>
      </div>
    </header>

    <div v-if="loading" class="state-box">
      <el-skeleton :rows="8" animated />
    </div>

    <!-- The binary is missing: say so and offer the install, not file paths. -->
    <div v-else-if="notInstalled" class="state-box state-text">
      <p>{{ t('serviceConfig.notInstalled', { name: displayName }) }}</p>
      <el-button v-if="service" type="primary" @click="router.push(installRouteOf(service))">
        {{ t('servicesPage.install') }}
      </el-button>
    </div>

    <div v-else-if="files.length === 0" class="state-box state-text">
      <template v-if="isConfigless">
        <p>{{ t('services.configlessSummary', { name: displayName }) }}</p>
        <p class="empty-hint">{{ t('services.configless.hint') }}</p>
      </template>
      <template v-else>
        <p>{{ t('services.noConfigSummary', { name: displayName }) }}</p>
        <p class="empty-hint">{{ t('serviceConfig.noConfigHint') }}</p>
        <details class="empty-details">
          <summary>{{ t('serviceConfig.details') }}</summary>
          <p class="empty-hint">{{ t('serviceConfig.lookedIn', { paths: expectedPathsHint }) }}</p>
        </details>
        <el-button size="small" :loading="loading" @click="load">{{ t('services.reload') }}</el-button>
      </template>
    </div>

    <div v-else class="config-body">
      <el-tabs v-model="activeFilePath" class="file-tabs" @tab-change="onTabChange">
        <el-tab-pane
          v-for="file in files"
          :key="file.path"
          :name="file.path"
        >
          <template #label>
            <span class="tab-label">
              <el-icon aria-hidden="true"><Document /></el-icon>
              <span>{{ file.name }}</span>
              <span v-if="dirtyFiles.has(file.path)" class="dirty-dot" :title="t('serviceConfig.unsaved')" />
            </span>
          </template>
        </el-tab-pane>
      </el-tabs>

      <div v-if="activeFile" class="editor-wrap">
        <!-- WDC rewrites these files from its own settings (sites, ports,
             PHP versions); a hand edit is lost on the next change there. -->
        <el-alert
          v-if="isGenerated"
          type="warning"
          :closable="false"
          show-icon
          :title="t('serviceConfig.generatedTitle')"
          :description="t('serviceConfig.generatedHint')"
        />
        <div class="file-meta">
          <div class="meta-row">
            <span class="meta-label">{{ t('serviceConfig.meta.path') }}</span>
            <code class="meta-value mono">{{ activeFile.path }}</code>
          </div>
          <div class="meta-row">
            <span class="meta-label">{{ t('serviceConfig.meta.size') }}</span>
            <span class="meta-value mono">{{ contentSize }}</span>
          </div>
          <div class="meta-row">
            <span class="meta-label">{{ t('serviceConfig.meta.lines') }}</span>
            <span class="meta-value mono">{{ lineCount }}</span>
          </div>
        </div>

        <div class="editor-main">
          <MonacoEditor
            v-model="editingContent"
            :path="activeFile.path"
            :language="fileFormat"
            :aria-label="editorLabel"
            @save="saveCurrent"
          />
        </div>

        <el-alert
          v-if="validation.state === 'failed'"
          type="error"
          :closable="false"
          :title="t('serviceConfig.status.failed')"
          show-icon
        >
          <pre v-if="validation.error" class="validation-output mono">{{ validation.error }}</pre>
          <el-button v-if="validation.error" size="small" @click="copyValidation">{{ t('servicesPage.lastError.copy') }}</el-button>
        </el-alert>

        <div class="validation-bar">
          <div class="val-left" role="status" aria-live="polite">
            <span v-if="validation.state === 'validating'">{{ t('serviceConfig.status.validating') }}</span>
            <span v-else-if="validation.state === 'passed'" class="val-ok">{{ t('serviceConfig.status.passedLong') }}</span>
            <span v-else-if="dirty" class="val-dirty">{{ t('serviceConfig.unsavedHint') }}</span>
          </div>
          <div class="val-right">
            <el-button size="small" :disabled="!dirty" @click="revert">
              {{ t('serviceConfig.revert') }}
            </el-button>
            <el-button size="small" @click="validateOnly" :loading="validation.state === 'validating'">
              {{ t('serviceConfig.validate') }}
            </el-button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Refresh, Document } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { fetchServiceConfig, saveServiceConfig, validateServiceConfig, type ConfigFile } from '../../api/daemon'
import { useDaemonStore } from '../../stores/daemon'
import { shortServiceName as shortName } from '../../utils/serviceState'
import { cleanError, installRouteOf, stateOf } from '../../utils/services'
import { formatBytes } from '../../utils/format'
import MonacoEditor from '../shared/MonacoEditor.vue'

defineOptions({ name: 'ServiceConfig' })

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()
const daemonStore = useDaemonStore()

const serviceId = computed(() => String(route.params.id || ''))

const files = ref<ConfigFile[]>([])
const originalContents = reactive<Record<string, string>>({})
const editedContents = reactive<Record<string, string>>({})
const dirtyFiles = reactive(new Set<string>())
const loading = ref(false)
const saving = ref(false)
const activeFilePath = ref<string>('')

const validation = reactive({
  state: 'idle' as 'idle' | 'validating' | 'passed' | 'failed',
  error: ''
})

const activeFile = computed<ConfigFile | null>(() =>
  files.value.find(f => f.path === activeFilePath.value) ?? null
)

const editingContent = computed<string>({
  get: () => {
    const p = activeFilePath.value
    return p && p in editedContents ? editedContents[p] : (activeFile.value?.content ?? '')
  },
  set: (val: string) => {
    const p = activeFilePath.value
    if (!p) return
    editedContents[p] = val
    onEdit()
  }
})

const dirty = computed(() => {
  const p = activeFilePath.value
  return p ? dirtyFiles.has(p) : false
})

const contentSize = computed(() => formatBytes(new Blob([editingContent.value]).size, locale.value))

// WDC's header on files it writes. Only my.ini is written once ("on first
// run"); httpd.conf, the vhosts and php.ini are regenerated.
const isGenerated = computed(() => {
  const head = (originalContents[activeFilePath.value] ?? '').split('\n', 3).join('\n')
  return /generated by nks webdev console/i.test(head) && !/on first run/i.test(head)
})

const lineCount = computed(() => editingContent.value.split('\n').length)

const fileFormat = computed(() => {
  const n = activeFile.value?.name?.toLowerCase() ?? ''
  if (n.endsWith('.conf') || n.endsWith('.cnf')) return 'conf'
  if (n.endsWith('.ini')) return 'ini'
  if (n.endsWith('.toml')) return 'toml'
  if (n.endsWith('.json')) return 'json'
  if (n.endsWith('.yml') || n.endsWith('.yaml')) return 'yaml'
  return 'text'
})

const service = computed(() => daemonStore.services.find(s => s.id === serviceId.value))
const displayName = computed(() => service.value ? shortName(service.value) : serviceId.value)
const notInstalled = computed(() => !!service.value && stateOf(service.value) === 'notInstalled')
const editorLabel = computed(() => `${activeFile.value?.name ?? ''}, ${displayName.value}`)

// Services that legitimately have no file-backed config — they configure
// themselves via CLI args, per-site TOML, or environment variables. The
// empty-state card shows a friendly hint for these instead of suggesting
// the daemon broke.
const CONFIGLESS_SERVICES = new Set(['mailpit', 'node', 'cloudflare', 'cloudflared', 'mkcert'])
const isConfigless = computed(() => CONFIGLESS_SERVICES.has(serviceId.value.toLowerCase()))

// Shown under "Details" so a power user knows where WDC looked. Matches the
// lookup order in ServiceConfigManager.GetFilesAsync — keep in sync.
const expectedPathsHint = computed(() => {
  const id = serviceId.value.toLowerCase()
  if (id === 'apache') return '~/.wdc/binaries/apache/<version>/conf/httpd.conf'
  if (id === 'php') return '~/.wdc/binaries/php/<version>/php.ini'
  if (id === 'mysql') return '~/.wdc/data/mysql/my.ini, ~/.wdc/binaries/mysql/<version>/my.ini'
  if (id === 'redis') return '~/.wdc/binaries/redis/<version>/redis.conf'
  if (id === 'caddy') return '~/.wdc/caddy/Caddyfile, ~/.wdc/binaries/caddy/<version>/Caddyfile'
  return t('serviceConfig.pluginLocations')
})

function onEdit() {
  const p = activeFilePath.value
  if (!p) return
  const orig = originalContents[p] ?? ''
  if (editedContents[p] !== orig) dirtyFiles.add(p)
  else dirtyFiles.delete(p)
  validation.state = 'idle'
  validation.error = ''
}

function onTabChange() {
  validation.state = 'idle'
  validation.error = ''
}

async function load() {
  if (!serviceId.value) return
  loading.value = true
  try {
    const data = await fetchServiceConfig(serviceId.value)
    files.value = data.files
    for (const k of Object.keys(originalContents)) delete originalContents[k]
    for (const k of Object.keys(editedContents)) delete editedContents[k]
    for (const f of data.files) {
      originalContents[f.path] = f.content
      editedContents[f.path] = f.content
    }
    dirtyFiles.clear()
    // Stay on the open file after a save or reload.
    if (!files.value.some(f => f.path === activeFilePath.value)) activeFilePath.value = files.value[0]?.path ?? ''
  } catch (err: unknown) {
    ElMessage.error(t('serviceConfig.loadFailed', { err: cleanError(err) }))
    files.value = []
  } finally {
    loading.value = false
  }
}

/** True when nothing is unsaved or the user agreed to drop it. */
async function confirmDiscard(message: string, confirmText: string): Promise<boolean> {
  if (dirtyFiles.size === 0) return true
  try {
    await ElMessageBox.confirm(message, t('serviceConfig.unsavedConfirm.title'), {
      type: 'warning',
      confirmButtonText: confirmText,
      cancelButtonText: t('common.cancel'),
    })
    return true
  } catch {
    return false
  }
}

function discardEdits() {
  for (const k of Object.keys(originalContents)) editedContents[k] = originalContents[k]
  dirtyFiles.clear()
}

async function reload() {
  if (!await confirmDiscard(t('serviceConfig.unsavedConfirm.message'), t('serviceConfig.unsavedConfirm.reload'))) return
  await load()
}

// Unsaved edits survive neither another service's config (same route,
// new :id) nor leaving the page: ask first instead of dropping them.
onBeforeRouteUpdate((to, from) => {
  if (to.params.id === from.params.id) return true
  return confirmDiscard(t('serviceConfig.unsavedConfirm.leave'), t('serviceConfig.unsavedConfirm.discard'))
})
onBeforeRouteLeave(async () => {
  if (!await confirmDiscard(t('serviceConfig.unsavedConfirm.leave'), t('serviceConfig.unsavedConfirm.discard'))) return false
  discardEdits()
  return true
})

function revert() {
  const p = activeFilePath.value
  if (!p) return
  editedContents[p] = originalContents[p] ?? ''
  dirtyFiles.delete(p)
  validation.state = 'idle'
  validation.error = ''
}

async function validateOnly() {
  if (!activeFile.value) return
  validation.state = 'validating'
  validation.error = ''
  try {
    const result = await validateServiceConfig(serviceId.value, activeFile.value.path, editingContent.value)
    validation.state = result.isValid ? 'passed' : 'failed'
    validation.error = result.isValid ? '' : (result.output || '')
  } catch (err: unknown) {
    validation.state = 'failed'
    validation.error = cleanError(err)
  }
}

async function saveCurrent() {
  if (!activeFile.value || !dirty.value || saving.value) return
  saving.value = true
  try {
    const validationResult = await validateServiceConfig(serviceId.value, activeFile.value.path, editingContent.value)
    if (!validationResult.isValid) {
      validation.state = 'failed'
      validation.error = validationResult.output || ''
      return
    }

    const result = await saveServiceConfig(serviceId.value, activeFile.value.path, editingContent.value)
    originalContents[activeFile.value.path] = editingContent.value
    editedContents[activeFile.value.path] = editingContent.value
    dirtyFiles.delete(activeFile.value.path)
    validation.state = 'idle'
    validation.error = ''
    await load()
    if (result.applied) ElMessage.success(t('serviceConfig.savedApplied', { name: displayName.value }))
    else ElMessage.warning(t('serviceConfig.savedNotApplied', { name: displayName.value, msg: result.message }))
  } catch (err: unknown) {
    ElMessage.error(t('serviceConfig.saveFailed', { err: cleanError(err) }))
  } finally {
    saving.value = false
  }
}

async function copyValidation() {
  try {
    await navigator.clipboard.writeText(validation.error)
    ElMessage.success(t('servicesPage.lastError.copied'))
  } catch (e) {
    ElMessage.error(cleanError(e))
  }
}

function goBack() {
  if (window.history.length > 1) router.back()
  else router.push('/services')
}

watch(serviceId, (id) => { if (id) void load() })
onMounted(() => { void load() })
</script>

<style scoped>
.svc-config-page {
  height: 100%;
}

.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
}
.back-link { margin-left: calc(-1 * var(--wdc-space-2)); margin-bottom: var(--wdc-space-2); }
.page-title { font-size: 1.728rem; line-height: 1.1; letter-spacing: -0.028em; }
.page-sub { margin-top: var(--wdc-space-1); color: var(--wdc-text-2); }
.header-actions { display: flex; gap: var(--wdc-space-2); }

.state-box { padding: var(--wdc-space-5) 0; }
.state-text { display: grid; gap: var(--wdc-space-3); justify-items: start; max-width: 60ch; }
.empty-hint { color: var(--wdc-text-2); font-size: 0.87rem; line-height: 1.5; }
.empty-details summary { cursor: pointer; color: var(--wdc-text-2); font-size: 0.87rem; }
.empty-details .empty-hint { margin-top: var(--wdc-space-2); font-family: var(--wdc-font-mono); overflow-wrap: anywhere; }

.config-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}

.file-tabs { flex-shrink: 0; }
.file-tabs :deep(.el-tabs__nav-wrap::after) { background-color: var(--wdc-border); }

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  font-size: 0.82rem;
  font-weight: 500;
}
.dirty-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--wdc-status-starting);
  margin-left: var(--wdc-space-1);
}

.editor-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  gap: var(--wdc-space-3);
  padding-top: var(--wdc-space-3);
}

.file-meta {
  display: flex;
  flex-wrap: wrap;
  gap: var(--wdc-space-4-5);
  flex-shrink: 0;
}
.meta-row {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  font-size: 0.78rem;
}
.meta-label {
  color: var(--wdc-text-3);
  font-size: 0.82rem;
  font-weight: 500;
  font-family: var(--wdc-font-display);
}
.meta-value {
  color: var(--wdc-text);
  font-weight: 500;
  overflow-wrap: anywhere;
}

.editor-main {
  flex: 1;
  display: flex;
  min-height: 360px;
  min-width: 0;
}

.validation-output {
  margin: var(--wdc-space-2) 0;
  max-height: 200px;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-size: 0.78rem;
}

.validation-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-3);
  flex-shrink: 0;
}
.val-left, .val-right {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
}
.val-right :deep(.el-button + .el-button) { margin-left: 0; }
.val-ok { color: var(--wdc-status-running); font-size: 0.82rem; }
.val-dirty { color: var(--wdc-text-2); font-size: 0.82rem; }
</style>
