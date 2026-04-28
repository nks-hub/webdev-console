<template>
  <div v-if="suggestions.length > 0" class="suggested-grants-banner">
    <div class="banner-header">
      <span class="banner-title">
        💡 {{ t('mcpHub.suggested.title', { n: suggestions.length }) }}
      </span>
      <el-button size="small" link @click="dismissed = true" class="dismiss-btn">
        ✕
      </el-button>
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
          <code class="mono kind">{{ s.kindLabel || s.kind }}</code>
          <el-tag
            v-if="s.kindDanger === 'destructive'"
            type="danger"
            size="small"
            effect="plain"
          >destructive</el-tag>
          <span class="muted">→</span>
          <code class="mono target">{{ s.domain }}/{{ s.host }}</code>
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
    console.warn('[SuggestedGrantsBanner] refresh failed:', err)
  }
}

async function approve(s: McpGrantSuggestion, idx: number): Promise<void> {
  pendingIdx.value = idx
  try {
    // Create an "always" grant matching this exact (kind, domain) combo.
    // Host pattern is broad (*) so future host additions in the same
    // domain stay covered without re-approval.
    await createMcpGrant({
      scopeType: 'always',
      scopeValue: null,
      kindPattern: s.kind,
      targetPattern: `${s.domain}/*`,
      grantedBy: 'auto-suggest',
      note: `Suggested from ${s.occurrences} manual approvals (last 7 days)`,
    })
    ElMessage.success(t('mcpHub.suggested.created', { kind: s.kindLabel || s.kind, domain: s.domain }))
    // Remove that suggestion from the list — the next refresh would
    // also drop it because matchedGrantId on future intents won't be
    // null anymore.
    allSuggestions.value = allSuggestions.value.filter((_, i) => i !== idx)
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
  background: var(--el-color-warning-light-9);
  border-radius: 6px;
  padding: 10px 14px;
  margin-bottom: 8px;
}
.banner-header {
  display: flex; align-items: center; justify-content: space-between;
  margin-bottom: 4px;
}
.banner-title { font-weight: 600; font-size: 13px; }
.dismiss-btn { padding: 0 4px; }
.banner-hint { margin: 0 0 8px 0; font-size: 12px; }
.suggestion-list { display: flex; flex-direction: column; gap: 4px; }
.suggestion-row {
  display: flex; align-items: center; justify-content: space-between;
  gap: 12px;
  padding: 6px 8px;
  background: var(--el-bg-color);
  border-radius: 4px;
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
  color: var(--el-color-warning); font-weight: 600;
  background: var(--el-color-warning-light-7); padding: 1px 6px; border-radius: 3px;
}
.muted { color: var(--el-text-color-secondary); }
</style>
