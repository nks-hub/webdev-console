import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import { usePluginsStore } from '../stores/plugins'
import { i18n } from '../i18n'

// Lazy-loaded so heavy page deps (monaco-editor, echarts, xterm) land in
// per-route chunks instead of the main bundle. Eagerly importing all 18
// pages pulled ServiceConfig's MonacoEditor into index.js which alone
// brought ~5 MB of editor workers + language modes, inflating the main
// bundle to 12 MB.
const Dashboard = () => import('../components/pages/Dashboard.vue')
const Sites = () => import('../components/pages/Sites.vue')
const Settings = () => import('../components/pages/Settings.vue')
const PluginManager = () => import('../components/pages/PluginManager.vue')
const PluginPage = () => import('../components/pages/PluginPage.vue')
const Binaries = () => import('../components/pages/Binaries.vue')
const Databases = () => import('../components/pages/Databases.vue')
const SslManager = () => import('../components/pages/SslManager.vue')
const PhpManager = () => import('../components/pages/PhpManager.vue')
const ServiceConfig = () => import('../components/pages/ServiceConfig.vue')
const SiteEdit = () => import('../components/pages/SiteEdit.vue')
const CloudflareTunnel = () => import('../components/pages/CloudflareTunnel.vue')
const ApachePluginPage = () => import('../components/pages/ApachePluginPage.vue')
const PhpPluginPage = () => import('../components/pages/PhpPluginPage.vue')
const MySqlPluginPage = () => import('../components/pages/MySqlPluginPage.vue')
const PostgreSqlPluginPage = () => import('../components/pages/PostgreSqlPluginPage.vue')
const MailpitPluginPage = () => import('../components/pages/MailpitPluginPage.vue')
const RedisPluginPage = () => import('../components/pages/RedisPluginPage.vue')
const ComposerManager = () => import('../components/pages/ComposerManager.vue')
const HostsManager = () => import('../components/pages/HostsManager.vue')
const Help = () => import('../components/pages/Help.vue')
const Login = () => import('../components/pages/Login.vue')
const BackupsPage = () => import('../components/pages/BackupsPage.vue')
const McpHub = () => import('../components/pages/McpHub.vue')
const ServicesPage = () => import('../components/pages/ServicesPage.vue')

const baseRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/sites' },
  { path: '/dashboard', component: Dashboard, meta: { title: 'Dashboard', titleKey: 'nav.overview' } },
  { path: '/sites', component: Sites, meta: { title: 'Sites', titleKey: 'nav.sites' } },
  { path: '/services', component: ServicesPage, meta: { title: 'Services', titleKey: 'nav.services' } },
  { path: '/sites/:domain/edit', component: SiteEdit, props: true, meta: { title: 'Edit Site', titleKey: 'nav.editSite' } },
  { path: '/databases', component: Databases, meta: { title: 'Databases', titleKey: 'nav.databases', advancedNav: true } },
  { path: '/ssl', component: SslManager, meta: { title: 'SSL', titleKey: 'nav.ssl', advancedNav: true } },
  { path: '/php', component: PhpManager, meta: { title: 'PHP', titleKey: 'nav.php', advancedNav: true } },
  { path: '/cloudflare', component: CloudflareTunnel, meta: { title: 'Cloudflare', titleKey: 'nav.cloudflare', advancedNav: true } },
  { path: '/plugins/apache', component: ApachePluginPage, meta: { title: 'Apache', advancedNav: true } },
  { path: '/plugins/php-custom', component: PhpPluginPage, meta: { title: 'PHP', titleKey: 'nav.php', advancedNav: true } },
  { path: '/plugins/mysql', component: MySqlPluginPage, meta: { title: 'MySQL', advancedNav: true } },
  { path: '/plugins/postgresql', component: PostgreSqlPluginPage, meta: { title: 'PostgreSQL', advancedNav: true } },
  { path: '/plugins/mailpit', component: MailpitPluginPage, meta: { title: 'Mailpit', advancedNav: true } },
  { path: '/plugins/redis', component: RedisPluginPage, meta: { title: 'Redis', advancedNav: true } },
  { path: '/settings', component: Settings, meta: { title: 'Settings', titleKey: 'nav.settings' } },
  { path: '/plugins', component: PluginManager, meta: { title: 'Plugins', titleKey: 'nav.plugins', advancedNav: true } },
  { path: '/plugin/:id', component: PluginPage, props: true, meta: { title: 'Plugin', titleKey: 'nav.plugins', advancedNav: true } },
  { path: '/binaries', component: Binaries, meta: { title: 'Binaries', titleKey: 'nav.binaries', advancedNav: true } },
  { path: '/composer', component: ComposerManager, meta: { title: 'Composer', titleKey: 'nav.composer', advancedNav: true } },
  { path: '/hosts', component: HostsManager, meta: { title: 'Hosts', titleKey: 'nav.hosts', advancedNav: true } },
  { path: '/service/:id', redirect: to => ({ path: '/services', query: { service: String(to.params.id) } }) },
  { path: '/service/:id/config', component: ServiceConfig, props: true, meta: { title: 'Service configuration', titleKey: 'services.title' } },
  { path: '/backups', component: BackupsPage, meta: { title: 'Backups', titleKey: 'nav.backups' } },
  // Phase 8 — single MCP hub with 4 tabs (Activity / Requests / Rules / Catalog).
  // /mcp redirects to /mcp/activity (the new default — full audit feed).
  // MCP visibility is driven by featureFlagsStore.showMcpSurface, not the
  // Simple/Advanced toggle — operators on Simple who run an AI client
  // need to reach the audit log too (incident 2026-05-07).
  { path: '/mcp', redirect: '/mcp/activity' },
  { path: '/mcp/activity', component: McpHub, meta: { title: 'MCP', titleKey: 'nav.mcp' } },
  { path: '/mcp/intents', component: McpHub, meta: { title: 'MCP', titleKey: 'nav.mcp' } },
  { path: '/mcp/grants',  component: McpHub, meta: { title: 'MCP', titleKey: 'nav.mcp' } },
  { path: '/mcp/kinds',   component: McpHub, meta: { title: 'MCP', titleKey: 'nav.mcp' } },
  { path: '/help', component: Help, meta: { title: 'Help', titleKey: 'nav.help' } },
  { path: '/login', component: Login, meta: { title: 'Sign in', titleKey: 'nav.signIn' } },
  // Unknown paths (stale links, removed pages) used to render an empty
  // shell; send them to the default landing page instead.
  { path: '/:pathMatch(.*)*', redirect: '/sites' },
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes: baseRoutes,
})

// Simple vs Advanced is a density setting, not an access level: routes
// marked `advancedNav` are only *listed* (sidebar, command palette,
// Ctrl+N shortcuts) in Advanced mode, but always open when reached by a
// link. The old guard silently bounced Simple-mode users to /sites from
// links on their own pages (service tiles, "edit config", deep links).
router.beforeEach((to) => {
  // F91: block deep-links to plugin-owned routes whose plugin is disabled.
  // isRouteVisible fails open before the first /api/plugins/ui fetch so the
  // initial navigation on app launch is never blocked by a pending request.
  const plugins = usePluginsStore()
  if (plugins.isPluginOwnedRoute(to.path) && !plugins.isRouteVisible(to.path)) {
    return { path: '/sites' }
  }
})

// Window title in the UI language; meta.title is the English fallback for
// routes without a titleKey.
export function updateDocumentTitle(route = router.currentRoute.value): void {
  const { t, te } = i18n.global
  const key = route.meta?.titleKey as string | undefined
  const page = key && te(key) ? t(key) : (route.meta?.title as string | undefined)
  const app = t('app.name')
  document.title = page ? `${page} — ${app}` : app
}

router.afterEach((to) => updateDocumentTitle(to))
