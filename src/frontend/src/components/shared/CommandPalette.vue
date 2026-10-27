<template>
  <el-dialog
    v-model="visible"
    :show-close="false"
    :title="t('commandPalette.title')"
    :aria-label="t('commandPalette.title')"
    width="520px"
    class="command-dialog"
    @opened="focusInput"
    @closed="restoreFocus"
    @close="close"
  >
    <div class="command-palette">
      <el-input
        ref="inputRef"
        v-model="query"
        :placeholder="t('commandPalette.placeholder')"
        :aria-label="t('commandPalette.placeholder')"
        aria-autocomplete="list"
        aria-haspopup="listbox"
        :aria-expanded="results.length > 0"
        aria-controls="cmd-listbox"
        :aria-activedescendant="results.length ? `cmd-opt-${selectedIndex}` : undefined"
        size="large"
        clearable
        @keydown.enter.prevent="executeSelected"
        @keydown.escape="close"
        @keydown.up.prevent="moveSelection(-1)"
        @keydown.down.prevent="moveSelection(1)"
      />
      <div id="cmd-listbox" class="command-list" role="listbox" :aria-label="t('commandPalette.title')" v-if="results.length">
        <template v-for="(entry, i) in results" :key="entry.cmd.id">
          <div v-if="entry.heading" class="command-group" role="presentation">{{ entry.heading }}</div>
          <div
            :id="`cmd-opt-${i}`"
            class="command-item"
            :class="{ selected: i === selectedIndex }"
            role="option"
            :aria-selected="i === selectedIndex"
            @click="execute(entry.cmd)"
            @mouseenter="selectedIndex = i"
          >
            <el-icon class="cmd-icon" aria-hidden="true"><component :is="entry.cmd.icon" /></el-icon>
            <div class="cmd-info">
              <span class="cmd-label">
                <template v-for="(part, k) in highlight(entry.cmd.label)" :key="k"><mark v-if="part.hit">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template>
              </span>
              <span class="cmd-desc" v-if="entry.cmd.description">{{ entry.cmd.description }}</span>
            </div>
            <span class="cmd-tag" v-if="entry.cmd.advanced && uiModeStore.isSimple">{{ t('commandPalette.advancedTag') }}</span>
            <span class="cmd-shortcut" v-if="entry.cmd.shortcut">{{ entry.cmd.shortcut }}</span>
          </div>
        </template>
      </div>
      <div class="command-empty" role="status" v-else>
        {{ t('commandPalette.empty') }}
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, watch, nextTick, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import {
  Monitor, DataBoard, Coin, Lock, Grid, Box, Connection, Setting, Share, Message, Plus, Refresh,
  VideoPlay, VideoPause, Promotion, CircleClose, Edit, Link, DataLine, Key, Operation, SetUp,
  FolderOpened, Download,
} from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import { isRunning, serviceViewState, shortServiceName } from '../../utils/serviceState'
import { runServiceAction, runServiceBatch } from '../../utils/serviceActions'
import { openMailpit } from '../../utils/mailpit'
import { manageRouteOf } from '../../utils/services'

const { t } = useI18n()
const router = useRouter()
const uiModeStore = useUiModeStore()
const featureFlagsStore = useFeatureFlagsStore()
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
const inputRef = ref<{ focus: () => void; input?: HTMLInputElement } | null>(null)

type Group = 'recommended' | 'recent' | 'navigate' | 'sites' | 'services' | 'actions'
const GROUP_ORDER: Group[] = ['recent', 'recommended', 'navigate', 'sites', 'services', 'actions']

interface Command {
  id: string
  group: Group
  label: string
  description?: string
  icon: Component
  shortcut?: string
  /** Extra search words (not shown). */
  keywords?: string
  /** Page that only exists in the Advanced menu; tagged in Simple mode. */
  advanced?: boolean
  /** Also offered in the empty-query "recommended" list. */
  recommended?: boolean
  action: () => void
}

