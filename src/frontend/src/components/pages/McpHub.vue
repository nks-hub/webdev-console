<template>
  <div class="page mcp-hub-page">
    <div class="hub-header">
      <h2>{{ t('mcpHub.title') }}</h2>
      <!-- The three words the rest of the page uses, in one sentence each. -->
      <ul v-if="advanced" class="hub-glossary">
        <li><strong>{{ t('mcpHub.tabs.requests') }}</strong>: {{ t('mcpHub.glossary.requests') }}</li>
        <li><strong>{{ t('mcpHub.tabs.rules') }}</strong>: {{ t('mcpHub.glossary.rules') }}</li>
        <li><strong>{{ t('mcpHub.tabs.catalog') }}</strong>: {{ t('mcpHub.glossary.catalog') }}</li>
      </ul>
      <!-- One line is not a list. -->
      <p v-else class="hub-glossary hub-glossary--single">
        <strong>{{ t('mcpHub.tabs.requests') }}</strong>: {{ t('mcpHub.glossary.requests') }}
      </p>
    </div>

    <!-- Connect an AI client — copy-paste registration for Claude Code,
         Claude Desktop and Cursor, the MCP enable switch, and live status
         (enabled, daemon ready, Node.js, last call). Collapses itself once
         an AI client has called WDC. -->
    <McpConnectCard />

    <!-- With MCP off the daemon refuses AI clients and every /api/mcp
         list answers 404, so there is nothing to show but the switch
         above. Turning MCP on mounts the panels below fresh. -->
    <div v-if="!flags.mcpEnabled" class="hub-off" data-testid="mcp-hub-off">
      <el-icon :size="28" aria-hidden="true"><Lock /></el-icon>
      <div>
        <strong>{{ t('mcpHub.off.title') }}</strong>
        <p class="muted">{{ t('mcpHub.off.body') }}</p>
      </div>
    </div>

    <template v-else>
      <!-- Simple mode keeps the connect card, Activity and Requests; rules,
           the action list and their tooling are advanced-mode only. A link
           to /mcp/grants or /mcp/kinds says so instead of silently showing
           Activity. -->
      <el-alert
        v-if="!advanced && advancedOnlyRoute"
        type="info"
        show-icon
        :closable="false"
        class="hub-simple-note"
        data-testid="mcp-hub-simple-note"
      >
        <template #title>{{ t('mcpHub.simpleOnly') }}</template>
        <el-button size="small" @click="uiMode.setUiMode('advanced')">{{ t('mcpHub.switchToAdvanced') }}</el-button>
      </el-alert>

      <!-- Suggested grants — nudges based on repeated manual approvals.
           Cross-tab so it surfaces regardless of which subview the user
           is currently looking at. -->
      <SuggestedGrantsBanner v-if="advanced" />

      <!-- At-a-glance stats: actionable when something needs attention,
           informational otherwise. -->
      <div v-if="advanced && grantsStats" class="hub-stats">
        <!-- Which onboarding profile the active rules match, if any. -->
        <span class="stat tier-chip" :class="`tier-${trustTier}`" :title="t(`mcpHub.tier.${trustTier}Hint`)">
          <el-icon v-if="trustTier === 'permissive'" aria-hidden="true"><Warning /></el-icon>
          <strong>{{ tierLabel }}</strong>
        </span>
        <span v-activate class="stat clickable-stat" @click="onActiveClick">
          <strong>{{ grantsStats.active }}</strong>
          <span class="muted">{{ t('mcpHub.stats.active') }}</span>
        </span>
        <span
          v-if="grantsStats.deadweight > 0"
          v-activate
          class="stat warn clickable-stat"
          :title="t('mcpHub.stats.deadweightHint')"
          @click="onDeadweightClick"
        >
          <el-icon aria-hidden="true"><Warning /></el-icon>
          <strong>{{ grantsStats.deadweight }}</strong>
          <span class="muted">{{ t('mcpHub.stats.deadweight') }}</span>
        </span>
        <span v-activate class="stat clickable-stat" @click="onTotalMatchesClick">
          <strong>{{ grantsStats.totalMatches }}</strong>
          <span class="muted">{{ t('mcpHub.stats.totalMatches') }}</span>
        </span>
        <span v-if="grantsStats.lastMatchAt" class="stat">
          <span class="muted">{{ t('mcpHub.stats.lastMatch') }}</span>
          <strong>{{ formatRelative(grantsStats.lastMatchAt) }}</strong>
        </span>
        <span
          v-if="alwaysConfirmCount > 0"
          v-activate
          class="stat warn clickable-stat"
          @click="onAlwaysConfirmClick"
        >
          <el-icon aria-hidden="true"><Lock /></el-icon>
          <strong>{{ alwaysConfirmCount }}</strong>
          <span class="muted">{{ t('mcpHub.stats.alwaysConfirm') }}</span>
        </span>
      </div>

      <!-- Panes are lazy: each loads its data when first opened. -->
      <div ref="tabsWrap">
        <el-tabs v-model="activeTab" class="hub-tabs" @tab-change="onTabChange">
          <!-- Activity — unified feed of every MCP tool call, including reads.
               Default tab because it answers "what is AI doing right now". -->
          <el-tab-pane name="activity" lazy>
            <template #label>
              <span class="tab-label">
                <el-icon><DataLine /></el-icon>
                {{ t('mcpHub.tabs.activity') }}
              </span>
            </template>
            <McpActivity />
          </el-tab-pane>

          <!-- Requests (intents): AI requests that need the operator. The
               badge counts the ones still waiting for a decision. -->
          <el-tab-pane name="intents" lazy>
            <template #label>
              <span class="tab-label">
                <el-icon><Lock /></el-icon>
                {{ t('mcpHub.tabs.requests') }}
                <el-badge v-if="counts.intents > 0" :value="counts.intents" type="warning" class="tab-badge" />
              </span>
            </template>
            <McpIntents />
          </el-tab-pane>

          <!-- Rules (grants): requests approved without asking. -->
          <el-tab-pane v-if="advanced" name="grants" lazy>
            <template #label>
              <span class="tab-label">
                <el-icon><Key /></el-icon>
                {{ t('mcpHub.tabs.rules') }}
                <el-badge v-if="counts.grants > 0" :value="counts.grants" type="success" class="tab-badge" />
              </span>
            </template>
            <!-- Profiles are rules, so they live on the Rules tab: above
                 the tabs they pushed Activity off the first screen. Hides
                 once a profile is picked or the panel is dismissed. -->
            <McpOnboardingPanel />
            <McpGrants />
          </el-tab-pane>

          <!-- Approved actions (kinds): what an AI has to ask about. -->
          <el-tab-pane v-if="advanced" name="kinds" lazy>
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
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { Lock, Key, Operation, DataLine, Warning } from '@element-plus/icons-vue'
import McpActivity from './McpActivity.vue'
import McpIntents from './McpIntents.vue'
import McpGrants from './McpGrants.vue'
import McpKinds from './McpKinds.vue'
import SuggestedGrantsBanner from '../mcp/SuggestedGrantsBanner.vue'
import McpOnboardingPanel from '../mcp/McpOnboardingPanel.vue'
import McpConnectCard from '../mcp/McpConnectCard.vue'
import { detectProfile } from '../mcp/mcpProfiles'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import { useMcpConfirmStore } from '../../stores/mcpConfirm'
import { useUiModeStore } from '../../stores/uiMode'
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
const flags = useFeatureFlagsStore()
const confirmStore = useMcpConfirmStore()
const uiMode = useUiModeStore()
const advanced = computed(() => uiMode.isAdvanced)

