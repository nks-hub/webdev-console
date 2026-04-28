<template>
  <div class="log-viewer">
    <div class="log-toolbar">
      <el-select v-model="logLevel" size="small" style="width: 100px">
        <el-option :label="t('logs.viewer.levelAll')" value="all" />
        <el-option :label="t('logs.viewer.levelError')" value="error" />
        <el-option :label="t('logs.viewer.levelWarn')" value="warn" />
        <el-option :label="t('logs.viewer.levelInfo')" value="info" />
      </el-select>
      <el-input
        v-model="searchText"
        size="small"
        :placeholder="t('logs.viewer.search')"
        clearable
        style="width: 160px"
        @keydown.enter="searchInTerminal"
      />
      <el-button size="small" @click="searchInTerminal" :disabled="!searchText">{{ t('logs.viewer.find') }}</el-button>
      <el-button size="small" @click="copyLogs">{{ t('logs.viewer.copy') }}</el-button>
      <el-button size="small" @click="clearTerminal">{{ t('logs.viewer.clear') }}</el-button>
    </div>
    <div ref="terminalRef" class="terminal-container" />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import { SearchAddon } from '@xterm/addon-search'
import '@xterm/xterm/css/xterm.css'
import { daemonBaseUrl, daemonToken } from '../../api/daemon'

const { t } = useI18n()

// daemonApi is declared as non-optional in ../../api/daemon.ts; the local
// redeclaration here used to mark it optional, which produced conflicting
// `declare global` merges. Dropped — we lean on the central declaration
// and guard access with optional-chaining where needed.

const props = defineProps<{ serviceId?: string }>()
const terminalRef = ref<HTMLElement>()
const logLevel = ref('all')
const searchText = ref('')

let terminal: Terminal | null = null
let fitAddon: FitAddon | null = null
let searchAddon: SearchAddon | null = null
let closeEventSource: (() => void) | null = null

function clearTerminal() {
  terminal?.clear()
}

function searchInTerminal() {
  if (!searchText.value || !searchAddon) return
  searchAddon.findNext(searchText.value)
}

function copyLogs() {
  if (!terminal) return
  const sel = terminal.getSelection()
  if (sel) {
    navigator.clipboard.writeText(sel)
  } else {
    // Copy all visible buffer
    const buf = terminal.buffer.active
    const lines: string[] = []
    for (let i = 0; i < buf.length; i++) {
      const line = buf.getLine(i)
      if (line) lines.push(line.translateToString())
    }
    navigator.clipboard.writeText(lines.join('\n').trimEnd())
  }
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
    // Scrollback sized to match the Phase 4 acceptance criterion
    // ("handles 10k+ lines without lag") and e2e scenario #14 which
    // fetches 10 000 lines from /api/services/{id}/logs.
    scrollback: 10000,
  })

  fitAddon = new FitAddon()
  terminal.loadAddon(fitAddon)
  terminal.loadAddon(new WebLinksAddon())
  searchAddon = new SearchAddon()
  terminal.loadAddon(searchAddon)

  terminal.open(terminalRef.value)
  fitAddon.fit()

  // Fetch historical logs from daemon
  if (props.serviceId) {
    try {
      const baseUrl = daemonBaseUrl()
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
      ? `${daemonBaseUrl()}/api/events?token=${encodeURIComponent(token)}`
      : `${daemonBaseUrl()}/api/events`
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
