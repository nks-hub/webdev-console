<template>
  <nav class="sidebar" :aria-label="$t('nav.mainNavigation')">
    <a
      v-activate:link
      class="brand"
      :aria-label="$t('nav.brandHome')"
      @click="navigate('/dashboard')"
    >
      <span class="brand-mark" aria-hidden="true">wdc</span>
      <span class="brand-name">{{ $t('app.name') }}</span>
    </a>

    <!--
      Navigation in four groups (workspace, environment, network, operations)
      plus an application footer. Each row has an icon; a trailing count or
      problem badge replaces the per-service LEDs and switches the old
      sidebar carried: starting and stopping lives on Overview and
      Services, where the row says *why* something is not running.
      In Simple mode the Advanced destinations are left out of the nav, but
      they stay reachable by URL, by shortcut and from the command palette.
    -->
    <template v-for="group in groups" :key="group.id">
      <p :id="`nav-g-${group.id}`" class="nav-group">{{ $t(group.labelKey) }}</p>
      <ul class="nav-list" :aria-labelledby="`nav-g-${group.id}`">
        <li v-for="item in group.items" :key="item.key">
          <a
            v-activate:link
            class="nav-item"
            :class="{ active: isActive(item), 'nav-item-tunnel': item.tunnelLive }"
            :aria-current="isActive(item) ? 'page' : undefined"
            :aria-describedby="item.problems ? `nav-problems-${item.key}` : undefined"
            @click="navigate(item.route, item.problems ? { filter: 'problems' } : undefined)"
          >
            <el-icon class="nav-icon" :size="16" aria-hidden="true"><component :is="item.icon" /></el-icon>
            <span class="nav-label">{{ item.label }}</span>
            <span v-if="item.problems" class="nav-problems" :title="item.problems.detail">
              <span class="nav-problems-text">{{ item.problems.label }}</span>
              <span :id="`nav-problems-${item.key}`" class="visually-hidden">{{ item.problems.detail }}</span>
            </span>
            <template v-else>
              <span v-if="item.tag" class="nav-tag">{{ item.tag }}</span>
              <span
                v-if="item.count !== undefined"
                class="nav-count mono"
                :class="{ 'is-pending': item.pending }"
                :title="item.pending ? $t('nav.mcpPending', { n: item.count }) : undefined"
              >{{ item.count }}</span>
            </template>
          </a>
        </li>
      </ul>
    </template>

    <ul class="nav-list nav-foot" :aria-label="$t('nav.application')">
      <li v-if="uiModeStore.isAdvanced">
        <a v-activate:link class="nav-item" :class="{ active: isPluginsActive }"
          :aria-current="isPluginsActive ? 'page' : undefined" @click="navigate('/plugins')">
          <el-icon class="nav-icon" :size="16" aria-hidden="true"><Share /></el-icon>
          <span class="nav-label">{{ $t('nav.plugins') }}</span>
        </a>
      </li>
      <li>
        <a v-activate:link class="nav-item" :class="{ active: isPathActive('/settings') }"
          :aria-current="isPathActive('/settings') ? 'page' : undefined" @click="navigate('/settings')">
          <el-icon class="nav-icon" :size="16" aria-hidden="true"><Setting /></el-icon>
          <span class="nav-label">{{ $t('nav.settings') }}</span>
        </a>
      </li>
      <li>
        <a v-activate:link class="nav-item" :class="{ active: isPathActive('/help') }"
          :aria-current="isPathActive('/help') ? 'page' : undefined" @click="navigate('/help')">
          <el-icon class="nav-icon" :size="16" aria-hidden="true"><QuestionFilled /></el-icon>
          <span class="nav-label">{{ $t('nav.help') }}</span>
        </a>
      </li>
      <!-- Catalog account: shows the real identity when signed in and opens
           Settings > Account (sign-out lives there). -->
      <li>
        <a
          v-activate
          class="nav-item nav-item-sso"
          :class="{ signedin: authStore.isAuthenticated, active: isPathActive('/login') }"
          :aria-current="isPathActive('/login') ? 'page' : undefined"
          :title="authStore.isAuthenticated
            ? $t('nav.signedInAsTitle', { name: authStore.displayName || '?' })
            : $t('nav.signInTitle')"
          @click="openAccount"
        >
          <el-icon class="nav-icon" :size="16" aria-hidden="true"><User /></el-icon>
          <span class="nav-label nav-label-sso">
            <template v-if="authStore.isAuthenticated">
              <span class="sso-caption">{{ $t('nav.signedIn') }}</span>
              <span class="sso-email mono">{{ authStore.displayName || '…' }}</span>
            </template>
            <template v-else>{{ $t('nav.signIn') }}</template>
          </span>
          <span v-if="authStore.loginPending" class="sso-spinner" aria-hidden="true" />
        </a>
      </li>
    </ul>
  </nav>
