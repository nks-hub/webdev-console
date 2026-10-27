<template>
  <div class="sites-page">
    <header class="page-head">
      <div>
        <h1 class="page-title">{{ $t('sites.title') }}</h1>
        <p class="page-sub">{{ sitesSummary }}</p>
      </div>
      <div class="header-actions">
        <el-dropdown trigger="click" @command="onHeaderCommand">
          <el-button :aria-label="$t('sites.list.moreHeader')" :title="$t('sites.list.moreHeader')">
            <el-icon aria-hidden="true"><MoreFilled /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="hosts">{{ $t('sites.openHosts') }}</el-dropdown-item>
              <el-dropdown-item command="reapply" :disabled="!sitesStore.sites.length || reapplying">{{ $t('sites.reapplyAll') }}</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-button type="primary" @click="openCreate()">
          <el-icon aria-hidden="true"><Plus /></el-icon>{{ $t('sites.create') }}
        </el-button>
      </div>
    </header>

    <div v-if="loadError" class="state-panel" role="alert">
      <p class="state-title">{{ $t('sites.list.loadFailedTitle') }}</p>
      <p class="state-text">{{ $t('sites.list.loadFailedText') }}</p>
      <el-button @click="reload">{{ $t('common.retry') }}</el-button>
    </div>

    <el-skeleton v-else-if="sitesStore.loading && sitesStore.sites.length === 0" :rows="3" animated class="state-skeleton" />

    <SitesEmptyState v-else-if="sitesStore.sites.length === 0" @create="openCreate()" @create-existing="openCreate(true)" />

    <template v-else>
      <WebServerBanner />

      <div v-if="showToolbar" class="toolbar" role="search">
        <el-input
          ref="searchInput"
          v-model="search"
          :placeholder="$t('sites.filterPlaceholder')"
          :aria-label="$t('sites.filterPlaceholder')"
          :prefix-icon="Search"
          clearable
          class="toolbar-search"
          @keydown.esc="onSearchEsc"
        >
          <template #suffix>
            <kbd v-if="!search" class="kbd-hint" aria-hidden="true">/</kbd>
          </template>
        </el-input>
        <div class="tray" role="group" :aria-label="$t('sites.list.stateFilter')">
          <button
            v-for="f in stateFilters"
            :key="f.id"
            type="button"
            class="tray-key"
            :aria-pressed="stateFilter === f.id"
            @click="stateFilter = f.id"
          >{{ f.label }} <span class="mono tray-count">{{ f.count }}</span></button>
        </div>
      </div>

      <div v-if="advanced && selected.length" class="bulk-bar" role="region" :aria-label="$t('sites.bulk.title')">
        <strong>{{ $t('sites.bulk.selected', { n: selected.length }, selected.length) }}</strong>
        <el-button size="small" :loading="bulkBusy" @click="runBulk('enable')">{{ $t('sites.list.enable') }}</el-button>
        <el-button size="small" :loading="bulkBusy" @click="runBulk('disable')">{{ $t('sites.list.disable') }}</el-button>
        <el-button size="small" :loading="bulkBusy" @click="runBulk('apply')">{{ $t('siteApply.applyAgain') }}</el-button>
        <el-button size="small" type="danger" plain :loading="bulkBusy" @click="runBulk('delete')">{{ $t('sites.bulk.delete') }}</el-button>
        <el-button size="small" text @click="selected = []">{{ $t('sites.bulk.clear') }}</el-button>
      </div>

      <p class="visually-hidden" aria-live="polite">{{ liveText }}</p>

      <SitesTable
        v-if="filteredSites.length"
        v-model:selected="selected"
        :sites="filteredSites"
        :advanced="advanced"
        :compose-status="composeStatus"
        :tunnel-available="tunnelAvailable"
        @action="onRowAction"
      />
      <div v-else class="list-empty">
        <p>{{ search ? $t('sites.noMatching', { q: search }) : $t('sites.list.noneInFilter') }}</p>
        <el-button @click="clearFilters">{{ $t('sites.list.clearFilter') }}</el-button>
      </div>
    </template>

    <CreateSiteDialog v-model="showCreate" :pick-folder-on-open="createPicksFolder" @created="onCreated" />
    <DuplicateSiteDialog v-model="showDuplicate" :source="duplicateSource" @duplicated="onDuplicated" />
  </div>
