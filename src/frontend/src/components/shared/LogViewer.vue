<template>
  <div class="log-viewer">
    <div class="log-toolbar">
      <el-select v-model="logLevel" size="small" style="width: 120px" :aria-label="t('logs.viewer.levelLabel')">
        <el-option :label="t('logs.viewer.levelAll')" value="all" />
        <el-option :label="t('logs.viewer.levelError')" value="error" />
        <el-option :label="t('logs.viewer.levelWarn')" value="warn" />
        <el-option :label="t('logs.viewer.levelInfo')" value="info" />
      </el-select>
      <el-input
        v-model="searchText"
        size="small"
        :placeholder="t('logs.viewer.search')"
        :aria-label="t('logs.viewer.search')"
        clearable
        style="width: 160px"
        @keydown.enter="searchInTerminal"
      />
      <el-button size="small" @click="searchInTerminal" :disabled="!searchText">{{ t('logs.viewer.find') }}</el-button>
      <el-button size="small" @click="copyLogs">{{ t('logs.viewer.copy') }}</el-button>
      <el-button size="small" @click="clearTerminal">{{ t('logs.viewer.clear') }}</el-button>
    </div>
    <p v-if="notice" class="log-notice" role="status">{{ notice }}</p>
    <div ref="terminalRef" class="terminal-container" />
  </div>
</template>

<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted, onActivated, onDeactivated } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import { SearchAddon } from '@xterm/addon-search'
import '@xterm/xterm/css/xterm.css'
import { daemonBaseUrl, daemonToken, subscribeEventsMap } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { matchesLogLevel, stripStreamTag, type LogLevelFilter } from '../../utils/logLevel'
import { resolveToken, rgba } from '../../utils/themeColors'

const { t } = useI18n()

const props = defineProps<{ serviceId?: string }>()
const terminalRef = ref<HTMLElement>()
const logLevel = ref<LogLevelFilter>('all')
const searchText = ref('')
const notice = ref('')

// Matches the scrollback: the Phase 4 acceptance criterion ("handles 10k+
// lines without lag") and e2e scenario #14 fetch 10 000 lines.
const MAX_LINES = 10000
// Every line received, so the level filter can redraw without refetching.
let lines: string[] = []
let terminal: Terminal | null = null
let fitAddon: FitAddon | null = null
let searchAddon: SearchAddon | null = null
let closeEventSource: (() => void) | null = null
let watchTimer: ReturnType<typeof setInterval> | null = null
let resizeObserver: ResizeObserver | null = null
// Bumped by every start()/stop(): a history fetch that resolves after the
// viewer was hidden or closed must not subscribe (the subscription would
// leak and keep writing to a hidden or disposed terminal).
let generation = 0
let live = false

function append(line: string) {
  lines.push(line)
  if (lines.length > MAX_LINES) lines = lines.slice(-MAX_LINES)
  if (matchesLogLevel(line, logLevel.value)) terminal?.writeln(stripStreamTag(line))
}

function redraw() {
  if (!terminal) return
  terminal.clear()
  terminal.reset()
  for (const line of lines) if (matchesLogLevel(line, logLevel.value)) terminal.writeln(stripStreamTag(line))
}
watch(logLevel, redraw)

function clearTerminal() {
  lines = []
  terminal?.clear()
}

function searchInTerminal() {
  if (!searchText.value || !searchAddon) return
  searchAddon.findNext(searchText.value)
}

async function copyLogs() {
  if (!terminal) return
  const text = terminal.getSelection()
    || lines.filter(l => matchesLogLevel(l, logLevel.value)).map(stripStreamTag).join('\n').trimEnd()
  try {
    await navigator.clipboard.writeText(text)
    ElMessage.success(t('logs.viewer.copied'))
  } catch (e) {
    ElMessage.error(t('logs.viewer.copyFailed', { err: errorMessage(e) }))
  }
}

