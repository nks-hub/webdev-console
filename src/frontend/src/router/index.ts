import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import { useUiModeStore } from '../stores/uiMode'
import Dashboard from '../components/pages/Dashboard.vue'
import Sites from '../components/pages/Sites.vue'
import Settings from '../components/pages/Settings.vue'
import PluginManager from '../components/pages/PluginManager.vue'
import PluginPage from '../components/pages/PluginPage.vue'
import Binaries from '../components/pages/Binaries.vue'
import Databases from '../components/pages/Databases.vue'
import SslManager from '../components/pages/SslManager.vue'
import PhpManager from '../components/pages/PhpManager.vue'
import ServiceConfig from '../components/pages/ServiceConfig.vue'
import SiteEdit from '../components/pages/SiteEdit.vue'
import CloudflareTunnel from '../components/pages/CloudflareTunnel.vue'

const baseRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/sites' },
  { path: '/dashboard', component: Dashboard, meta: { title: 'Dashboard', titleKey: 'nav.overview' } },
  { path: '/sites', component: Sites, meta: { title: 'Sites', titleKey: 'nav.sites' } },
  { path: '/sites/:domain/edit', component: SiteEdit, props: true, meta: { title: 'Edit Site', titleKey: 'nav.editSite' } },
  { path: '/databases', component: Databases, meta: { title: 'Databases', titleKey: 'nav.databases', requiresAdvanced: true } },
  { path: '/ssl', component: SslManager, meta: { title: 'SSL', titleKey: 'nav.ssl', requiresAdvanced: true } },
  { path: '/php', component: PhpManager, meta: { title: 'PHP', titleKey: 'nav.php', requiresAdvanced: true } },
  { path: '/cloudflare', component: CloudflareTunnel, meta: { title: 'Cloudflare Tunnel', titleKey: 'nav.tunnel', requiresAdvanced: true } },
  { path: '/settings', component: Settings, meta: { title: 'Settings', titleKey: 'nav.settings' } },
  { path: '/plugins', component: PluginManager, meta: { title: 'Plugins', titleKey: 'nav.plugins', requiresAdvanced: true } },
  { path: '/plugin/:id', component: PluginPage, props: true, meta: { title: 'Plugin', titleKey: 'nav.plugins', requiresAdvanced: true } },
  { path: '/binaries', component: Binaries, meta: { title: 'Binaries', titleKey: 'nav.binaries', requiresAdvanced: true } },
  { path: '/service/:id', component: Dashboard, props: true, meta: { title: 'Service', titleKey: 'nav.services', requiresAdvanced: true } },
  { path: '/service/:id/config', component: ServiceConfig, props: true, meta: { title: 'Service Config', titleKey: 'nav.services', requiresAdvanced: true } },
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
})

router.afterEach((to) => {
  const title = (to.meta?.title as string) ?? 'NKS WebDev Console'
  document.title = `${title} — NKS WDC`
})
