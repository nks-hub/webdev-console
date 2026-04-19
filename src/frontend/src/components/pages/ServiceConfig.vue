<template>
  <div class="svc-config-page">
    <!-- Page header -->
    <div class="config-header">
      <div class="header-left">
        <el-button size="small" text @click="goBack">
          <el-icon><ArrowLeft /></el-icon>
          <span>{{ $t('services.back') }}</span>
        </el-button>
        <div class="title-block">
          <div class="title-label">{{ $t('services.title') }}</div>
          <div class="title-name">{{ displayName }}</div>
        </div>
      </div>
      <div class="header-actions">
        <el-button size="small" :loading="loading" @click="reload">
          <el-icon><Refresh /></el-icon>
          <span>{{ $t('services.reload') }}</span>
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
    </div>

    <!-- Loading state -->
    <div v-if="loading" class="state-box">
      <el-skeleton :rows="8" animated />
    </div>

    <!-- Empty state -->
    <div v-else-if="files.length === 0" class="state-box">
      <el-empty :description="emptyStateMessage">
        <template v-if="isConfigless">
          <p class="empty-hint">
            {{ $t('services.configless.hint') }}
          </p>
        </template>
        <template v-else>
          <p class="empty-hint">
            {{ $t('services.configless.daemonLooked', { paths: expectedPathsHint }) }}
            <strong>{{ $t('services.configless.daemonLookedBinaries') }}</strong>
            {{ $t('services.configless.daemonLookedAnd') }}
          </p>
        </template>
      </el-empty>
    </div>

    <!-- Content -->
    <div v-else class="config-body">
      <!-- File tabs -->
      <el-tabs v-model="activeFilePath" class="file-tabs" @tab-change="onTabChange">
        <el-tab-pane
          v-for="file in files"
          :key="file.path"
          :name="file.path"
        >
          <template #label>
            <span class="tab-label">
              <el-icon><Document /></el-icon>
              <span>{{ file.name }}</span>
              <span v-if="dirtyFiles.has(file.path)" class="dirty-dot" />
            </span>
          </template>
        </el-tab-pane>
      </el-tabs>

      <!-- Active file editor -->
      <div v-if="activeFile" class="editor-wrap">
        <!-- File meta row -->
        <div class="file-meta">
          <div class="meta-row">
            <span class="meta-label">Path</span>
            <code class="meta-value mono">{{ activeFile.path }}</code>
          </div>
          <div class="meta-row">
            <span class="meta-label">Size</span>
            <span class="meta-value mono">{{ contentSize }}</span>
          </div>
          <div class="meta-row">
            <span class="meta-label">Lines</span>
            <span class="meta-value mono">{{ lineCount }}</span>
          </div>
          <div class="meta-row">
            <span class="meta-label">Format</span>
            <el-tag size="small" effect="plain">{{ fileFormat }}</el-tag>
          </div>
        </div>

        <!-- Monaco code editor — replaces textarea, Phase 4 plan item -->
        <div class="editor-main">
          <MonacoEditor
            v-model="editingContent"
            :language="fileFormat"
          />
        </div>

        <!-- Validation + actions row -->
        <div class="validation-bar">
          <div class="val-left">
            <el-tag
              v-if="validation.state === 'idle'"
              size="small"
              type="info"
            >
              {{ t('serviceConfig.status.ready') }}
            </el-tag>
            <el-tag
              v-else-if="validation.state === 'validating'"
              size="small"
              type="warning"
              effect="plain"
            >
              {{ t('serviceConfig.status.validating') }}
            </el-tag>
            <el-tag
              v-else-if="validation.state === 'passed'"
              size="small"
              type="success"
            >{{ t('serviceConfig.status.passed') }}</el-tag>
            <el-tag
              v-else-if="validation.state === 'failed'"
              size="small"
              type="danger"
            >{{ validation.error || t('serviceConfig.status.failed') }}</el-tag>
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
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Refresh, Document } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { fetchServiceConfig, saveServiceConfig, validateServiceConfig, type ConfigFile } from '../../api/daemon'
import { useDaemonStore } from '../../stores/daemon'
import MonacoEditor from '../shared/MonacoEditor.vue'

const { t } = useI18n()
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

