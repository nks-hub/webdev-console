<template>
  <div ref="containerRef" class="monaco-wrap"></div>
</template>

<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch } from 'vue'
import * as monaco from 'monaco-editor'
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker'

// Minimal worker setup — no language-specific workers for conf/ini/etc
;(self as unknown as Record<string, unknown>).MonacoEnvironment = {
  getWorker: () => new editorWorker(),
}

const props = withDefaults(defineProps<{
  modelValue: string
  language?: string
  readOnly?: boolean
  height?: string
}>(), {
  language: 'plaintext',
  readOnly: false,
  height: '100%',
})

const emit = defineEmits<{
  (e: 'update:modelValue', v: string): void
  (e: 'ready', ed: monaco.editor.IStandaloneCodeEditor): void
}>()

const containerRef = ref<HTMLDivElement | null>(null)
let editor: monaco.editor.IStandaloneCodeEditor | null = null
let suppressChange = false

function mapLanguage(lang: string): string {
  const normalized = lang.toLowerCase()
  if (['conf', 'cnf', 'nginx'].includes(normalized)) return 'ini'
  if (['yml'].includes(normalized)) return 'yaml'
  if (['cmd', 'bat'].includes(normalized)) return 'bat'
  return normalized
}

// Define a custom dark theme that matches WDC design tokens
const WDC_DARK: monaco.editor.IStandaloneThemeData = {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'comment', foreground: '6b7094', fontStyle: 'italic' },
    { token: 'string', foreground: 'a5d6a7' },
    { token: 'number', foreground: 'ffab70' },
    { token: 'keyword', foreground: 'c4b5fd', fontStyle: 'bold' },
  ],
  colors: {
    'editor.background': '#0f111a',
    'editor.foreground': '#ffffff',
    'editorLineNumber.foreground': '#5c5f72',
    'editorLineNumber.activeForeground': '#a78bfa',
    'editor.selectionBackground': '#2a2e42',
    'editor.inactiveSelectionBackground': '#1e2133',
    'editorCursor.foreground': '#a78bfa',
    'editor.lineHighlightBackground': '#1a1d2c',
    'editorIndentGuide.background1': '#1e2133',
    'editorGutter.background': '#0f111a',
    'scrollbarSlider.background': '#2a2e4280',
    'scrollbarSlider.hoverBackground': '#3b3f5c80',
  },
}

onMounted(() => {
  if (!containerRef.value) return
  monaco.editor.defineTheme('wdc-dark', WDC_DARK)
  editor = monaco.editor.create(containerRef.value, {
    value: props.modelValue,
    language: mapLanguage(props.language),
    theme: 'wdc-dark',
    readOnly: props.readOnly,
    automaticLayout: true,
    fontFamily: "'JetBrains Mono', 'Cascadia Code', 'Fira Code', monospace",
    fontSize: 13,
    lineHeight: 20,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    renderLineHighlight: 'all',
    tabSize: 4,
    insertSpaces: true,
    wordWrap: 'off',
    folding: true,
    lineNumbers: 'on',
    glyphMargin: false,
    smoothScrolling: true,
    padding: { top: 10, bottom: 10 },
    contextmenu: true,
    scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
  })

  editor.onDidChangeModelContent(() => {
    if (suppressChange) return
    const v = editor?.getValue() ?? ''
    emit('update:modelValue', v)
  })

  emit('ready', editor)
})

watch(() => props.modelValue, (v) => {
  if (!editor) return
  if (v === editor.getValue()) return
  suppressChange = true
  editor.setValue(v)
  suppressChange = false
})

watch(() => props.language, (v) => {
  if (!editor) return
  const model = editor.getModel()
  if (model) monaco.editor.setModelLanguage(model, mapLanguage(v))
})

watch(() => props.readOnly, (v) => {
  editor?.updateOptions({ readOnly: v })
})

onBeforeUnmount(() => {
  editor?.dispose()
  editor = null
})
</script>

<style scoped>
.monaco-wrap {
  width: 100%;
  height: 100%;
  min-height: 280px;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  overflow: hidden;
  background: #0f111a;
}
</style>
