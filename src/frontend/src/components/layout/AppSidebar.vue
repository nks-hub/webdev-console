<template>
  <nav class="sidebar">
    <div class="sidebar-top">
      <div class="workspace-card">
        <div class="workspace-mark">NW</div>
        <div class="workspace-copy">
          <span class="workspace-title">NKS WebDev Console</span>
          <span class="workspace-subtitle">{{ runningCount }}/{{ services.length }} {{ $t('sidebar.servicesOnline') }}</span>
        </div>
      </div>
    </div>

    <div class="nav-cluster">
      <div class="nav-item" :class="{ active: isActive('/dashboard') }" @click="navigate('/dashboard')">
        <span class="nav-icon-shell"><el-icon :size="18"><House /></el-icon></span>
        <span class="nav-label">{{ $t('nav.overview') }}</span>
      </div>
      <div class="nav-item sites-btn" :class="{ active: isActive('/sites') }" @click="navigate('/sites')">
        <span class="nav-icon-shell"><el-icon :size="18"><Link /></el-icon></span>
        <span class="nav-label">{{ $t('nav.sites') }}</span>
      </div>
    </div>

    <template v-if="!uiModeStore.isSimple">
    <div class="sidebar-section">
      <div class="section-label">
        <span>{{ $t('nav.webServer') }}</span>
        <span class="section-count">{{ webServices.length }}</span>
      </div>
      <template v-for="svc in webServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Running' : 'Stopped' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="langServices.length">
      <div class="section-label">
        <span>{{ $t('nav.languages') }}</span>
        <span class="section-count">{{ langServices.length }}</span>
      </div>
      <template v-for="svc in langServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Ready' : 'Idle' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="dbServices.length">
      <div class="section-label">
        <span>{{ $t('nav.database') }}</span>
        <span class="section-count">{{ dbServices.length }}</span>
      </div>
      <template v-for="svc in dbServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Running' : 'Offline' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="cacheServices.length">
      <div class="section-label">
        <span>{{ $t('nav.cacheMail') }}</span>
        <span class="section-count">{{ cacheServices.length }}</span>
      </div>
      <template v-for="svc in cacheServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Running' : 'Standby' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>
    </template><!-- /advanced service sections -->

    <template v-if="!uiModeStore.isSimple">
    <div class="sidebar-section tools-section">
      <div class="section-label">
        <span>{{ $t('nav.tools') }}</span>
      </div>
      <!-- F91 phase 3: plugin-contributed nav entries. Order is driven by
           each plugin's manifest (UiSchemaBuilder.AddNavEntry order field)
           so the sidebar rearranges itself when plugins are enabled /
           disabled without hardcoded composer/hosts/ssl/cloudflare paths. -->
      <div
        v-for="entry in pluginsStore.toolsNavEntries"
        :key="entry.pluginId + ':' + entry.id"
        class="nav-item"
        :class="{
          active: isActive(entry.route),
          'nav-item-tunnel': entry.pluginId === 'nks.wdc.cloudflare' && cloudflareRunning,
        }"
        @click="navigate(entry.route)"
      >
        <span class="nav-icon-shell">
          <el-icon :size="18"><component :is="iconFor(entry.icon)" /></el-icon>
        </span>
        <span class="nav-label">{{ entry.label }}</span>
        <span
          v-if="entry.pluginId === 'nks.wdc.cloudflare' && exposedSiteCount > 0"
          class="nav-badge mono"
        >{{ exposedSiteCount }}</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/binaries') }" @click="navigate('/binaries')">
        <span class="nav-icon-shell"><el-icon :size="18"><Download /></el-icon></span>
        <span class="nav-label">{{ $t('nav.binaries') }}</span>
      </div>
    </div>
    </template><!-- /tools section -->

    <div class="sidebar-spacer" />

    <div class="sidebar-bottom">
      <div v-if="uiModeStore.isAdvanced" class="nav-item" :class="{ active: isActive('/databases') }" @click="navigate('/databases')">
        <span class="nav-icon-shell"><el-icon :size="18"><Coin /></el-icon></span>
        <span class="nav-label">{{ $t('nav.databases') }}</span>
      </div>
      <!-- PHP entry removed from bottom nav: per-runtime managers get crowded
           fast once we add Node/Go/Python/Ruby. Users still reach PHP via the
           Dashboard service toggle and the /plugin/nks.wdc.php panel. -->
      <div v-if="uiModeStore.isAdvanced" class="nav-item" :class="{ active: isActive('/plugins') }" @click="navigate('/plugins')">
        <span class="nav-icon-shell"><el-icon :size="18"><Box /></el-icon></span>
        <span class="nav-label">{{ $t('nav.plugins') }}</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/backups') }" @click="navigate('/backups')">
        <span class="nav-icon-shell"><el-icon :size="18"><Files /></el-icon></span>
        <span class="nav-label">Zálohy</span>
      </div>
      <!-- Phase 6.11b — admin audit view of all signed MCP intents.
           Phase 6.23 — gated by featureFlagsStore.mcpEnabled (default
           false). Advanced-only AND mcp.enabled=true to render —
           hidden by default for operators not running AI agents. -->
      <div
        v-if="uiModeStore.isAdvanced && featureFlagsStore.showMcpSurface"
        class="nav-item"
        :class="{ active: isActive('/mcp/intents') }"
        @click="navigate('/mcp/intents')"
      >
        <span class="nav-icon-shell"><el-icon :size="18"><Lock /></el-icon></span>
        <span class="nav-label">{{ $t('nav.mcpIntents') }}</span>
      </div>
      <!-- Phase 7.3 — persistent trust grants admin (list / revoke). -->
      <div
        v-if="uiModeStore.isAdvanced && featureFlagsStore.showMcpSurface"
        class="nav-item"
        :class="{ active: isActive('/mcp/grants') }"
        @click="navigate('/mcp/grants')"
      >
        <span class="nav-icon-shell"><el-icon :size="18"><Key /></el-icon></span>
        <span class="nav-label">{{ $t('nav.mcpGrants') }}</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/settings') }" @click="navigate('/settings')">
        <span class="nav-icon-shell"><el-icon :size="18"><Setting /></el-icon></span>
        <span class="nav-label">{{ $t('nav.settings') }}</span>
      </div>
      <!-- F89: Help entry — always visible in both Simple + Advanced modes. -->
      <div class="nav-item" :class="{ active: isActive('/help') }" @click="navigate('/help')">
        <span class="nav-icon-shell"><el-icon :size="18"><QuestionFilled /></el-icon></span>
        <span class="nav-label">Help</span>
      </div>
      <!-- F83: top-level sign-in entry point so users don't have to
           drill into Settings → About to discover the SSO flow. When
           already signed in the item shows the avatar mark + a "Sign out"
           action; when signed out a single click kicks off the deep-link
           flow against the catalog URL stored in SettingsStore. -->
      <!-- F91.14: surface the real SSO identity in the sidebar so the
           user sees "Signed in as lury@lury.cz" at a glance, not just a
           generic "Signed in". Falls back to "Signed in" when the JWT
           decode and /auth/me both produced nothing (unlikely, but
           keeps the layout stable). -->
      <div
        class="nav-item nav-item-sso"
        :class="{ signedin: authStore.isAuthenticated }"
        :title="authStore.isAuthenticated
          ? `Signed in as ${authStore.displayName || '(unknown)'} — click to sign out`
          : 'Sign in with SSO'"
        @click="toggleSso"
      >
        <span class="nav-icon-shell">
          <el-icon :size="18">
            <component :is="authStore.isAuthenticated ? UserFilled : User" />
          </el-icon>
        </span>
        <span class="nav-label nav-label-sso">
          <template v-if="authStore.isAuthenticated">
            <span class="sso-caption">Signed in</span>
            <span class="sso-email mono" :title="authStore.displayName || ''">
              {{ authStore.displayName || '…' }}
            </span>
          </template>
          <template v-else>Sign in</template>
        </span>
        <span v-if="authStore.loginPending" class="sso-spinner" />
      </div>
    </div>
  </nav>
