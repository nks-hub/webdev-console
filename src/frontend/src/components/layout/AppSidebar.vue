<template>
  <nav class="sidebar" :aria-label="$t('nav.mainNavigation')">
    <a
      v-activate:link
      class="brand"
      :aria-label="$t('nav.brandHome')"
      @click="navigate('/dashboard')"
    >
      <span class="brand-mark" aria-hidden="true">wdc</span>
      <span class="brand-name">WebDev Console</span>
    </a>

    <!--
      Navigation in four groups (workspace, stack, network, operations)
      plus an application footer. Text-first rows; a trailing count or
      status flag replaces the per-service LEDs and switches the old
      sidebar carried — starting and stopping lives on Overview and
      Services, where the row says *why* something is not running.
      In Simple mode the Advanced destinations are hidden from the nav
      but stay reachable by URL and from the command palette.
    -->
    <template v-for="group in groups" :key="group.id">
      <p :id="`nav-g-${group.id}`" class="nav-group">{{ $t(group.labelKey) }}</p>
      <ul class="nav-list" :aria-labelledby="`nav-g-${group.id}`">
        <li v-for="item in group.items" :key="item.key">
          <a
            v-activate:link
            class="nav-item"
            :class="{ active: isActive(item.route), 'nav-item-tunnel': item.tunnelLive }"
            :aria-current="isActive(item.route) ? 'page' : undefined"
            @click="navigate(item.route)"
          >
            <span class="nav-label">{{ item.label }}</span>
            <span v-if="item.flag" class="nav-flag" :title="item.flag.title">
              <el-icon :size="14" aria-hidden="true"><CircleCloseFilled /></el-icon>
              <span class="visually-hidden">{{ item.flag.title }}</span>
            </span>
            <span v-else-if="item.count !== undefined" class="nav-count mono">{{ item.count }}</span>
          </a>
        </li>
      </ul>
    </template>

    <ul class="nav-list nav-foot" :aria-label="$t('nav.application')">
      <li v-if="uiModeStore.isAdvanced">
        <a v-activate:link class="nav-item" :class="{ active: isActive('/plugins') }"
          :aria-current="isActive('/plugins') ? 'page' : undefined" @click="navigate('/plugins')">
          <span class="nav-label">{{ $t('nav.plugins') }}</span>
        </a>
      </li>
      <li>
        <a v-activate:link class="nav-item" :class="{ active: isActive('/settings') }"
          :aria-current="isActive('/settings') ? 'page' : undefined" @click="navigate('/settings')">
          <span class="nav-label">{{ $t('nav.settings') }}</span>
        </a>
      </li>
      <li>
        <a v-activate:link class="nav-item" :class="{ active: isActive('/help') }"
          :aria-current="isActive('/help') ? 'page' : undefined" @click="navigate('/help')">
          <span class="nav-label">{{ $t('nav.help') }}</span>
        </a>
      </li>
      <!-- Catalog sign-in: shows the real identity when signed in. -->
      <li>
        <a
          v-activate
          class="nav-item nav-item-sso"
          :class="{ signedin: authStore.isAuthenticated }"
          :title="authStore.isAuthenticated
            ? $t('nav.signedInAsTitle', { name: authStore.displayName || '?' })
            : $t('nav.signInTitle')"
          @click="toggleSso"
        >
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
import { computed, onMounted } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { CircleCloseFilled } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import { usePluginsStore } from '../../stores/plugins'
import { useAuthStore } from '../../stores/auth'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'

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

interface NavItem {
  key: string
  route: string
  label: string
  count?: number
  flag?: { title: string }
  tunnelLive?: boolean
}
interface NavGroup { id: string; labelKey: string; items: NavItem[] }

async function toggleSso() {
  if (authStore.isAuthenticated) {
    try {
      await ElMessageBox.confirm(t('nav.signOutConfirm'), t('nav.signOutTitle'), { type: 'warning' })
      authStore.logout()
      ElMessage.success(t('nav.signedOutToast'))
    } catch { /* user cancelled */ }
    return
  }
  void router.push('/login')
}

// Plugin-contributed entries need the /api/plugins/ui round-trip; the
// store call is idempotent when another page already warmed it.
onMounted(() => {
  if (pluginsStore.manifests.length === 0) void pluginsStore.loadAll()
})

const services = computed(() => daemonStore.services)
// Crashed services flag the Services entry. A stopped service is a
// choice, not a fault, so it does not raise the flag.
const crashedCount = computed(() => services.value.filter(s => s.state === 4).length)

const cloudflareRunning = computed(() =>
  services.value.some(s => s.id === 'cloudflare' && (s.state === 2 || s.status === 'running'))
)
const exposedSiteCount = computed(() =>
  sitesStore.sites.filter(s => s.cloudflare?.enabled).length
)

// Plugin tool pages are sorted into the group they belong to by route.
const NETWORK_ROUTES = new Set(['/ssl', '/hosts', '/cloudflare'])

const groups = computed<NavGroup[]>(() => {
  const adv = uiModeStore.isAdvanced
  const pluginItems: NavItem[] = pluginsStore.toolsNavEntries.map(e => ({
    key: `${e.pluginId}:${e.id}`,
    route: e.route,
    label: e.label,
    count: e.pluginId === 'nks.wdc.cloudflare' && exposedSiteCount.value > 0 ? exposedSiteCount.value : undefined,
    tunnelLive: e.pluginId === 'nks.wdc.cloudflare' && cloudflareRunning.value,
  }))
  const networkItems = pluginItems.filter(i => NETWORK_ROUTES.has(i.route))
  const toolItems = pluginItems.filter(i => !NETWORK_ROUTES.has(i.route))

  const out: NavGroup[] = [
    {
      id: 'workspace',
      labelKey: 'nav.groups.workspace',
      items: [
        { key: 'overview', route: '/dashboard', label: t('nav.overview') },
        { key: 'sites', route: '/sites', label: t('nav.sites'), count: sitesStore.sites.length || undefined },
      ],
    },
    {
      id: 'stack',
      labelKey: 'nav.groups.stack',
      items: [
        {
          key: 'services',
          route: '/services',
          label: t('nav.services'),
          flag: crashedCount.value > 0 ? { title: t('nav.servicesCrashed', { n: crashedCount.value }, crashedCount.value) } : undefined,
        },
        ...(adv ? [
          { key: 'databases', route: '/databases', label: t('nav.databases') },
          { key: 'binaries', route: '/binaries', label: t('nav.binaries') },
          ...toolItems,
        ] : []),
      ],
    },
  ]
  if (adv && networkItems.length) {
    out.push({ id: 'network', labelKey: 'nav.groups.network', items: networkItems })
  }
  const ops: NavItem[] = [{ key: 'backups', route: '/backups', label: t('nav.backups') }]
  if (featureFlagsStore.showMcpSurface) ops.push({ key: 'mcp', route: '/mcp', label: t('nav.mcp') })
  out.push({ id: 'operations', labelKey: 'nav.groups.operations', items: ops })
  return out
})

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}

function navigate(path: string) {
  emit('navigate')
  if (route.path === path) {
    void router.replace({ path, query: {} })
  } else {
    void router.push(path)
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
  margin: var(--wdc-space-3) 0 var(--wdc-space-1);
  padding-inline: var(--wdc-space-2);
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.82rem;
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
  justify-content: space-between;
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

.nav-label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.nav-count {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.nav-flag {
  display: inline-flex;
  color: var(--wdc-status-error);
}

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