const contentSize = computed(() => {
  const bytes = new Blob([editingContent.value]).size
  if (bytes < 1024) return `${bytes} B`
  return `${(bytes / 1024).toFixed(1)} KB`
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

const displayName = computed(() => {
  const svc = daemonStore.services.find((s: any) => s.id === serviceId.value)
  return svc?.displayName || svc?.id || serviceId.value
})

// Services that legitimately have no file-backed config — they configure
// themselves via CLI args, per-site TOML, or environment variables. The
// empty-state card shows a friendly hint for these instead of suggesting
// the daemon broke.
const CONFIGLESS_SERVICES = new Set(['mailpit', 'node', 'cloudflare', 'cloudflared', 'mkcert'])
const isConfigless = computed(() => CONFIGLESS_SERVICES.has(serviceId.value.toLowerCase()))

const emptyStateMessage = computed(() =>
  isConfigless.value
    ? t('services.configlessSummary', { name: displayName.value })
    : t('services.noConfigSummary', { name: displayName.value })
)

// Shown to the user so they know where WDC looked. Matches the lookup
// order in ServiceConfigManager.GetFilesAsync — keep in sync.
const expectedPathsHint = computed(() => {
  const id = serviceId.value.toLowerCase()
  if (id === 'apache') return '~/.wdc/binaries/apache/<version>/conf/httpd.conf'
  if (id === 'php') return '~/.wdc/binaries/php/<version>/php.ini'
  if (id === 'mysql') return '~/.wdc/data/mysql/my.ini, ~/.wdc/binaries/mysql/<version>/my.ini'
  if (id === 'redis') return '~/.wdc/binaries/redis/<version>/redis.conf'
  if (id === 'caddy') return '~/.wdc/caddy/Caddyfile, ~/.wdc/binaries/caddy/<version>/Caddyfile'
  return 'well-known plugin config locations'
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

function onTabChange(_name: string) {
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
    if (files.value.length > 0) activeFilePath.value = files.value[0].path
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : 'Failed to load config'
    ElMessage.error(msg)
    files.value = []
  } finally {
    loading.value = false
  }
}

async function reload() {
  if (dirtyFiles.size > 0) {
    try {
      await ElMessageBox.confirm(
        t('serviceConfig.unsavedConfirm.message'),
        t('serviceConfig.unsavedConfirm.title'),
        { type: 'warning', confirmButtonText: t('serviceConfig.unsavedConfirm.reload'), cancelButtonText: t('common.cancel') }
      )
    } catch { return }
  }
  await load()
}

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
    validation.error = result.isValid ? '' : (result.output || 'Validation failed')
  } catch (err: unknown) {
    validation.state = 'failed'
    validation.error = err instanceof Error ? err.message : 'Validation failed'
  }
}

async function saveCurrent() {
  if (!activeFile.value || !dirty.value) return
  saving.value = true
  try {
    const validationResult = await validateServiceConfig(serviceId.value, activeFile.value.path, editingContent.value)
    if (!validationResult.isValid) {
      validation.state = 'failed'
      validation.error = validationResult.output || 'Validation failed'
      return
    }

    const result = await saveServiceConfig(serviceId.value, activeFile.value.path, editingContent.value)
    originalContents[activeFile.value.path] = editingContent.value
    editedContents[activeFile.value.path] = editingContent.value
    dirtyFiles.delete(activeFile.value.path)
    validation.state = 'idle'
    validation.error = ''
    await load()
    if (result.applied) ElMessage.success(result.message)
    else ElMessage.warning(result.message)
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : 'Save failed'
    ElMessage.error(msg)
  } finally {
    saving.value = false
  }
}

function goBack() {
  if (window.history.length > 1) router.back()
  else router.push('/dashboard')
}

watch(serviceId, () => { void load() })
onMounted(() => { void load() })
</script>

<style scoped>
.svc-config-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 100%;
  background: var(--wdc-bg);
}

.config-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 20px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  flex-shrink: 0;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 14px;
}

.title-block {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.title-label {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--wdc-text-3);
}
.title-name {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.header-actions {
  display: flex;
  gap: 8px;
}

.state-box {
  padding: 32px 24px;
}

.config-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}

.file-tabs {
  padding: 0 20px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  flex-shrink: 0;
}
.file-tabs :deep(.el-tabs__nav-wrap::after) {
  display: none;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 0.82rem;
  font-weight: 500;
}
.dirty-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--wdc-status-starting);
  margin-left: 4px;
}

.editor-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  padding: 14px 20px;
  gap: 12px;
}

.file-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 18px;
  padding: 10px 14px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  flex-shrink: 0;
}
.meta-row {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 0.78rem;
}
.meta-label {
  color: var(--wdc-text-3);
  text-transform: uppercase;
  font-size: 0.68rem;
  letter-spacing: 0.08em;
  font-weight: 600;
}
.meta-value {
  color: var(--wdc-text);
  font-weight: 500;
}

.editor-main {
  flex: 1;
  display: flex;
  min-height: 280px;
  min-width: 0;
}

.validation-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 14px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  flex-shrink: 0;
}
.val-left, .val-right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', 'Fira Code', monospace;
}
</style>
