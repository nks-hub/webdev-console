<!--
  Real-time log viewer using a simple textarea.
  Replaces xterm.js for the POC stage; swap in Terminal from xterm.js for production.
-->
<template>
  <div class="log-viewer">
    <div class="log-toolbar">
      <el-select v-model="serviceId" size="small" placeholder="Service" style="width: 150px">
        <el-option
          v-for="s in services"
          :key="s.id"
          :label="s.name"
          :value="s.id"
        />
      </el-select>

      <el-select v-model="levelFilter" size="small" style="width: 100px">
        <el-option label="All" value="all" />
        <el-option label="Info" value="info" />
        <el-option label="Warn" value="warn" />
        <el-option label="Error" value="error" />
      </el-select>

      <el-input
        v-model="searchTerm"
        size="small"
        placeholder="Filter..."
        clearable
        style="width: 180px"
      />

      <el-button size="small" @click="copyLogs">Copy</el-button>
      <el-button size="small" :type="autoScroll ? 'primary' : ''" @click="autoScroll = !autoScroll">
        {{ autoScroll ? 'Auto-scroll On' : 'Auto-scroll Off' }}
      </el-button>
    </div>

    <div ref="logContainerRef" class="log-output" @scroll="onScroll">
      <div
        v-for="(line, idx) in filteredLines"
        :key="idx"
        class="log-line"
        :class="[`level-${line.level}`]"
      >
        <span class="timestamp">{{ line.timestamp }}</span>
        <span class="level-badge">{{ line.level.toUpperCase() }}</span>
        <span class="text">{{ line.message }}</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue'
import { useDaemonStore } from '../../stores/daemon'

interface LogLine {
  timestamp: string
  level: string
  message: string
}

const props = withDefaults(defineProps<{
  serviceId?: string
}>(), { serviceId: '' })

const daemonStore = useDaemonStore()
const services = computed(() => daemonStore.status?.services ?? [])

const serviceId = ref(props.serviceId || services.value[0]?.id || '')
const levelFilter = ref('all')
const searchTerm = ref('')
const autoScroll = ref(true)
const lines = ref<LogLine[]>([])
const logContainerRef = ref<HTMLElement | null>(null)

let eventSource: EventSource | null = null

const filteredLines = computed(() => {
  return lines.value.filter(l => {
    if (levelFilter.value !== 'all' && l.level !== levelFilter.value) return false
    if (searchTerm.value && !l.message.toLowerCase().includes(searchTerm.value.toLowerCase())) return false
    return true
  })
})

watch(serviceId, (id) => {
  if (id) connectStream(id)
}, { immediate: true })

function connectStream(id: string) {
  eventSource?.close()
  lines.value = []
  const port = window.daemonApi?.getPort() ?? 50051
  eventSource = new EventSource(`http://localhost:${port}/api/logs/${id}/stream`)
  eventSource.onmessage = (e: MessageEvent) => {
    try {
      const entry = JSON.parse(e.data as string) as LogLine
      lines.value.push(entry)
      if (lines.value.length > 2000) lines.value.splice(0, 200)
      if (autoScroll.value) void nextTick(scrollToBottom)
    } catch { /* ignore */ }
  }
}

function scrollToBottom() {
  const el = logContainerRef.value
  if (el) el.scrollTop = el.scrollHeight
}

function onScroll() {
  const el = logContainerRef.value
  if (!el) return
  autoScroll.value = el.scrollTop + el.clientHeight >= el.scrollHeight - 20
}

function copyLogs() {
  const text = filteredLines.value.map(l => `${l.timestamp} [${l.level}] ${l.message}`).join('\n')
  void navigator.clipboard.writeText(text)
}

import { onUnmounted } from 'vue'
onUnmounted(() => eventSource?.close())
</script>

<style scoped>
.log-viewer { display: flex; flex-direction: column; height: 100%; }
.log-toolbar { display: flex; align-items: center; gap: 8px; padding: 8px; flex-shrink: 0; flex-wrap: wrap; }
.log-output {
  flex: 1;
  overflow-y: auto;
  background: #0d0f1a;
  padding: 8px;
  font-family: 'Consolas', 'JetBrains Mono', monospace;
  font-size: 0.78rem;
  line-height: 1.6;
  min-height: 200px;
}
.log-line { display: flex; gap: 8px; }
.timestamp { color: #5a6070; flex-shrink: 0; }
.level-badge { flex-shrink: 0; width: 36px; font-weight: 600; }
.level-info  .level-badge { color: #38bdf8; }
.level-warn  .level-badge { color: #f59e0b; }
.level-error .level-badge { color: #ef4444; }
.text { color: #c9d1d9; word-break: break-all; }
</style>
