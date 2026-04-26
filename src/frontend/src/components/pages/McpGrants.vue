<template>
  <div class="page mcp-grants-page">
    <div class="page-header">
      <h2>{{ t('mcpGrants.title') }}</h2>
      <div class="actions">
        <el-button :loading="loading" @click="refresh">
          <el-icon><Refresh /></el-icon> {{ t('mcpGrants.refresh') }}
        </el-button>
        <el-button @click="testMatchDialogOpen = true">
          <el-icon><Search /></el-icon> {{ t('mcpGrants.testMatch.button') }}
        </el-button>
        <el-button :loading="sweeping" @click="onSweepNow">
          <el-icon><Delete /></el-icon> {{ t('mcpGrants.sweepNow') }}
        </el-button>
        <el-button type="primary" @click="createDialogOpen = true">
          <el-icon><Plus /></el-icon> {{ t('mcpGrants.newGrant') }}
        </el-button>
      </div>
    </div>

    <p class="muted">{{ t('mcpGrants.description') }}</p>

    <!-- Phase 7.5+++ — usage filter built on match telemetry. Three
         buckets: All / In-use (matchCount>0) / Deadweight (never matched
         AND >7d old). Counts inline so operators see the breakdown
         without flipping. -->
    <div v-if="grants.length > 0" class="usage-toolbar">
      <el-radio-group v-model="usageFilter" size="small">
        <el-radio-button value="all">
          {{ t('mcpGrants.usage.all') }} ({{ grants.length }})
        </el-radio-button>
        <el-radio-button value="inuse">
          {{ t('mcpGrants.usage.inuse') }} ({{ inUseCount }})
        </el-radio-button>
        <el-radio-button value="deadweight">
          {{ t('mcpGrants.usage.deadweight') }} ({{ deadweightCount }})
        </el-radio-button>
      </el-radio-group>
      <!-- Phase 7.5+++ — scope_type narrowing. Hidden when only one
           scope type is present (common single-AI installs). Counts
           computed from current grants list so they reflect reality. -->
      <el-select
        v-if="scopeTypeOptions.length > 1"
        v-model="scopeTypeFilter"
        clearable size="small"
        :placeholder="t('mcpGrants.scopeFilter.placeholder')"
        class="scope-filter"
      >
        <el-option v-for="opt in scopeTypeOptions" :key="opt.value"
          :value="opt.value">
          <span>{{ opt.value }} <span class="muted">({{ opt.count }})</span></span>
        </el-option>
      </el-select>
      <span v-if="deadweightCount > 0" class="muted hint">
        {{ t('mcpGrants.usage.deadweightHint', { days: DEADWEIGHT_AGE_DAYS }) }}
      </span>
      <!-- One-click bulk revoke for the deadweight bucket. Surfaces only
           when the filter is on Deadweight AND there's something to act
           on, so it reads as a contextual action rather than a global
           danger button. -->
      <el-button
        v-if="usageFilter === 'deadweight' && deadweightCount > 0"
        type="danger" size="small" plain
        :loading="revokingDeadweight"
        @click="onRevokeAllDeadweight"
      >
        {{ t('mcpGrants.usage.revokeAllDeadweight', { n: deadweightCount }) }}
      </el-button>
    </div>

    <el-alert
      v-if="!loading && grants.length === 0"
      :title="t('mcpGrants.empty.title')"
      :description="t('mcpGrants.empty.description')"
      type="info"
      show-icon
      :closable="false"
    />

    <!-- Phase 7.5+++ — bulk revoke toolbar. Mirrors McpIntents pattern:
         hidden when nothing selected; shows count + clear + revoke. -->
    <div v-if="selectedGrants.length > 0" class="bulk-toolbar">
      <span class="muted">{{ t('mcpGrants.bulk.selected', { n: selectedGrants.length }) }}</span>
      <el-button size="small" @click="clearSelection">{{ t('mcpGrants.bulk.clear') }}</el-button>
      <el-button
        type="danger" size="small" plain
        :loading="bulkRevoking"
        @click="onBulkRevoke"
      >
        {{ t('mcpGrants.bulk.revokeBtn', { n: selectedGrants.length }) }}
      </el-button>
    </div>

    <el-alert
      v-if="grants.length > 0 && filteredGrants.length === 0"
      :title="t('mcpGrants.usage.emptyForFilter')"
      type="info"
      show-icon
      :closable="false"
    />

    <el-table
      v-if="filteredGrants.length > 0"
      ref="tableRef"
      :data="filteredGrants" stripe size="small" class="grants-table"
      @selection-change="onSelectionChange"
    >
      <el-table-column type="selection" width="40" />
      <el-table-column prop="scopeType" :label="t('mcpGrants.col.scope')" min-width="100">
        <template #default="{ row }">
          <el-tag :type="scopeTagType(row.scopeType)" size="small">{{ row.scopeType }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="scopeValue" :label="t('mcpGrants.col.scopeValue')" min-width="180">
        <template #default="{ row }">
          <code v-if="row.scopeValue" class="mono">{{ truncate(row.scopeValue, 22) }}</code>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column prop="kindPattern" :label="t('mcpGrants.col.kind')" min-width="100">
        <template #default="{ row }">
          <code class="mono">{{ row.kindPattern }}</code>
        </template>
      </el-table-column>
      <el-table-column prop="targetPattern" :label="t('mcpGrants.col.target')" min-width="160">
        <template #default="{ row }">
          <code class="mono">{{ row.targetPattern }}</code>
        </template>
      </el-table-column>
      <el-table-column prop="grantedAt" :label="t('mcpGrants.col.grantedAt')" min-width="170">
        <template #default="{ row }">{{ formatDate(row.grantedAt) }}</template>
      </el-table-column>
      <el-table-column prop="expiresAt" :label="t('mcpGrants.col.expires')" min-width="200">
        <template #default="{ row }">
          <template v-if="row.expiresAt">
            <span :class="{ 'expires-soon': isExpiringSoon(row.expiresAt) }">
              {{ formatDate(row.expiresAt) }}
            </span>
            <span v-if="isExpiringSoon(row.expiresAt)"
                  class="expires-soon-badge"
                  :title="t('mcpGrants.expiringSoonTooltip')">
              ⏱ {{ formatExpiresIn(row.expiresAt) }}
            </span>
          </template>
          <el-tag v-else type="warning" size="small">{{ t('mcpGrants.permanent') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="matchCount" :label="t('mcpGrants.col.matchCount')" width="140" sortable>
        <template #default="{ row }">
          <el-tag v-if="isDeadweight(row)" type="warning" size="small" effect="dark"
            :title="t('mcpGrants.matches.deadweightTooltip', { days: DEADWEIGHT_AGE_DAYS })">
            {{ t('mcpGrants.matches.deadweight') }}
          </el-tag>
          <el-tag v-else-if="(row.matchCount ?? 0) === 0" type="info" size="small" effect="plain">
            {{ t('mcpGrants.matches.none') }}
          </el-tag>
          <!-- Phase 7.5+++ — clickable count drills down to McpIntents
               filtered by matchedGrantId. Cross-tab nav via router. -->
          <a v-else
            class="match-link"
            href="javascript:void(0)"
            :title="t('mcpGrants.matches.drilldownTooltip', { id: row.id })"
            @click="goToMatchedIntents(row.id)">
            <strong>{{ row.matchCount }}</strong>
            <span v-if="row.lastMatchedAt" class="muted last-match">
              · {{ formatRelative(row.lastMatchedAt) }}
            </span>
          </a>
        </template>
      </el-table-column>
      <el-table-column prop="note" :label="t('mcpGrants.col.note')" min-width="200">
        <template #default="{ row }"><span class="muted">{{ row.note || '—' }}</span></template>
      </el-table-column>
      <el-table-column :label="t('mcpGrants.col.actions')" width="120" fixed="right">
        <template #default="{ row }">
          <el-button type="danger" size="small" plain @click="revoke(row.id)">
            {{ t('mcpGrants.revoke') }}
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- Phase 7.5+++ — Test match dialog. Dry-run query that hits the
         same FindMatchingActiveAsync path the validator uses, so the
         result tells operators exactly which grant would auto-confirm
         (or that none would). -->
    <el-dialog v-model="testMatchDialogOpen" :title="t('mcpGrants.testMatch.title')" width="520">
      <p class="muted" style="margin-top: 0">{{ t('mcpGrants.testMatch.description') }}</p>
      <el-form :model="testMatchForm" label-width="120px" size="small">
        <el-form-item :label="t('mcpGrants.testMatch.sessionId')">
          <el-input v-model="testMatchForm.sessionId" placeholder="agent-42 (volitelné)" clearable />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.testMatch.instanceId')">
          <el-input v-model="testMatchForm.instanceId" placeholder="wdc-uuid (volitelné)" clearable />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.testMatch.apiKeyId')">
          <el-input v-model="testMatchForm.apiKeyId" placeholder="claude-code-LuRy (volitelné)" clearable />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.testMatch.kind')" required>
          <el-input v-model="testMatchForm.kind" placeholder="deploy | rollback | restore | …" />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.testMatch.target')" required>
          <el-input v-model="testMatchForm.target" placeholder="blog.loc | shop.loc | …" />
        </el-form-item>
      </el-form>

      <div v-if="testMatchResult !== null" class="test-match-result"
           :class="{ ok: testMatchResult.matched, none: !testMatchResult.matched }">
        <template v-if="testMatchResult.matched">
          <strong>✓ {{ t('mcpGrants.testMatch.resultMatch') }}</strong>
          <div class="muted" style="margin-top: 4px; font-size: 12px">
            <div>id: <code class="mono">{{ testMatchResult.grant?.id }}</code></div>
            <div>scope: <code class="mono">{{ testMatchResult.grant?.scopeType }}{{ testMatchResult.grant?.scopeValue ? ' = ' + testMatchResult.grant.scopeValue : '' }}</code></div>
            <div>kind: <code class="mono">{{ testMatchResult.grant?.kindPattern }}</code> · target: <code class="mono">{{ testMatchResult.grant?.targetPattern }}</code></div>
            <div>{{ t('mcpGrants.testMatch.previousMatches', { n: testMatchResult.grant?.matchCount ?? 0 }) }}</div>
          </div>
        </template>
        <template v-else>
          <strong>✗ {{ t('mcpGrants.testMatch.resultNoMatch') }}</strong>
          <div class="muted" style="margin-top: 4px; font-size: 12px">
            {{ t('mcpGrants.testMatch.resultNoMatchHint') }}
          </div>
        </template>
      </div>

      <template #footer>
        <el-button @click="testMatchDialogOpen = false">{{ t('common.close') }}</el-button>
        <el-button type="primary" :loading="testingMatch"
          :disabled="!testMatchForm.kind.trim() || !testMatchForm.target.trim()"
          @click="onRunTestMatch">
          {{ t('mcpGrants.testMatch.run') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- Create grant dialog -->
    <el-dialog v-model="createDialogOpen" :title="t('mcpGrants.dialog.title')" width="520">
      <el-form :model="form" label-width="120px" size="small">
        <el-form-item :label="t('mcpGrants.dialog.scopeType')">
          <el-select v-model="form.scopeType" @change="onScopeTypeChange">
            <el-option label="session" value="session" />
            <el-option label="api_key" value="api_key" />
            <el-option label="instance" value="instance" />
            <el-option label="always" value="always" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.scopeType !== 'always'" :label="t('mcpGrants.dialog.scopeValue')">
          <el-input v-model="form.scopeValue" :placeholder="scopeValuePlaceholder" />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.dialog.kindPattern')">
          <el-input v-model="form.kindPattern" placeholder="* | deploy | rollback | cancel | restore" />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.dialog.targetPattern')">
          <el-input v-model="form.targetPattern" placeholder="* | blog.loc | ..." />
        </el-form-item>
        <el-form-item :label="t('mcpGrants.dialog.expiry')">
          <el-radio-group v-model="form.expiryMode">
            <el-radio value="permanent">{{ t('mcpGrants.dialog.expiryPermanent') }}</el-radio>
            <el-radio value="30m">30 min</el-radio>
            <el-radio value="2h">2 h</el-radio>
            <el-radio value="24h">24 h</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="t('mcpGrants.dialog.note')">
          <el-input v-model="form.note" type="textarea" :rows="2" :placeholder="t('mcpGrants.dialog.notePlaceholder')" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createDialogOpen = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="creating" @click="submitCreate">
          {{ t('mcpGrants.dialog.create') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, Plus, Delete, Search } from '@element-plus/icons-vue'
import {
  listMcpGrants, createMcpGrant, revokeMcpGrant, sweepMcpGrantsNow,
  testMatchMcpGrant, subscribeEventsMap,
  type McpGrantRow, type McpGrantScopeType, type McpGrantTestMatchResult,
} from '../../api/daemon'

const { t } = useI18n()
const router = useRouter()
const loading = ref(false)
const grants = ref<McpGrantRow[]>([])

// Phase 7.5+++ — drilldown to McpIntents filtered by this grant id.
// Uses URL query so deep-links work + the back button restores state.
function goToMatchedIntents(grantId: string): void {
  void router.push({ path: '/mcp/intents', query: { matchedGrantId: grantId } })
}

// Phase 7.5+++ — bulk-revoke selection state. Mirrors McpIntents:
// el-table emits selection-change with FULL set; we mirror it as a
// reactive ref so the toolbar count + revoke button derive cleanly.
const selectedGrants = ref<McpGrantRow[]>([])
const bulkRevoking = ref(false)
const tableRef = ref<unknown>(null)

// Phase 7.5+++ — manual sweep trigger ("clean now" button).
const sweeping = ref(false)

// Phase 7.5+++ — dry-run test-match dialog state.
const testMatchDialogOpen = ref(false)
const testingMatch = ref(false)
const testMatchResult = ref<McpGrantTestMatchResult | null>(null)
const testMatchForm = ref({
  sessionId: '', instanceId: '', apiKeyId: '',
  kind: 'deploy', target: 'blog.loc',
})

async function onRunTestMatch(): Promise<void> {
  if (testingMatch.value) return
  testingMatch.value = true
  try {
    testMatchResult.value = await testMatchMcpGrant({
      sessionId:  testMatchForm.value.sessionId.trim() || null,
      instanceId: testMatchForm.value.instanceId.trim() || null,
      apiKeyId:   testMatchForm.value.apiKeyId.trim() || null,
      kind:       testMatchForm.value.kind.trim(),
      target:     testMatchForm.value.target.trim(),
    })
  } catch (e) {
    ElMessage.error((e as Error).message)
    testMatchResult.value = null
  } finally {
    testingMatch.value = false
  }
}

// Phase 7.5+++ — usage filter built on telemetry. "Deadweight" =
// grant has never matched (matchCount=0) AND was minted >7 days ago.
// The 7-day window is hardcoded to match the operator-facing hint
// text; if it ever becomes settings-driven, change in both places.
const DEADWEIGHT_AGE_DAYS = 7
type UsageFilter = 'all' | 'inuse' | 'deadweight'
const usageFilter = ref<UsageFilter>('all')

function isDeadweight(g: McpGrantRow): boolean {
  if ((g.matchCount ?? 0) > 0) return false
  try {
    const ageMs = Date.now() - new Date(g.grantedAt).getTime()
    return ageMs > DEADWEIGHT_AGE_DAYS * 86_400_000
  } catch { return false }
}

const inUseCount = computed(() =>
  grants.value.filter((g) => (g.matchCount ?? 0) > 0).length)
const deadweightCount = computed(() =>
  grants.value.filter(isDeadweight).length)

// Phase 7.5+++ — scope_type filter (combines with usage tri-state).
const scopeTypeFilter = ref<string | null>(null)

const scopeTypeOptions = computed(() => {
  const counts = new Map<string, number>()
  for (const g of grants.value) {
    counts.set(g.scopeType, (counts.get(g.scopeType) ?? 0) + 1)
  }
  // Stable order matching the Create dialog: session, api_key, instance, always.
  const order = ['session', 'api_key', 'instance', 'always']
  return order
    .filter((s) => counts.has(s))
    .map((value) => ({ value, count: counts.get(value) ?? 0 }))
})

const filteredGrants = computed<McpGrantRow[]>(() => {
  let rows = grants.value
  if (scopeTypeFilter.value) {
    rows = rows.filter((g) => g.scopeType === scopeTypeFilter.value)
  }
  switch (usageFilter.value) {
    case 'inuse': return rows.filter((g) => (g.matchCount ?? 0) > 0)
    case 'deadweight': return rows.filter(isDeadweight)
    default: return rows
  }
})

const revokingDeadweight = ref(false)

async function onRevokeAllDeadweight(): Promise<void> {
  if (revokingDeadweight.value) return
  const targets = grants.value.filter(isDeadweight)
  if (targets.length === 0) return
  try {
    await ElMessageBox.confirm(
      t('mcpGrants.usage.revokeAllDeadweightConfirm', {
        n: targets.length, days: DEADWEIGHT_AGE_DAYS,
      }),
      t('mcpGrants.usage.revokeAllDeadweightTitle'),
      {
        type: 'warning',
        confirmButtonText: t('mcpGrants.bulk.confirmRevokeBtn'),
        cancelButtonText: t('common.cancel'),
      },
    )
  } catch { return }

  revokingDeadweight.value = true
  let ok = 0; let failed = 0
  // Serial — matches the bulk-revoke pattern. Daemon DELETEs are
  // cheap; serial keeps the failure surface deterministic.
  for (const target of targets) {
    try {
      await revokeMcpGrant(target.id)
      ok++
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e)
      if (msg.includes('not_found') || msg.includes('already')) ok++
      else failed++
    }
  }
  revokingDeadweight.value = false
  if (failed === 0) ElMessage.success(t('mcpGrants.bulk.toastOk', { n: ok }))
  else ElMessage.warning(t('mcpGrants.bulk.toastPartial', { ok, failed }))
  await refresh()
}

const createDialogOpen = ref(false)
const creating = ref(false)
const form = ref<{
  scopeType: McpGrantScopeType
  scopeValue: string
  kindPattern: string
  targetPattern: string
  expiryMode: 'permanent' | '30m' | '2h' | '24h'
  note: string
}>({
  scopeType: 'session',
  scopeValue: '',
  kindPattern: '*',
  targetPattern: '*',
  expiryMode: '30m',
  note: '',
})

const scopeValuePlaceholder = computed(() => {
  switch (form.value.scopeType) {
    case 'session':  return 'mcp-session-id (e.g. agent-42)'
    case 'api_key':  return 'api-key fingerprint (NOT the key itself)'
    case 'instance': return 'wdc instance UUID'
    default:         return ''
  }
})

// Phase 7.5+++ — subscribe to mcp:grant-changed SSE so the table
// auto-refreshes when ANY caller (banner button, admin dialog, MCP
// CLI, another open tab) creates or revokes a grant. Cleanup on
// unmount keeps EventSource count bounded.
let unsubscribeGrantSse: (() => void) | null = null

onMounted(() => {
  refresh()
  unsubscribeGrantSse = subscribeEventsMap({
    'mcp:grant-changed': () => { void refresh() },
  })
})

onBeforeUnmount(() => {
  if (unsubscribeGrantSse) { unsubscribeGrantSse(); unsubscribeGrantSse = null }
})

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const r = await listMcpGrants()
    grants.value = r.entries
  } catch (e) {
    ElMessage.error(t('mcpGrants.toastLoadFailed', { error: (e as Error).message }))
  } finally {
    loading.value = false
  }
}

function onSelectionChange(rows: McpGrantRow[]): void {
  selectedGrants.value = rows
}

function clearSelection(): void {
  // Reach into the el-table instance to clear its checkbox state.
  // Element Plus exposes clearSelection() on the table ref.
  const tbl = tableRef.value as { clearSelection?: () => void } | null
  tbl?.clearSelection?.()
  selectedGrants.value = []
}

async function onBulkRevoke(): Promise<void> {
  const targets = selectedGrants.value.slice()
  if (targets.length === 0 || bulkRevoking.value) return
  try {
    const head = targets.slice(0, 5)
      .map((r) => `${r.kindPattern}@${r.targetPattern}`)
      .join(', ')
    const moreSuffix = targets.length > 5
      ? t('mcpGrants.bulk.confirmMore', { n: targets.length - 5 })
      : ''
    await ElMessageBox.confirm(
      t('mcpGrants.bulk.confirmMessage', { n: targets.length, targets: head + moreSuffix }),
      t('mcpGrants.bulk.confirmTitle'),
      {
        type: 'warning',
        confirmButtonText: t('mcpGrants.bulk.confirmRevokeBtn'),
        cancelButtonText: t('common.cancel'),
      },
    )
  } catch { return }

  bulkRevoking.value = true
  let ok = 0
  let failed = 0
  // Serial revoke — daemon writes are cheap and serial keeps the
  // failure surface deterministic (operator sees "5 ok, 2 failed"
  // rather than racing parallel DELETEs).
  for (const target of targets) {
    try {
      await revokeMcpGrant(target.id)
      ok++
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e)
      // 404 not_found / already_revoked is benign: end state reached.
      if (msg.includes('not_found') || msg.includes('already')) ok++
      else failed++
    }
  }
  bulkRevoking.value = false
  clearSelection()
  if (failed === 0) ElMessage.success(t('mcpGrants.bulk.toastOk', { n: ok }))
  else ElMessage.warning(t('mcpGrants.bulk.toastPartial', { ok, failed }))
  await refresh()
}

async function onSweepNow(): Promise<void> {
  if (sweeping.value) return
  sweeping.value = true
  try {
    const r = await sweepMcpGrantsNow()
    if (r.deleted > 0) {
      ElMessage.success(t('mcpGrants.sweepDone', { n: r.deleted }))
      // Janitor broadcasts mcp:grant-changed → SSE handler refreshes,
      // but call refresh() explicitly to cover the case where the SSE
      // round-trip is slower than the operator's eye.
      await refresh()
    } else {
      ElMessage.info(t('mcpGrants.sweepNothing'))
    }
  } catch (e) {
    ElMessage.error(t('mcpGrants.sweepFailed', { error: (e as Error).message }))
  } finally {
    sweeping.value = false
  }
}

async function revoke(id: string): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('mcpGrants.confirmRevoke.message'),
      t('mcpGrants.confirmRevoke.title'),
      { type: 'warning', confirmButtonText: t('mcpGrants.revoke'), cancelButtonText: t('common.cancel') },
    )
  } catch { return }
  try {
    await revokeMcpGrant(id)
    ElMessage.success(t('mcpGrants.toastRevoked'))
    await refresh()
  } catch (e) {
    ElMessage.error(t('mcpGrants.toastRevokeFailed', { error: (e as Error).message }))
  }
}

