<template>
  <div class="sites-table-wrap">
    <table class="sites-table">
      <caption class="visually-hidden">{{ $t('sites.list.caption') }}</caption>
      <thead>
        <tr>
          <th v-if="advanced" scope="col" class="col-select">
            <el-checkbox
              :model-value="allSelected"
              :indeterminate="someSelected && !allSelected"
              :aria-label="$t('sites.bulk.selectAll')"
              @change="toggleAll"
            />
          </th>
          <th scope="col" class="col-web" :aria-sort="ariaSort('domain')">
            <button v-if="advanced" type="button" class="th-sort" @click="sortBy('domain')">
              {{ $t('sites.list.colWeb') }}<span class="sort-mark" aria-hidden="true">{{ sortMark('domain') }}</span>
            </button>
            <template v-else>{{ $t('sites.list.colWeb') }}</template>
          </th>
          <th scope="col" class="col-state" :aria-sort="ariaSort('state')">
            <button v-if="advanced" type="button" class="th-sort" @click="sortBy('state')">
              {{ $t('sites.list.colState') }}<span class="sort-mark" aria-hidden="true">{{ sortMark('state') }}</span>
            </button>
            <template v-else>{{ $t('sites.list.colState') }}</template>
          </th>
          <th scope="col" class="col-runtime" :aria-sort="ariaSort('runtime')">
            <button v-if="advanced" type="button" class="th-sort" @click="sortBy('runtime')">
              {{ $t('sites.list.colRuntime') }}<span class="sort-mark" aria-hidden="true">{{ sortMark('runtime') }}</span>
            </button>
            <template v-else>{{ $t('sites.list.colRuntime') }}</template>
          </th>
          <th scope="col" class="col-folder">{{ $t('sites.list.colFolder') }}</th>
          <th v-if="showNetwork" scope="col" class="col-network">{{ $t('sites.list.colNetwork') }}</th>
          <th v-if="showTunnel" scope="col" class="col-tunnel">{{ $t('sites.list.colTunnel') }}</th>
          <th scope="col" class="col-actions"><span class="visually-hidden">{{ $t('common.actions') }}</span></th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.site.domain">
          <tr
            class="site-row"
            :class="{
              'is-disabled': row.state.kind === 'disabled',
              'is-failed': row.state.kind === 'failed',
              'is-selected': selectedSet.has(row.site.domain),
              'is-open': expanded.has(row.site.domain),
            }"
            @click="onRowClick($event, row.site)"
          >
            <td v-if="advanced" class="col-select">
              <el-checkbox
                :model-value="selectedSet.has(row.site.domain)"
                :aria-label="$t('sites.bulk.selectRow', { domain: row.site.domain })"
                @change="toggleOne(row.site.domain)"
              />
            </td>

            <td class="col-web">
              <div class="web-line">
                <router-link class="web-domain" :to="detailLink(row.site)" :title="$t('sites.list.menu.edit')">{{ row.site.domain }}</router-link>
                <span v-if="row.site.framework" class="chip">{{ row.site.framework }}</span>
                <span
                  v-if="composeStatus[row.site.domain]?.hasCompose"
                  class="chip"
                  :title="composeStatus[row.site.domain]?.composeFile || ''"
                >{{ $t('sites.composeBadge') }}</span>
                <span
                  v-if="row.site.aliases?.length"
                  class="alias-count"
                  tabindex="0"
                  :title="row.site.aliases.join(', ')"
                  :aria-label="$t('sites.list.aliasesLabel', { n: row.site.aliases.length, list: row.site.aliases.join(', ') }, row.site.aliases.length)"
                >+{{ row.site.aliases.length }}</span>
              </div>
              <div class="web-line">
                <a
                  v-if="row.state.kind !== 'disabled'"
                  class="web-url"
                  :href="row.url"
                  :aria-label="$t('sites.list.openUrlAria', { url: row.url })"
                  @click.prevent.stop="openUrl(row.url)"
                >
                  <el-icon v-if="row.site.sslEnabled" :size="12" aria-hidden="true"><Lock /></el-icon>{{ row.url }}
                </a>
                <span v-else class="web-url is-off">{{ row.url }}</span>
              </div>
              <div class="web-folder" :title="displayPath(row.site.documentRoot)">
                <span class="narrow-runtime">{{ row.runtime.label }} · </span>{{ displayPath(row.site.documentRoot) }}
              </div>
            </td>

            <td class="col-state">
              <SiteStatusBadge :state="row.state" />
              <button
                v-if="row.state.kind === 'failed'"
                type="button"
                class="link-btn"
                :aria-expanded="expanded.has(row.site.domain)"
                :aria-controls="`detail-${row.index}`"
                @click.stop="toggleExpanded(row.site.domain)"
              >{{ expanded.has(row.site.domain) ? $t('sites.list.hideDetail') : $t('sites.list.showDetail') }}</button>
            </td>

            <td class="col-runtime">
              <span class="runtime" :class="{ 'is-missing': row.runtime.missing }" :title="row.runtime.title">
                <el-icon v-if="row.runtime.missing" :size="13" aria-hidden="true"><WarningFilled /></el-icon>
                {{ row.runtime.label }}<template v-if="row.runtime.missing"> – {{ $t('sites.list.missing') }}</template>
              </span>
            </td>

            <td class="col-folder">
              <span class="folder-path" :title="displayPath(row.site.documentRoot)"><bdi dir="ltr">{{ displayPath(row.site.documentRoot) }}</bdi></span>
            </td>

            <td v-if="showNetwork" class="col-network">
              <span v-if="row.network" class="mono network">{{ row.network }}</span>
              <span v-else class="muted" aria-hidden="true">—</span>
            </td>

            <td v-if="showTunnel" class="col-tunnel">
              <PluginSlot name="sites-row-badges" :context="{ site: row.site }" />
            </td>

            <td class="col-actions">
              <div class="row-actions">
                <el-button
                  size="small"
                  :disabled="row.state.kind === 'disabled'"
                  :title="row.state.kind === 'waiting' ? $t('sites.list.openWaiting') : undefined"
                  :aria-label="$t('sites.list.openAria', { domain: row.site.domain })"
                  @click.stop="openUrl(row.url)"
                >
                  <el-icon aria-hidden="true"><TopRight /></el-icon><span class="act-text">{{ $t('common.open') }}</span>
                </el-button>
                <el-button
                  size="small"
                  :title="$t('sites.list.folderAria', { domain: row.site.domain })"
                  :aria-label="$t('sites.list.folderAria', { domain: row.site.domain })"
                  @click.stop="emit('action', 'folder', row.site)"
                >
                  <el-icon aria-hidden="true"><Folder /></el-icon>
                </el-button>
                <el-dropdown
                  trigger="click"
                  :teleported="true"
                  :popper-options="{ modifiers: [{ name: 'preventOverflow', options: { boundary: 'viewport', padding: 8 } }] }"
                  @command="(cmd: string) => emit('action', cmd, row.site)"
                >
                  <el-button size="small" text :title="$t('sites.list.moreHeader')" :aria-label="$t('sites.card.moreActions', { domain: row.site.domain })" @click.stop>
                    <el-icon aria-hidden="true"><MoreFilled /></el-icon>
                  </el-button>
                  <template #dropdown>
                    <el-dropdown-menu>
                      <el-dropdown-item command="edit">{{ $t('sites.list.menu.edit') }}</el-dropdown-item>
                      <el-dropdown-item command="duplicate">{{ $t('sites.list.menu.duplicate') }}</el-dropdown-item>
                      <el-dropdown-item command="detect">{{ $t('sites.detect') }}</el-dropdown-item>
                      <el-dropdown-item command="apply">{{ $t('siteApply.applyAgain') }}</el-dropdown-item>
                      <el-dropdown-item command="toggle">{{ row.site.enabled !== false ? $t('sites.list.disable') : $t('sites.list.enable') }}</el-dropdown-item>
                      <el-dropdown-item command="delete" divided class="danger-item">{{ $t('sites.list.menu.delete') }}</el-dropdown-item>
                    </el-dropdown-menu>
                  </template>
                </el-dropdown>
              </div>
            </td>
          </tr>

          <tr v-if="expanded.has(row.site.domain)" :id="`detail-${row.index}`" class="detail-row">
            <td :colspan="columnCount">
              <SiteApplyPipeline :domain="row.site.domain" compact />
            </td>
          </tr>
        </template>
      </tbody>
    </table>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Folder, Lock, MoreFilled, TopRight, WarningFilled } from '@element-plus/icons-vue'
