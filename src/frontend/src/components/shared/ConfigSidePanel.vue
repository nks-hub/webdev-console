<!--
  Slide-in config editor panel — deliberately NOT an el-drawer and NOT a
  router-view route. Per user directive: "config editor jako postranní panel
  místo drawer". Renders as a fixed-position aside that slides in from the
  right edge, overlays the current page without a modal backdrop (so the
  content behind stays visible and interactive), and closes on Esc or the
  close button.

  Takes all its config loading / editing / validation logic from the same
  fetchServiceConfig endpoint that the old ServiceConfig.vue full page used —
  no behavior change, only layout.
-->
<template>
  <Teleport to="body">
    <Transition name="slide-right">
      <aside
        v-if="open"
        class="config-side-panel"
        role="complementary"
        :aria-label="`${displayName} configuration`"
      >
        <div class="csp-header">
          <div class="csp-title-block">
            <span class="csp-eyebrow">Configuration</span>
            <span class="csp-title">{{ displayName }}</span>
          </div>
          <div class="csp-header-actions">
            <el-button
              size="small"
              :loading="loading"
              @click="reload"
              :title="'Reload files from disk'"
            >
              Reload
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
            <el-button
              size="small"
              text
              class="csp-close-btn"
              :title="'Close (Esc)'"
              @click="$emit('close')"
            >
              <el-icon><Close /></el-icon>
            </el-button>
          </div>
        </div>

        <div v-if="loading" class="csp-state-box">
          <el-skeleton :rows="6" animated />
        </div>

        <div v-else-if="files.length === 0" class="csp-state-box">
          <el-empty description="No config files found for this service." :image-size="60" />
        </div>

        <div v-else class="csp-body">
          <el-tabs v-model="activeFilePath" class="csp-file-tabs" @tab-change="onTabChange">
            <el-tab-pane
              v-for="file in files"
              :key="file.path"
              :name="file.path"
            >
              <template #label>
                <span class="csp-tab-label">
                  <span>{{ file.name }}</span>
                  <span v-if="dirtyFiles.has(file.path)" class="csp-dirty-dot" />
                </span>
              </template>
            </el-tab-pane>
          </el-tabs>

          <div v-if="activeFile" class="csp-editor-wrap">
            <div class="csp-file-meta">
              <span class="csp-meta-item mono" :title="activeFile.path">
                {{ shortPath(activeFile.path) }}
              </span>
              <span class="csp-meta-item mono">{{ contentSize }}</span>
              <span class="csp-meta-item mono">{{ lineCount }} lines</span>
              <el-tag size="small" effect="plain" class="csp-meta-tag">{{ fileFormat }}</el-tag>
            </div>

            <div class="csp-editor-main">
              <MonacoEditor v-model="editingContent" :language="fileFormat" />
            </div>

            <div class="csp-validation-bar">
              <el-tag
                v-if="validation.state === 'idle'"
                size="small"
                type="info"
              >Ready</el-tag>
              <el-tag
                v-else-if="validation.state === 'validating'"
                size="small"
                type="warning"
                effect="plain"
              >Validating…</el-tag>
              <el-tag
                v-else-if="validation.state === 'passed'"
                size="small"
                type="success"
              >Passed</el-tag>
              <el-tag
                v-else-if="validation.state === 'failed'"
                size="small"
                type="danger"
              >{{ validation.error || 'Failed' }}</el-tag>
              <div class="csp-val-spacer" />
              <el-button size="small" :disabled="!dirty" @click="revert">Revert</el-button>
              <el-button
                size="small"
                @click="validateOnly"
                :loading="validation.state === 'validating'"
              >
                Validate
              </el-button>
            </div>
          </div>
        </div>
      </aside>
    </Transition>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Close } from '@element-plus/icons-vue'
import { fetchServiceConfig, saveServiceConfig, validateServiceConfig, type ConfigFile } from '../../api/daemon'
import { useDaemonStore } from '../../stores/daemon'
import MonacoEditor from './MonacoEditor.vue'

defineOptions({ name: 'ConfigSidePanel' })

const props = defineProps<{
  open: boolean
  serviceId: string | null
}>()

const emit = defineEmits<{
  (e: 'close'): void
}>()

const daemonStore = useDaemonStore()

const files = ref<ConfigFile[]>([])
const originalContents = reactive<Record<string, string>>({})
const editedContents = reactive<Record<string, string>>({})
const dirtyFiles = reactive(new Set<string>())
const loading = ref(false)
const saving = ref(false)
const activeFilePath = ref<string>('')

const validation = reactive({
  state: 'idle' as 'idle' | 'validating' | 'passed' | 'failed',
  error: '',
})

const activeFile = computed<ConfigFile | null>(
  () => files.value.find(f => f.path === activeFilePath.value) ?? null
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
    const orig = originalContents[p] ?? ''
    if (editedContents[p] !== orig) dirtyFiles.add(p)
    else dirtyFiles.delete(p)
    validation.state = 'idle'
    validation.error = ''
  },
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
  const id = props.serviceId || ''
  const svc = daemonStore.services.find(s => s.id === id)
  return svc?.displayName || svc?.id || id
})

