<template>
  <div class="page mcp-hub-page">
    <div class="hub-header">
      <h2>{{ t('mcpHub.title') }}</h2>
      <p class="muted">{{ t('mcpHub.description') }}</p>
    </div>

    <el-tabs v-model="activeTab" class="hub-tabs" @tab-change="onTabChange">
      <el-tab-pane name="intents">
        <template #label>
          <span class="tab-label">
            <el-icon><Lock /></el-icon>
            {{ t('mcpHub.tabs.intents') }}
            <el-badge v-if="counts.intents > 0" :value="counts.intents" type="warning" class="tab-badge" />
          </span>
        </template>
        <McpIntents />
      </el-tab-pane>
      <el-tab-pane name="grants">
        <template #label>
          <span class="tab-label">
            <el-icon><Key /></el-icon>
            {{ t('mcpHub.tabs.grants') }}
            <el-badge v-if="counts.grants > 0" :value="counts.grants" type="success" class="tab-badge" />
          </span>
        </template>
        <McpGrants />
      </el-tab-pane>
      <el-tab-pane name="kinds">
        <template #label>
          <span class="tab-label">
            <el-icon><Operation /></el-icon>
            {{ t('mcpHub.tabs.kinds') }}
            <el-badge v-if="counts.kinds > 0" :value="counts.kinds" type="info" class="tab-badge" />
          </span>
        </template>
        <McpKinds />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { Lock, Key, Operation } from '@element-plus/icons-vue'
import McpIntents from './McpIntents.vue'
import McpGrants from './McpGrants.vue'
import McpKinds from './McpKinds.vue'
import {
  fetchIntentInventory,
  listMcpGrants,
  listMcpKinds,
  subscribeEventsMap,
} from '../../api/daemon'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

type TabName = 'intents' | 'grants' | 'kinds'

const activeTab = ref<TabName>(parseTab())

function parseTab(): TabName {
  if (route.path.endsWith('/kinds')) return 'kinds'
  if (route.path.endsWith('/grants')) return 'grants'
  return 'intents'
}

// Keep tab state ↔ URL in sync so deep-links (`/mcp/grants`,
// `/mcp/kinds`) land directly on the right tab and the browser back
// button works.
watch(() => route.path, () => { activeTab.value = parseTab() })

function onTabChange(name: string | number): void {
  const next = name === 'kinds'
    ? '/mcp/kinds'
    : name === 'grants'
      ? '/mcp/grants'
      : '/mcp/intents'
  if (route.path !== next) router.push(next)
}

// Phase 7.5+++ — tab badges with active counts. Fetch lightweight
// summaries (count only, full data lives in child components) and
// auto-refresh on the existing SSE events. The badges give operators
// at-a-glance visibility without forcing them to switch tabs.
const counts = reactive({ intents: 0, grants: 0, kinds: 0 })
let unsubscribeHubSse: (() => void) | null = null

async function refreshCounts(): Promise<void> {
  try {
    // ready + pending_confirmation = "live" intents (the rest are
    // terminal: consumed/expired). Operators care about ones that
    // could still fire.
    const [intents, grants, kinds] = await Promise.all([
      fetchIntentInventory(200).catch(() => ({ entries: [] })),
      listMcpGrants().catch(() => ({ entries: [] })),
      listMcpKinds().catch(() => ({ entries: [] })),
    ])
    counts.intents = intents.entries.filter((e) =>
      e.state === 'ready' || e.state === 'pending_confirmation').length
    counts.grants = grants.entries.length
    counts.kinds = kinds.entries.length
  } catch { /* badges stay stale rather than disrupt the page */ }
}

onMounted(() => {
  activeTab.value = parseTab()
  refreshCounts()
  unsubscribeHubSse = subscribeEventsMap({
    'mcp:intent-changed': () => { void refreshCounts() },
    'mcp:confirm-request': () => { void refreshCounts() },
    'mcp:grant-changed': () => { void refreshCounts() },
  })
})

onBeforeUnmount(() => {
  if (unsubscribeHubSse) { unsubscribeHubSse(); unsubscribeHubSse = null }
})
</script>

<style scoped>
.page { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.hub-header h2 { margin: 0 0 4px; }
.hub-header .muted { color: var(--el-text-color-secondary); margin: 0 0 8px; font-size: 13px; }
.hub-tabs :deep(.el-tabs__content) { padding-top: 8px; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
.tab-badge { margin-left: 4px; }
/* Push the badge counter inline rather than absolute-positioned. */
.tab-badge :deep(.el-badge__content) {
  position: static;
  transform: none;
  vertical-align: middle;
  height: 16px;
  line-height: 14px;
  padding: 0 6px;
  font-size: 10px;
  border: none;
}
</style>
