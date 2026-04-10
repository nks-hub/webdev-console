import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import Dashboard from '../components/pages/Dashboard.vue'
import Sites from '../components/pages/Sites.vue'
import Settings from '../components/pages/Settings.vue'
import PluginManager from '../components/pages/PluginManager.vue'
import PluginPage from '../components/pages/PluginPage.vue'
import Binaries from '../components/pages/Binaries.vue'
import Databases from '../components/pages/Databases.vue'

const baseRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/sites' },
  { path: '/dashboard', component: Dashboard, meta: { title: 'Services' } },
  { path: '/sites', component: Sites, meta: { title: 'Sites' } },
  { path: '/databases', component: Databases, meta: { title: 'Databases' } },
  { path: '/settings', component: Settings, meta: { title: 'Settings' } },
  { path: '/plugins', component: PluginManager, meta: { title: 'Plugins' } },
  { path: '/plugin/:id', component: PluginPage, props: true, meta: { title: 'Plugin' } },
  { path: '/binaries', component: Binaries, meta: { title: 'Binaries' } },
  { path: '/service/:id', component: Dashboard, props: true, meta: { title: 'Service' } },
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes: baseRoutes,
})

router.afterEach((to) => {
  const title = (to.meta?.title as string) ?? 'NKS WebDev Console'
  document.title = `${title} — NKS WDC`
})