function onScopeTypeChange(): void {
  if (form.value.scopeType === 'always') form.value.scopeValue = ''
}

async function submitCreate(): Promise<void> {
  if (form.value.scopeType !== 'always' && !form.value.scopeValue.trim()) {
    ElMessage.warning(t('mcpGrants.dialog.scopeValueRequired'))
    return
  }
  creating.value = true
  try {
    const expiresAt = form.value.expiryMode === 'permanent'
      ? null
      : new Date(Date.now() + expiryMs(form.value.expiryMode)).toISOString()
    await createMcpGrant({
      scopeType: form.value.scopeType,
      scopeValue: form.value.scopeType === 'always' ? null : form.value.scopeValue.trim(),
      kindPattern: form.value.kindPattern || '*',
      targetPattern: form.value.targetPattern || '*',
      expiresAt,
      grantedBy: 'gui-admin',
      note: form.value.note || undefined,
    })
    ElMessage.success(t('mcpGrants.toastCreated'))
    createDialogOpen.value = false
    // Reset form for next entry
    form.value.scopeValue = ''
    form.value.note = ''
    await refresh()
  } catch (e) {
    ElMessage.error(t('mcpGrants.toastCreateFailed', { error: (e as Error).message }))
  } finally {
    creating.value = false
  }
}

