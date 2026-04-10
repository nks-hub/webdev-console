<template>
  <el-dialog
    v-model="visible"
    :show-close="false"
    width="520px"
    class="command-dialog"
    @close="close"
  >
    <div class="command-palette">
      <el-input
        ref="inputRef"
        v-model="query"
        placeholder="Type a command..."
        size="large"
        clearable
        @keydown.enter="executeFirst"
        @keydown.escape="close"
      />
      <div class="command-list" v-if="filteredCommands.length">
        <div
          v-for="(cmd, i) in filteredCommands"
          :key="cmd.id"
          class="command-item"
          :class="{ selected: i === selectedIndex }"
          @click="execute(cmd)"
          @mouseenter="selectedIndex = i"
        >
          <span class="cmd-icon">{{ cmd.icon }}</span>
          <div class="cmd-info">
            <span class="cmd-label">{{ cmd.label }}</span>
            <span class="cmd-desc" v-if="cmd.description">{{ cmd.description }}</span>
          </div>
          <span class="cmd-shortcut" v-if="cmd.shortcut">{{ cmd.shortcut }}</span>
        </div>
      </div>
      <div class="command-empty" v-else-if="query">
        No matching commands
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { useServicesStore } from '../../stores/services'
import { useDaemonStore } from '../../stores/daemon'

const router = useRouter()
const servicesStore = useServicesStore()
const daemonStore = useDaemonStore()

const visible = ref(false)
const query = ref('')
const selectedIndex = ref(0)
const inputRef = ref<any>(null)

interface Command {
  id: string
  label: string
  description?: string
  icon: string
  shortcut?: string
  action: () => void
}

const commands = computed<Command[]>(() => [
  { id: 'sites', label: 'Go to Sites', icon: '🌐', shortcut: '', action: () => router.push('/sites') },
  { id: 'dashboard', label: 'Go to Dashboard', icon: '📊', action: () => router.push('/dashboard') },
  { id: 'binaries', label: 'Go to Binaries', icon: '📦', action: () => router.push('/binaries') },
  { id: 'plugins', label: 'Go to Plugins', icon: '🔌', action: () => router.push('/plugins') },
  { id: 'settings', label: 'Go to Settings', icon: '⚙️', action: () => router.push('/settings') },
  { id: 'new-site', label: 'Create New Site', icon: '➕', shortcut: 'Ctrl+N', action: () => { router.push('/sites'); /* TODO: auto-open dialog */ } },
  ...daemonStore.services.map((svc: any) => ({
    id: `start-${svc.id}`,
    label: `Start ${svc.displayName || svc.id}`,
    description: svc.state === 2 ? 'Already running' : '',
    icon: '▶',
    action: () => { if (svc.state !== 2) servicesStore.start(svc.id) },
  })),
  ...daemonStore.services.map((svc: any) => ({
    id: `stop-${svc.id}`,
    label: `Stop ${svc.displayName || svc.id}`,
    description: svc.state === 0 ? 'Already stopped' : '',
    icon: '⏹',
    action: () => { if (svc.state === 2) servicesStore.stop(svc.id) },
  })),
  { id: 'start-all', label: 'Start All Services', icon: '🚀', action: () => {
    daemonStore.services.filter((s: any) => s.state === 0).forEach((s: any) => servicesStore.start(s.id))
  }},
  { id: 'stop-all', label: 'Stop All Services', icon: '🛑', action: () => {
    daemonStore.services.filter((s: any) => s.state === 2).forEach((s: any) => servicesStore.stop(s.id))
  }},
])

const filteredCommands = computed(() => {
  if (!query.value) return commands.value.slice(0, 10)
  const q = query.value.toLowerCase()
  return commands.value.filter(c =>
    c.label.toLowerCase().includes(q) ||
    c.id.toLowerCase().includes(q) ||
    (c.description ?? '').toLowerCase().includes(q)
  ).slice(0, 10)
})

watch(query, () => { selectedIndex.value = 0 })

function open() {
  visible.value = true
  query.value = ''
  selectedIndex.value = 0
  nextTick(() => inputRef.value?.focus())
}

function close() {
  visible.value = false
}

function execute(cmd: Command) {
  cmd.action()
  close()
}

function executeFirst() {
  const cmd = filteredCommands.value[selectedIndex.value]
  if (cmd) execute(cmd)
}

defineExpose({ open })
</script>

<style scoped>
.command-palette {
  display: flex;
  flex-direction: column;
}

.command-list {
  max-height: 320px;
  overflow-y: auto;
  margin-top: 8px;
}

.command-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
  transition: background 0.1s;
}
.command-item:hover,
.command-item.selected {
  background: var(--wdc-hover);
}

.cmd-icon {
  font-size: 1rem;
  width: 24px;
  text-align: center;
  flex-shrink: 0;
}

.cmd-info {
  flex: 1;
  display: flex;
  flex-direction: column;
}

.cmd-label {
  font-size: 0.88rem;
  color: var(--wdc-text);
}

.cmd-desc {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}

.cmd-shortcut {
  font-size: 0.68rem;
  color: var(--wdc-text-3);
  background: var(--wdc-surface-2);
  padding: 2px 6px;
  border-radius: 3px;
  font-family: 'JetBrains Mono', monospace;
}

.command-empty {
  padding: 24px;
  text-align: center;
  color: var(--wdc-text-3);
  font-size: 0.85rem;
}
</style>

<style>
.command-dialog .el-dialog__header { display: none; }
.command-dialog .el-dialog__body { padding: 12px; }
.command-dialog .el-dialog { border-radius: var(--wdc-radius-lg) !important; }
</style>
