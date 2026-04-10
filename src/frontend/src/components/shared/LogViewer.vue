<template>
  <div class="log-viewer">
    <div class="log-toolbar">
      <el-select v-model="logLevel" size="small" style="width: 120px">
        <el-option label="All" value="all" />
        <el-option label="Error" value="error" />
        <el-option label="Warning" value="warn" />
        <el-option label="Info" value="info" />
      </el-select>
      <el-button size="small" @click="clearTerminal">Clear</el-button>
    </div>
    <div ref="terminalRef" class="terminal-container" />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import '@xterm/xterm/css/xterm.css'

const props = defineProps<{ serviceId?: string }>()
const terminalRef = ref<HTMLElement>()
const logLevel = ref('all')

let terminal: Terminal | null = null
let fitAddon: FitAddon | null = null

function clearTerminal() {
  terminal?.clear()
}

onMounted(() => {
  if (!terminalRef.value) return

  terminal = new Terminal({
    theme: {
      background: '#1a1a2e',
      foreground: '#e0e0e0',
      cursor: '#67C23A',
      selectionBackground: '#67C23A40',
    },
    fontSize: 13,
    fontFamily: 'JetBrains Mono, Consolas, monospace',
    convertEol: true,
    disableStdin: true,
    scrollback: 5000,
  })

  fitAddon = new FitAddon()
  terminal.loadAddon(fitAddon)
  terminal.loadAddon(new WebLinksAddon())

  terminal.open(terminalRef.value)
  fitAddon.fit()

  // Demo output to verify ANSI rendering
  terminal.writeln('\x1b[32m[INFO]\x1b[0m  NKS WebDev Console Log Viewer initialized')
  terminal.writeln('\x1b[33m[WARN]\x1b[0m  This is a warning message')
  terminal.writeln('\x1b[31m[ERROR]\x1b[0m This is an error message')
  terminal.writeln('\x1b[36m[DEBUG]\x1b[0m Service: ' + (props.serviceId ?? 'none'))

  const resizeObserver = new ResizeObserver(() => fitAddon?.fit())
  resizeObserver.observe(terminalRef.value)
})

onUnmounted(() => {
  terminal?.dispose()
})
</script>

<style scoped>
.log-viewer { display: flex; flex-direction: column; height: 100%; }
.log-toolbar { display: flex; gap: 8px; padding: 8px; background: var(--wdc-surface, #1e1e2e); }
.terminal-container { flex: 1; min-height: 200px; }
</style>