type TabName = 'activity' | 'intents' | 'grants' | 'kinds'

const activeTab = ref<TabName>(parseTab())
const tabsWrap = ref<HTMLElement | null>(null)

function parseTab(): TabName {
  if (route.path.endsWith('/intents')) return 'intents'
  if (!uiMode.isAdvanced) return 'activity'
  if (route.path.endsWith('/kinds')) return 'kinds'
  if (route.path.endsWith('/grants')) return 'grants'
  return 'activity'
}

watch(advanced, () => { activeTab.value = parseTab() })

const advancedOnlyRoute = computed(() => /\/mcp\/(grants|kinds)$/.test(route.path))

// A toast or notification opens /mcp/intents for a waiting request; the
// connect card and panels above would leave the table below the fold.
async function revealPendingRequests(): Promise<void> {
  if (activeTab.value !== 'intents' || confirmStore.count === 0) return
  await nextTick()
  tabsWrap.value?.scrollIntoView({ block: 'start', behavior: 'smooth' })
}

watch(() => route.path, () => {
  activeTab.value = parseTab()
  void revealPendingRequests()
})

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
const grantsRaw = ref<{ kindPattern: string; targetPattern: string }[]>([])

// The onboarding profile the active grants match ('custom' otherwise).
const trustTier = computed(() => detectProfile(grantsRaw.value))
const tierLabel = computed(() => trustTier.value === 'custom'
  ? t('mcpHub.tier.custom')
  : t(`mcpOnboarding.profiles.${trustTier.value}.name`))

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
  // The /api/mcp lists answer 404 while MCP is off.
  if (!flags.mcpEnabled) return
  try {
    const [intents, grants, kinds, stats] = await Promise.all([
      fetchIntentInventory(200).catch(() => ({ entries: [] })),
      listMcpGrants().catch(() => ({ entries: [] })),
      listMcpKinds().catch(() => ({ entries: [] })),
      fetchMcpGrantsStats().catch(() => null),
    ])
    // Only the requests still waiting for the operator, not the approved
    // ones the AI has yet to use.
    counts.intents = intents.entries.filter((e) => e.state === 'pending_confirmation').length
    counts.grants = grants.entries.length
    counts.kinds = kinds.entries.length
    alwaysConfirmCount.value = kinds.entries.filter((k) => k.alwaysConfirm === true).length
    grantsStats.value = stats
    grantsRaw.value = grants.entries.map(g => ({
      kindPattern: g.kindPattern ?? '',
      targetPattern: g.targetPattern ?? '',
    }))
  } catch { /* badges stay stale rather than disrupt the page */ }
}