function authHeaders(): Record<string, string> {
  const token = daemonToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** Loads the log history, then follows new lines while the viewer is visible. */
async function start() {
  if (live || !props.serviceId || !terminal) return
  live = true
  const run = ++generation
  const id = props.serviceId
  notice.value = ''
  lines = []
  terminal.clear()
  terminal.reset()

  try {
    const resp = await fetch(`${daemonBaseUrl()}/api/services/${encodeURIComponent(id)}/logs?lines=100`, { headers: authHeaders() })
    if (run !== generation) return
    if (resp.status === 404) {
      notice.value = t('logs.viewer.noLog')
    } else if (!resp.ok) {
      notice.value = t('logs.viewer.loadFailed', { err: `HTTP ${resp.status}` })
    } else {
      const history = await resp.json() as string[]
      if (run !== generation) return
      if (!Array.isArray(history) || history.length === 0) notice.value = t('logs.viewer.noLog')
      else history.forEach(append)
    }
  } catch (e) {
    if (run !== generation) return
    notice.value = t('logs.viewer.loadFailed', { err: errorMessage(e) })
  }

  // The daemon only tails a service's log while a viewer renews its watch
  // lease (30 s), so a hidden viewer lets the lease lapse.
  const renew = () => {
    void fetch(`${daemonBaseUrl()}/api/services/${encodeURIComponent(id)}/logs/watch`, {
      method: 'POST',
      headers: authHeaders(),
    }).catch(() => { /* daemon restarting: the next renewal retries */ })
  }
  renew()
  watchTimer = setInterval(renew, 15_000)
  // Shared /api/events connection (see subscribeEventsMap). The daemon's
  // log events carry the text as `message`; older builds used `line`.
  closeEventSource = subscribeEventsMap({
    log: (raw) => {
      const data = raw as { serviceId?: string; line?: string; message?: string }
      const text = data.line ?? data.message
      if (data.serviceId !== id || !text) return
      notice.value = ''
      append(text)
    },
  })
}

function stop() {
  generation++
  live = false
  closeEventSource?.()
  closeEventSource = null
  if (watchTimer) clearInterval(watchTimer)
  watchTimer = null
}

function themeColors() {
  const bg = rgba(resolveToken('--wdc-log-bg', { r: 17, g: 21, b: 28 }))
  const fg = rgba(resolveToken('--wdc-log-text', { r: 227, g: 231, b: 238 }))
  const accent = resolveToken('--wdc-accent', { r: 143, g: 180, b: 255 })
  return {
    background: bg,
    foreground: fg,
    cursor: rgba(accent),
    selectionBackground: rgba(accent, 0.3),
  }
}

onMounted(() => {
  if (!terminalRef.value) return

  terminal = new Terminal({
    theme: themeColors(),
    fontSize: 13,
    fontFamily: "'JetBrains Mono', 'Cascadia Code', Consolas, monospace",
    convertEol: true,
    disableStdin: true,
    scrollback: MAX_LINES,
  })

  fitAddon = new FitAddon()
  terminal.loadAddon(fitAddon)
  terminal.loadAddon(new WebLinksAddon())
  searchAddon = new SearchAddon()
  terminal.loadAddon(searchAddon)

  terminal.open(terminalRef.value)
  fitAddon.fit()

  resizeObserver = new ResizeObserver(() => fitAddon?.fit())
  resizeObserver.observe(terminalRef.value)

  void start()
})

// Inside a kept-alive page the viewer is hidden, not unmounted: stop the
// watch lease and the SSE subscription while hidden, reload on return.
onActivated(() => { void start() })
onDeactivated(stop)

onUnmounted(() => {
  stop()
  resizeObserver?.disconnect()
  resizeObserver = null
  terminal?.dispose()
  terminal = null
  fitAddon = null
  searchAddon = null
})
</script>

<style scoped>
.log-viewer { display: flex; flex-direction: column; height: 100%; min-height: 240px; }
.log-toolbar { display: flex; flex-wrap: wrap; gap: 8px; padding: 8px 12px; background: var(--wdc-surface); border-bottom: 1px solid var(--wdc-border); }
.log-toolbar :deep(.el-button + .el-button) { margin-left: 0; }
.log-notice { margin: 0; padding: 8px 12px; color: var(--wdc-text-3); font-size: 0.82rem; background: var(--wdc-surface); }
.terminal-container { flex: 1; min-height: 200px; padding: 6px 0 0 8px; background: var(--wdc-log-bg); }
</style>
