<template>
  <div ref="containerRef" class="monaco-wrap"></div>
</template>

<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch } from 'vue'
import * as monaco from './monaco'

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
  // Highlighting only (no JSON/TOML language service is bundled):
  // JSON reads well with the JavaScript tokenizer, TOML with INI.
  if (normalized === 'json') return 'javascript'
  if (normalized === 'toml') return 'ini'
  if (['sh', 'bash', 'zsh'].includes(normalized)) return 'shell'
  if (['text', 'txt', ''].includes(normalized)) return 'plaintext'
  return normalized
}

// Define a custom dark theme that matches WDC design tokens
const WDC_DARK: monaco.editor.IStandaloneThemeData = {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'comment', foreground: '8a93a6', fontStyle: 'italic' },
    { token: 'string', foreground: '9fd8a8' },
    { token: 'number', foreground: 'f2b880' },
    { token: 'keyword', foreground: '8fb4ff', fontStyle: 'bold' },
  ],
  colors: {
    // Graphite editor in both themes, like the log boxes (--wdc-log-bg).
    'editor.background': '#11151c',
    'editor.foreground': '#e3e7ee',
    'editorLineNumber.foreground': '#5d6577',
    'editorLineNumber.activeForeground': '#8fb4ff',
    'editor.selectionBackground': '#24324d',
    'editor.inactiveSelectionBackground': '#1c2433',
    'editorCursor.foreground': '#8fb4ff',
    'editor.lineHighlightBackground': '#171c26',
    'editorIndentGuide.background1': '#1f2633',
    'editorGutter.background': '#11151c',
    'scrollbarSlider.background': '#2b344680',
    'scrollbarSlider.hoverBackground': '#3a465c80',
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
    fontFamily: "'JetBrains Mono', 'Cascadia Code', ui-monospace, monospace",
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
  /* Use theme token so the container matches the Monaco dark theme in
     dark mode and the page background in light mode. The embedded Monaco
     editor has its own wdc-dark theme that handles its internal colors. */
  background: var(--wdc-bg);
}
</style>
