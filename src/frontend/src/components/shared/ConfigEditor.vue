<!--
  Config file editor backed by a simple textarea for POC.
  For production: replace textarea with Monaco Editor (@monaco-editor/loader).
  Implements full validate→apply pipeline with ValidationBadge.
-->
<template>
  <div class="config-editor">
    <div class="editor-toolbar">
      <span class="config-path">{{ configPath }}</span>
      <div class="toolbar-actions">
        <ValidationBadge ref="badge" @confirmed="applyEdit" @revert="revertEdit" />
        <el-button size="small" type="primary" :disabled="!dirty || validating" @click="validateAndApply">
          Save &amp; Apply
        </el-button>
        <el-button size="small" :disabled="!dirty" @click="revertEdit">Discard</el-button>
      </div>
    </div>

    <textarea
      v-model="localContent"
      class="editor-area"
      spellcheck="false"
      @input="dirty = true"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import ValidationBadge from './ValidationBadge.vue'

const props = defineProps<{
  configPath: string
  syntax?: string
  initialContent?: string
}>()

const emit = defineEmits<{
  saved: [content: string]
}>()

const badge = ref<InstanceType<typeof ValidationBadge> | null>(null)
const localContent = ref(props.initialContent ?? '')
const savedContent = ref(props.initialContent ?? '')
const dirty = ref(false)
const validating = ref(false)

watch(() => props.initialContent, (v) => {
  if (v !== undefined) { localContent.value = v; savedContent.value = v; dirty.value = false }
})

async function validateAndApply() {
  validating.value = true
  badge.value?.startValidation()
  try {
    const port = window.daemonApi?.getPort() ?? 50051
    const res = await fetch(`http://localhost:${port}/api/config/validate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path: props.configPath, content: localContent.value }),
    })
    const data = await res.json() as { valid: boolean; error?: string }
    badge.value?.setResult(data.valid, data.error)
    if (!data.valid) { validating.value = false }
  } catch (e) {
    badge.value?.setResult(false, String(e))
    validating.value = false
  }
}

async function applyEdit() {
  const port = window.daemonApi?.getPort() ?? 50051
  await fetch(`http://localhost:${port}/api/config/write`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path: props.configPath, content: localContent.value }),
  })
  savedContent.value = localContent.value
  dirty.value = false
  validating.value = false
  emit('saved', localContent.value)
}

function revertEdit() {
  localContent.value = savedContent.value
  dirty.value = false
  validating.value = false
  badge.value?.reset()
}
</script>

<style scoped>
.config-editor { display: flex; flex-direction: column; height: 100%; }
.editor-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 8px;
  border-bottom: 1px solid var(--el-border-color);
  flex-shrink: 0;
  flex-wrap: wrap;
  gap: 8px;
}
.config-path { font-family: monospace; font-size: 0.82rem; color: var(--el-text-color-secondary); }
.toolbar-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.editor-area {
  flex: 1;
  resize: none;
  background: #0d0f1a;
  color: #c9d1d9;
  font-family: 'Consolas', 'JetBrains Mono', monospace;
  font-size: 0.82rem;
  border: none;
  outline: none;
  padding: 12px;
  line-height: 1.6;
  tab-size: 4;
}
</style>