</template>

<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { MoreFilled, Plus, Search } from '@element-plus/icons-vue'
import CreateSiteDialog from '../sites/CreateSiteDialog.vue'
import DuplicateSiteDialog from '../sites/DuplicateSiteDialog.vue'
import SitesEmptyState from '../sites/SitesEmptyState.vue'
import SitesTable from '../sites/SitesTable.vue'
import WebServerBanner from '../sites/WebServerBanner.vue'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { usePluginsStore } from '../../stores/plugins'
import { useSiteApplyStore } from '../../stores/siteApply'
import { useSiteStateStore } from '../../stores/siteState'
import type { SiteInfo } from '../../api/types'
import { daemonBaseUrl, fetchDockerComposeStatus, setSiteEnabled, type DockerComposeStatus } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { openHostsFile } from '../../utils/hostsFile'
import { revealFolder } from '../../utils/siteUrl'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
const uiModeStore = useUiModeStore()
const pluginsStore = usePluginsStore()
const siteApplyStore = useSiteApplyStore()
const siteState = useSiteStateStore()

const advanced = computed(() => uiModeStore.isAdvanced)
const tunnelAvailable = computed(() => pluginsStore.contributionsForSlot('sites-row-badges').length > 0)

const search = ref('')
const searchInput = ref<{ focus?: () => void; blur?: () => void } | null>(null)
const selected = ref<string[]>([])
const loadError = ref(false)
const reapplying = ref(false)
const bulkBusy = ref(false)

// ── Dialogs ────────────────────────────────────────────────────────
const showCreate = ref(false)
const createPicksFolder = ref(false)
const showDuplicate = ref(false)
const duplicateSource = ref('')

function openCreate(pickFolder = false) {
  createPicksFolder.value = pickFolder
  showCreate.value = true
}

// Ctrl+N and the onboarding wizard arrive as /sites?create=1; the query is
// dropped again so the same shortcut works a second time.
watch(() => route.query.create, (val) => {
  if (val !== '1') return
  openCreate()
  void router.replace({ path: route.path, query: {} })
}, { immediate: true })

function onCreated(domain: string) {
  void router.push(`/sites/${encodeURIComponent(domain)}/edit`)
}
function onDuplicated(domain: string) {
  selected.value = []
  void router.push(`/sites/${encodeURIComponent(domain)}/edit`)
}

// ── State of the list ──────────────────────────────────────────────
interface Entry { site: SiteInfo; kind: ReturnType<typeof siteState.stateOf>['kind']; problem: boolean }
const entries = computed<Entry[]>(() => sitesStore.sites.map(site => {
  const kind = siteState.stateOf(site).kind
  return { site, kind, problem: kind === 'failed' || siteState.runtimeOf(site).missing }
}))

const stateFilter = ref<'all' | 'problems' | 'disabled'>('all')
const counts = computed(() => ({
  failed: entries.value.filter(e => e.kind === 'failed').length,
  problems: entries.value.filter(e => e.problem).length,
  disabled: entries.value.filter(e => e.kind === 'disabled').length,
}))
const stateFilters = computed(() => [
  { id: 'all' as const, label: t('sites.list.filter.all'), count: sitesStore.sites.length },
  { id: 'problems' as const, label: t('sites.list.filter.problems'), count: counts.value.problems },
  { id: 'disabled' as const, label: t('sites.list.filter.disabled'), count: counts.value.disabled },
])

