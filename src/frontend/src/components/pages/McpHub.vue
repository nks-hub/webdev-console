<template>
  <div class="page mcp-hub-page">
    <div class="hub-header">
      <h2>{{ t('mcpHub.title') }}</h2>
      <p class="muted">{{ t('mcpHub.description') }}</p>
      <!-- Phase 7.5+++ — at-a-glance summary card. Telemetry that the
           individual tabs would only surface after switching. -->
      <div v-if="grantsStats" class="hub-stats">
        <span class="stat">
          <strong>{{ grantsStats.active }}</strong>
          <span class="muted">{{ t('mcpHub.stats.active') }}</span>
        </span>
        <span
          v-if="grantsStats.deadweight > 0"
          class="stat warn always-confirm-stat"
          @click="onDeadweightClick"
        >
          <strong>{{ grantsStats.deadweight }}</strong>
          <span class="muted">{{ t('mcpHub.stats.deadweight') }}</span>
        </span>
        <span class="stat">
          <strong>{{ grantsStats.totalMatches }}</strong>
          <span class="muted">{{ t('mcpHub.stats.totalMatches') }}</span>
        </span>
        <span v-if="grantsStats.lastMatchAt" class="stat">
          <span class="muted">{{ t('mcpHub.stats.lastMatch') }}</span>
          <strong>{{ formatRelative(grantsStats.lastMatchAt) }}</strong>
        </span>
        <!-- Phase 7.5+++ — always-confirm summary. Sourced from the same
             listMcpKinds() refresh that powers the Kinds tab badge.
             Click pulls operator into the Kinds tab where the 🔒 chips
             live; from there one more click jumps to Settings. -->
        <span
          v-if="alwaysConfirmCount > 0"
          class="stat warn always-confirm-stat"
          @click="onAlwaysConfirmClick"
        >
          <strong>🔒 {{ alwaysConfirmCount }}</strong>
          <span class="muted">{{ t('mcpHub.stats.alwaysConfirm') }}</span>
        </span>
      </div>
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
  fetchMcpGrantsStats,
  listMcpGrants,
  listMcpKinds,
  subscribeEventsMap,
  type McpGrantsStats,
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
const grantsStats = ref<McpGrantsStats | null>(null)
// Phase 7.5+++ — count of kinds in mcp.always_confirm_kinds (those that
// the operator has ring-fenced past grants). Refreshed from the same
// listMcpKinds() call so no extra round-trip.
const alwaysConfirmCount = ref<number>(0)
let unsubscribeHubSse: (() => void) | null = null

function onDeadweightClick(): void {
  // Phase 7.5+++ — go to /mcp/grants?usage=deadweight so the operator
  // can review/revoke unused grants. Mirrors the always-confirm
  // navigation pattern.
  void router.push({ path: '/mcp/grants', query: { usage: 'deadweight' } })
}

function onAlwaysConfirmClick(): void {
  // Phase 7.5+++ — go to /mcp/kinds with danger filter pre-set to
  // destructive (the kinds eligible for always-confirm). This narrows
  // operator focus from the start instead of forcing them to pick the
  // filter manually.
  void router.push({ path: '/mcp/kinds', query: { danger: 'destructive' } })
}

async function refreshCounts(): Promise<void> {
  try {
    // ready + pending_confirmation = "live" intents (the rest are
    // terminal: consumed/expired). Operators care about ones that
    // could still fire.
    const [intents, grants, kinds, stats] = await Promise.all([
      fetchIntentInventory(200).catch(() => ({ entries: [] })),
      listMcpGrants().catch(() => ({ entries: [] })),
      listMcpKinds().catch(() => ({ entries: [] })),
      fetchMcpGrantsStats().catch(() => null),
    ])
    counts.intents = intents.entries.filter((e) =>
      e.state === 'ready' || e.state === 'pending_confirmation').length
    counts.grants = grants.entries.length
    counts.kinds = kinds.entries.length
    alwaysConfirmCount.value = kinds.entries.filter((k) => k.alwaysConfirm === true).length
    grantsStats.value = stats
  } catch { /* badges stay stale rather than disrupt the page */ }
}

// Phase 7.5+++ — humanise lastMatchAt for the at-a-glance card.
function formatRelative(iso: string): string {
  try {
    const dt = new Date(iso).getTime()
    if (!Number.isFinite(dt)) return ''
    const deltaSec = Math.max(0, Math.round((Date.now() - dt) / 1000))
    if (deltaSec < 60) return t('mcpHub.stats.justNow')
    if (deltaSec < 3600) return t('mcpHub.stats.minutesAgo', { n: Math.floor(deltaSec / 60) })
    if (deltaSec < 86400) return t('mcpHub.stats.hoursAgo', { n: Math.floor(deltaSec / 3600) })
    return t('mcpHub.stats.daysAgo', { n: Math.floor(deltaSec / 86400) })
  } catch { return '' }
}

onMounted(() => {
  activeTab.value = parseTab()
  refreshCounts()
  unsubscribeHubSse = subscribeEventsMap({
    'mcp:intent-changed': () => { void refreshCounts() },
    'mcp:confirm-request': () => { void refreshCounts() },
    'mcp:grant-changed': () => { void refreshCounts() },
    // Phase 7.5+++ — settings change refreshes the kinds list so the
    // ring-fenced count chip stays accurate without manual reload.
    'mcp:settings-changed': () => { void refreshCounts() },
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
.hub-stats {
  display: flex; flex-wrap: wrap; gap: 16px; margin: 8px 0 4px;
  padding: 8px 12px; background: var(--el-fill-color-light);
  border-radius: 4px; font-size: 13px;
}
.hub-stats .stat { display: inline-flex; align-items: center; gap: 6px; }
.hub-stats .stat strong { font-weight: 600; }
.hub-stats .stat.warn strong { color: var(--el-color-warning); }
.always-confirm-stat { cursor: pointer; user-select: none; }
.always-confirm-stat:hover strong { filter: brightness(1.2); }
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