import type { DockerComposeStatus } from '../../api/daemon'
import type { SiteInfo } from '../../api/types'
import { useSiteStateStore, type SiteState } from '../../stores/siteState'
import { displayPath, openUrl, siteUrl } from '../../utils/siteUrl'
import PluginSlot from '../shared/PluginSlot.vue'
import SiteApplyPipeline from './SiteApplyPipeline.vue'
import SiteStatusBadge from './SiteStatusBadge.vue'

/**
 * The one sites table. Simple mode shows Web, State, Runs on, Folder; Advanced
 * adds selection, sorting and the Network / Tunnel columns, but only when some
 * site actually has something to show there.
 */
const props = defineProps<{
  sites: SiteInfo[]
  advanced: boolean
  composeStatus: Record<string, DockerComposeStatus>
  /** The Cloudflare plugin contributes the tunnel cell; the column is pointless without it. */
  tunnelAvailable: boolean
}>()
const selected = defineModel<string[]>('selected', { default: () => [] })
const emit = defineEmits<{ (e: 'action', cmd: string, site: SiteInfo): void }>()

const router = useRouter()
const siteState = useSiteStateStore()

type SortKey = 'domain' | 'state' | 'runtime'
const sort = ref<{ key: SortKey; dir: 1 | -1 }>({ key: 'domain', dir: 1 })
const expanded = ref<Set<string>>(new Set())

