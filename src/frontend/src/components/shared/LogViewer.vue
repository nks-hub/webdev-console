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

declare global {
  interface Window {
    daemonApi?: { getPort: () => number; getToken: () => string }
  }
}

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = window.daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

function daemonToken(): string {
  return window.daemonApi?.getToken?.() || new URLSearchParams(window.location.search).get('token') || ''
}

const props = defineProps<{ serviceId?: string }>()
const terminalRef = ref<HTMLElement>()
const logLevel = ref('all')

let terminal: Terminal | null = null
let fitAddon: FitAddon | null = null
let closeEventSource: (() => void) | null = null

function clearTerminal() {
  terminal?.clear()
}

onMounted(async () => {
  if (!terminalRef.value) return

  terminal = new Terminal({
    theme: {
      background: '#0f1117',
      foreground: '#eceef6',
      cursor: '#6366f1',
      selectionBackground: '#6366f140',
      black: '#1a1c28',
      red: '#ef4444',
      green: '#22c55e',
      yellow: '#f59e0b',
      blue: '#6366f1',
      magenta: '#8b5cf6',
      cyan: '#06b6d4',
      white: '#eceef6',
    },
    fontSize: 13,
    fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace",
    convertEol: true,
    disableStdin: true,
    scrollback: 5000,
  })

  fitAddon = new FitAddon()
  terminal.loadAddon(fitAddon)
  terminal.loadAddon(new WebLinksAddon())

  terminal.open(terminalRef.value)
  fitAddon.fit()

  // Fetch historical logs from daemon
  if (props.serviceId) {
    try {
      const baseUrl = daemonBase()
      const token = daemonToken()
      const headers: Record<string, string> = {}
      if (token) headers['Authorization'] = `Bearer ${token}`

      const resp = await fetch(`${baseUrl}/api/services/${props.serviceId}/logs?lines=100`, { headers })
      if (resp.ok) {
        const logs = await resp.json() as string[]
        logs.forEach(line => terminal?.writeln(line))
      }
    } catch {
      // daemon may not be running
    }
  }

  // Subscribe to SSE log events and append matching lines
  if (props.serviceId) {
    const token = daemonToken()
    const url = token
      ? `${daemonBase()}/api/events?token=${encodeURIComponent(token)}`
      : `${daemonBase()}/api/events`
    const es = new EventSource(url)

    es.addEventListener('log', (e: MessageEvent) => {
      try {
        const data = JSON.parse(e.data) as { serviceId?: string; line?: string }
        if (data.serviceId === props.serviceId && data.line) {
          terminal?.writeln(data.line)
        }
      } catch { /* ignore parse errors */ }
    })

    closeEventSource = () => es.close()
  }

  const resizeObserver = new ResizeObserver(() => fitAddon?.fit())
  resizeObserver.observe(terminalRef.value)
})

onUnmounted(() => {
  closeEventSource?.()
  terminal?.dispose()
})
</script>

<style scoped>
.log-viewer { display: flex; flex-direction: column; height: 100%; }
.log-toolbar { display: flex; gap: 8px; padding: 8px 12px; background: var(--wdc-surface); border-bottom: 1px solid var(--wdc-border); }
.terminal-container { flex: 1; min-height: 200px; }
</style>
