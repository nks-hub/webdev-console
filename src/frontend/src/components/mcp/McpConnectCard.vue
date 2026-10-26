<template>
  <section class="mcp-connect-card" data-testid="mcp-connect-card" :aria-label="t('mcpConnect.title')">
    <div class="connect-header">
      <div class="connect-heading">
        <h3 class="connect-title">{{ t('mcpConnect.title') }}</h3>
        <p class="connect-hint muted">{{ t('mcpConnect.hint') }}</p>
      </div>
      <div class="connect-header-actions">
        <label class="enable-switch">
          <span>{{ t('mcpConnect.enableLabel') }}</span>
          <el-switch
            :model-value="flags.mcpEnabled"
            :loading="savingEnabled"
            data-testid="mcp-connect-enable"
            :aria-label="t('mcpConnect.enableLabel')"
            @change="onToggleEnabled"
          />
        </label>
        <el-button size="small" text @click="expanded = !expanded">
          {{ expanded ? t('mcpConnect.hideSetup') : t('mcpConnect.showSetup') }}
        </el-button>
      </div>
    </div>

    <!-- Live status: what still blocks an AI client from working. -->
    <ul class="connect-status" role="list" :aria-label="t('mcpConnect.statusAria')">
      <li :class="flags.mcpEnabled ? 'ok' : 'bad'">
        <span class="dot" aria-hidden="true" />
        {{ flags.mcpEnabled ? t('mcpConnect.status.enabled') : t('mcpConnect.status.disabled') }}
      </li>
      <li :class="serverInfo?.portFilePresent ? 'ok' : 'bad'">
        <span class="dot" aria-hidden="true" />
        {{ serverInfo?.portFilePresent ? t('mcpConnect.status.portFile') : t('mcpConnect.status.noPortFile') }}
      </li>
      <li v-if="serverInfo" :class="serverInfo.serverExists ? 'ok' : 'bad'">
        <span class="dot" aria-hidden="true" />
        {{ serverInfo.serverExists ? t('mcpConnect.status.serverFound') : t('mcpConnect.status.serverMissing') }}
      </li>
      <li :class="lastCallAt ? 'ok' : 'idle'">
        <span class="dot" aria-hidden="true" />
        <template v-if="lastCallAt">
          {{ t('mcpConnect.status.lastCall', { when: formatRelative(lastCallAt), tool: lastCallTool }) }}
        </template>
        <template v-else>{{ t('mcpConnect.status.noCalls') }}</template>
      </li>
    </ul>

    <div v-if="expanded" class="connect-body">
      <div v-for="snippet in snippets" :key="snippet.id" class="snippet" :data-snippet="snippet.id">
        <div class="snippet-head">
          <div>
            <div class="snippet-title">{{ t(`mcpConnect.clients.${snippet.id}.title`) }}</div>
            <div class="snippet-where muted">{{ t(`mcpConnect.clients.${snippet.id}.where`) }}</div>
          </div>
          <el-button size="small" :icon="DocumentCopy" :data-testid="`mcp-connect-copy-${snippet.id}`" @click="copy(snippet.text)">
            {{ t('mcpConnect.copy') }}
          </el-button>
        </div>
        <pre class="snippet-code"><code>{{ snippet.text }}</code></pre>
      </div>
      <p class="connect-footnote muted">
        {{ t('mcpConnect.nodeRequired') }}
        <template v-if="!serverInfo">{{ t('mcpConnect.pathUnknown') }}</template>
      </p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { DocumentCopy } from '@element-plus/icons-vue'
import { fetchMcpToolCalls, saveSettings, subscribeEventsMap } from '../../api/daemon'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import {
  MCP_SERVER_PATH_PLACEHOLDER,
  claudeCodeCommand,
  mcpServersJson,
} from '../../utils/mcpConnect'

const { t } = useI18n()
const flags = useFeatureFlagsStore()

const serverInfo = ref<McpServerInfo | null>(null)
const lastCallAt = ref<string | null>(null)
const lastCallTool = ref('')
const savingEnabled = ref(false)
// Open by default until an AI client has actually called WDC.
const expanded = ref(true)

const serverPath = computed(() => serverInfo.value?.serverPath ?? MCP_SERVER_PATH_PLACEHOLDER)

const snippets = computed(() => [
  { id: 'claudeCode', text: claudeCodeCommand(serverPath.value) },
  { id: 'claudeDesktop', text: mcpServersJson(serverPath.value) },
  { id: 'cursor', text: mcpServersJson(serverPath.value) },
])

async function refreshServerInfo(): Promise<void> {
  try {
    serverInfo.value = (await window.electronAPI?.getMcpServerInfo?.()) ?? null
  } catch {
    serverInfo.value = null
  }
}

