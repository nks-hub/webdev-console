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

    <el-empty
      v-if="!entries.length && !loading"
      description="No MCP intents recorded — the daemon hasn't seen any AI-signed destructive ops yet."
    />

    <el-table
      v-else
      :data="entries"
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
