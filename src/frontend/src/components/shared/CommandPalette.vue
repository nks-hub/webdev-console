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
        :placeholder="t('commandPalette.placeholder')"
        size="large"
        clearable
        @keydown.enter.prevent="executeFirst"
        @keydown.escape="close"
        @keydown.up.prevent="moveSelection(-1)"
        @keydown.down.prevent="moveSelection(1)"
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
          <el-icon class="cmd-icon"><component :is="cmd.icon" /></el-icon>
          <div class="cmd-info">
            <span class="cmd-label">{{ cmd.label }}</span>
            <span class="cmd-desc" v-if="cmd.description">{{ cmd.description }}</span>
          </div>
          <span class="cmd-shortcut" v-if="cmd.shortcut">{{ cmd.shortcut }}</span>
        </div>
      </div>
      <div class="command-empty" v-else-if="query">
        {{ t('commandPalette.empty') }}
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'

const { t } = useI18n()
import {
  Monitor, DataAnalysis, Coin, Lock, Grid, Box, Connection,
  Setting, Share, Message, Plus, Refresh, VideoPlay, VideoPause,
  Promotion, CircleClose, Edit, Link, DataLine, Key, Operation,
} from '@element-plus/icons-vue'
import { useServicesStore } from '../../stores/services'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'

const router = useRouter()
const servicesStore = useServicesStore()
const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()

const visible = ref(false)
const query = ref('')
const selectedIndex = ref(0)
// Template ref to the Element Plus <el-input> wrapper. We only call
// focus() on it, so pinning to a structural shape with that single
// method captures what the component actually uses without coupling to
// the full ElInput instance type (which changes across element-plus
// versions).
const inputRef = ref<{ focus: () => void } | null>(null)

interface Command {
  id: string
  label: string
  description?: string
  icon: Component
  shortcut?: string
  action: () => void
}

const commands = computed<Command[]>(() => [
  { id: 'sites', label: t('commandPalette.gotoSites'), icon: Monitor, shortcut: '', action: () => router.push('/sites') },
  { id: 'dashboard', label: t('commandPalette.gotoDashboard'), icon: DataAnalysis, action: () => router.push('/dashboard') },
  { id: 'databases', label: t('commandPalette.gotoDatabases'), icon: Coin, action: () => router.push('/databases') },
  { id: 'ssl', label: t('commandPalette.gotoSsl'), icon: Lock, action: () => router.push('/ssl') },
  { id: 'php', label: t('commandPalette.gotoPhp'), icon: Grid, action: () => router.push('/php') },
  { id: 'binaries', label: t('commandPalette.gotoBinaries'), icon: Box, action: () => router.push('/binaries') },
  { id: 'plugins', label: t('commandPalette.gotoPlugins'), icon: Share, action: () => router.push('/plugins') },
  { id: 'settings', label: t('commandPalette.gotoSettings'), icon: Setting, action: () => router.push('/settings') },
  { id: 'cloudflare', label: t('commandPalette.gotoCloudflare'), icon: Connection, action: () => router.push('/cloudflare') },
  // MCP shortcuts — Phase 8 redesign surfaces 4 quick jumps for the
  // most common operator paths into the MCP module.
  { id: 'mcp-activity', label: t('commandPalette.gotoMcpActivity'), description: t('commandPalette.gotoMcpActivityDesc'), icon: DataLine, action: () => router.push('/mcp/activity') },
  { id: 'mcp-requests', label: t('commandPalette.gotoMcpRequests'), description: t('commandPalette.gotoMcpRequestsDesc'), icon: Lock, action: () => router.push('/mcp/intents') },
  { id: 'mcp-rules', label: t('commandPalette.gotoMcpRules'), description: t('commandPalette.gotoMcpRulesDesc'), icon: Key, action: () => router.push('/mcp/grants') },
  { id: 'mcp-catalog', label: t('commandPalette.gotoMcpCatalog'), description: t('commandPalette.gotoMcpCatalogDesc'), icon: Operation, action: () => router.push('/mcp/kinds') },
  { id: 'mailpit-ui', label: t('commandPalette.openMailpit'), icon: Message, action: () => window.open('http://localhost:8025', '_blank') },
  { id: 'new-site', label: t('commandPalette.newSite'), icon: Plus, shortcut: 'Ctrl+N', action: () => router.push({ path: '/sites', query: { create: '1' } }) },
  { id: 'refresh', label: t('commandPalette.refresh'), icon: Refresh, shortcut: 'F5', action: () => daemonStore.poll() },
  ...daemonStore.services.map(svc => ({
    id: `start-${svc.id}`,
    label: t('commandPalette.startService', { name: svc.displayName || svc.id }),
    description: svc.state === 2 ? t('commandPalette.alreadyRunning') : '',
    icon: VideoPlay,
    action: () => { if (svc.state !== 2) servicesStore.start(svc.id) },
  })),
  ...daemonStore.services.map(svc => ({
    id: `stop-${svc.id}`,
    label: t('commandPalette.stopService', { name: svc.displayName || svc.id }),
    description: svc.state === 0 ? t('commandPalette.alreadyStopped') : '',
    icon: VideoPause,
    action: () => { if (svc.state === 2) servicesStore.stop(svc.id) },
  })),
  { id: 'start-all', label: t('commandPalette.startAll'), icon: Promotion, action: () => {
    daemonStore.services.filter(s => s.state === 0).forEach(s => servicesStore.start(s.id))
  }},
  { id: 'stop-all', label: t('commandPalette.stopAll'), icon: CircleClose, action: () => {
    daemonStore.services.filter(s => s.state === 2).forEach(s => servicesStore.stop(s.id))
  }},
  // Dynamic per-site commands — open in browser + edit
  ...sitesStore.sites.map(site => ({
    id: `open-site-${site.domain}`,
    label: t('commandPalette.openSite', { domain: site.domain }),
    icon: Link,
    action: () => {
      const proto = site.sslEnabled ? 'https' : 'http'
      const port = site.sslEnabled ? (site.httpsPort || 443) : (site.httpPort || 80)
      const suffix = (site.sslEnabled && port === 443) || (!site.sslEnabled && port === 80) ? '' : `:${port}`
      window.open(`${proto}://${site.domain}${suffix}`, '_blank')
    },
  })),
  ...sitesStore.sites.map(site => ({
    id: `edit-site-${site.domain}`,
    label: t('commandPalette.editSite', { domain: site.domain }),
    icon: Edit,
    action: () => router.push(`/sites/${site.domain}/edit`),
  })),
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

function moveSelection(delta: number) {
  const len = filteredCommands.value.length
  if (len === 0) return
  selectedIndex.value = (selectedIndex.value + delta + len) % len
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
  gap: 12px;
  padding: 12px 14px;
  border-radius: var(--wdc-radius);
  cursor: pointer;
  transition: background 0.1s, box-shadow 0.1s;
  border-left: 3px solid transparent;
}
.command-item:hover {
  background: var(--wdc-hover);
}
.command-item.selected {
  background: var(--wdc-accent-dim);
  border-left-color: var(--wdc-accent);
  box-shadow: 0 0 0 1px var(--wdc-accent-glow);
}
.command-item.selected .cmd-label { color: var(--wdc-accent); font-weight: 600; }

.cmd-icon {
  font-size: 1rem;
  width: 24px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
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
  border: 1px solid var(--wdc-border);
  padding: 2px 7px;
  border-radius: var(--wdc-radius-sm);
  font-family: 'JetBrains Mono', monospace;
  box-shadow: 0 1px 0 var(--wdc-border-strong);
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