</template>

<script setup lang="ts">
import { computed, type Component } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import {
  DataBoard, Monitor, SetUp, Coin, Box, Lock, Grid, Connection, Document,
  FolderOpened, Share, Setting, QuestionFilled, Collection, Operation, Tools, User,
} from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import { usePluginsStore } from '../../stores/plugins'
import { useAuthStore } from '../../stores/auth'
import { useMcpConfirmStore } from '../../stores/mcpConfirm'
import { useI18n } from 'vue-i18n'
import { isRunning } from '../../utils/serviceState'
import { stateKeyOf } from '../../utils/services'

const emit = defineEmits<{ navigate: [] }>()
const { t } = useI18n()

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()
const uiModeStore = useUiModeStore()
const featureFlagsStore = useFeatureFlagsStore()
const pluginsStore = usePluginsStore()
const authStore = useAuthStore()
const mcpConfirmStore = useMcpConfirmStore()

interface NavItem {
  key: string
  route: string
  label: string
  icon: Component
  count?: number
  /** Small muted label after the name ("off"). */
  tag?: string
  /** The count is a queue waiting for the user, so it stands out. */
  pending?: boolean
  problems?: { count: number; label: string; detail: string }
  tunnelLive?: boolean
}
interface NavGroup { id: string; labelKey: string; items: NavItem[] }

function openAccount() {
  emit('navigate')
  void router.push(authStore.isAuthenticated ? { path: '/settings', query: { tab: 'account' } } : '/login')
}

// Same set the Overview and the status bar count (visible plugins only).
const problems = computed(() => {
  const list = daemonStore.problemServices
  if (!list.length) return undefined
  const detail = list
    .map(s => t(stateKeyOf(s) === 'startFailed' ? 'nav.problemStartFailed' : 'nav.problemExited', { name: s.displayName || s.id }))
    .join('. ')
  return { count: list.length, label: t('nav.problemsBadge', { n: list.length }, list.length), detail }
})

const cloudflareRunning = computed(() =>
  daemonStore.visibleServices.some(s => s.id === 'cloudflare' && isRunning(s))
)
const exposedSiteCount = computed(() =>
  sitesStore.sites.filter(s => s.cloudflare?.enabled).length
)

// Pages of the app itself. Plugins contribute nav entries for some of them
// (with their own label); the rest would otherwise have no menu entry at all,
// so each one is added here unless its plugin was switched off.
interface CoreTool { route: string; icon: Component; labelKey: string; group: 'stack' | 'network' }
const CORE_TOOLS: CoreTool[] = [
  { route: '/php', icon: Grid, labelKey: 'nav.php', group: 'stack' },
  { route: '/composer', icon: Collection, labelKey: 'nav.composer', group: 'stack' },
  { route: '/ssl', icon: Lock, labelKey: 'nav.ssl', group: 'network' },
  { route: '/hosts', icon: Document, labelKey: 'nav.hosts', group: 'network' },
  { route: '/cloudflare', icon: Connection, labelKey: 'nav.cloudflare', group: 'network' },
]
const NETWORK_ROUTES = new Set(CORE_TOOLS.filter(c => c.group === 'network').map(c => c.route))
const ROUTE_ICONS: Record<string, Component> = Object.fromEntries(CORE_TOOLS.map(c => [c.route, c.icon]))