</template>

<script setup lang="ts">
import { computed, onMounted, markRaw, type Component } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { Link, Download, Box, Setting, Coin, Lock, Cpu, House, Connection, Document, Files, QuestionFilled, User, UserFilled, Key } from '@element-plus/icons-vue'
import ServiceIcon from '../shared/ServiceIcon.vue'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import { useServicesStore } from '../../stores/services'
import { useUiModeStore } from '../../stores/uiMode'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import { usePluginsStore } from '../../stores/plugins'
import { useAuthStore } from '../../stores/auth'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { ServiceInfo } from '../../api/types'

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()
const uiModeStore = useUiModeStore()
const featureFlagsStore = useFeatureFlagsStore()
const pluginsStore = usePluginsStore()
const authStore = useAuthStore()

async function toggleSso() {
  if (authStore.isAuthenticated) {
    try {
      await ElMessageBox.confirm('Sign out of the catalog?', 'Sign out', { type: 'warning' })
      authStore.logout()
      ElMessage.success('Signed out')
    } catch { /* user cancelled */ }
    return
  }
  // Route to the dedicated login page instead of firing the OIDC flow
  // inline — gives us a landing surface for provider/catalog info + the
  // "continue without signing in" escape hatch users asked for.
  void router.push('/login')
}