// Remembered across sessions: the last few commands that were run.
const RECENT_KEY = 'wdc.palette.recent'
const RECENT_MAX = 4
function readRecent(): string[] {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(RECENT_KEY) ?? '[]')
    return Array.isArray(raw) ? raw.filter((x): x is string => typeof x === 'string') : []
  } catch { return [] }
}
const recentIds = ref<string[]>(readRecent())
function remember(id: string) {
  recentIds.value = [id, ...recentIds.value.filter(x => x !== id)].slice(0, RECENT_MAX)
  try { localStorage.setItem(RECENT_KEY, JSON.stringify(recentIds.value)) } catch { /* private mode */ }
}

function go(path: string, label: string, icon: Component, extra: Partial<Command> = {}): Command {
  const target = router.resolve(path)
  return {
    id: `go:${path}`,
    group: 'navigate',
    label,
    icon,
    advanced: !!target.meta.advancedNav,
    action: () => { void router.push(path) },
    ...extra,
  }
}

// Pages in Simple mode stay reachable here (tagged "Advanced"), unlike the
// menu, which leaves them out.
const navigation = computed<Command[]>(() => {
  const nav: Command[] = [
    go('/dashboard', t('commandPalette.gotoDashboard'), DataBoard, { recommended: true }),
    go('/sites', t('commandPalette.gotoSites'), Monitor, { recommended: true }),
    go('/services', t('commandPalette.gotoServices'), SetUp, { recommended: true }),
    go('/databases', t('commandPalette.gotoDatabases'), Coin),
    go('/ssl', t('commandPalette.gotoSsl'), Lock),
    go('/php', t('commandPalette.gotoPhp'), Grid),
    go('/binaries', t('commandPalette.gotoBinaries'), Box),
    go('/plugins', t('commandPalette.gotoPlugins'), Share),
    go('/cloudflare', t('commandPalette.gotoCloudflare'), Connection),
    go('/backups', t('nav.backups'), FolderOpened),
    go('/settings', t('commandPalette.gotoSettings'), Setting, { recommended: true }),
  ]
  // Always listed; while MCP is off the page itself offers the switch.
  const off = !featureFlagsStore.mcpEnabled
  const desc = (key: string) => off ? t('commandPalette.mcpOff') : t(key)
  nav.push(
    go('/mcp/activity', t('commandPalette.gotoMcpActivity'), DataLine, { description: desc('commandPalette.gotoMcpActivityDesc') }),
    go('/mcp/intents', t('commandPalette.gotoMcpRequests'), Lock, { description: desc('commandPalette.gotoMcpRequestsDesc') }),
    go('/mcp/grants', t('commandPalette.gotoMcpRules'), Key, { description: desc('commandPalette.gotoMcpRulesDesc') }),
    go('/mcp/kinds', t('commandPalette.gotoMcpCatalog'), Operation, { description: desc('commandPalette.gotoMcpCatalogDesc') }),
  )
  return nav
})

function siteUrl(site: { domain: string; sslEnabled?: boolean; httpPort?: number; httpsPort?: number }): string {
  const proto = site.sslEnabled ? 'https' : 'http'
  const port = site.sslEnabled ? (site.httpsPort || 443) : (site.httpPort || 80)
  const suffix = (site.sslEnabled && port === 443) || (!site.sslEnabled && port === 80) ? '' : `:${port}`
  return `${proto}://${site.domain}${suffix}`
}

const siteCommands = computed<Command[]>(() => sitesStore.sites.flatMap(site => [
  {
    id: `open-site:${site.domain}`,
    group: 'sites' as const,
    label: t('commandPalette.openSite', { domain: site.domain }),
    icon: Link,
    action: () => { window.open(siteUrl(site), '_blank') },
  },
  {
    id: `edit-site:${site.domain}`,
    group: 'sites' as const,
    label: t('commandPalette.editSite', { domain: site.domain }),
    icon: Edit,
    action: () => { void router.push(`/sites/${encodeURIComponent(site.domain)}/edit`) },
  },
]))

