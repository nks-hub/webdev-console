<template>
  <div v-if="suggestions.length > 0" class="suggested-grants-banner">
    <div class="banner-header">
      <span class="banner-title">
        {{ t('mcpHub.suggested.title', { n: suggestions.length }, suggestions.length) }}
      </span>
      <el-button
        size="small"
        link
        class="dismiss-btn"
        :icon="Close"
        :aria-label="t('mcpHub.suggested.dismiss')"
        :title="t('mcpHub.suggested.dismiss')"
        @click="dismissed = true"
      />
    </div>
    <p class="banner-hint muted">{{ t('mcpHub.suggested.hint') }}</p>
    <div class="suggestion-list">
      <div
        v-for="(s, idx) in suggestions"
        :key="`${s.kind}-${s.domain}-${s.host}`"
        class="suggestion-row"
        :class="{ pending: pendingIdx === idx }"
      >
        <span class="suggestion-text">
          <span class="kind">{{ mcpKindLabel(s) }}</span>
          <el-tag
            v-if="s.kindDanger === 'destructive'"
            type="danger"
            size="small"
            effect="plain"
          >{{ t('mcpKinds.danger.destructive') }}</el-tag>
          <span class="muted">–</span>
          <code class="mono target">{{ s.domain }}</code>
          <span class="occurrences">
            {{ t('mcpHub.suggested.occurrences', { n: s.occurrences }) }}
          </span>
        </span>
        <el-button
          size="small"
          type="primary"
          :loading="pendingIdx === idx"
          :disabled="pendingIdx !== null && pendingIdx !== idx"
          @click="approve(s, idx)"
        >
          {{ t('mcpHub.suggested.autoApprove') }}
        </el-button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Close } from '@element-plus/icons-vue'
import { mcpKindLabel } from '../../utils/mcpKindLabel'
import {
  fetchMcpSuggestedGrants,
  createMcpGrant,
  subscribeEventsMap,
  type McpGrantSuggestion,
} from '../../api/daemon'

const { t } = useI18n()

const allSuggestions = ref<McpGrantSuggestion[]>([])
const dismissed = ref(false)
const pendingIdx = ref<number | null>(null)

const suggestions = computed(() => (dismissed.value ? [] : allSuggestions.value))

async function refresh(): Promise<void> {
  try {
    const data = await fetchMcpSuggestedGrants(7, 3)
    allSuggestions.value = data.suggestions
  } catch (err) {
    // Not shown to the operator: suggestions are optional, and the page
    // reports a daemon that is down elsewhere.
    console.warn('[SuggestedGrantsBanner] refresh failed:', err)
  }
}

async function approve(s: McpGrantSuggestion, idx: number): Promise<void> {
  pendingIdx.value = idx
  try {
    // An "always" grant for exactly this (kind, domain) pair. The daemon
    // matches the target pattern against the intent's domain alone, so the
    // target is the domain itself (a "domain/*" pattern never matched).
    await createMcpGrant({
      scopeType: 'always',
      scopeValue: null,
      kindPattern: s.kind,
      targetPattern: s.domain,
      grantedBy: 'auto-suggest',
      note: `Suggested from ${s.occurrences} manual approvals (last 7 days)`,
    })
    ElMessage.success(t('mcpHub.suggested.created', { kind: mcpKindLabel(s), domain: s.domain }))
    // The daemon leaves out pairs a grant covers, so the next refresh
    // agrees; drop every row of this pair now (one per host).
    allSuggestions.value = allSuggestions.value.filter((x) => x.kind !== s.kind || x.domain !== s.domain)
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(t('mcpHub.suggested.failed', { error: msg }))
  } finally {
    pendingIdx.value = null
  }
}

// Refresh on grant + intent changes anywhere in the app — a manual
// approve-from-banner or a new destructive intent confirm changes the
// pool of "manually approved" rows the suggestion engine reads.
let unsubscribe: (() => void) | null = null

onMounted(() => {
  void refresh()
  unsubscribe = subscribeEventsMap({
    'mcp:grant-changed': () => { void refresh() },
    'mcp:intent-changed': () => { void refresh() },
  })
})

onBeforeUnmount(() => {
  if (unsubscribe) unsubscribe()
})
</script>

<style scoped>
.suggested-grants-banner {
  border: 1px solid var(--el-color-warning-light-5);
  background: color-mix(in oklab, var(--wdc-status-starting) 8%, var(--wdc-surface));
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-3-5) var(--wdc-space-4-5);
  margin-bottom: var(--wdc-space-3);
  box-shadow: var(--wdc-shadow-sm);
}
.banner-header {
  display: flex; align-items: center; justify-content: space-between;
  margin-bottom: var(--wdc-space-1);
}
.banner-title { font-weight: 600; font-size: 13px; }
.dismiss-btn { padding: 0 4px; }
.banner-hint { margin: 0 0 8px 0; font-size: 12px; }
.suggestion-list { display: flex; flex-direction: column; gap: 4px; }
.suggestion-row {
  display: flex; align-items: center; justify-content: space-between;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-2) var(--wdc-space-3);
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  font-size: 12px;
}
.suggestion-row.pending { opacity: 0.7; }
.suggestion-text {
  display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
}
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.kind { font-weight: 600; }
.target { color: var(--el-text-color-secondary); }
.occurrences {
  color: var(--el-color-warning); font-weight: 700;
  background: var(--el-color-warning-light-7); padding: 1px 8px; border-radius: 999px;
  font-size: 10px;
}
.muted { color: var(--el-text-color-secondary); }
</style>