// The service pages live under several paths; /plugin/<id> belongs to the plugin manager.
const SERVICE_PAGES = [
  '/services', '/service', '/plugins/apache', '/plugins/php-custom', '/plugins/mysql',
  '/plugins/postgresql', '/plugins/mailpit', '/plugins/redis',
]

function toolItems(): NavItem[] {
  // Plugins send one English label; a page the app knows keeps its translated name.
  const coreLabel = (route: string) => CORE_TOOLS.find(c => c.route === route)?.labelKey
  const fromPlugins: NavItem[] = pluginsStore.toolsNavEntries.map(e => ({
    key: `${e.pluginId}:${e.id}`,
    route: e.route,
    label: coreLabel(e.route) ? t(coreLabel(e.route)!) : e.label,
    icon: ROUTE_ICONS[e.route] ?? Tools,
  }))
  const known = new Set(fromPlugins.map(i => i.route))
  const fixed: NavItem[] = CORE_TOOLS
    .filter(c => !known.has(c.route) && pluginsStore.isRouteVisible(c.route))
    .map(c => ({ key: `core:${c.route}`, route: c.route, label: t(c.labelKey), icon: c.icon }))
  return [...fixed, ...fromPlugins].map(i => i.route === '/cloudflare'
    ? {
      ...i,
      count: exposedSiteCount.value > 0 ? exposedSiteCount.value : undefined,
      tunnelLive: cloudflareRunning.value,
    }
    : i)
}

const groups = computed<NavGroup[]>(() => {
  const adv = uiModeStore.isAdvanced
  const tools = adv ? toolItems() : []
  const networkItems = tools.filter(i => NETWORK_ROUTES.has(i.route))
  const stackTools = tools
    .filter(i => !NETWORK_ROUTES.has(i.route))
    .sort((a, b) => Number(b.route === '/php') - Number(a.route === '/php'))

  const servicesItem: NavItem = {
    key: 'services', route: '/services', label: t('nav.services'), icon: SetUp, problems: problems.value,
  }
  const workspace: NavItem[] = [
    { key: 'overview', route: '/dashboard', label: t('nav.overview'), icon: DataBoard },
    { key: 'sites', route: '/sites', label: t('nav.sites'), icon: Monitor, count: sitesStore.sites.length || undefined },
  ]

  // Simple mode would leave "Services" alone in its own group; fold it into the workspace.
  const out: NavGroup[] = adv
    ? [
      { id: 'workspace', labelKey: 'nav.groups.workspace', items: workspace },
      {
        id: 'stack',
        labelKey: 'nav.groups.stack',
        items: [
          servicesItem,
          { key: 'databases', route: '/databases', label: t('nav.databases'), icon: Coin },
          ...stackTools,
          { key: 'binaries', route: '/binaries', label: t('nav.binaries'), icon: Box },
        ],
      },
    ]
    : [{ id: 'workspace', labelKey: 'nav.groups.workspace', items: [...workspace, servicesItem] }]
  if (networkItems.length) {
    out.push({ id: 'network', labelKey: 'nav.groups.network', items: networkItems })
  }
  const ops: NavItem[] = [{ key: 'backups', route: '/backups', label: t('nav.backups'), icon: FolderOpened }]
  // Always listed: the MCP page holds the on/off switch, so it must be reachable while MCP is off.
  ops.push({
    key: 'mcp', route: '/mcp', label: t('nav.mcp'), icon: Operation,
    tag: featureFlagsStore.mcpEnabled ? undefined : t('nav.mcpOff'),
    count: mcpConfirmStore.count || undefined,
    pending: mcpConfirmStore.count > 0,
  })
  out.push({ id: 'operations', labelKey: 'nav.groups.operations', items: ops })
  return out
})

function isPathActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}

function isActive(item: NavItem) {
  return item.route === '/services' ? SERVICE_PAGES.some(isPathActive) : isPathActive(item.route)
}
const isPluginsActive = computed(() => route.path === '/plugins' || route.path.startsWith('/plugin/'))

function navigate(path: string, query?: Record<string, string>) {
  emit('navigate')
  if (route.path === path) {
    void router.replace({ path, query: query ?? {} })
  } else {
    void router.push({ path, query })
  }
}
</script>