// Search and the status filter earn their space once the list is long; with a handful of
// sites they are just more controls to read (they stay while a filter is active).
const SEARCH_FROM = 6
const showToolbar = computed(() =>
  sitesStore.sites.length >= SEARCH_FROM || !!search.value || stateFilter.value !== 'all')

const sitesSummary = computed(() => {
  const total = sitesStore.sites.length
  if (total === 0) return t('sites.list.summaryEmpty')
  const parts = [t('sites.list.summaryTotal', { n: total }, total)]
  if (counts.value.failed) parts.push(t('sites.list.summaryFailed', { n: counts.value.failed }, counts.value.failed))
  if (counts.value.disabled) parts.push(t('sites.list.summaryDisabled', { n: counts.value.disabled }, counts.value.disabled))
  return parts.join(' · ')
})

const filteredSites = computed(() => {
  const q = search.value.trim().toLowerCase()
  return entries.value
    .filter(e => stateFilter.value === 'all' || (stateFilter.value === 'problems' ? e.problem : e.kind === 'disabled'))
    .filter(({ site: s }) => !q
      || s.domain.toLowerCase().includes(q)
      || (s.aliases ?? []).some(a => a.toLowerCase().includes(q))
      || s.documentRoot.toLowerCase().includes(q)
      || (s.framework ?? '').toLowerCase().includes(q))
    .map(e => e.site)
})

const liveText = computed(() =>
  search.value || stateFilter.value !== 'all'
    ? t('sites.list.shown', { n: filteredSites.value.length, total: sitesStore.sites.length })
    : '')

function clearFilters() {
  search.value = ''
  stateFilter.value = 'all'
}

// Drop selections of sites that disappeared (deleted here or elsewhere).
watch(() => sitesStore.sites, (list) => {
  const live = new Set(list.map(s => s.domain))
  if (selected.value.some(d => !live.has(d))) selected.value = selected.value.filter(d => live.has(d))
})

// ── Keyboard: "/" jumps to the search box, Esc clears it ───────────
function onSearchEsc() {
  if (search.value) search.value = ''
  else searchInput.value?.blur?.()
}
function onKeydown(e: KeyboardEvent) {
  if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return
  const el = e.target as HTMLElement | null
  if (el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName))) return
  if (!searchInput.value) return
  e.preventDefault()
  searchInput.value.focus?.()
}

// ── Data ───────────────────────────────────────────────────────────
// Docker Compose detection: lazy, a failure for one site never blocks the table.
const composeStatus = reactive<Record<string, DockerComposeStatus>>({})
async function refreshComposeStatuses() {
  const live = new Set(sitesStore.sites.map(s => s.domain))
  for (const domain of Object.keys(composeStatus)) if (!live.has(domain)) delete composeStatus[domain]
  await Promise.all(sitesStore.sites.map(async (s) => {
    try { composeStatus[s.domain] = await fetchDockerComposeStatus(s.domain) } catch { /* no badge */ }
  }))
}

async function reload() {
  loadError.value = false
  try {
    await sitesStore.load()
  } catch {
    loadError.value = true
    return
  }
  void siteApplyStore.hydrate()
  void siteState.refresh()
}

// The page stays cached by <keep-alive>, so the "/" shortcut is only live while it is shown
// (otherwise it would swallow the key on every other page), and what may have changed
// elsewhere (sites, Apache or PHP installed, a site applied) is fetched again on return.
let activatedOnce = false
onMounted(() => { void reload() })
onActivated(() => {
  window.addEventListener('keydown', onKeydown)
  if (activatedOnce) void reload()
  activatedOnce = true
})
onDeactivated(() => window.removeEventListener('keydown', onKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))

// Re-scan when the set of sites or any document root changes.
watch(
  () => sitesStore.sites.map(s => `${s.domain}::${s.documentRoot}`).join('|'),
  () => { void refreshComposeStatuses() },
  { immediate: true },
)

