<template>
  <div class="page mcp-intents-page">
    <div class="page-header">
      <h2>{{ t('mcpIntents.title') }}</h2>
      <el-button :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> {{ t('mcpIntents.refresh') }}
      </el-button>
    </div>

    <p class="muted">{{ t('mcpIntents.description') }}</p>

    <!-- Phase 7.5+++ — active matchedGrant drilldown banner. Surfaces
         when operator clicked a grant id; one-click clear restores the
         full inventory. -->
    <el-alert
      v-if="matchedGrantFilter"
      type="info"
      show-icon
      :closable="false"
      class="grant-drilldown-banner"
    >
      <template #title>
        <span>
          {{ t('mcpIntents.matchedGrantBanner') }}
          <code class="mono">{{ matchedGrantFilter }}</code>
        </span>
      </template>
      <template #default>
        <el-button size="small" @click="setMatchedGrantFilter(null)">
          {{ t('mcpIntents.clearMatchedGrantFilter') }}
        </el-button>
      </template>
    </el-alert>

    <!-- State summary chips -->
    <div class="state-summary" role="group" :aria-label="t('mcpIntents.stateSummaryAria')">
      <el-tag v-for="s in stateCounts" :key="s.state" :type="stateTagType(s.state)" effect="plain">
        {{ s.state }}: {{ s.count }}
      </el-tag>
    </div>

    <!-- Phase 6.16b — per-domain breakdown. Helps spot which sites are
         minting the most AI activity. Only renders when 2+ domains
         present (single-domain inventories already get summary chips). -->
    <div
      v-if="domainStats.length > 1"
      class="domain-stats"
      role="group"
      :aria-label="t('mcpIntents.domainBreakdownAria')"
    >
      <div class="domain-stats-title">{{ t('mcpIntents.domainBreakdownTitle') }}</div>
      <div class="domain-stats-grid">
        <div v-for="d in domainStats" :key="d.domain" class="domain-stats-cell">
          <code class="mono domain-name" :title="d.domain">{{ d.domain }}</code>
          <div class="domain-stats-counts">
            <el-tag
              v-for="(count, state) in d.byState"
              :key="state"
              :type="stateTagType(state)"
              size="small"
              effect="plain"
              class="domain-stats-chip"
            >
              {{ state }}: {{ count }}
            </el-tag>
          </div>
        </div>
      </div>
    </div>

    <!-- Phase 6.13b — filter toolbar. State + kind selects + free-text
         domain search. Filters apply client-side over the loaded list
         (cap 200) — no server round-trip needed. -->
    <div class="filter-toolbar" role="search" :aria-label="t('mcpIntents.filter.groupAria')">
      <el-select
        v-model="stateFilter"
        :placeholder="t('mcpIntents.filter.allStates')"
        clearable
        size="small"
        style="width: 180px"
        :aria-label="t('mcpIntents.filter.stateAria')"
      >
        <el-option label="ready" value="ready" />
        <el-option label="pending_confirmation" value="pending_confirmation" />
        <el-option label="consumed" value="consumed" />
        <el-option label="expired" value="expired" />
      </el-select>
      <el-select
        v-model="kindFilter"
        :placeholder="t('mcpIntents.filter.allKinds')"
        clearable
        size="small"
        style="width: 140px"
        :aria-label="t('mcpIntents.filter.kindAria')"
      >
        <el-option label="deploy" value="deploy" />
        <el-option label="rollback" value="rollback" />
        <el-option label="cancel" value="cancel" />
        <el-option label="restore" value="restore" />
      </el-select>
      <el-input
        v-model="domainFilter"
        :placeholder="t('mcpIntents.filter.search')"
        size="small"
        clearable
        style="max-width: 260px"
        :aria-label="t('mcpIntents.filter.searchAria')"
      >
        <template #prefix><el-icon><Search /></el-icon></template>
      </el-input>
      <span v-if="filteredEntries.length !== entries.length" class="muted filter-count">
        {{ t('mcpIntents.filter.showing', { visible: filteredEntries.length, total: entries.length }) }}
      </span>
    </div>

    <el-empty
      v-if="!entries.length && !loading"
      :description="t('mcpIntents.emptyNoIntents')"
    />

    <el-empty
      v-else-if="!filteredEntries.length"
      :image-size="60"
      :description="t('mcpIntents.emptyNoMatch')"
    />

    <template v-else>
      <!-- Phase 6.18b — bulk action toolbar. Renders when at least one
           revokable row is selected. Sits inside the table render path
           so the empty-state v-if/v-else-if chain above stays intact. -->
      <div v-if="selectedRevokable.length > 0" class="bulk-toolbar" role="toolbar"
           :aria-label="t('mcpIntents.bulk.toolbarAria')">
        <span class="muted">{{ t('mcpIntents.bulk.selected', { n: selectedRevokable.length }) }}</span>
        <el-button
          type="danger"
          size="small"
          :loading="bulkRevoking"
          @click="onBulkRevoke"
        >
          {{ t('mcpIntents.bulk.revokeSelected', { n: selectedRevokable.length }) }}
        </el-button>
        <el-button size="small" @click="clearSelection">{{ t('mcpIntents.bulk.clearSelection') }}</el-button>
      </div>

      <el-table
        ref="tableRef"
        :data="filteredEntries"
        stripe
        size="small"
        :empty-text="t('mcpIntents.emptyTable')"
        class="intent-table"
        @selection-change="onSelectionChange"
      >
        <!-- Selection column. el-table's `type=selection` + `selectable`
             prop disables the checkbox for non-revokable rows. -->
        <el-table-column type="selection" width="40" :selectable="rowSelectable" />
      <el-table-column :label="t('mcpIntents.col.created')" width="170">
        <template #default="{ row }">
          <span class="mono">{{ formatDate(row.createdAt) }}</span>
        </template>
      </el-table-column>

      <el-table-column prop="domain" :label="t('mcpIntents.col.domain')" min-width="140" />
      <el-table-column prop="host" :label="t('mcpIntents.col.host')" width="120" />
      <el-table-column prop="kind" :label="t('mcpIntents.col.kind')" min-width="180">
        <template #default="{ row }">
          <!-- Phase 7.5+++ — operator-locale label via humanKindLabel
               (mirrors the helper in McpKinds, McpConfirmBanner,
               Settings always-confirm picker, McpGrants tooltip).
               When the localized label differs from the daemon-supplied
               one, surface the daemon label as the title attr so plugin
               authors can verify their English shipped through. -->
          <el-tag
            :type="row.kindDanger === 'destructive' ? 'danger' : kindTagType(row.kind)"
            size="small"
            effect="plain"
            :title="kindTagTitle(row)"
          >
            {{ humanKindLabel(row) }}
          </el-tag>
          <code v-if="humanKindLabel(row) !== row.kind" class="kind-id-mono mono">{{ row.kind }}</code>
        </template>
      </el-table-column>

      <el-table-column :label="t('mcpIntents.col.state')" width="170">
        <template #default="{ row }">
          <el-tag :type="stateTagType(row.state)" size="small" effect="plain">
            <el-icon class="state-icon" aria-hidden="true">
              <component :is="stateIcon(row.state)" />
            </el-icon>
            {{ row.state }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column :label="t('mcpIntents.col.expires')" width="170">
        <template #default="{ row }">
          <span class="mono" :class="{ expired: row.state === 'expired' }">
            {{ formatRelative(row.expiresAt) }}
          </span>
        </template>
      </el-table-column>

      <el-table-column :label="t('mcpIntents.col.intentId')" width="130">
        <template #default="{ row }">
          <code
            class="mono intent-id"
            :title="row.intentId"
            @click="copyId(row.intentId)"
          >
            {{ row.intentId.slice(0, 8) }}…
          </code>
        </template>
      </el-table-column>

      <el-table-column :label="t('mcpIntents.col.matchedGrant')" width="130">
        <template #default="{ row }">
          <code v-if="row.matchedGrantId" class="mono auto-grant"
            :title="t('mcpIntents.matchedGrantClickHint', { id: row.matchedGrantId })"
            @click="setMatchedGrantFilter(row.matchedGrantId)">
            ⚡ {{ row.matchedGrantId.slice(0, 8) }}…
          </code>
          <span v-else-if="row.confirmedAt" class="muted"
            :title="t('mcpIntents.confirmedManually')">
            {{ t('mcpIntents.manualConfirm') }}
          </span>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>

      <el-table-column label="" width="100" align="right">
        <template #default="{ row }">
          <el-button
            v-if="row.state === 'ready' || row.state === 'pending_confirmation'"
            size="small"
            link
            type="danger"
            :loading="revokingId === row.intentId"
            :disabled="revokingId !== null && revokingId !== row.intentId"
            :aria-label="t('mcpIntents.revokeAria')"
            @click="onRevoke(row)"
          >
            {{ t('mcpIntents.revoke') }}
          </el-button>
        </template>
      </el-table-column>
      </el-table>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import {
  Refresh,
  CircleCheck,
  Warning,
  CircleClose,
  Loading,
  Search,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'

const { t } = useI18n()
import {
  fetchIntentInventory,
  revokeIntent,
  subscribeEventsMap,
  type IntentInventoryEntry,
} from '../../api/daemon'

const entries = ref<IntentInventoryEntry[]>([])
const loading = ref(false)
const revokingId = ref<string | null>(null)

// Phase 6.18b — bulk revoke selection state. The el-table emits
// selection-change with the FULL selected set (not deltas) so we mirror
// it as a Set for fast membership checks.
const selectedIntents = ref<IntentInventoryEntry[]>([])
const bulkRevoking = ref(false)
const tableRef = ref<unknown>(null)

function rowSelectable(row: IntentInventoryEntry): boolean {
  // Only ready/pending_confirmation rows are revokable. Mirroring the
  // single-row Revoke button visibility check.
  return row.state === 'ready' || row.state === 'pending_confirmation'
}

function onSelectionChange(rows: IntentInventoryEntry[]): void {
  selectedIntents.value = rows
}

const selectedRevokable = computed(() =>
  selectedIntents.value.filter((r) => rowSelectable(r)))

function clearSelection(): void {
  // Reach into the el-table instance to clear its checkbox state.
  // Element Plus exposes clearSelection() on the table ref.
  const t = tableRef.value as { clearSelection?: () => void } | null
  t?.clearSelection?.()
  selectedIntents.value = []
}

async function onBulkRevoke(): Promise<void> {
  const targets = selectedRevokable.value
  if (targets.length === 0 || bulkRevoking.value) return
  try {
    const targetsHead = targets.slice(0, 5).map((r) => `${r.kind} on ${r.domain}`).join(', ')
    const moreSuffix = targets.length > 5 ? t('mcpIntents.bulk.confirmMore', { n: targets.length - 5 }) : ''
    await ElMessageBox.confirm(
      t('mcpIntents.bulk.confirmMessage', { n: targets.length, targets: targetsHead + moreSuffix }),
      t('mcpIntents.bulk.confirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('mcpIntents.bulk.confirmRevokeBtn'),
        cancelButtonText: t('mcpIntents.bulk.cancel'),
      },
    )
  } catch { return }

  bulkRevoking.value = true
  let ok = 0
  let failed = 0
  // Serial revoke — daemon writes are cheap and serial keeps the
  // failure surface deterministic (operator sees "5 ok, 2 failed"
  // rather than racing parallel POSTs).
  for (const target of targets) {
    try {
      await revokeIntent(target.intentId)
      ok++
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      // 409 already_used is benign here (same as single-row revoke);
      // count as success since the desired end state was reached.
      if (msg.includes('already_used')) ok++
      else failed++
    }
  }
  bulkRevoking.value = false
  clearSelection()
  if (failed === 0) {
    ElMessage.success(t('mcpIntents.bulk.toastOk', { n: ok }))
  } else {
    ElMessage.warning(t('mcpIntents.bulk.toastPartial', { ok, failed }))
  }
  await refresh()
}

// Phase 6.13b — client-side filters. Empty/null = no filter applied.
const stateFilter = ref<string | null>(null)
const kindFilter = ref<string | null>(null)
const domainFilter = ref('')

// Phase 7.5+++ — server-side matchedGrantId drilldown. When set, we
// re-fetch with the filter so the count + entries reflect the full
// archive of intents this grant approved (not just what's in the
// current 200-row window). null = filter inactive.
const matchedGrantFilter = ref<string | null>(null)

function setMatchedGrantFilter(id: string | null): void {
  matchedGrantFilter.value = id
  void refresh()
}

const filteredEntries = computed<IntentInventoryEntry[]>(() => {
  const q = domainFilter.value.trim().toLowerCase()
  return entries.value.filter((e) => {
    if (stateFilter.value && e.state !== stateFilter.value) return false
    if (kindFilter.value && e.kind !== kindFilter.value) return false
    if (q && !e.domain.toLowerCase().includes(q) && !e.host.toLowerCase().includes(q)) {
      return false
    }
    return true
  })
})

async function refresh(): Promise<void> {
  loading.value = true
  try {
    // Phase 7.5+++ — pass current matchedGrant filter so the inventory
    // shows the full audit chain (not capped to recent 200 across all
    // grants when one grant might have hundreds of historical hits).
    const result = await fetchIntentInventory(200, matchedGrantFilter.value)
    entries.value = result.entries
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Failed to load intents: ${msg}`)
  } finally {
    loading.value = false
  }
}

const stateCounts = computed(() => {
  const counts = new Map<string, number>()
  for (const e of entries.value) {
    counts.set(e.state, (counts.get(e.state) ?? 0) + 1)
  }
  // Stable order: ready → pending_confirmation → consumed → expired
  const order = ['ready', 'pending_confirmation', 'consumed', 'expired']
  return order
    .filter((s) => counts.has(s))
    .map((state) => ({ state, count: counts.get(state) ?? 0 }))
})

interface DomainStats {
  domain: string
  total: number
  byState: Record<string, number>
}

/**
 * Phase 6.16b — group entries by domain and per-state count. Sorted by
 * total descending so the most-active domain leads (usually production).
 * Computed off `entries` (not filteredEntries) so the breakdown shows
 * the FULL distribution regardless of active filters — gives the
 * operator a sense of where activity is coming from before they
 * narrow with filters.
 */
const domainStats = computed<DomainStats[]>(() => {
  const byDomain = new Map<string, DomainStats>()
  for (const e of entries.value) {
    const cur = byDomain.get(e.domain) ?? {
      domain: e.domain,
      total: 0,
      byState: {} as Record<string, number>,
    }
    cur.total++
    cur.byState[e.state] = (cur.byState[e.state] ?? 0) + 1
    byDomain.set(e.domain, cur)
  }
  return Array.from(byDomain.values()).sort((a, b) => b.total - a.total)
})

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString()
}

function formatRelative(iso: string): string {
  const now = Date.now()
  const t = new Date(iso).getTime()
  const sec = Math.round((t - now) / 1000)
  if (sec < -60) return `${Math.abs(Math.round(sec / 60))}m ago`
  if (sec < 0) return `${Math.abs(sec)}s ago`
  if (sec < 60) return `in ${sec}s`
  if (sec < 3600) return `in ${Math.round(sec / 60)}m`
  return `in ${Math.round(sec / 3600)}h`
}

function stateTagType(state: string): 'success' | 'warning' | 'info' | 'danger' {
  if (state === 'ready') return 'success'
  if (state === 'pending_confirmation') return 'warning'
  if (state === 'consumed') return 'info'
  return 'danger' // expired
}

function stateIcon(state: string) {
  if (state === 'ready') return CircleCheck
  if (state === 'pending_confirmation') return Warning
  if (state === 'consumed') return CircleCheck
  return CircleClose
}

function kindTagType(kind: string): 'danger' | 'warning' | 'info' {
  if (kind === 'restore' || kind === 'rollback') return 'warning'
  if (kind === 'deploy') return 'danger'
  return 'info' // cancel
}

// Phase 7.5+++ — same kind-label helper as the other 4 MCP surfaces
// (McpKinds table, McpConfirmBanner, Settings picker, McpGrants
// tooltip). 3-level fallback: localized i18n key → daemon-supplied
// kindLabel → bare wire id.
function humanKindLabel(row: { kind: string; kindLabel?: string }): string {
  const key = `mcpKinds.labels.${row.kind}`
  const localized = t(key)
  return localized !== key ? localized : (row.kindLabel || row.kind)
}

// Title attribute for the kind tag — surfaces daemon-supplied label
// when our localization replaces it (so plugin authors verify their
// shipped English carries through), plus pluginId for provenance.
function kindTagTitle(row: { kind: string; kindLabel?: string; kindPluginId?: string }): string {
  const localized = humanKindLabel(row)
  const daemon = row.kindLabel || row.kind
  const provenance = row.kindPluginId ? ` (${row.kindPluginId})` : ''
  return localized !== daemon ? `${daemon}${provenance}` : `${row.kind}${provenance}`
}

async function onRevoke(row: IntentInventoryEntry): Promise<void> {
  if (revokingId.value !== null) return
  try {
    await ElMessageBox.confirm(
      t('mcpIntents.single.confirmMessage', {
        id: row.intentId.slice(0, 8),
        kind: row.kind,
        domain: row.domain,
        host: row.host,
      }),
      t('mcpIntents.single.confirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('mcpIntents.single.confirmRevokeBtn'),
        cancelButtonText: t('mcpIntents.single.cancel'),
      },
    )
  } catch { return }

  revokingId.value = row.intentId
  try {
    await revokeIntent(row.intentId)
    ElMessage.success(t('mcpIntents.single.toastRevoked'))
    await refresh()
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(t('mcpIntents.single.toastRevokeFailed', { error: msg }))
  } finally {
    revokingId.value = null
  }
}

async function copyId(id: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(id)
    ElMessage.success(t('mcpIntents.single.toastIdCopied'))
  } catch {
    ElMessage.warning(t('mcpIntents.single.toastCopyFail'))
  }
}

// Phase 7.5+++ — subscribe to mcp:intent-changed (lifecycle: created/
// confirmed/revoked) AND mcp:confirm-request (banner trigger). Both
// invalidate the inventory so the table never lags behind reality.
// Single shared EventSource via subscribeEventsMap multiplex.
let unsubscribeIntentSse: (() => void) | null = null

// Phase 7.5+++ — read URL `?matchedGrantId=<id>` on mount + on route
// change so deep-links from McpGrants drilldown apply the filter
// without manual user interaction. Keeps local ref + URL in sync via
// `watch`; setMatchedGrantFilter() updates the ref but not the URL
// to avoid feedback loops (URL is read-only input, not output).
const route = useRoute()
const router = useRouter()

// Phase 7.5+++ — kind + domain filter URL sync for consistency with
// other MCP pages. Initial values applied in onMounted (defined below).
function applyKindFromRoute(): void {
  const fromRoute = (route.query.kind as string | undefined) ?? null
  if (fromRoute !== kindFilter.value) kindFilter.value = fromRoute
}
function applyDomainFromRoute(): void {
  const fromRoute = (route.query.domain as string | undefined) ?? ''
  if (fromRoute !== domainFilter.value) domainFilter.value = fromRoute
}
watch(() => route.query.kind, applyKindFromRoute)
watch(() => route.query.domain, applyDomainFromRoute)
watch(kindFilter, (next) => {
  const current = (route.query.kind as string | undefined) ?? null
  const desired = next || null
  if (current === desired) return
  const { kind: _, ...rest } = route.query
  void router.replace({ path: route.path, query: desired ? { ...rest, kind: desired } : rest })
})
watch(domainFilter, (next) => {
  const current = (route.query.domain as string | undefined) ?? ''
  const desired = next || ''
  if (current === desired) return
  const { domain: _, ...rest } = route.query
  void router.replace({ path: route.path, query: desired ? { ...rest, domain: desired } : rest })
})

function applyMatchedGrantFromRoute(): void {
  const fromRoute = (route.query.matchedGrantId as string | undefined) ?? null
  if (fromRoute && fromRoute !== matchedGrantFilter.value) {
    matchedGrantFilter.value = fromRoute
    void refresh()
  }
}

// Phase 7.5+++ — also honour ?state= URL filter so deep-links can land
// directly on a state slice (e.g. `?state=pending_confirmation` for
// "intents waiting on operator click"). Bidirectional: local change
// pushes back to URL via watcher below.
function applyStateFromRoute(): void {
  const fromRoute = (route.query.state as string | undefined) ?? null
  if (fromRoute !== stateFilter.value) {
    stateFilter.value = fromRoute
  }
}

onMounted(() => {
  applyMatchedGrantFromRoute()
  applyStateFromRoute()
  applyKindFromRoute()
  applyDomainFromRoute()
  refresh()
  unsubscribeIntentSse = subscribeEventsMap({
    'mcp:intent-changed': () => { void refresh() },
    'mcp:confirm-request': () => { void refresh() },
  })
})

// Re-apply when navigating between tabs that share the page instance.
watch(() => route.query.matchedGrantId, () => { applyMatchedGrantFromRoute() })
watch(() => route.query.state, () => { applyStateFromRoute() })

// Push local stateFilter changes to URL so back/forward preserves it.
watch(stateFilter, (next) => {
  const current = (route.query.state as string | undefined) ?? null
  const desired = next || null
  if (current === desired) return
  const { state: _, ...rest } = route.query
  void router.replace({
    path: route.path,
    query: desired ? { ...rest, state: desired } : rest,
  })
})

onBeforeUnmount(() => {
  if (unsubscribeIntentSse) { unsubscribeIntentSse(); unsubscribeIntentSse = null }
})
</script>

<style scoped>
.mcp-intents-page {
  padding: 16px 24px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.page-header h2 {
  margin: 0;
  font-size: 20px;
}
.muted {
  color: var(--el-text-color-secondary);
  font-size: 13px;
  max-width: 760px;
}
.state-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.domain-stats {
  padding: 10px 12px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
}
.domain-stats-title {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-bottom: 6px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.domain-stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 8px;
}
.domain-stats-cell {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 6px 8px;
  background: var(--el-bg-color);
  border-radius: 3px;
  border: 1px solid var(--el-border-color-lighter);
}
.domain-name {
  font-weight: 600;
  font-size: 12px;
  word-break: break-all;
}
.domain-stats-counts {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.domain-stats-chip {
  font-variant-numeric: tabular-nums;
}
.filter-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 4px;
}
.filter-count {
  margin-left: auto;
  font-size: 12px;
}
.bulk-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: var(--el-color-warning-light-9, #fdf6ec);
  border: 1px solid var(--el-color-warning, #e6a23c);
  border-left-width: 4px;
  border-radius: 4px;
  margin-bottom: 4px;
}
.intent-table {
  width: 100%;
}
.kind-id-mono {
  margin-left: 6px;
  font-size: 11px;
  color: var(--el-text-color-secondary);
  opacity: 0.7;
}
.mono {
  font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
  font-size: 12px;
}
.expired {
  color: var(--el-color-danger);
}
.intent-id {
  cursor: copy;
  user-select: all;
}
.auto-grant {
  cursor: pointer;
  color: var(--el-color-warning);
  font-weight: 600;
  text-decoration: underline dotted;
  text-underline-offset: 2px;
}
.auto-grant:hover { opacity: 0.85; }
.grant-drilldown-banner { margin-bottom: 4px; }
.state-icon {
  margin-right: 4px;
  vertical-align: -2px;
}
</style>
