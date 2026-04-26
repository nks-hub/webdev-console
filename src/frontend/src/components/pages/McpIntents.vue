<template>
  <div class="page mcp-intents-page">
    <div class="page-header">
      <h2>MCP Intent Inventory</h2>
      <el-button :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> Refresh
      </el-button>
    </div>

    <p class="muted">
      Audit trail of HMAC-signed intent tokens minted by AI / CI clients
      via the Model Context Protocol. Each intent authorises a single
      destructive operation (deploy / rollback / cancel / restore) on a
      specific (domain, host) pair. Used intents stay around for 7 days
      as audit history; expired-unused intents are swept after 1 day.
    </p>

    <!-- State summary chips -->
    <div class="state-summary" role="group" aria-label="Intent state summary">
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
      aria-label="Intent breakdown by domain"
    >
      <div class="domain-stats-title">Per-domain breakdown:</div>
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
    <div class="filter-toolbar" role="search" aria-label="Filter intents">
      <el-select
        v-model="stateFilter"
        placeholder="All states"
        clearable
        size="small"
        style="width: 180px"
        aria-label="Filter by state"
      >
        <el-option label="ready" value="ready" />
        <el-option label="pending_confirmation" value="pending_confirmation" />
        <el-option label="consumed" value="consumed" />
        <el-option label="expired" value="expired" />
      </el-select>
      <el-select
        v-model="kindFilter"
        placeholder="All kinds"
        clearable
        size="small"
        style="width: 140px"
        aria-label="Filter by kind"
      >
        <el-option label="deploy" value="deploy" />
        <el-option label="rollback" value="rollback" />
        <el-option label="cancel" value="cancel" />
        <el-option label="restore" value="restore" />
      </el-select>
      <el-input
        v-model="domainFilter"
        placeholder="Search domain or host…"
        size="small"
        clearable
        style="max-width: 260px"
        aria-label="Search by domain or host"
      >
        <template #prefix><el-icon><Search /></el-icon></template>
      </el-input>
      <span v-if="filteredEntries.length !== entries.length" class="muted filter-count">
        Showing {{ filteredEntries.length }} of {{ entries.length }}
      </span>
    </div>

    <el-empty
      v-if="!entries.length && !loading"
      description="No MCP intents recorded — the daemon hasn't seen any AI-signed destructive ops yet."
    />

    <el-empty
      v-else-if="!filteredEntries.length"
      :image-size="60"
      description="No intents match the current filters"
    />

    <el-table
      v-else
      :data="filteredEntries"
      stripe
      size="small"
      :empty-text="'No intents'"
      class="intent-table"
    >
      <el-table-column label="Created" width="170">
        <template #default="{ row }">
          <span class="mono">{{ formatDate(row.createdAt) }}</span>
        </template>
      </el-table-column>

      <el-table-column prop="domain" label="Domain" min-width="140" />
      <el-table-column prop="host" label="Host" width="120" />
      <el-table-column prop="kind" label="Kind" width="100">
        <template #default="{ row }">
          <el-tag :type="kindTagType(row.kind)" size="small" effect="plain">
            {{ row.kind }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column label="State" width="170">
        <template #default="{ row }">
          <el-tag :type="stateTagType(row.state)" size="small" effect="plain">
            <el-icon class="state-icon" aria-hidden="true">
              <component :is="stateIcon(row.state)" />
            </el-icon>
            {{ row.state }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column label="Expires" width="170">
        <template #default="{ row }">
          <span class="mono" :class="{ expired: row.state === 'expired' }">
            {{ formatRelative(row.expiresAt) }}
          </span>
        </template>
      </el-table-column>

      <el-table-column label="Intent ID" width="130">
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

      <el-table-column label="" width="100" align="right">
        <template #default="{ row }">
          <el-button
            v-if="row.state === 'ready' || row.state === 'pending_confirmation'"
            size="small"
            link
            type="danger"
            :loading="revokingId === row.intentId"
            :disabled="revokingId !== null && revokingId !== row.intentId"
            aria-label="Revoke this intent (mark as used without firing)"
            @click="onRevoke(row)"
          >
            Revoke
          </el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  Refresh,
  CircleCheck,
  Warning,
  CircleClose,
  Loading,
  Search,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  fetchIntentInventory,
  revokeIntent,
  type IntentInventoryEntry,
} from '../../api/daemon'

const entries = ref<IntentInventoryEntry[]>([])
const loading = ref(false)
const revokingId = ref<string | null>(null)

// Phase 6.13b — client-side filters. Empty/null = no filter applied.
const stateFilter = ref<string | null>(null)
const kindFilter = ref<string | null>(null)
const domainFilter = ref('')

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
    const result = await fetchIntentInventory(200)
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

async function onRevoke(row: IntentInventoryEntry): Promise<void> {
  if (revokingId.value !== null) return
  try {
    await ElMessageBox.confirm(
      `Revoke intent ${row.intentId.slice(0, 8)}? ` +
        `(${row.kind} on ${row.domain} → ${row.host}) ` +
        `Any AI client trying to fire this token will get already_used.`,
      'Revoke MCP intent',
      { type: 'warning', confirmButtonText: 'Revoke', cancelButtonText: 'Cancel' },
    )
  } catch { return }

  revokingId.value = row.intentId
  try {
    await revokeIntent(row.intentId)
    ElMessage.success('Intent revoked')
    await refresh()
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err)
    ElMessage.error(`Revoke failed: ${msg}`)
  } finally {
    revokingId.value = null
  }
}

async function copyId(id: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(id)
    ElMessage.success('Intent ID copied')
  } catch {
    ElMessage.warning('Copy failed — clipboard access denied')
  }
}

onMounted(() => refresh())
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
.intent-table {
  width: 100%;
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
.state-icon {
  margin-right: 4px;
  vertical-align: -2px;
}
</style>