function shortPath(p: string): string {
  // Collapse long Windows paths to a readable tail so the meta row
  // doesn't overflow the narrow panel.
  if (p.length <= 64) return p
  return '…' + p.substring(p.length - 60)
}

function onTabChange(_name: string) {
  validation.state = 'idle'
  validation.error = ''
}

async function load() {
  if (!props.serviceId) return
  loading.value = true
  try {
    const data = await fetchServiceConfig(props.serviceId)
    files.value = data.files
    for (const k of Object.keys(originalContents)) delete originalContents[k]
    for (const k of Object.keys(editedContents)) delete editedContents[k]
    for (const f of data.files) {
      originalContents[f.path] = f.content
      editedContents[f.path] = f.content
    }
    dirtyFiles.clear()
    if (files.value.length > 0) activeFilePath.value = files.value[0].path
    else activeFilePath.value = ''
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : 'Failed to load config'
    ElMessage.error(msg)
    files.value = []
  } finally {
    loading.value = false
  }
}

async function reload() {
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
  if (!activeFile.value || !props.serviceId) return
  validation.state = 'validating'
  validation.error = ''
  try {
    const result = await validateServiceConfig(props.serviceId, activeFile.value.path, editingContent.value)
    validation.state = result.isValid ? 'passed' : 'failed'
    validation.error = result.isValid ? '' : (result.output || 'Validation failed')
  } catch (err: unknown) {
    validation.state = 'failed'
    validation.error = err instanceof Error ? err.message : 'Validation failed'
  }
}

async function saveCurrent() {
  if (!activeFile.value || !dirty.value || !props.serviceId) return
  saving.value = true
  try {
    const validationResult = await validateServiceConfig(props.serviceId, activeFile.value.path, editingContent.value)
    if (!validationResult.isValid) {
      validation.state = 'failed'
      validation.error = validationResult.output || 'Validation failed'
      return
    }

    const result = await saveServiceConfig(props.serviceId, activeFile.value.path, editingContent.value)
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

// Close on Esc when panel is open
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.open) {
    e.preventDefault()
    emit('close')
  }
}

// Reload whenever (open, serviceId) changes — clearing and refetching so the
// panel always shows fresh files without stale state from a prior open.
watch(
  () => [props.open, props.serviceId] as const,
  ([isOpen, id]) => {
    if (isOpen && id) void load()
  },
  { immediate: true }
)

onMounted(() => window.addEventListener('keydown', onKeydown))
onUnmounted(() => window.removeEventListener('keydown', onKeydown))
</script>

<style scoped>
/* Fixed slide-in panel — NOT an el-drawer (user directive). The content
   behind stays fully interactive because we don't render a modal backdrop.
   Panel lives above the app's z-index baseline so it draws over the sidebar
   + header + main content. */
.config-side-panel {
  position: fixed;
  top: 0;
  right: 0;
  bottom: 0;
  width: min(560px, 46vw);
  z-index: 2200;
  display: flex;
  flex-direction: column;
  background: var(--wdc-surface);
  border-left: 1px solid var(--wdc-border);
  box-shadow: -16px 0 48px rgba(0, 0, 0, 0.55);
  overflow: hidden;
}

/* Slide in from the right edge. Tail matches the --wdc transition duration
   used elsewhere (120ms) so the motion feels native. */
.slide-right-enter-active,
.slide-right-leave-active {
  transition: transform 0.22s cubic-bezier(0.4, 0, 0.2, 1),
              opacity 0.18s ease-out;
}
.slide-right-enter-from,
.slide-right-leave-to {
  transform: translateX(100%);
  opacity: 0.85;
}

.csp-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface-2);
  flex-shrink: 0;
}

.csp-title-block {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.csp-eyebrow {
  font-size: 0.66rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--wdc-text-3);
}
.csp-title {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.csp-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
.csp-close-btn {
  min-width: 28px;
  padding: 4px 10px !important;
  font-size: 1rem !important;
  font-weight: 600;
  color: var(--wdc-text-2) !important;
}
.csp-close-btn:hover {
  color: var(--wdc-text) !important;
}

.csp-state-box {
  padding: 24px 18px;
}

.csp-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}

.csp-file-tabs {
  padding: 0 10px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  flex-shrink: 0;
}
.csp-file-tabs :deep(.el-tabs__nav-wrap::after) {
  display: none;
}
.csp-tab-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 0.8rem;
  font-weight: 500;
}
.csp-dirty-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--wdc-status-starting);
}

.csp-editor-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  padding: 12px 14px;
  gap: 10px;
}

.csp-file-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  background: var(--wdc-bg);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  flex-shrink: 0;
  font-size: 0.72rem;
}
.csp-meta-item {
  color: var(--wdc-text-3);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 260px;
}
.csp-meta-item:first-child {
  color: var(--wdc-text-2);
  flex: 1;
  max-width: 300px;
}
.csp-meta-tag {
  flex-shrink: 0;
}

.csp-editor-main {
  flex: 1;
  display: flex;
  min-height: 220px;
  min-width: 0;
}

.csp-validation-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  background: var(--wdc-bg);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  flex-shrink: 0;
}
.csp-val-spacer {
  flex: 1;
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', 'Fira Code', monospace;
}
</style>