function expiryMs(mode: '30m' | '2h' | '24h'): number {
  return mode === '30m' ? 30 * 60_000 : mode === '2h' ? 2 * 3_600_000 : 24 * 3_600_000
}

function scopeTagType(scope: string): 'success' | 'warning' | 'info' | 'danger' {
  switch (scope) {
    case 'session':  return 'info'
    case 'api_key':  return 'success'
    case 'instance': return 'warning'
    case 'always':   return 'danger'
    default:         return 'info'
  }
}

function truncate(s: string | null, n: number): string {
  if (!s) return ''
  return s.length > n ? s.slice(0, n) + '…' : s
}

function formatDate(iso: string): string {
  try { return new Date(iso).toLocaleString() } catch { return iso }
}

// Phase 7.5+++ — humanise lastMatchedAt for the inline "· 5m ago" annotation
// on the match column. Cheap relative-time without a date library.
function formatRelative(iso: string): string {
  try {
    const dt = new Date(iso).getTime()
    if (!Number.isFinite(dt)) return ''
    const deltaSec = Math.max(0, Math.round((Date.now() - dt) / 1000))
    if (deltaSec < 60) return t('mcpGrants.matches.justNow')
    if (deltaSec < 3600) return t('mcpGrants.matches.minutesAgo', { n: Math.floor(deltaSec / 60) })
    if (deltaSec < 86400) return t('mcpGrants.matches.hoursAgo', { n: Math.floor(deltaSec / 3600) })
    return t('mcpGrants.matches.daysAgo', { n: Math.floor(deltaSec / 86400) })
  } catch { return '' }
}