// ── Actions ────────────────────────────────────────────────────────
async function toggleEnabled(site: SiteInfo) {
  const enabled = site.enabled === false
  try {
    await siteApplyStore.track(site.domain, () => setSiteEnabled(site.domain, enabled))
    ElMessage.success(t(enabled ? 'sitesToast.enabled' : 'sitesToast.disabled', { domain: site.domain }))
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(t('sitesToast.toggleFailed', { err: errorMessage(e) }))
  }
}

async function detectFramework(domain: string) {
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain)}/detect-framework`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...sitesStore.authHeaders() },
    })
    if (!res.ok) throw new Error((await res.text().catch(() => res.statusText)) || `HTTP ${res.status}`)
    const data = await res.json() as { framework?: string }
    if (data.framework) ElMessage.success(t('sitesToast.detected', { framework: data.framework }))
    else ElMessage.info(t('sitesToast.noFramework'))
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(t('sitesToast.detectFailed', { err: errorMessage(e) }))
  }
}

async function applyAgain(domain: string) {
  try {
    const result = await siteApplyStore.apply(domain)
    if (result.success) ElMessage.success(t('siteApply.appliedOk'))
    else {
      const n = result.steps.filter(s => s.status === 'failed').length
      ElMessage.warning(t('siteApply.someFailed', { n }, n))
    }
  } catch (e) {
    ElMessage.error(t('toast.applyFailed', { err: errorMessage(e) }))
  }
}

async function confirmDelete(domains: string[]): Promise<boolean> {
  const single = domains.length === 1
  try {
    await ElMessageBox.confirm(
      single ? t('sitesToast.deleteConfirm', { domain: domains[0] }) : t('sites.bulk.deleteConfirm', { n: domains.length }, domains.length),
      t('sites.delete'),
      { type: 'warning', confirmButtonText: t('common.delete'), cancelButtonText: t('common.cancel'), confirmButtonClass: 'el-button--danger' },
    )
  } catch {
    return false
  }
  let failed = 0
  for (const domain of domains) {
    try { await sitesStore.remove(domain) } catch (e) {
      failed++
      ElMessage.error(t('sitesToast.deleteFailed', { err: `${domain}: ${errorMessage(e)}` }))
    }
  }
  if (failed < domains.length) {
    ElMessage.success(single ? t('sitesToast.deleted', { domain: domains[0] }) : t('sites.bulk.deleted', { n: domains.length - failed }, domains.length - failed))
  }
  return true
}

async function reapplyAll() {
  try {
    await ElMessageBox.confirm(t('sites.reapplyAllConfirm'), t('sites.reapplyAllTitle'), {
      confirmButtonText: t('common.apply'),
      cancelButtonText: t('common.cancel'),
    })
  } catch {
    return
  }
  reapplying.value = true
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/reapply-all`, { method: 'POST', headers: sitesStore.authHeaders() })
    if (!res.ok) {
      ElMessage.error(t('toast.httpFailed', { status: res.status }))
      return
    }
    const results: Array<{ domain: string; ok: boolean; error?: string }> = await res.json()
    const failed = results.filter(r => !r.ok)
    if (failed.length === 0) ElMessage.success(t('toast.vhostsRegenerated', { n: results.length }))
    else ElMessage.warning(t('toast.vhostsPartial', { ok: results.length - failed.length, failed: failed.length, list: failed.map(f => f.domain).join(', ') }))
  } catch (e) {
    ElMessage.error(t('toast.reapplyFailed', { err: errorMessage(e) }))
  } finally {
    reapplying.value = false
    void siteApplyStore.hydrate()
  }
}