<style scoped>
.sidebar {
  width: 232px;
  min-width: 232px;
  height: 100%;
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-0-5);
  padding: var(--wdc-space-3) var(--wdc-space-3) var(--wdc-space-4);
  background: var(--wdc-chrome);
  border-right: 1px solid var(--wdc-border);
  overflow-y: auto;
  scrollbar-width: thin;
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-1-5) var(--wdc-space-2);
  margin-bottom: var(--wdc-space-2);
  border-radius: var(--wdc-radius);
  font-family: var(--wdc-font-display);
  font-weight: 600;
  font-size: 1rem;
  color: var(--wdc-text);
  cursor: pointer;
  user-select: none;
  text-decoration: none;
}
.brand-mark {
  display: inline-grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: var(--wdc-radius);
  background: var(--wdc-text);
  color: var(--wdc-bg);
  font-family: var(--wdc-font-mono);
  font-size: 0.72rem;
  font-weight: 500;
}

.nav-group {
  margin: var(--wdc-space-4) 0 var(--wdc-space-1);
  padding-inline: var(--wdc-space-2);
  font-family: var(--wdc-font-display);
  font-weight: 600;
  font-size: 0.72rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--wdc-text-3);
}

.nav-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: var(--wdc-space-0-5);
}

.nav-item {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  min-height: 32px;
  padding: var(--wdc-space-1) var(--wdc-space-2);
  border-radius: var(--wdc-radius);
  color: var(--wdc-text-2);
  font-size: 0.93rem;
  text-decoration: none;
  cursor: pointer;
  user-select: none;
  transition: background-color var(--wdc-dur-micro) var(--wdc-ease-out), color var(--wdc-dur-micro) var(--wdc-ease-out);
}
@media (hover: hover) {
  .nav-item:hover { background: var(--wdc-surface-2); color: var(--wdc-text); }
}
.nav-item:focus-visible {
  outline: 2px solid var(--wdc-accent);
  outline-offset: -2px;
}
.nav-item.active {
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent-ink);
  font-weight: 500;
}
.nav-item-tunnel .nav-label { color: var(--wdc-status-starting); }

.nav-icon { flex-shrink: 0; color: var(--wdc-text-3); }
.nav-item.active .nav-icon,
.nav-item:hover .nav-icon { color: currentColor; }

.nav-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.nav-count {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.nav-count.is-pending {
  min-width: 1.4em;
  padding: 0 var(--wdc-space-1-5);
  border-radius: 999px;
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent-ink);
  font-weight: 600;
  text-align: center;
}
.nav-tag {
  flex-shrink: 0;
  padding: 0 var(--wdc-space-1-5);
  border: 1px solid var(--wdc-border-strong);
  border-radius: 999px;
  font-size: 0.68rem;
  color: var(--wdc-text-3);
}
/* Problem badge: number + word on a solid red pill, readable without hover. */
.nav-problems {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  padding: 1px var(--wdc-space-2);
  border-radius: 999px;
  background: var(--wdc-status-error);
  color: var(--wdc-badge-fg, #fff);
  font-size: 0.72rem;
  font-weight: 600;
  line-height: 1.5;
}
.nav-problems-text { font-family: var(--wdc-font-display); }

.nav-foot {
  margin-top: auto;
  padding-top: var(--wdc-space-3);
  border-top: 1px solid var(--wdc-border);
}

.nav-label-sso {
  display: flex;
  flex-direction: column;
  line-height: 1.25;
}
.sso-caption {
  font-size: 0.74rem;
  color: var(--wdc-text-3);
}
.sso-email {
  font-size: 0.78rem;
  overflow: hidden;
  text-overflow: ellipsis;
}
.sso-spinner {
  width: 12px;
  height: 12px;
  border: 2px solid var(--wdc-border-strong);
  border-top-color: var(--wdc-accent);
  border-radius: 50%;
  animation: sso-spin 0.8s linear infinite;
}
@keyframes sso-spin { to { transform: rotate(360deg); } }

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}
</style>
