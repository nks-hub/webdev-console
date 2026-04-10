import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'
import * as ElementPlusIcons from '@element-plus/icons-vue'

import { router } from './router/index'
import App from './App.vue'

// Register panel components in the plugin registry
import { registerPanelComponent } from './plugins/PluginRegistry'
import ServiceCard from './components/shared/ServiceCard.vue'
import VersionSwitcher from './components/shared/VersionSwitcher.vue'
import ConfigEditor from './components/shared/ConfigEditor.vue'
import LogViewer from './components/shared/LogViewer.vue'

registerPanelComponent('service-status-card', ServiceCard)
registerPanelComponent('version-switcher', VersionSwitcher)
registerPanelComponent('config-editor', ConfigEditor)
registerPanelComponent('log-viewer', LogViewer)

const app = createApp(App)

// Register all Element Plus icons globally
for (const [name, component] of Object.entries(ElementPlusIcons)) {
  app.component(name, component)
}

app.use(createPinia())
app.use(ElementPlus)
app.use(router)
app.mount('#app')
