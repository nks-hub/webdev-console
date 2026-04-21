import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import { useUiModeStore } from '../stores/uiMode'
import { usePluginsStore } from '../stores/plugins'

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
const MailpitPluginPage = () => import('../components/pages/MailpitPluginPage.vue')
const RedisPluginPage = () => import('../components/pages/RedisPluginPage.vue')
const ComposerManager = () => import('../components/pages/ComposerManager.vue')
const HostsManager = () => import('../components/pages/HostsManager.vue')
const Help = () => import('../components/pages/Help.vue')
const Login = () => import('../components/pages/Login.vue')
const BackupsPage = () => import('../components/pages/BackupsPage.vue')

const baseRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/sites' },
  { path: '/dashboard', component: Dashboard, meta: { title: 'Dashboard', titleKey: 'nav.overview' } },
  { path: '/sites', component: Sites, meta: { title: 'Sites', titleKey: 'nav.sites' } },
  { path: '/sites/:domain/edit', component: SiteEdit, props: true, meta: { title: 'Edit Site', titleKey: 'nav.editSite' } },
  { path: '/databases', component: Databases, meta: { title: 'Databases', titleKey: 'nav.databases', requiresAdvanced: true } },
  { path: '/ssl', component: SslManager, meta: { title: 'SSL', titleKey: 'nav.ssl', requiresAdvanced: true } },
  { path: '/php', component: PhpManager, meta: { title: 'PHP', titleKey: 'nav.php', requiresAdvanced: true } },
  { path: '/cloudflare', component: CloudflareTunnel, meta: { title: 'Cloudflare Tunnel', titleKey: 'nav.tunnel', requiresAdvanced: true } },
  { path: '/plugins/apache', component: ApachePluginPage, meta: { title: 'Apache', titleKey: 'nav.webServer', requiresAdvanced: true } },
  { path: '/plugins/php-custom', component: PhpPluginPage, meta: { title: 'PHP', titleKey: 'nav.php', requiresAdvanced: true } },
  { path: '/plugins/mysql', component: MySqlPluginPage, meta: { title: 'MySQL', titleKey: 'nav.database', requiresAdvanced: true } },
  { path: '/plugins/mailpit', component: MailpitPluginPage, meta: { title: 'Mailpit', titleKey: 'nav.cacheMail', requiresAdvanced: true } },
  { path: '/plugins/redis', component: RedisPluginPage, meta: { title: 'Redis', titleKey: 'nav.cacheMail', requiresAdvanced: true } },
  { path: '/settings', component: Settings, meta: { title: 'Settings', titleKey: 'nav.settings' } },
  { path: '/plugins', component: PluginManager, meta: { title: 'Plugins', titleKey: 'nav.plugins', requiresAdvanced: true } },
  { path: '/plugin/:id', component: PluginPage, props: true, meta: { title: 'Plugin', titleKey: 'nav.plugins', requiresAdvanced: true } },
  { path: '/binaries', component: Binaries, meta: { title: 'Binaries', titleKey: 'nav.binaries', requiresAdvanced: true } },
  { path: '/composer', component: ComposerManager, meta: { title: 'Composer', titleKey: 'nav.composer', requiresAdvanced: true } },
  { path: '/hosts', component: HostsManager, meta: { title: 'Hosts', titleKey: 'nav.hosts', requiresAdvanced: true } },
  { path: '/service/:id', component: Dashboard, props: true, meta: { title: 'Service', titleKey: 'nav.services', requiresAdvanced: true } },
  { path: '/service/:id/config', component: ServiceConfig, props: true, meta: { title: 'Service Config', titleKey: 'nav.services', requiresAdvanced: true } },
  { path: '/backups', component: BackupsPage, meta: { title: 'Zálohy', titleKey: 'nav.backups' } },
  { path: '/help', component: Help, meta: { title: 'Help', titleKey: 'nav.help' } },
  { path: '/login', component: Login, meta: { title: 'Sign in', chromeless: true } },
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes: baseRoutes,
})

router.beforeEach((to) => {
  const uiMode = useUiModeStore()
  if (to.meta.requiresAdvanced && uiMode.isSimple) {
    return { path: '/sites' }
  }
  // F91: block deep-links to plugin-owned routes whose plugin is disabled.
  // isRouteVisible fails open before the first /api/plugins/ui fetch so the
  // initial navigation on app launch is never blocked by a pending request.
  const plugins = usePluginsStore()
  if (plugins.isPluginOwnedRoute(to.path) && !plugins.isRouteVisible(to.path)) {
    return { path: '/sites' }
  }
})

router.afterEach((to) => {
  const title = (to.meta?.title as string) ?? 'NKS WebDev Console'
  document.title = `${title} — NKS WDC`
})