// Problems first when sorting by state.
const STATE_ORDER: Record<SiteState['kind'], number> = { failed: 0, applying: 1, waiting: 2, running: 3, disabled: 4 }

function networkLabel(site: SiteInfo): string {
  const parts: string[] = []
  if ((site.httpPort || 80) !== 80) parts.push(`:${site.httpPort}`)
  if (site.sslEnabled && (site.httpsPort || 443) !== 443) parts.push(`:${site.httpsPort}`)
  const binds = (site.bindAddresses?.length ? site.bindAddresses : [site.bindAddress || '*']).filter(b => b && b !== '*')
  if (binds.length) parts.push(binds.length > 2 ? `${binds[0]} +${binds.length - 1}` : binds.join(', '))
  return parts.join(' ')
}

const rows = computed(() => {
  const list = props.sites.map(site => ({
    site,
    state: siteState.stateOf(site),
    runtime: siteState.runtimeOf(site),
    url: siteUrl(site),
    network: networkLabel(site),
    index: 0,
  }))
  const { key, dir } = sort.value
  list.sort((a, b) => {
    const byDomain = a.site.domain.localeCompare(b.site.domain)
    if (key === 'state') return (STATE_ORDER[a.state.kind] - STATE_ORDER[b.state.kind]) * dir || byDomain
    if (key === 'runtime') return a.runtime.label.localeCompare(b.runtime.label) * dir || byDomain
    return byDomain * dir
  })
  list.forEach((r, i) => { r.index = i })
  return list
})

const showNetwork = computed(() => props.advanced && rows.value.some(r => r.network))
const showTunnel = computed(() => props.advanced && props.tunnelAvailable && props.sites.some(s => s.cloudflare?.enabled))
const columnCount = computed(() => 5 + (props.advanced ? 1 : 0) + (showNetwork.value ? 1 : 0) + (showTunnel.value ? 1 : 0))

function sortBy(key: SortKey) {
  sort.value = sort.value.key === key ? { key, dir: sort.value.dir === 1 ? -1 : 1 } : { key, dir: 1 }
}
const sortMark = (key: SortKey) => (sort.value.key === key ? (sort.value.dir === 1 ? '▲' : '▼') : '')
const ariaSort = (key: SortKey) =>
  !props.advanced ? undefined : sort.value.key !== key ? 'none' : sort.value.dir === 1 ? 'ascending' : 'descending'

const selectedSet = computed(() => new Set(selected.value))
const allSelected = computed(() => props.sites.length > 0 && props.sites.every(s => selectedSet.value.has(s.domain)))
const someSelected = computed(() => props.sites.some(s => selectedSet.value.has(s.domain)))
function toggleAll(on: boolean | string | number) {
  selected.value = on ? props.sites.map(s => s.domain) : []
}
function toggleOne(domain: string) {
  const next = new Set(selected.value)
  if (!next.delete(domain)) next.add(domain)
  selected.value = [...next]
}

function toggleExpanded(domain: string) {
  const next = new Set(expanded.value)
  if (!next.delete(domain)) next.add(domain)
  expanded.value = next
}

const detailLink = (site: SiteInfo) => `/sites/${encodeURIComponent(site.domain)}/edit`

// A click anywhere on the row opens the site, except on controls inside it.
function onRowClick(e: MouseEvent, site: SiteInfo) {
  if ((e.target as HTMLElement).closest('a, button, input, label, .el-checkbox, .el-dropdown, [tabindex]')) return
  void router.push(detailLink(site))
}
</script>

<style scoped>
.sites-table-wrap {
  container-type: inline-size;
  overflow-x: auto;
  border-top: 1px solid var(--wdc-border);
}
.sites-table {
  width: 100%;
  border-collapse: collapse;
  text-align: left;
}
.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}

th {
  padding: var(--wdc-space-2-5) var(--wdc-space-2-5);
  border-bottom: 1px solid var(--wdc-border-strong);
  background: var(--wdc-surface);
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-display);
  font-size: 0.8rem;
  font-weight: 600;
  white-space: nowrap;
}
.th-sort {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  margin: calc(var(--wdc-space-1) * -1) calc(var(--wdc-space-2) * -1);
  padding: var(--wdc-space-1) var(--wdc-space-2);
  border: 0;
  border-radius: var(--wdc-radius-xs);
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}
@media (hover: hover) { .th-sort:hover { color: var(--wdc-text); } }
.th-sort:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 1px; }
.sort-mark { min-width: 0.7em; font-size: 0.65rem; color: var(--wdc-accent-ink); }

