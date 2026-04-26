<template>
  <div class="page mcp-hub-page">
    <div class="hub-header">
      <h2>{{ t('mcpHub.title') }}</h2>
      <p class="muted">{{ t('mcpHub.description') }}</p>
    </div>

    <el-tabs v-model="activeTab" class="hub-tabs" @tab-change="onTabChange">
      <el-tab-pane name="intents">
        <template #label>
          <span class="tab-label"><el-icon><Lock /></el-icon> {{ t('mcpHub.tabs.intents') }}</span>
        </template>
        <McpIntents />
      </el-tab-pane>
      <el-tab-pane name="grants">
        <template #label>
          <span class="tab-label"><el-icon><Key /></el-icon> {{ t('mcpHub.tabs.grants') }}</span>
        </template>
        <McpGrants />
      </el-tab-pane>
      <el-tab-pane name="kinds">
        <template #label>
          <span class="tab-label"><el-icon><Operation /></el-icon> {{ t('mcpHub.tabs.kinds') }}</span>
        </template>
        <McpKinds />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { Lock, Key, Operation } from '@element-plus/icons-vue'
import McpIntents from './McpIntents.vue'
import McpGrants from './McpGrants.vue'
import McpKinds from './McpKinds.vue'

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

onMounted(() => { activeTab.value = parseTab() })
</script>

<style scoped>
.page { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.hub-header h2 { margin: 0 0 4px; }
.hub-header .muted { color: var(--el-text-color-secondary); margin: 0 0 8px; font-size: 13px; }
.hub-tabs :deep(.el-tabs__content) { padding-top: 8px; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
</style>
