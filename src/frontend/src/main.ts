import { createApp, defineAsyncComponent } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'
import './assets/tailwind.css'
import * as ElementPlusIcons from '@element-plus/icons-vue'

import { router } from './router/index'
import { i18n } from './i18n'
import App from './App.vue'

// Register panel components in the plugin registry. Wrapped in
// defineAsyncComponent so their heavy deps (echarts for MetricsChart,
// xterm for LogViewer) get code-split into their own chunks instead of
// being pulled into the main bundle through this eager import barrel.
import { registerPanelComponent } from './plugins/PluginRegistry'

registerPanelComponent('service-status-card', defineAsyncComponent(() => import('./components/shared/ServiceCard.vue')))
registerPanelComponent('version-switcher', defineAsyncComponent(() => import('./components/shared/VersionSwitcher.vue')))
registerPanelComponent('config-editor', defineAsyncComponent(() => import('./components/shared/ConfigEditor.vue')))
registerPanelComponent('log-viewer', defineAsyncComponent(() => import('./components/shared/LogViewer.vue')))
registerPanelComponent('metrics-chart', defineAsyncComponent(() => import('./components/shared/MetricsChart.vue')))

const app = createApp(App)

// Register all Element Plus icons globally
for (const [name, component] of Object.entries(ElementPlusIcons)) {
  app.component(name, component)
}

app.use(createPinia())
app.use(ElementPlus)
app.use(router)
app.use(i18n)

// Global error handler — prevents white screen on component errors
app.config.errorHandler = (err, instance, info) => {
  console.error('[Vue Error]', info, err)
}

app.mount('#app')