// Only actions that make sense for the service's state: no "start" on a
// running service, nothing to start on one that is not installed (install instead).
const serviceCommands = computed<Command[]>(() => daemonStore.visibleServices.flatMap(svc => {
  const name = shortServiceName(svc)
  const state = serviceViewState(svc)
  const command = (id: string, label: string, icon: Component, action: () => void): Command =>
    ({ id: `${id}:${svc.id}`, group: 'services', label, icon, action })

  if (state === 'notInstalled') {
    return [command('install', t('commandPalette.installService', { name }), Download,
      () => { void router.push({ path: '/binaries', query: { app: svc.binaryApp ?? svc.id } }) })]
  }
  const out: Command[] = []
  if (state === 'stopped' || state === 'crashed') {
    out.push(command('start', t('commandPalette.startService', { name }), VideoPlay, () => { void runServiceAction(svc, 'start') }))
  } else if (isRunning(svc)) {
    out.push(command('stop', t('commandPalette.stopService', { name }), VideoPause, () => { void runServiceAction(svc, 'stop') }))
    out.push(command('restart', t('commandPalette.restartService', { name }), Refresh, () => { void runServiceAction(svc, 'restart') }))
  }
  // The page that holds the service's settings, logs and versions.
  const manage = manageRouteOf(svc.id)
  if (manage) out.push(command('manage', t('commandPalette.manageService', { name }), Setting, () => { void router.push(manage) }))
  return out
}))

const actionCommands = computed<Command[]>(() => {
  const services = daemonStore.visibleServices
  const mailpit = services.find(s => s.id === 'mailpit')
  const startable = services.filter(s => serviceViewState(s) === 'stopped')
  const out: Command[] = [
    {
      id: 'new-site', group: 'actions', label: t('commandPalette.newSite'), icon: Plus, shortcut: 'Ctrl+N',
      recommended: true, action: () => { void router.push({ path: '/sites', query: { create: '1' } }) },
    },
    { id: 'refresh', group: 'actions', label: t('commandPalette.refresh'), icon: Refresh, shortcut: 'F5', action: () => { void daemonStore.poll() } },
  ]
  if (mailpit && isRunning(mailpit)) {
    out.push({ id: 'mailpit-ui', group: 'actions', label: t('commandPalette.openMailpit'), icon: Message, action: openMailpit })
  }
  if (startable.length) {
    out.push({ id: 'start-all', group: 'actions', label: t('commandPalette.startAll'), icon: Promotion, action: () => { void runServiceBatch('start', startable) } })
  }
  if (daemonStore.runningServices.length) {
    out.push({ id: 'stop-all', group: 'actions', label: t('commandPalette.stopAll'), icon: CircleClose, action: () => { void runServiceBatch('stop', daemonStore.runningServices) } })
  }
  return out
})

const allCommands = computed<Command[]>(() => [
  ...navigation.value, ...siteCommands.value, ...serviceCommands.value, ...actionCommands.value,
])

// Matching ignores case and diacritics: "prehled" finds "Přehled".
function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase()
}

interface Entry { cmd: Command; heading?: string }

