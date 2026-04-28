<template>
  <div class="page mcp-hub-page">
    <!-- Onboarding panel — first-time setup with preset profiles. Stays
         at the very top of the page so the four trust-tier cards are
         the first thing the operator sees, before any header chrome.
         Auto-hides after a profile is picked or after the user dismisses it. -->
    <McpOnboardingPanel />

    <!-- Suggested grants — nudges based on repeated manual approvals.
         Cross-tab so it surfaces regardless of which subview the user
         is currently looking at. -->
    <SuggestedGrantsBanner />

    <div class="hub-header">
      <h2>{{ t('mcpHub.title') }}</h2>
      <p class="muted">{{ t('mcpHub.description') }}</p>

      <!-- At-a-glance stats: actionable when something needs attention,
           informational otherwise. Cleanup hint surfaces when grants are
           dead-weight (granted but never matched). -->
      <div v-if="grantsStats" class="hub-stats">
        <!-- Trust tier indicator — derived from current grant set:
             0 grants = Sandbox (everything asks)
             1+ specific grants without wildcard = Balanced
             single wildcard `*` `*` grant = Full Trust
             other shapes = Custom -->
        <span class="stat tier-chip" :class="`tier-${trustTier}`" :title="t(`mcpHub.tier.${trustTier}Hint`)">
          <strong>{{ tierIcon }} {{ t(`mcpHub.tier.${trustTier}`) }}</strong>
        </span>
        <span class="stat clickable-stat" @click="onActiveClick">
          <strong>{{ grantsStats.active }}</strong>
          <span class="muted">{{ t('mcpHub.stats.active') }}</span>
        </span>
        <span
          v-if="grantsStats.deadweight > 0"
          class="stat warn clickable-stat"
          :title="t('mcpHub.stats.deadweightHint')"
          @click="onDeadweightClick"
        >
          <strong>⚠️ {{ grantsStats.deadweight }}</strong>
          <span class="muted">{{ t('mcpHub.stats.deadweight') }}</span>
        </span>
        <span class="stat clickable-stat" @click="onTotalMatchesClick">
          <strong>{{ grantsStats.totalMatches }}</strong>
          <span class="muted">{{ t('mcpHub.stats.totalMatches') }}</span>
        </span>
        <span v-if="grantsStats.lastMatchAt" class="stat">
          <span class="muted">{{ t('mcpHub.stats.lastMatch') }}</span>
          <strong>{{ formatRelative(grantsStats.lastMatchAt) }}</strong>
        </span>
        <span
          v-if="alwaysConfirmCount > 0"
          class="stat warn clickable-stat"
          @click="onAlwaysConfirmClick"
        >
          <strong>🔒 {{ alwaysConfirmCount }}</strong>
          <span class="muted">{{ t('mcpHub.stats.alwaysConfirm') }}</span>
        </span>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="hub-tabs" @tab-change="onTabChange">
      <!-- Activity — unified feed of every MCP tool call, including reads.
           Default tab because it answers "what is AI doing right now". -->
      <el-tab-pane name="activity">
        <template #label>
          <span class="tab-label">
            <el-icon><DataLine /></el-icon>
            {{ t('mcpHub.tabs.activity') }}
          </span>
        </template>
        <McpActivity />
      </el-tab-pane>

      <!-- Requests — formerly "Intents". Signed AI requests requiring
           approval (destructive ops). -->
      <el-tab-pane name="intents">
        <template #label>
          <span class="tab-label">
            <el-icon><Lock /></el-icon>
            {{ t('mcpHub.tabs.requests') }}
            <el-badge v-if="counts.intents > 0" :value="counts.intents" type="warning" class="tab-badge" />
          </span>
        </template>
        <McpIntents />
      </el-tab-pane>

      <!-- Rules — formerly "Grants". Auto-approve rules. -->
      <el-tab-pane name="grants">
        <template #label>
          <span class="tab-label">
            <el-icon><Key /></el-icon>
            {{ t('mcpHub.tabs.rules') }}
            <el-badge v-if="counts.grants > 0" :value="counts.grants" type="success" class="tab-badge" />
          </span>
        </template>
        <McpGrants />
      </el-tab-pane>

      <!-- Catalog — formerly "Kinds". List of action types AI can request. -->
      <el-tab-pane name="kinds">
        <template #label>
          <span class="tab-label">
            <el-icon><Operation /></el-icon>
            {{ t('mcpHub.tabs.catalog') }}
            <el-badge v-if="counts.kinds > 0" :value="counts.kinds" type="info" class="tab-badge" />
          </span>
        </template>
        <McpKinds />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { Lock, Key, Operation, DataLine } from '@element-plus/icons-vue'
