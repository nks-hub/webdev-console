<template>
  <nav class="sidebar">
    <!--
      Workspace card removed — it duplicated the header (NW logo +
      "NKS WDC"). Service-count summary lives in the bottom status
      bar already, so no information is lost. Sidebar now starts
      directly with the nav cluster, saving ~70px vertical space.
    -->

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
           false). Once an operator explicitly flips mcp.enabled=true
           in daemon settings the AI surface is opt-in regardless of
           UI mode — hiding it again behind the Simple/Advanced toggle
           silently buried the feature for operators who use Simple
           but run an AI client (incident 2026-05-07). -->
      <!-- Phase 7.3 — single MCP hub entry; tabs inside the page split
           between Intents (audit log) and Oprávnění (persistent grants). -->
      <div
        v-if="featureFlagsStore.showMcpSurface"
        class="nav-item"
        :class="{ active: isActive('/mcp/intents') || isActive('/mcp/grants') }"
        @click="navigate('/mcp/intents')"
      >
        <span class="nav-icon-shell"><el-icon :size="18"><Lock /></el-icon></span>
        <span class="nav-label">{{ $t('nav.mcp') }}</span>
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
/*
  Sidebar redesign — flat surface, single right-edge separator. Was
  decorated with a radial cyan gradient + status texts in every
  service row. Now: wider but breathes more, drop meta text under
  each service name, tighten vertical rhythm.
*/
.sidebar {
  width: 240px;
  display: flex;
  flex-direction: column;
  background: var(--wdc-surface);
  border-right: 1px solid var(--wdc-border);
  flex-shrink: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 16px 12px 12px;
}

/*
  Workspace card — slimmer, no border, fits inline as a status block
  rather than a "branded card". Saves ~20px vertical so the nav
  cluster + sections are visible without scrolling.
*/
.sidebar-top {
  margin-bottom: 16px;
}

.workspace-card {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 8px 12px;
  border-bottom: 1px solid var(--wdc-border);
}

.workspace-mark {
  width: 32px;
  height: 32px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  background: var(--wdc-accent);
  color: #ffffff;
  font-size: 0.74rem;
  font-weight: 800;
  letter-spacing: 0.06em;
}

.workspace-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
  gap: 2px;
}

.workspace-title {
  color: var(--wdc-text);
  font-size: 0.82rem;
  font-weight: 700;
  letter-spacing: 0;
  line-height: 1.2;
}

.workspace-subtitle {
  color: var(--wdc-text-3);
  font-size: 0.68rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  font-weight: 600;
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
  margin-bottom: 4px;
}

/*
  Section labels — drop the count chip (was visually competing with
  service item LEDs). Smaller, looser tracking, more whitespace.
*/
.section-label {
  display: flex;
  align-items: center;
  font-size: 0.66rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.10em;
  color: var(--wdc-text-3);
  padding: 16px 10px 6px;
}

.section-count { display: none; }

/*
  Service item redesign — single-row, drop meta text. Just icon +
  name + LED + switch. Was 48px tall with two stacked text rows;
  now 36px with one row, tighter vertical rhythm.
*/
.service-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 6px 10px;
  border-radius: 6px;
  transition: background 0.1s;
  min-height: 36px;
  border: none;
  cursor: pointer;
}

.service-item:hover { background: var(--wdc-hover); }
.service-item.active {
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
}
.service-item.active .svc-name { color: var(--wdc-accent); }

.svc-copy {
  min-width: 0;
  flex: 1;
  display: flex;
  align-items: center;
}

.svc-name {
  font-size: 0.84rem;
  font-weight: 500;
  color: var(--wdc-text-2);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/*
  Meta text dropped — status is conveyed by the LED dot color, no
  need to spell "RUNNING" / "READY" / "OFFLINE" under every name.
  Was the single biggest source of sidebar noise.
*/
.svc-meta { display: none; }

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
  /* Solid left accent bar + tinted fill in both modes — unified, no
     hardcoded hex per mode. */
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  border-left-color: var(--wdc-accent);
  font-weight: 700;
}

/*
  Tunnel entry — Cloudflare brand orange when cloudflared is live.
  The colored icon + border-left signals running status without
  forcing the user to scan the sidebar.
*/
.nav-item-tunnel { color: var(--wdc-cat-tunnel); }
.nav-item-tunnel .nav-icon-shell { color: var(--wdc-cat-tunnel); }
.nav-item-tunnel.active {
  background: rgba(243, 128, 32, 0.14);
  border-left-color: var(--wdc-cat-tunnel);
  color: var(--wdc-cat-tunnel);
}
:global(html.dark) .nav-item-tunnel { color: #fff2b3; }

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
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
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
  background: #9a4a00;
  color: #ffffff;
  font-size: 0.72rem;
  font-weight: 700;
  border-radius: 10px;
  min-width: 18px;
  text-align: center;
}

:global(html.dark) .workspace-subtitle,
:global(html.dark) .section-label { color: #ffffff; }
:global(html.dark) .nav-badge { background: #ffb15f; color: #141006; }

.nav-icon-shell {
  width: 34px;
  height: 34px;
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

.sidebar-bottom .nav-item,
.sidebar-bottom .nav-item .nav-label {
  color: var(--wdc-text);
}

:global(html.dark) .sidebar-bottom .nav-item,
:global(html.dark) .sidebar-bottom .nav-item .nav-label {
  color: #ffffff !important;
}
</style>