// Plugin-contributed sidebar entries need the /api/plugins/ui round-trip to
// populate before the Tools section can render. pluginsStore.loadAll is also
// called elsewhere (Plugins page), so the sidebar is idempotent: if the
// store is already warm this returns immediately.
onMounted(() => {
  if (pluginsStore.manifests.length === 0) void pluginsStore.loadAll()
})

// Map the icon name (Element Plus component identifier) shipped in each
// plugin's NavContribution to the actual runtime component. Falls back to
// Box so a plugin shipping an unknown icon name still renders a sidebar row.
const ICON_REGISTRY: Record<string, Component> = markRaw({
  Link, Download, Box, Setting, Coin, Lock, Cpu, House, Connection, Document, Files, QuestionFilled,
})
function iconFor(name: string): Component {
  return ICON_REGISTRY[name] ?? Box
}

// Sidebar is always expanded — the collapse toggle was dropped because
// it added no value (sidebar fits at any reasonable window width) and
// the icon-only mode hid service names that users needed to glance at.
const services = computed(() => daemonStore.services)
const runningCount = computed(() => services.value.filter(s => s.state === 2).length)

// Tunnel entry in the bottom nav lights up when cloudflared is running AND
// shows a badge with the count of sites currently exposed through it.
// Both are derived state — no extra fetches, just reuse daemon + sites stores.
const cloudflareRunning = computed(() =>
  services.value.some(s => s.id === 'cloudflare' && (s.state === 2 || s.status === 'running'))
)
const exposedSiteCount = computed(() =>
  sitesStore.sites.filter(s => s.cloudflare?.enabled).length
)

const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
  'Mailpit': 'Mailpit',
}
function shortName(svc: ServiceInfo): string {
  return SHORT_NAMES[svc.displayName ?? ''] || svc.displayName || svc.id
}

// F91.3: sidebar categories are driven by the plugin store, not a hardcoded
// table. Each plugin calls UiSchemaBuilder.SetServiceCategory(category, id),
// which registers a `service-row:{category}:{id}` surface. Disabling the
// plugin drops that surface, so the row vanishes. Cloudflare no longer
// appears here because its plugin declares only Tools surfaces.
function servicesInCategory(category: string) {
  const allowed = pluginsStore.serviceIdsInCategory(category)
  return services.value.filter(s => allowed.has(s.id))
}

const webServices = computed(() => servicesInCategory('web'))
const langServices = computed(() => servicesInCategory('lang'))
const dbServices = computed(() => servicesInCategory('db'))
const cacheServices = computed(() => servicesInCategory('cache'))

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}

function navigate(path: string) {
  if (route.path === path) {
    void router.replace({ path, query: {} })
  } else {
    void router.push(path)
  }
}

function openService(id: string) {
  // Cloudflare has a dedicated configuration page — the generic service
  // config drawer expects file-based configs and 500s for API-driven
  // services. Route directly to the Cloudflare Tunnel management page.
  if (id === 'cloudflare') {
    void router.push('/cloudflare')
    return
  }
  void router.push(`/service/${id}/config`)
}

async function toggleSvc(svc: ServiceInfo) {
  const name = svc.displayName || svc.id
  try {
    if (svc.state === 2) {
      await servicesStore.stop(svc.id)
      ElMessage.success(`${name} stopped`)
    } else {
      await servicesStore.start(svc.id)
      ElMessage.success(`${name} started`)
    }
  } catch (err) {
    ElMessage.error(`${name}: ${err instanceof Error ? err.message : String(err)}`)
  }
}
</script>

<style scoped>
.sidebar {
  width: 256px;
  display: flex;
  flex-direction: column;
  background:
    radial-gradient(circle at top left, rgba(86, 194, 255, 0.14), transparent 26%),
    linear-gradient(180deg, rgba(255, 255, 255, 0.04), transparent 26%),
    var(--wdc-surface);
  border-right: 1px solid rgba(255, 255, 255, 0.1);
  flex-shrink: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 12px 10px 10px;
}

.sidebar-top {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-bottom: 12px;
}

.workspace-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 12px;
  border-radius: var(--wdc-radius);
  /* Flat: solid border, solid fill — no gradient, no inner glow */
  border: 1px solid var(--wdc-border);
  background: var(--wdc-surface-2);
}

.workspace-mark {
  width: 38px;
  height: 38px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
  /* Flat: solid accent tile */
  background: var(--wdc-accent);
  color: var(--wdc-bg);
  font-size: 0.82rem;
  font-weight: 800;
  letter-spacing: 0.08em;
}