// Phase 7.5+++ — "expires soon" detection + relative formatter for the
// future direction. Threshold is 1 hour; under that the row gets the
// .expires-soon styling (bold orange) so operators see "this trust
// window is about to close" without parsing dates by hand.
const EXPIRES_SOON_THRESHOLD_SEC = 3600

function expiresInSeconds(iso: string): number {
  try {
    const dt = new Date(iso).getTime()
    if (!Number.isFinite(dt)) return Number.POSITIVE_INFINITY
    return Math.max(0, Math.round((dt - Date.now()) / 1000))
  } catch { return Number.POSITIVE_INFINITY }
}

function isExpiringSoon(iso: string | null | undefined): boolean {
  if (!iso) return false
  const s = expiresInSeconds(iso)
  return s > 0 && s <= EXPIRES_SOON_THRESHOLD_SEC
}

function formatExpiresIn(iso: string): string {
  const s = expiresInSeconds(iso)
  if (!Number.isFinite(s) || s <= 0) return t('mcpGrants.expiresIn.expired')
  if (s < 60) return t('mcpGrants.expiresIn.seconds', { n: s })
  if (s < 3600) return t('mcpGrants.expiresIn.minutes', { n: Math.floor(s / 60) })
  return t('mcpGrants.expiresIn.hours', { n: Math.floor(s / 3600) })
}
</script>