td {
  padding: var(--wdc-space-3) var(--wdc-space-2-5);
  border-bottom: 1px solid var(--wdc-border);
  vertical-align: middle;
}
.site-row { cursor: pointer; }
@media (hover: hover) { .site-row:hover { background: var(--wdc-hover); } }
.site-row:focus-within { background: var(--wdc-hover); }
.site-row.is-selected { background: var(--wdc-accent-dim); }
.site-row.is-open td { border-bottom-color: transparent; }
.detail-row td {
  padding: var(--wdc-space-2) var(--wdc-space-4) var(--wdc-space-5) var(--wdc-space-6);
  background: var(--wdc-surface-2);
}

.col-select { width: 40px; padding-right: 0; }
.col-web { min-width: 240px; }
.col-state { width: 170px; min-width: 150px; }
.col-runtime { width: 130px; min-width: 110px; }
.col-folder { min-width: 160px; }
.col-network { width: 110px; }
.col-tunnel { width: 190px; }
.col-actions { width: 1%; white-space: nowrap; }

.web-line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-1-5);
  min-width: 0;
}
.web-line + .web-line { margin-top: var(--wdc-space-0-5); }
.web-domain {
  color: var(--wdc-text);
  font-family: var(--wdc-font-display);
  font-weight: 600;
  font-size: 0.97rem;
  text-decoration: none;
  overflow-wrap: anywhere;
}
.web-domain:hover { text-decoration: underline; text-decoration-color: var(--wdc-accent); text-underline-offset: 0.2em; }
.web-domain:focus-visible,
.web-url:focus-visible,
.alias-count:focus-visible,
.link-btn:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; border-radius: var(--wdc-radius-xs); }
.is-disabled .web-domain { color: var(--wdc-text-2); }

.chip {
  padding: 0 var(--wdc-space-1-5);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-xs);
  color: var(--wdc-text-2);
  font-size: 0.72rem;
  line-height: 1.5;
}
.alias-count {
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-mono);
  font-size: 0.75rem;
  cursor: help;
}
.web-url {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  max-width: 100%;
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-mono);
  font-size: 0.8rem;
  text-decoration: none;
  overflow-wrap: anywhere;
}
a.web-url:hover { color: var(--wdc-accent-ink); text-decoration: underline; text-underline-offset: 0.2em; }
.web-url .el-icon { color: var(--wdc-status-running); flex-shrink: 0; }
.web-url.is-off { color: var(--wdc-text-3); text-decoration: line-through; text-decoration-color: var(--wdc-border-strong); }
.narrow-runtime { display: none; color: var(--wdc-text-2); }
.web-folder {
  display: none;
  margin-top: var(--wdc-space-0-5);
  overflow: hidden;
  color: var(--wdc-text-3);
  font-family: var(--wdc-font-mono);
  font-size: 0.74rem;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.link-btn {
  display: block;
  margin-top: var(--wdc-space-0-5);
  padding: 0;
  border: 0;
  background: none;
  color: var(--wdc-accent-ink);
  font: inherit;
  font-size: 0.8rem;
  text-decoration: underline;
  text-underline-offset: 0.2em;
  cursor: pointer;
}

.runtime { display: inline-flex; align-items: center; gap: var(--wdc-space-1); color: var(--wdc-text); font-size: 0.87rem; }
.runtime.is-missing { color: var(--wdc-status-starting); }
.folder-path {
  display: block;
  max-width: 240px;
  overflow: hidden;
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-mono);
  font-size: 0.78rem;
  text-align: left;
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl; /* ellipsis on the left: the end of a path is what tells sites apart */
}
.network { color: var(--wdc-text-2); font-size: 0.8rem; }
.muted { color: var(--wdc-text-3); }

.row-actions { display: flex; align-items: center; justify-content: flex-end; gap: var(--wdc-space-1); }
.row-actions :deep(.el-button + .el-button) { margin-left: 0; }
.row-actions :deep(.el-button > span) { display: inline-flex; align-items: center; gap: var(--wdc-space-1); }

/* Narrow container: the folder moves under the address, the buttons lose their words. */
@container (max-width: 1000px) {
  .col-folder { display: none; }
  .web-folder { display: block; max-width: 38ch; }
}
@container (max-width: 760px) {
  .act-text { display: none; }
}
/* Phone width: runtime, network and tunnel move into the web cell or drop out. */
@container (max-width: 700px) {
  .col-runtime,
  .col-network,
  .col-tunnel { display: none; }
  .narrow-runtime { display: inline; }
}
:global(.danger-item) { color: var(--wdc-status-error) !important; }
</style>