watch(() => flags.mcpEnabled, (on) => { if (on) void refreshCounts() })

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
  void refreshCounts()
  void revealPendingRequests()
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
/* The global page rule pads the hub; children space themselves. */
.mcp-hub-page { gap: 0; }
.hub-header {
  padding: 0 0 var(--wdc-space-4);
  background: transparent;
}
.hub-header h2 {
  margin: 0;
  font-size: 1.6rem;
  font-weight: 800;
  letter-spacing: -0.02em;
  color: var(--wdc-text);
}
.hub-glossary {
  margin: var(--wdc-space-2) 0 0;
  padding-left: var(--wdc-space-4);
  color: var(--wdc-text-2);
  font-size: 0.87rem;
  line-height: 1.5;
}
.hub-glossary--single { padding-left: 0; }
.hub-simple-note { margin-bottom: var(--wdc-space-4); }
.hub-simple-note :deep(.el-alert__description) { margin-top: var(--wdc-space-2); }
.hub-off {
  display: flex;
  align-items: flex-start;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-4) var(--wdc-space-5);
  border: 1px dashed var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  color: var(--wdc-text-2);
}
.hub-off p { margin: var(--wdc-space-1) 0 0; }
.muted { color: var(--wdc-text-3); }
.hub-tabs :deep(.el-tabs__content) { padding-top: 8px; }
/* Tab pages are sections of the hub: their titles sit below the hub title. */
.hub-tabs :deep(.page-header h2) { font-size: 1.2rem; }
.tab-label { display: inline-flex; align-items: center; gap: 6px; }
.hub-stats {
  display: flex; flex-wrap: wrap; gap: 18px; margin: 4px 0 12px;
  padding: var(--wdc-space-3-5) var(--wdc-space-4-5); background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius); font-size: 13px;
  box-shadow: var(--wdc-shadow-sm);
}
.hub-stats .stat { display: inline-flex; align-items: center; gap: 6px; }
.hub-stats .stat strong { font-weight: 600; }
.hub-stats .stat.warn strong,
.hub-stats .stat.warn .el-icon { color: var(--el-color-warning); }
.tier-chip {
  padding: var(--wdc-space-1) var(--wdc-space-3);
  border-radius: var(--wdc-radius-full);
  font-size: 11px;
  font-weight: 600;
  border: 1px solid transparent;
}
.tier-chip.tier-minimal { background: var(--el-color-info-light-9); color: var(--el-color-info); border-color: var(--el-color-info-light-7); }
.tier-chip.tier-balanced,
.tier-chip.tier-localDev { background: var(--el-color-success-light-9); color: var(--el-color-success); border-color: var(--el-color-success-light-7); }
.tier-chip.tier-permissive { background: var(--el-color-warning-light-9); color: var(--el-color-warning); border-color: var(--el-color-warning-light-7); }
.tier-chip.tier-custom { background: var(--wdc-surface-2); color: var(--el-text-color-secondary); border-color: var(--wdc-border); }
.clickable-stat { cursor: pointer; user-select: none; }
.clickable-stat:hover strong { filter: brightness(1.2); }
.tab-badge { margin-left: 4px; }
.tab-badge :deep(.el-badge__content) {
  position: static;
  transform: none;
  vertical-align: middle;
  height: 16px;
  line-height: 14px;
  padding: 0 var(--wdc-space-1-5);
  font-size: 10px;
  border: none;
}
</style>