<style scoped>
.page { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.page-header { display: flex; align-items: center; justify-content: space-between; }
.page-header h2 { margin: 0; }
.page-header .actions { display: flex; gap: 8px; }
.muted { color: var(--el-text-color-secondary); }
.grants-table { margin-top: 8px; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; font-size: 12px; }
.last-match { margin-left: 4px; font-size: 11px; }
.match-link {
  color: inherit;
  text-decoration: none;
  cursor: pointer;
  border-bottom: 1px dotted var(--el-color-info);
}
.match-link:hover {
  color: var(--el-color-primary);
  border-bottom-color: var(--el-color-primary);
}
.expires-soon {
  color: var(--el-color-warning);
  font-weight: 600;
}
.expires-soon-badge {
  display: inline-block;
  margin-left: 6px;
  padding: 0 6px;
  font-size: 11px;
  background: var(--el-color-warning-light-9);
  border: 1px solid var(--el-color-warning-light-5);
  border-radius: 3px;
  color: var(--el-color-warning-dark-2);
}
.test-match-result {
  margin-top: 12px; padding: 8px 12px; border-radius: 4px;
  font-size: 13px;
}
.test-match-result.ok {
  background: var(--el-color-success-light-9);
  border-left: 3px solid var(--el-color-success);
}
.test-match-result.none {
  background: var(--el-color-info-light-9);
  border-left: 3px solid var(--el-color-info);
}
.bulk-toolbar {
  display: flex; align-items: center; gap: 12px;
  padding: 8px 12px; margin-top: 4px;
  background: var(--el-fill-color-light);
  border-radius: 4px;
}
.usage-toolbar {
  display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
}
.usage-toolbar .hint { font-size: 12px; }
.scope-filter { width: 180px; }
</style>
