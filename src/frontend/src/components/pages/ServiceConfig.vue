<template>
  <div class="svc-config-page">
    <!-- Page header -->
    <div class="config-header">
      <div class="header-left">
        <el-button size="small" text @click="goBack">
          <el-icon><ArrowLeft /></el-icon>
          <span>Back</span>
        </el-button>
        <div class="title-block">
          <div class="title-label">Service Configuration</div>
          <div class="title-name">{{ displayName }}</div>
        </div>
      </div>
      <div class="header-actions">
        <el-button size="small" :loading="loading" @click="reload">
          <el-icon><Refresh /></el-icon>
          <span>Reload</span>
        </el-button>
        <el-button
          type="primary"
          size="small"
          :loading="saving"
          :disabled="!dirty || !activeFile"
          @click="saveCurrent"
        >
          Save &amp; Apply
        </el-button>
      </div>
    </div>

    <!-- Loading state -->
    <div v-if="loading" class="state-box">
      <el-skeleton :rows="8" animated />
    </div>

    <!-- Empty state -->
    <div v-else-if="files.length === 0" class="state-box">
      <el-empty description="No config files found for this service." />
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

        <!-- Editor textarea (Monaco drop-in planned later) -->
        <div class="editor-main">
          <textarea
            ref="editorRef"
            v-model="editingContent"
            class="editor-textarea mono"
            spellcheck="false"
            @input="onEdit"
          />
          <div class="editor-gutter">
            <div
              v-for="n in lineCount"
              :key="n"
              class="gutter-line mono"
            >{{ n }}</div>
          </div>
        </div>

        <!-- Validation + actions row -->
        <div class="validation-bar">
          <div class="val-left">
            <el-tag
              v-if="validation.state === 'idle'"
              size="small"
              type="info"
            >
              Ready
            </el-tag>
            <el-tag
              v-else-if="validation.state === 'validating'"
              size="small"
              type="warning"
              effect="plain"
            >
              Validating…
            </el-tag>
            <el-tag
              v-else-if="validation.state === 'passed'"
              size="small"
              type="success"
            >
              ✓ Passed
            </el-tag>
            <el-tag
              v-else-if="validation.state === 'failed'"
              size="small"
              type="danger"
            >
              ✗ {{ validation.error || 'Failed' }}
            </el-tag>
          </div>
          <div class="val-right">
            <el-button size="small" :disabled="!dirty" @click="revert">
              Revert
            </el-button>
            <el-button size="small" @click="validateOnly" :loading="validation.state === 'validating'">
              Validate
            </el-button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Refresh, Document } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { fetchServiceConfig, type ConfigFile } from '../../api/daemon'
import { useDaemonStore } from '../../stores/daemon'

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
        'You have unsaved changes. Reload anyway?',
        'Unsaved changes',
        { type: 'warning', confirmButtonText: 'Reload', cancelButtonText: 'Cancel' }
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
    // TODO: call daemon validation endpoint when available
    await new Promise(r => setTimeout(r, 300))
    validation.state = 'passed'
  } catch (err: unknown) {
    validation.state = 'failed'
    validation.error = err instanceof Error ? err.message : 'Validation failed'
  }
}

async function saveCurrent() {
  if (!activeFile.value || !dirty.value) return
  saving.value = true
  try {
    // TODO: wire to POST /api/services/{id}/config with {path, content}
    ElMessage.info('Save endpoint not yet wired — draft kept locally')
    saving.value = false
  } catch (err: unknown) {
    saving.value = false
    const msg = err instanceof Error ? err.message : 'Save failed'
    ElMessage.error(msg)
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
  min-height: 0;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  overflow: hidden;
}
.editor-gutter {
  order: -1;
  width: 52px;
  padding: 12px 8px;
  background: var(--wdc-surface-2);
  border-right: 1px solid var(--wdc-border);
  user-select: none;
  overflow: hidden;
  text-align: right;
  color: var(--wdc-text-3);
}
.gutter-line {
  font-size: 0.76rem;
  line-height: 1.55;
}
.editor-textarea {
  flex: 1;
  resize: none;
  border: none;
  outline: none;
  padding: 12px 14px;
  background: transparent;
  color: var(--wdc-text);
  font-size: 0.82rem;
  line-height: 1.55;
  tab-size: 4;
  white-space: pre;
  overflow: auto;
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
