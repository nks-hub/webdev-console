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
        <ValidationBadge
          ref="badge"
          :service-id="serviceId"
          @confirmed="applyEdit"
          @revert="revertEdit"
        />
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
  // Optional — when set, ValidationBadge subscribes to SSE `validation`
  // events for this service and mirrors phase updates into its state
  // machine, complementing the imperative startValidation/setResult calls
  // below so external triggers (CLI `wdc config validate`, another tab
  // editing the same file) also reflect in this component.
  serviceId?: string
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
  // The imperative startValidation call below is a fallback for the case
  // where the user edits a file that isn't tied to any specific serviceId
  // (no SSE events will fire). When serviceId IS set, the daemon's SSE
  // broadcast will also update the badge via the reactive path — both
  // kick the state machine into the same "validating" phase so the
  // double-call is idempotent.
  badge.value?.startValidation()
  try {
    const port = window.daemonApi?.getPort() ?? 50051
    const token = window.daemonApi?.getToken?.() ?? ''
    const res = await fetch(`http://localhost:${port}/api/config/validate`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify({
        configPath: props.configPath,
        content: localContent.value,
        serviceId: props.serviceId,
      }),
    })
    const data = await res.json() as { isValid?: boolean; valid?: boolean; output?: string; error?: string }
    const ok = data.isValid ?? data.valid ?? false
    badge.value?.setResult(ok, data.output ?? data.error)
    if (!ok) { validating.value = false }
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
  /* Use theme tokens so the fallback textarea respects light mode.
     Prior hardcoded #0d0f1a / #c9d1d9 gave a 12.36:1 dark-mode ratio
     (WCAG AAA) but froze the editor dark even when the rest of the
     app switched to light theme. */
  background: var(--wdc-bg);
  color: var(--wdc-text-2);
  font-family: 'Consolas', 'JetBrains Mono', monospace;
  font-size: 0.82rem;
  border: none;
  outline: none;
  padding: 12px;
  line-height: 1.6;
  tab-size: 4;
}
</style>