const results = computed<Entry[]>(() => {
  const q = fold(query.value.trim())
  let picked: Command[]
  if (!q) {
    const byId = new Map(allCommands.value.map(c => [c.id, c]))
    const recent = recentIds.value.map(id => byId.get(id)).filter((c): c is Command => !!c)
    const recentSet = new Set(recent.map(c => c.id))
    const recommended = allCommands.value.filter(c => c.recommended && !recentSet.has(c.id))
    picked = [
      ...recent.map(c => ({ ...c, group: 'recent' as const })),
      ...recommended.map(c => ({ ...c, group: 'recommended' as const })),
    ]
  } else {
    // Label matches first (prefix before substring), then description / keyword matches.
    const scored = allCommands.value.flatMap(c => {
      const label = fold(c.label)
      const rest = fold(`${c.description ?? ''} ${c.keywords ?? ''}`)
      const score = label.startsWith(q) ? 0 : label.includes(q) ? 1 : rest.includes(q) ? 2 : -1
      return score < 0 ? [] : [{ c, score }]
    })
    scored.sort((a, b) => a.score - b.score)
    picked = scored.slice(0, 30).map(s => s.c)
  }
  const ordered = [...picked].sort((a, b) =>
    q ? 0 : GROUP_ORDER.indexOf(a.group) - GROUP_ORDER.indexOf(b.group))
  let lastGroup: Group | null = null
  return ordered.map(cmd => {
    const heading = !q && cmd.group !== lastGroup ? t(`commandPalette.groups.${cmd.group}`) : undefined
    lastGroup = cmd.group
    return { cmd, heading }
  })
})

// Splits a label around the matched text so it can be marked without v-html.
function highlight(label: string): Array<{ text: string; hit: boolean }> {
  const q = query.value.trim()
  if (!q) return [{ text: label, hit: false }]
  const start = fold(label).indexOf(fold(q))
  // Folding never changes the length of Latin/Czech text, so offsets carry over.
  if (start < 0 || fold(label).length !== label.length) return [{ text: label, hit: false }]
  const end = start + q.length
  return [
    { text: label.slice(0, start), hit: false },
    { text: label.slice(start, end), hit: true },
    { text: label.slice(end), hit: false },
  ].filter(p => p.text)
}

watch(query, () => { selectedIndex.value = 0 })

// ElInput drops a `role` attribute, so the combobox role goes on the <input> itself.
function focusInput() {
  inputRef.value?.input?.setAttribute('role', 'combobox')
  inputRef.value?.focus()
}

// Focus goes back to whatever opened the palette (the header search, a menu entry).
let opener: HTMLElement | null = null
function restoreFocus() {
  const el = opener
  opener = null
  if (el?.isConnected && document.activeElement === document.body) el.focus()
}

function open() {
  if (!visible.value) {
    opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    query.value = ''
    selectedIndex.value = 0
    visible.value = true
  }
  nextTick(focusInput)
}

function close() {
  visible.value = false
}

function execute(cmd: Command) {
  remember(cmd.id)
  close()
  cmd.action()
}

function executeSelected() {
  const entry = results.value[selectedIndex.value]
  if (entry) execute(entry.cmd)
}

function moveSelection(delta: number) {
  const len = results.value.length
  if (len === 0) return
  selectedIndex.value = (selectedIndex.value + delta + len) % len
  void nextTick(() => document.getElementById(`cmd-opt-${selectedIndex.value}`)?.scrollIntoView({ block: 'nearest' }))
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
  margin-top: var(--wdc-space-2);
}

.command-item {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-3) var(--wdc-space-3-5);
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
.command-item.selected .cmd-label { color: var(--wdc-accent-ink); font-weight: 600; }

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

.command-group {
  margin: var(--wdc-space-2) var(--wdc-space-3-5) var(--wdc-space-1);
  font-family: var(--wdc-font-display);
  font-size: 0.72rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--wdc-text-3);
}
.cmd-label mark {
  background: transparent;
  color: var(--wdc-accent-ink);
  font-weight: 600;
  text-decoration: underline;
  text-decoration-color: var(--wdc-accent);
  text-underline-offset: 0.15em;
}
.cmd-tag {
  font-size: 0.68rem;
  color: var(--wdc-text-3);
  border: 1px solid var(--wdc-border);
  padding: 0 var(--wdc-space-1-5);
  border-radius: 999px;
}

.cmd-shortcut {
  font-size: 0.68rem;
  color: var(--wdc-text-3);
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  padding: var(--wdc-space-0-5) var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  font-family: 'JetBrains Mono', monospace;
  box-shadow: 0 1px 0 var(--wdc-border-strong);
}

.command-empty {
  padding: var(--wdc-space-5);
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
