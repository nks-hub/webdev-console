import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'
import Dashboard from '../components/pages/Dashboard.vue'
import Sites from '../components/pages/Sites.vue'
import Settings from '../components/pages/Settings.vue'
import PluginManager from '../components/pages/PluginManager.vue'
import PluginPage from '../components/pages/PluginPage.vue'
import Binaries from '../components/pages/Binaries.vue'

const baseRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/sites' },
  { path: '/dashboard', component: Dashboard },
  { path: '/sites', component: Sites },
  { path: '/settings', component: Settings },
  { path: '/plugins', component: PluginManager },
  { path: '/plugin/:id', component: PluginPage, props: true },
  { path: '/binaries', component: Binaries },
  // Service detail pages — clicking service name in sidebar opens this
  { path: '/service/:id', component: Dashboard, props: true },
]

export const router = createRouter({
  history: createWebHashHistory(),
  routes: baseRoutes,
})