import McpActivity from './McpActivity.vue'
import McpIntents from './McpIntents.vue'
import McpGrants from './McpGrants.vue'
import McpKinds from './McpKinds.vue'
import SuggestedGrantsBanner from '../mcp/SuggestedGrantsBanner.vue'
import McpOnboardingPanel from '../mcp/McpOnboardingPanel.vue'
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

type TabName = 'activity' | 'intents' | 'grants' | 'kinds'

const activeTab = ref<TabName>(parseTab())

function parseTab(): TabName {
  if (route.path.endsWith('/kinds')) return 'kinds'
  if (route.path.endsWith('/grants')) return 'grants'
  if (route.path.endsWith('/intents')) return 'intents'
  return 'activity'
}

watch(() => route.path, () => { activeTab.value = parseTab() })

function onTabChange(name: string | number): void {
  const next = name === 'kinds' ? '/mcp/kinds'
    : name === 'grants' ? '/mcp/grants'
      : name === 'intents' ? '/mcp/intents'
        : '/mcp/activity'
  if (route.path !== next) router.push(next)
}

const counts = reactive({ intents: 0, grants: 0, kinds: 0 })
const grantsStats = ref<McpGrantsStats | null>(null)
const alwaysConfirmCount = ref<number>(0)
const grantsRaw = ref<{ kindPattern: string; targetPattern: string; scopeType: string }[]>([])

// Trust tier classification — purely derived from the current grant
// set, no separate stored field. Matches the 4 onboarding profiles.
const trustTier = computed<'sandbox' | 'balanced' | 'fullTrust' | 'custom'>(() => {
  const grants = grantsRaw.value
  if (grants.length === 0) return 'sandbox'
  // Full Trust = single grant of `*` × `*`
  const isFullTrust = grants.length === 1
    && (grants[0].kindPattern === '*' || grants[0].kindPattern === '')
    && (grants[0].targetPattern === '*' || grants[0].targetPattern === '')
  if (isFullTrust) return 'fullTrust'
  // Balanced = exactly the deploy + rollback wildcards (any order)
  const hasDeploy = grants.some(g => g.kindPattern === 'deploy' && g.targetPattern === '*')
  const hasRollback = grants.some(g => g.kindPattern === 'rollback' && g.targetPattern === '*')
  if (grants.length === 2 && hasDeploy && hasRollback) return 'balanced'
  return 'custom'
})

const tierIcon = computed(() => {
  switch (trustTier.value) {
    case 'sandbox': return '🛡️'
    case 'balanced': return '⚖️'
    case 'fullTrust': return '🚀'
    default: return '🎯'
  }
})
let unsubscribeHubSse: (() => void) | null = null

function onActiveClick(): void {
  void router.push({ path: '/mcp/grants' })
}

function onTotalMatchesClick(): void {
  void router.push({ path: '/mcp/intents' })
}

function onDeadweightClick(): void {
  void router.push({ path: '/mcp/grants', query: { usage: 'deadweight' } })
}

function onAlwaysConfirmClick(): void {
  void router.push({ path: '/mcp/kinds', query: { danger: 'destructive' } })
}

async function refreshCounts(): Promise<void> {
  try {
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
    // Capture pattern shape for trust-tier derivation.
    grantsRaw.value = grants.entries.map(g => ({
      kindPattern: g.kindPattern ?? '',
      targetPattern: g.targetPattern ?? '',
      scopeType: g.scopeType,
    }))
  } catch { /* badges stay stale rather than disrupt the page */ }
}

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
.tier-chip {
  padding: 2px 10px;
  border-radius: 12px;
  font-size: 12px;
  border: 1px solid transparent;
}
.tier-chip.tier-sandbox { background: var(--el-color-info-light-9); color: var(--el-color-info); border-color: var(--el-color-info-light-7); }
.tier-chip.tier-balanced { background: var(--el-color-success-light-9); color: var(--el-color-success); border-color: var(--el-color-success-light-7); }
.tier-chip.tier-fullTrust { background: var(--el-color-warning-light-9); color: var(--el-color-warning); border-color: var(--el-color-warning-light-7); }
.tier-chip.tier-custom { background: var(--el-fill-color); color: var(--el-text-color-secondary); }
.clickable-stat { cursor: pointer; user-select: none; }
.clickable-stat:hover strong { filter: brightness(1.2); }
.tab-badge { margin-left: 4px; }
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
