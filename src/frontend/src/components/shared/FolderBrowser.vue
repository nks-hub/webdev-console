<template>
  <el-dialog
    :model-value="modelValue"
    title="Choose a folder"
    width="720px"
    top="6vh"
    :close-on-click-modal="false"
    destroy-on-close
    @update:model-value="$emit('update:modelValue', $event)"
  >
    <div class="fb-root">
      <!-- Path bar -->
      <div class="fb-pathbar">
        <el-button
          size="small"
          :disabled="!current?.parent || loading"
          @click="navigate(current?.parent ?? undefined)"
          title="Parent directory"
        >
          <el-icon><Back /></el-icon>
        </el-button>
        <el-button size="small" :disabled="loading" @click="navigate(undefined)" title="Drives / home">
          <el-icon><HomeFilled /></el-icon>
        </el-button>
        <el-input
          v-model="pathInput"
          size="small"
          placeholder="Type a path and press Enter…"
          class="fb-path-input"
          @keydown.enter="navigate(pathInput)"
        />
        <el-button size="small" type="primary" :disabled="loading" @click="navigate(pathInput)">
          Go
        </el-button>
      </div>

      <!-- Entry list -->
      <div class="fb-list" v-loading="loading">
        <div v-if="!loading && entries.length === 0" class="fb-empty">
          (empty directory)
        </div>
        <div
          v-for="entry in entries"
          :key="entry.path"
          class="fb-row"
          :class="{ 'fb-row-dir': entry.isDir, 'fb-row-file': !entry.isDir }"
          @click="onRowClick(entry)"
          @dblclick="onRowDoubleClick(entry)"
        >
          <el-icon class="fb-icon">
            <Folder v-if="entry.isDir" />
            <Document v-else />
          </el-icon>
          <span class="fb-name">{{ entry.name }}</span>
          <span v-if="entry.isFile" class="fb-size">{{ formatSize(entry.size) }}</span>
        </div>
      </div>

      <!-- Selection row -->
      <div class="fb-selection">
        <span class="fb-selection-label">Selected:</span>
        <code class="fb-selection-path">{{ selectedPath || current?.path || '(none)' }}</code>
      </div>
    </div>

    <template #footer>
      <el-button @click="$emit('update:modelValue', false)">Cancel</el-button>
      <el-button
        type="primary"
        :disabled="!canConfirm"
        @click="confirm"
      >
        Select this folder
      </el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { Folder, Document, Back, HomeFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { browseFolder } from '../../api/daemon'
import type { FsBrowseResponse, FsEntry } from '../../api/daemon'

const props = defineProps<{
  modelValue: boolean
  initialPath?: string
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', v: boolean): void
  (e: 'select', path: string): void
}>()

const loading = ref(false)
const current = ref<FsBrowseResponse | null>(null)
const pathInput = ref('')
const selectedPath = ref('')

const entries = computed<FsEntry[]>(() => current.value?.entries ?? [])
const canConfirm = computed(() => !!(selectedPath.value || current.value?.path))

async function navigate(path?: string) {
  loading.value = true
  try {
    const res = await browseFolder(path && path.trim() ? path.trim() : undefined)
    current.value = res
    pathInput.value = res.path
    selectedPath.value = ''
  } catch (e: any) {
    ElMessage.error(`Cannot open path: ${e?.message ?? e}`)
  } finally {
    loading.value = false
  }
}

function onRowClick(entry: FsEntry) {
  // Single click: mark selected (if dir) so user can confirm this specific dir
  if (entry.isDir) selectedPath.value = entry.path
}

function onRowDoubleClick(entry: FsEntry) {
  // Double click on dir: drill into it. On file: ignored.
  if (entry.isDir) void navigate(entry.path)
}

function confirm() {
  const pick = selectedPath.value || current.value?.path
  if (!pick) return
  emit('select', pick)
  emit('update:modelValue', false)
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

// Initial load + reload when dialog opens
watch(() => props.modelValue, (open) => {
  if (open) {
    selectedPath.value = ''
    void navigate(props.initialPath || undefined)
  }
}, { immediate: true })
</script>

<style scoped>
.fb-root {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.fb-pathbar {
  display: flex;
  align-items: center;
  gap: 6px;
}
.fb-path-input {
  flex: 1;
}
.fb-path-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
}

.fb-list {
  height: 360px;
  overflow-y: auto;
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-sm);
  background: var(--wdc-surface-2);
}

.fb-empty {
  padding: 40px 16px;
  text-align: center;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}

.fb-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 14px;
  cursor: pointer;
  border-bottom: 1px solid var(--wdc-border);
  transition: background 0.08s;
}
.fb-row:last-child { border-bottom: none; }
.fb-row:hover {
  background: var(--wdc-hover);
}
.fb-row.fb-row-dir .fb-icon { color: var(--wdc-accent); }
.fb-row.fb-row-file .fb-icon { color: var(--wdc-text-3); }
.fb-row.fb-row-file { opacity: 0.7; cursor: default; }

.fb-icon {
  font-size: 1.05rem;
  flex-shrink: 0;
}

.fb-name {
  flex: 1;
  font-size: 0.88rem;
  color: var(--wdc-text);
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.fb-row-file .fb-name { color: var(--wdc-text-2); font-weight: 400; }

.fb-size {
  font-size: 0.75rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
}

.fb-selection {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 14px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.fb-selection-label {
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
}
.fb-selection-path {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
  color: var(--wdc-accent);
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