async function runBulk(kind: 'enable' | 'disable' | 'apply' | 'delete') {
  const targets = sitesStore.sites.filter(s => selected.value.includes(s.domain))
  if (!targets.length) return
  bulkBusy.value = true
  try {
    if (kind === 'delete') {
      if (await confirmDelete(targets.map(s => s.domain))) selected.value = []
      return
    }
    let failed = 0
    for (const site of targets) {
      try {
        if (kind === 'apply') await siteApplyStore.apply(site.domain)
        else await siteApplyStore.track(site.domain, () => setSiteEnabled(site.domain, kind === 'enable'))
      } catch (e) {
        failed++
        ElMessage.error(t('sitesToast.toggleFailed', { err: `${site.domain}: ${errorMessage(e)}` }))
      }
    }
    if (kind !== 'apply') await sitesStore.load()
    if (failed < targets.length) ElMessage.success(t(`sites.bulk.done.${kind}`, { n: targets.length - failed }, targets.length - failed))
  } finally {
    bulkBusy.value = false
  }
}

function onHeaderCommand(cmd: string) {
  if (cmd === 'hosts') void openHostsFile()
  else if (cmd === 'reapply') void reapplyAll()
}

async function onRowAction(cmd: string, site: SiteInfo) {
  switch (cmd) {
    case 'edit': void router.push(`/sites/${encodeURIComponent(site.domain)}/edit`); break
    case 'folder':
      if (!(await revealFolder(site.documentRoot))) ElMessage.info(t('sitesToast.folderPickerDesktopOnly'))
      break
    case 'duplicate': duplicateSource.value = site.domain; showDuplicate.value = true; break
    case 'detect': void detectFramework(site.domain); break
    case 'apply': void applyAgain(site.domain); break
    case 'toggle': void toggleEnabled(site); break
    case 'delete': void confirmDelete([site.domain]); break
  }
}
</script>

<style scoped>
.sites-page { display: grid; gap: var(--wdc-space-4); min-width: 0; align-content: start; }

.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
}
.page-title {
  font-size: 1.728rem;
  line-height: 1.1;
  letter-spacing: -0.028em;
}
.page-sub {
  margin-top: var(--wdc-space-2);
  color: var(--wdc-text-2);
}
.header-actions { display: flex; align-items: center; gap: var(--wdc-space-2); }
.header-actions :deep(.el-button + .el-button) { margin-left: 0; }
.header-actions :deep(.el-button > span) { display: inline-flex; align-items: center; gap: var(--wdc-space-1-5); }

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-3);
}
.toolbar-search { width: 320px; max-width: 100%; }
.kbd-hint {
  padding: 0 var(--wdc-space-1-5);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-xs);
  color: var(--wdc-text-3);
  font-family: var(--wdc-font-mono);
  font-size: 0.72rem;
  line-height: 1.5;
}

.tray {
  display: inline-flex;
  gap: var(--wdc-space-0-5);
  padding: var(--wdc-space-0-5);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface-2);
}
.tray-key {
  min-height: 30px;
  padding: 0 var(--wdc-space-3);
  border: 1px solid transparent;
  border-radius: var(--wdc-radius);
  background: transparent;
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.87rem;
  cursor: pointer;
}
@media (hover: hover) { .tray-key:hover { color: var(--wdc-text); } }
.tray-key[aria-pressed="true"] {
  background: var(--wdc-key-face);
  border-color: var(--wdc-border-strong);
  color: var(--wdc-text);
  box-shadow: var(--wdc-shadow-key-press);
}
.tray-key:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 1px; }
.tray-count { margin-left: var(--wdc-space-1); color: var(--wdc-text-3); font-size: 0.78rem; }

.bulk-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-lg);
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent-ink);
}
.bulk-bar strong { margin-right: var(--wdc-space-2); font-family: var(--wdc-font-display); }
.bulk-bar :deep(.el-button + .el-button) { margin-left: 0; }

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}

.list-empty,
.state-panel {
  display: grid;
  justify-items: start;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-5) 0;
  border-top: 1px solid var(--wdc-border);
  color: var(--wdc-text-2);
}
.state-title { font-family: var(--wdc-font-display); font-weight: 600; font-size: 1.07rem; color: var(--wdc-text); }
.state-text { margin: 0; }
.state-skeleton { padding: var(--wdc-space-3) 0; }

@media (max-width: 700px) {
  .toolbar-search { width: 100%; }
}
</style>
