<template>
  <div class="settings-page">
    <h2>Settings</h2>


    <el-tabs v-model="activeTab">
      <el-tab-pane label="Ports" name="ports">
        <el-form label-position="left" label-width="160px" size="small" style="max-width: 400px">
          <el-form-item label="HTTP Port">
            <el-input-number v-model="ports.http" :min="1" :max="65535" />
          </el-form-item>
          <el-form-item label="HTTPS Port">
            <el-input-number v-model="ports.https" :min="1" :max="65535" />
          </el-form-item>
          <el-form-item label="MySQL Port">
            <el-input-number v-model="ports.mysql" :min="1" :max="65535" />
          </el-form-item>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="General" name="general">
        <el-form label-position="left" label-width="180px" size="small" style="max-width: 420px">
          <el-form-item label="Theme">
            <el-radio-group :model-value="themeStore.mode" @update:model-value="themeStore.setMode($event as ThemeMode)">
              <el-radio-button value="dark">Dark</el-radio-button>
              <el-radio-button value="light">Light</el-radio-button>
              <el-radio-button value="system">System</el-radio-button>
            </el-radio-group>
          </el-form-item>
          <el-form-item label="Run on startup">
            <el-switch v-model="runOnStartup" />
          </el-form-item>
          <el-form-item label="Auto-start services">
            <el-switch v-model="autoStart" />
          </el-form-item>
        </el-form>
      </el-tab-pane>

      <el-tab-pane label="About" name="about">
        <div class="about-section">
          <p><strong>NKS WDC</strong></p>
          <p class="version">Version {{ appVersion }}</p>
          <p class="subtitle">Local development server manager</p>
        </div>
      </el-tab-pane>
    </el-tabs>

    <div class="settings-footer">
      <el-button type="primary" size="small" :loading="saving" @click="save">Save Settings</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { useThemeStore, type ThemeMode } from '../../stores/theme'

const appVersion = import.meta.env.VITE_APP_VERSION as string | undefined ?? '0.1.0'
const themeStore = useThemeStore()

const activeTab = ref('ports')
const ports = reactive({ http: 80, https: 443, mysql: 3306 })
const runOnStartup = ref(false)
const autoStart = ref(true)
const saving = ref(false)

declare global {
  interface Window {
    daemonApi?: { getPort: () => number; getToken: () => string }
  }
}

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = window.daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

function authHeaders(): Record<string, string> {
  const urlToken = new URLSearchParams(window.location.search).get('token')
  const token = window.daemonApi?.getToken?.() || urlToken || ''
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  return headers
}

onMounted(async () => {
  try {
    const r = await fetch(`${daemonBase()}/api/settings`, { headers: authHeaders() })
    if (!r.ok) return
    const data = await r.json() as Record<string, string>
    if (data['ports.http']) ports.http = parseInt(data['ports.http'])
    if (data['ports.https']) ports.https = parseInt(data['ports.https'])
    if (data['ports.mysql']) ports.mysql = parseInt(data['ports.mysql'])
    if (data['general.runOnStartup']) runOnStartup.value = data['general.runOnStartup'] === 'true'
    if (data['general.autoStart']) autoStart.value = data['general.autoStart'] === 'true'
  } catch {
    // daemon not reachable — keep defaults
  }
})

async function save() {
  saving.value = true
  try {
    const payload: Record<string, string> = {
      'ports.http': String(ports.http),
      'ports.https': String(ports.https),
      'ports.mysql': String(ports.mysql),
      'general.runOnStartup': String(runOnStartup.value),
      'general.autoStart': String(autoStart.value),
    }
    const r = await fetch(`${daemonBase()}/api/settings`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify(payload),
    })
    if (!r.ok) throw new Error(`HTTP ${r.status}`)
    ElMessage.success('Settings saved')
  } catch (e: any) {
    ElMessage.error(`Failed to save settings: ${e.message}`)
  } finally {
    saving.value = false
  }
}
</script>

<style scoped>
.settings-page { padding: 24px; }
.settings-page h2 { margin: 0 0 20px; font-size: 1.2rem; }
.settings-footer { margin-top: 24px; }
.about-section { display: flex; flex-direction: column; gap: 4px; }
.version { font-family: monospace; color: var(--el-text-color-secondary); }
.subtitle { color: var(--el-text-color-secondary); font-size: 0.85rem; }
</style>