// The audit log records every MCP tool call, so its newest row is the last
// time any AI client reached WDC through the MCP server.
async function refreshLastCall(): Promise<void> {
  try {
    const page = await fetchMcpToolCalls(1, 0)
    const newest = page.entries[0]
    lastCallAt.value = newest?.calledAt ?? null
    lastCallTool.value = newest?.toolName ?? ''
  } catch {
    // Daemon offline — keep the last known value.
  }
}

async function refresh(): Promise<void> {
  await Promise.all([refreshServerInfo(), refreshLastCall()])
}

async function onToggleEnabled(value: string | number | boolean): Promise<void> {
  const enabled = value === true
  savingEnabled.value = true
  try {
    // Same setting as Settings → Advanced → MCP.
    await saveSettings({ 'mcp.enabled': String(enabled) })
    await flags.load()
    ElMessage.success(enabled ? t('mcpConnect.enabledToast') : t('mcpConnect.disabledToast'))
  } catch (err) {
    ElMessage.error(t('mcpConnect.toggleFailed', { error: err instanceof Error ? err.message : String(err) }))
  } finally {
    savingEnabled.value = false
  }
}

async function copy(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text)
    ElMessage.success(t('mcpConnect.copied'))
  } catch {
    ElMessage.warning(t('mcpConnect.copyFailed'))
  }
}

function formatRelative(iso: string): string {
  const sec = Math.round((Date.now() - new Date(iso).getTime()) / 1000)
  if (Number.isNaN(sec)) return iso
  if (sec < 60) return t('mcpConnect.ago.seconds', { n: Math.max(sec, 0) })
  if (sec < 3600) return t('mcpConnect.ago.minutes', { n: Math.round(sec / 60) })
  if (sec < 86_400) return t('mcpConnect.ago.hours', { n: Math.round(sec / 3600) })
  return t('mcpConnect.ago.days', { n: Math.round(sec / 86_400) })
}

let unsubscribe: (() => void) | null = null
// The MCP token file appears when the daemon (re)starts; poll the cheap
// local check so the status follows a daemon restart without a reload.
let timer: ReturnType<typeof setInterval> | null = null

onMounted(async () => {
  await refresh()
  if (lastCallAt.value) expanded.value = false
  unsubscribe = subscribeEventsMap({
    'mcp:tool-call': () => { void refreshLastCall() },
    'mcp:settings-changed': () => { void flags.load() },
  })
  timer = setInterval(() => { void refreshServerInfo() }, 15_000)
})

onBeforeUnmount(() => {
  unsubscribe?.()
  if (timer) clearInterval(timer)
})
</script>

<style scoped>
.mcp-connect-card {
  border: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  border-radius: var(--wdc-radius-lg);
  padding: var(--wdc-space-4) var(--wdc-space-5);
  margin-bottom: var(--wdc-space-4);
  box-shadow: var(--wdc-shadow-card);
}
.connect-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
}
.connect-title { margin: 0; font-size: 16px; font-weight: 600; }
.connect-hint { margin: 4px 0 0; font-size: 13px; }
.connect-header-actions {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
}
.enable-switch {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-2);
  font-size: 13px;
  font-weight: 600;
}
.connect-status {
  list-style: none;
  display: flex;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
  margin: var(--wdc-space-3) 0 0;
  padding: 0;
}
.connect-status li {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--wdc-border);
  background: var(--wdc-surface-2);
  color: var(--wdc-text-2);
}
.connect-status .dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--el-text-color-placeholder);
}
.connect-status .ok .dot { background: var(--el-color-success); }
.connect-status .bad .dot { background: var(--el-color-danger); }
.connect-status .bad { color: var(--el-text-color-primary); }
.connect-body {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: var(--wdc-space-3);
  margin-top: var(--wdc-space-4);
}
.snippet {
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-3);
  background: var(--wdc-surface-2);
  min-width: 0;
}
.snippet-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: var(--wdc-space-2);
  margin-bottom: var(--wdc-space-2);
}
.snippet-title { font-weight: 600; font-size: 13px; }
.snippet-where { font-size: 12px; margin-top: 2px; word-break: break-word; }
.snippet-code {
  margin: 0;
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border-radius: var(--wdc-radius-sm);
  background: var(--wdc-bg);
  border: 1px solid var(--wdc-border);
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--el-text-color-primary);
}
.connect-footnote {
  grid-column: 1 / -1;
  margin: 0;
  font-size: 12px;
}
.muted { color: var(--el-text-color-secondary); }

@media (max-width: 640px) {
  .mcp-connect-card { padding: var(--wdc-space-3-5); }
  .connect-body { grid-template-columns: 1fr; }
}
</style>