.workspace-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.workspace-title {
  color: var(--wdc-text);
  font-size: 0.88rem;
  font-weight: 700;
  letter-spacing: 0.01em;
}

.workspace-subtitle {
  color: var(--wdc-text-3);
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.09em;
}

.nav-cluster {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 10px;
}

.sites-btn {
  font-weight: 700;
}

.sidebar-section {
  margin-bottom: 8px;
}

.section-label {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 0.74rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--wdc-text-3);
  padding: 12px 10px 8px;
}

.section-count {
  min-width: 20px;
  height: 20px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  color: var(--wdc-text-2);
  font-size: 0.66rem;
  letter-spacing: 0;
}

.service-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 14px;
  transition: background 0.1s, border-color 0.1s;
  min-height: 48px;
  border: 1px solid transparent;
  cursor: pointer;
}

.service-item:hover {
  background: rgba(255, 255, 255, 0.04);
  border-color: rgba(255, 255, 255, 0.08);
}

.service-item.active {
  background: linear-gradient(180deg, rgba(86, 194, 255, 0.14), rgba(86, 194, 255, 0.06));
  border-color: rgba(86, 194, 255, 0.28);
}

.svc-copy {
  min-width: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.svc-name {
  font-size: 0.92rem;
  font-weight: 600;
  color: var(--wdc-text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.svc-meta {
  color: var(--wdc-text-3);
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.svc-led {
  width: 8px;
  height: 8px;
  flex-shrink: 0;
  border-radius: 999px;
  /* Flat: use token for "off" state so it's visible in both modes */
  background: var(--wdc-status-stopped);
}

.svc-led.on {
  /* Flat: solid dot, no outer glow ring */
  background: var(--wdc-status-running);
}

.nav-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 11px 12px;
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
  color: var(--wdc-text-2);
  font-size: 0.92rem;
  font-weight: 600;
  transition: background 0.1s, color 0.1s, border-left-color 0.1s;
  /* Flat: 3px left edge that becomes accent when active */
  border-left: 3px solid transparent;
}

.nav-item:hover {
  /* Flat: solid surface-2, no alpha layering */
  background: var(--wdc-surface-2);
  color: var(--wdc-text);
}

.nav-item.active {
  /* Flat: strong solid left indicator + subtle accent-tinted fill */
  background: var(--wdc-accent-dim);
  color: var(--wdc-text);
  border-left-color: var(--wdc-accent);
}

/* Tunnel entry — when cloudflared is actively running, tint the icon
   + border in the Cloudflare brand orange so users get a visual "live"
   indicator without hunting through the page. Overrides the accent blue
   from .nav-item.active when both apply. */
.nav-item-tunnel {
  border-left-color: #f38020;
}
.nav-item-tunnel .nav-icon-shell {
  color: #f38020;
}
.nav-item-tunnel.active {
  background: rgba(243, 128, 32, 0.12);
  border-left-color: #f38020;
}

/* F83 SSO entry — subtle accent when signed in so the state is
   legible without crowding the bottom-nav visual weight. */
.nav-item-sso.signedin .nav-icon-shell { color: #16a34a; }
.nav-item-sso.signedin { border-left-color: #16a34a; }
.nav-label-sso {
  display: flex;
  flex-direction: column;
  line-height: 1.1;
  overflow: hidden;
  min-width: 0;
}
.nav-label-sso .sso-caption {
  font-size: 0.62rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
}
.nav-label-sso .sso-email {
  font-size: 0.78rem;
  font-weight: 600;
  color: var(--wdc-text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.sso-spinner {
  display: inline-block;
  width: 10px;
  height: 10px;
  border: 2px solid currentColor;
  border-right-color: transparent;
  border-radius: 50%;
  animation: sso-spin 0.7s linear infinite;
}
@keyframes sso-spin { to { transform: rotate(360deg); } }

.nav-label {
  flex: 1;
  white-space: nowrap;
}

.nav-badge {
  display: inline-block;
  padding: 1px 7px;
  background: #f38020;
  color: #ffffff;
  font-size: 0.68rem;
  font-weight: 700;
  border-radius: 10px;
  min-width: 18px;
  text-align: center;
}

.nav-icon-shell {
  width: 30px;
  height: 30px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: var(--wdc-radius-sm);
  /* Flat: solid surface-2 tile, no alpha */
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
}

.sidebar-spacer {
  flex: 1;
}

.sidebar-bottom {
  border-top: 1px solid rgba(255, 255, 255, 0.08);
  padding-top: 8px;
  margin-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
</style>
