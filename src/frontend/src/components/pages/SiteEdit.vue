<template>
  <div class="site-edit-page">
    <!-- One site page for both UI modes. Simple vs Advanced only changes
         density: Advanced shows more groups on the Settings tab plus the
         Deploy and History tabs. Everything is edited on the Settings tab and
         saved with one bar, so there is a single save model. -->
    <header class="page-header">
      <div class="header-left">
        <nav class="crumbs" :aria-label="$t('sites.backToSites')">
          <router-link class="crumb-link" to="/sites">{{ $t('sites.title') }}</router-link>
          <span aria-hidden="true">/</span>
          <span aria-current="page">{{ domain }}</span>
        </nav>
        <h1 class="title-name">{{ domain }}</h1>
        <div v-if="site" class="title-meta">
          <SiteStatusBadge :state="state" />
          <a v-if="state.kind !== 'disabled'" class="title-url" :href="url" @click.prevent="openUrl(url)">{{ url }}</a>
        </div>
      </div>
      <div v-if="site" class="header-actions">
        <el-button :disabled="state.kind === 'disabled'" @click="openUrl(url)">
          <el-icon aria-hidden="true"><TopRight /></el-icon>{{ $t('common.open') }}
        </el-button>
        <el-button @click="openFolder">
          <el-icon aria-hidden="true"><Folder /></el-icon>{{ $t('sites.list.folder') }}
        </el-button>
      </div>
    </header>

    <div v-if="loading" class="state-box">
      <el-skeleton :rows="10" animated />
    </div>

    <div v-else-if="!site" class="state-box">
      <el-empty :description="$t('sites.notFound', { domain })">
        <el-button type="primary" @click="router.push('/sites')">{{ $t('sites.backToSites') }}</el-button>
      </el-empty>
    </div>

    <div v-else class="edit-body">
      <SiteApplyPipeline :domain="domain" class="site-apply" />

      <el-tabs v-model="activeTab" class="site-tabs">
        <!-- ── Overview: read-only ── -->
        <el-tab-pane name="overview" :label="$t('siteEdit.overview')">
          <div class="tab-content">
            <SiteOverview :site="site" :toggling="toggling" @change="goToSetting" @toggle="toggleEnabled" />
          </div>
        </el-tab-pane>

        <!-- ── Settings: the one place a site is edited ── -->
        <el-tab-pane name="settings" :label="$t('siteEdit.settings')">
          <div class="tab-content settings">
            <EditCard v-if="uiMode.isAdvanced" id="set-domain" :title="$t('siteEdit.domainGroup')">
              <el-form label-position="top" @submit.prevent>
                <el-form-item :label="$t('sites.domain')">
                  <el-input :model-value="site.domain" disabled />
                  <div class="hint">{{ $t('siteEdit.domainReadonly') }}</div>
                </el-form-item>
                <el-form-item :label="$t('sites.aliases')">
                  <div class="alias-chips">
                    <el-tag
                      v-for="alias in aliases"
                      :key="alias"
                      class="alias-chip"
                      closable
                      @close="removeAlias(alias)"
                    >{{ alias }}</el-tag>
                    <el-input
                      v-if="aliasInputVisible"
                      ref="aliasInputRef"
                      v-model="aliasInput"
                      size="small"
                      class="alias-input"
                      placeholder="www.myapp.loc"
                      :aria-label="$t('siteEdit.addAlias')"
                      @keydown.enter.prevent="addAlias"
                      @blur="addAlias"
                    />
                    <el-button v-else size="small" class="alias-add-btn" @click="showAliasInput">+ {{ $t('siteEdit.addAlias') }}</el-button>
                  </div>
                  <div class="hint">{{ $t('sites.new.aliasesHint') }}</div>
                </el-form-item>
              </el-form>
            </EditCard>

            <EditCard id="set-folder" :title="$t('siteEdit.folderGroup')">
              <el-form label-position="top" @submit.prevent>
                <el-form-item :label="$t('sites.new.folder')" required>
                  <el-input v-model="site.documentRoot" placeholder="C:\work\htdocs\myapp" class="path-input" spellcheck="false">
                    <template #append>
                      <el-button :aria-label="$t('sites.edit.browseDocRoot')" @click="showFolderBrowser = true">
                        <el-icon aria-hidden="true"><FolderOpened /></el-icon>
                        <span>{{ $t('sites.new.pick') }}</span>
                      </el-button>
                    </template>
                  </el-input>
                  <div v-if="folderState === 'exists'" class="hint hint-ok">
                    <el-icon aria-hidden="true"><Check /></el-icon>{{ $t('sites.new.folderExists') }}
                  </div>
                  <div v-else-if="folderState === 'missing'" class="hint hint-warn">
                    <el-icon aria-hidden="true"><WarningFilled /></el-icon>{{ $t('sites.new.folderMissing') }}
                  </div>
                  <div v-else class="hint">{{ $t('siteEdit.folderHint') }}</div>
                </el-form-item>
              </el-form>
            </EditCard>

            <EditCard id="set-runtime" :title="$t('siteEdit.runtimeGroup')">
              <div class="runtime-picker" role="radiogroup" :aria-label="$t('siteEdit.runtimeGroup')">
                <button
                  v-for="rt in runtimeChoices"
                  :key="rt.id"
                  type="button"
                  role="radio"
                  class="runtime-card"
                  :class="{ active: selectedRuntime === rt.id }"
                  :aria-checked="selectedRuntime === rt.id"
                  @click="selectRuntime(rt.id)"
                >
                  <span class="runtime-card-title">{{ rt.title }}</span>
                  <span class="runtime-card-desc">{{ rt.desc }}</span>
                </button>
              </div>

              <div v-if="selectedRuntime === 'php'" class="runtime-extra">
                <template v-if="siteState.phpVersions === null || siteState.phpVersions.length">
                  <span class="sub-label">{{ $t('sites.phpVersion') }}</span>
                  <div class="php-version-grid" role="radiogroup" :aria-label="$t('sites.phpVersion')">
                    <button
                      v-for="v in siteState.phpVersions ?? []"
                      :key="v.value"
                      type="button"
                      role="radio"
                      class="php-version-btn"
                      :class="{ active: site.phpVersion === v.value }"
                      :aria-checked="site.phpVersion === v.value"
                      :title="`PHP ${v.full}`"
                      @click="site.phpVersion = v.value"
                    >PHP {{ v.value }}<span v-if="v.isDefault" class="php-default">{{ $t('sites.list.phpDefault') }}</span></button>
                  </div>
                  <div v-if="phpVersionMissing" class="inline-callout">
                    <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                    <span>{{ $t('siteEdit.phpMissing', { v: site.phpVersion }) }}</span>
                    <el-button size="small" @click="router.push('/binaries')">{{ $t('siteEdit.openBinaries') }}</el-button>
                  </div>
                </template>
                <div v-else class="inline-callout">
                  <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                  <span>{{ $t('sites.new.noPhp') }}</span>
                  <el-button size="small" :loading="siteState.installing.has('php')" @click="siteState.install('php')">{{ $t('sites.new.installPhp') }}</el-button>
                </div>
              </div>

              <div v-else-if="selectedRuntime === 'node'" class="runtime-extra">
                <div class="node-fields">
                  <label class="field">
                    <span class="sub-label">{{ $t('siteEdit.upstreamPort') }}</span>
                    <el-input-number v-model="nodePort" :min="1024" :max="65535" controls-position="right" />
                  </label>
                  <label class="field field-grow">
                    <span class="sub-label">{{ $t('siteEdit.startCommand') }}</span>
                    <el-input v-model="nodeCommand" placeholder="npm start" clearable />
                  </label>
                </div>
                <div class="hint">{{ $t('siteEdit.upstreamPortHint') }} {{ $t('siteEdit.startCommandHint') }}</div>

                <div v-if="uiMode.isAdvanced" class="node-process">
                  <span class="sub-label">{{ $t('siteEdit.process') }}</span>
                  <div v-if="nodePlugin !== 'ok'" class="inline-callout">
                    <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                    <span>{{ $t(nodePlugin === 'disabled' ? 'siteEdit.nodePluginDisabled' : 'siteEdit.nodePluginMissing') }}</span>
                    <el-button
                      v-if="nodePlugin === 'disabled'"
                      size="small"
                      :loading="nodePluginBusy"
                      @click="enableNodePlugin"
                    >{{ $t('siteEdit.enablePlugin') }}</el-button>
                    <el-button v-else size="small" @click="router.push('/plugins')">{{ $t('siteEdit.openPlugins') }}</el-button>
                  </div>
                  <div v-else class="node-process-row">
                    <HealthStatusDot
                      :level="nodeProcessState === 2 ? 'ok' : nodeProcessState === 4 ? 'err' : nodeProcessState === 1 ? 'warn' : 'muted'"
                      :pulse="nodeProcessState === 1"
                    />
                    <span class="node-process-state">
                      {{ nodeProcessState === 2 ? $t('common.running') : nodeProcessState === 4 ? $t('common.crashed') : nodeProcessState === 1 ? $t('common.starting') : $t('common.stopped') }}
                      <template v-if="nodeProcessPid"> (PID {{ nodeProcessPid }})</template>
                    </span>
                    <el-button v-if="nodeProcessState !== 2" size="small" type="primary" :loading="nodeProcessLoading" @click="startNodeProcess">{{ $t('siteEdit.start') }}</el-button>
                    <template v-else>
                      <el-button size="small" :loading="nodeProcessLoading" @click="stopNodeProcess">{{ $t('common.stop') }}</el-button>
                      <el-button size="small" :loading="nodeProcessLoading" @click="restartNodeProcess">{{ $t('siteEdit.restart') }}</el-button>
                    </template>
                  </div>
                </div>
              </div>
            </EditCard>

            <EditCard id="set-https" :title="$t('siteEdit.httpsGroup')">
              <label class="toggle-line">
                <el-switch v-model="site.sslEnabled" />
                <span class="toggle-label">{{ $t('siteEdit.httpsToggle') }}</span>
              </label>
              <div v-if="site.sslEnabled && httpsNeedsSetup" class="inline-callout">
                <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                <span>{{ $t('sites.new.httpsNeedsCa') }}</span>
                <el-button size="small" :loading="siteState.installing.has('https')" @click="siteState.install('https')">{{ $t('sites.new.installHttps') }}</el-button>
              </div>
              <div v-else class="hint">{{ $t('sites.new.httpsHint') }}</div>
            </EditCard>

            <EditCard v-if="uiMode.isAdvanced" id="set-network" :title="$t('siteEdit.networkGroup')">
              <el-form label-position="top" @submit.prevent>
                <el-form-item :label="$t('sites.bindIp')">
                  <el-select
                    v-model="site.bindAddresses"
                    class="bind-address-select"
                    multiple
                    clearable
                    collapse-tags
                    collapse-tags-tooltip
                    filterable
                    :loading="bindLoading"
                    :disabled="bindLoading || bindOptions.length === 0"
                    @change="onBindChange"
                  >
                    <el-option v-for="opt in bindOptions" :key="opt.value" :label="opt.label" :value="opt.value">
                      <span class="bind-opt-label">{{ opt.label }}</span>
                      <span v-if="opt.wildcard || opt.loopback" class="bind-opt-badge">{{ $t('sites.bindIpRecommended') }}</span>
                    </el-option>
                  </el-select>
                  <div class="hint">{{ $t('sites.bindIpHint') }}</div>
                </el-form-item>
                <div class="port-grid">
                  <el-form-item :label="$t('siteEdit.httpPort')">
                    <el-input-number v-model="site.httpPort" :min="1" :max="65535" controls-position="right" />
                  </el-form-item>
                  <el-form-item v-if="site.sslEnabled" :label="$t('siteEdit.httpsPort')">
                    <el-input-number v-model="site.httpsPort" :min="1" :max="65535" controls-position="right" />
                  </el-form-item>
                </div>
                <div class="hint">{{ $t('siteEdit.portsHint') }}</div>
              </el-form>
            </EditCard>

            <EditCard v-if="siteState.cloudflareActive" id="set-tunnel" :title="$t('siteEdit.tunnelGroup')">
              <label class="toggle-line">
                <el-switch :model-value="!!site.cloudflare?.enabled" :disabled="!tunnelConfigured" @change="setTunnel" />
                <span class="toggle-label">{{ $t('siteEdit.tunnelLabel') }}</span>
              </label>
              <div v-if="!tunnelConfigured" class="inline-callout">
                <el-icon aria-hidden="true"><WarningFilled /></el-icon>
                <span>{{ $t('siteEdit.tunnelNotConfigured') }}</span>
                <el-button size="small" @click="router.push('/cloudflare')">{{ $t('siteEdit.openCloudflare') }}</el-button>
              </div>
              <div v-else-if="site.cloudflare?.enabled" class="hint mono">https://{{ site.cloudflare.subdomain }}.{{ site.cloudflare.zoneName }}</div>
            </EditCard>

            <EditCard v-if="uiMode.isAdvanced" id="set-framework" :title="$t('siteEdit.frameworkGroup')">
              <div class="framework-row">
                <el-input v-model="site.framework" :placeholder="$t('siteEdit.frameworkPlaceholder')" :aria-label="$t('sites.framework')" />
                <el-button :loading="detecting" @click="detectFramework">
                  <el-icon aria-hidden="true"><Search /></el-icon><span>{{ $t('siteEdit.autoDetect') }}</span>
                </el-button>
              </div>
              <div class="hint">{{ $t('siteEdit.frameworkHint') }}</div>

              <div v-if="composeInfo" class="compose-block">
                <span class="sub-label">{{ $t('siteEdit.dockerCard') }}: <code>{{ composeInfo.fileName }}</code></span>
                <div class="compose-actions">
                  <el-button size="small" type="primary" :loading="composeLoading" @click="runCompose('up')">{{ $t('siteEdit.composeUp') }}</el-button>
                  <el-button size="small" :loading="composeLoading" @click="runCompose('down')">{{ $t('siteEdit.composeDown') }}</el-button>
                  <el-button size="small" :loading="composeLoading" @click="runCompose('restart')">{{ $t('common.restart') }}</el-button>
                  <el-button size="small" :loading="composeLoading" @click="runCompose('ps')">{{ $t('common.status') }}</el-button>
                </div>
                <pre v-if="composeOutput" class="compose-output">{{ composeOutput }}</pre>
              </div>
            </EditCard>

            <section id="set-danger" class="danger-box" :aria-labelledby="`${uid}-danger`">
              <h2 :id="`${uid}-danger`" class="danger-title">{{ $t('siteEdit.deleteTitle') }}</h2>
              <p class="danger-desc">{{ $t('siteEdit.deleteDesc') }}</p>
              <el-button type="danger" plain @click="confirmDelete">{{ $t('siteEdit.deleteAction') }}</el-button>
            </section>
          </div>
        </el-tab-pane>

        <el-tab-pane v-if="uiMode.isAdvanced && featureFlagsStore.showDeploySurface" name="deploy" :label="$t('siteEdit.deploy')">
          <div class="tab-content"><DeploySiteTab :domain="domain" /></div>
        </el-tab-pane>

        <!-- Plugin-contributed tabs (SSL, Cloudflare, Composer, …); they edit the
             same site copy and save with the same bar. -->
        <PluginSlot
          name="site-edit-tabs"
          :context="{ domain, site, redirectHttps }"
          @update:site="onPluginSiteUpdate"
          @update:redirectHttps="(v: boolean) => redirectHttps = v"
          @dirty="markDirty"
        />

        <el-tab-pane v-if="uiMode.isAdvanced" name="history" :label="`${$t('siteEdit.history')} (${history.length})`">
          <div class="tab-content">
            <div class="info-banner">
              <el-icon aria-hidden="true"><InfoFilled /></el-icon>
              <span>{{ $t('siteEdit.historyBanner') }}</span>
            </div>
            <el-empty v-if="history.length === 0" :description="$t('sites.history.emptyDescription')" :image-size="64" />
            <ul v-else class="history-list">
              <li v-for="(h, i) in history" :key="h.timestamp" class="history-row">
                <span class="history-when">{{ formatDate(h.createdAt ?? h.timestamp) }}</span>
                <span class="history-label">
                  <span class="version-badge">v{{ history.length - i }}</span>
                  {{ h.label || $t('siteEdit.snapshot') }}
                </span>
                <el-button size="small" text type="primary" @click="rollback(h.timestamp, h.createdAt)">{{ $t('common.restore') }}</el-button>
              </li>
            </ul>
          </div>
        </el-tab-pane>

        <el-tab-pane name="logs" :label="$t('siteEdit.logs')">
          <div class="tab-content tab-wide">
            <el-radio-group v-model="logsView" class="logs-switch" :aria-label="$t('siteEdit.logs')">
              <el-radio-button value="errors">{{ $t('siteEdit.errors') }}</el-radio-button>
              <el-radio-button value="access">{{ $t('siteEdit.access') }}</el-radio-button>
            </el-radio-group>
            <SiteErrorLogs v-if="logsView === 'errors'" :domain="site.domain" />
            <SiteAccessLogs v-else :domain="site.domain" />
          </div>
        </el-tab-pane>

        <el-tab-pane name="metrics" :label="$t('siteEdit.metrics')">
          <div class="tab-content tab-wide">
            <SiteMetrics :domain="site.domain" :active="activeTab === 'metrics'" />
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>

    <div v-if="site && dirty" class="save-bar" role="region" :aria-label="$t('siteEdit.unsavedAria')">
      <span class="save-text">{{ $t('siteEdit.unsaved') }}</span>
      <el-button :disabled="saving" @click="discard">{{ $t('common.discard') }}</el-button>
      <el-button type="primary" :loading="saving" @click="save">{{ $t('sites.saveAndApply') }}</el-button>
    </div>

    <FolderBrowser v-model="showFolderBrowser" :initial-path="site?.documentRoot" @select="onFolderPick" />
  </div>
</template>

<script setup lang="ts">
// Named export so <keep-alive exclude="SiteEdit"> in App.vue can skip caching
// this page — SiteEdit is parametric by :domain and MUST refresh state on
// every navigation, unlike Dashboard/Sites/Binaries which benefit from cache.
defineOptions({ name: 'SiteEdit' })
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { Check, Folder, FolderOpened, InfoFilled, Search, TopRight, WarningFilled } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import DeploySiteTab from '../deploy/DeploySiteTab.vue'
import EditCard from '../shared/EditCard.vue'
import FolderBrowser from '../shared/FolderBrowser.vue'
import HealthStatusDot from '../shared/HealthStatusDot.vue'
import PluginSlot from '../shared/PluginSlot.vue'
import SiteAccessLogs from './SiteAccessLogs.vue'
import SiteErrorLogs from './SiteErrorLogs.vue'
import SiteApplyPipeline from '../sites/SiteApplyPipeline.vue'
import SiteMetrics from '../sites/SiteMetrics.vue'
import SiteOverview from '../sites/SiteOverview.vue'
import SiteStatusBadge from '../sites/SiteStatusBadge.vue'
import { useFolderState } from '../sites/folderCheck'
import { useSitesStore } from '../../stores/sites'
import { useUiModeStore } from '../../stores/uiMode'
import { useFeatureFlagsStore } from '../../stores/featureFlags'
import { usePluginsStore } from '../../stores/plugins'
import { useSiteApplyStore } from '../../stores/siteApply'
import { useSiteStateStore } from '../../stores/siteState'
import type { SiteInfo } from '../../api/types'
import {
  fetchNodeSites, startNodeSite, stopNodeSite, restartNodeSite,
  fetchDockerComposeStatus, type DockerComposeStatus,
  composeUp, composeDown, composeRestart, composePs,
  fetchBindAddressOptions, daemonBaseUrl, setSiteEnabled,
  type BindAddressOption,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { openUrl, revealFolder, siteUrl } from '../../utils/siteUrl'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
const uiMode = useUiModeStore()
const featureFlagsStore = useFeatureFlagsStore()
const pluginsStore = usePluginsStore()
const siteApplyStore = useSiteApplyStore()
const siteState = useSiteStateStore()
const uid = useId()

const domain = computed(() => String(route.params.domain || ''))

const site = ref<SiteInfo | null>(null)
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)
const toggling = ref(false)
const redirectHttps = ref(true)

const state = computed(() => (site.value ? siteState.stateOf(site.value) : { kind: 'running' as const }))
const url = computed(() => (site.value ? siteUrl(site.value) : ''))

// ── Tabs ───────────────────────────────────────────────────────────────
// Deep links (?tab=…): the old Danger/General/Runtime tabs are now Settings,
// Errors/Access are the two views of Logs.
const TAB_ALIASES: Record<string, string> = { general: 'settings', runtime: 'settings', danger: 'settings', errors: 'logs', access: 'logs' }
function queryTab(): string {
  const q = route.query.tab
  const tab = typeof q === 'string' && q ? q : 'overview'
  return TAB_ALIASES[tab] ?? tab
}
const activeTab = ref(queryTab())
const logsView = ref<'errors' | 'access'>(route.query.tab === 'access' ? 'access' : 'errors')

// The activity card links to this same route, so the query is watched
// instead of read once, and the active tab is mirrored back into the query.
watch(() => route.query.tab, () => {
  if (!route.params.domain) return // leaving the page also changes the query
  const tab = queryTab()
  if (route.query.tab === 'access') logsView.value = 'access'
  else if (route.query.tab === 'errors') logsView.value = 'errors'
  if (tab !== activeTab.value) activeTab.value = tab
})
watch(activeTab, (tab) => {
  if (!route.params.domain || tab === queryTab()) return
  const query = { ...route.query }
  if (tab === 'overview') delete query.tab
  else query.tab = tab
  void router.replace({ query })
})

// Fall back to Overview when the active tab has no pane to show: an Advanced-only tab
// in Simple mode, Deploy with the deploy surface off, or an unknown ?tab= value.
const BASE_TABS: readonly string[] = ['overview', 'settings', 'logs', 'metrics']
const ADVANCED_TABS: readonly string[] = ['deploy', 'history']
const pluginTabNames = computed(() =>
  pluginsStore.contributionsForSlot('site-edit-tabs')
    .map(c => c.props?.name)
    .filter((n): n is string => typeof n === 'string' && n !== ''),
)
watch(
  [activeTab, () => uiMode.isSimple, () => featureFlagsStore.loaded, pluginTabNames, () => pluginsStore.manifests.length],
  () => {
    const tab = activeTab.value
    if (BASE_TABS.includes(tab)) return
    if (ADVANCED_TABS.includes(tab)) {
      const deployHidden = tab === 'deploy' && featureFlagsStore.loaded && !featureFlagsStore.showDeploySurface
      if (uiMode.isSimple || deployHidden) activeTab.value = 'overview'
      return
    }
    if (pluginTabNames.value.includes(tab)) return
    // Plugin manifests not loaded yet: the tab may still be contributed.
    if (pluginsStore.manifests.length === 0) return
    activeTab.value = 'overview'
  },
  { immediate: true },
)

/** "Change" links on the overview open the matching group on the Settings tab. */
async function goToSetting(section: string) {
  activeTab.value = 'settings'
  await nextTick()
  const el = document.getElementById(`set-${section}`)
  el?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  el?.querySelector<HTMLElement>('input, button, [tabindex="0"]')?.focus({ preventScroll: true })
}

// ── Working copy of the site ───────────────────────────────────────────
// The page edits a copy and sends it whole on save. `storeBaseline` is the store
// entry the copy was last synced from, so a later change of the store entry
// (a plugin tab saved, a rollback) can be diffed against it without losing
// the user's unsaved edits.
let storeBaseline: SiteInfo | null = null
let seeding = false

const cloneSite = (s: SiteInfo): SiteInfo => JSON.parse(JSON.stringify(s)) as SiteInfo
const sameValue = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b)

function normalizeBind(values: string[] | undefined): string[] {
  const selected = [...new Set((values ?? []).map(v => String(v).trim()).filter(Boolean))]
  if (!selected.includes('*')) return selected
  return selected[selected.length - 1] === '*' ? ['*'] : selected.filter(v => v !== '*')
}

function normalizeSiteCopy(s: SiteInfo) {
  s.bindAddresses = normalizeBind(s.bindAddresses?.length ? s.bindAddresses : [s.bindAddress || '*'])
  s.bindAddress = s.bindAddresses[0] ?? '*'
  if (s.domain?.toLowerCase() === 'localhost') s.localhostLoopbackEnabled = true
}

function seedFromStore(found: SiteInfo | undefined) {
  seeding = true
  storeBaseline = found ? cloneSite(found) : null
  const copy = found ? cloneSite(found) : null
  if (copy) normalizeSiteCopy(copy)
  site.value = copy
  dirty.value = false
  // Watchers triggered by the seed run before this resolves.
  void nextTick(() => { seeding = false; dirty.value = false })
}

function markDirty() { if (!seeding) dirty.value = true }

// Any edit of the copy marks the page dirty; aliases and the Node fields live
// in their own refs and are folded back into the copy on save.
const aliases = ref<string[]>([])
const nodePort = ref(3000)
const nodeCommand = ref('')
watch(() => site.value?.aliases, (al) => { aliases.value = [...(al ?? [])] }, { immediate: true })
watch(() => site.value?.nodeUpstreamPort, (v) => { if (typeof v === 'number' && v > 0) nodePort.value = v }, { immediate: true })
watch(() => site.value?.nodeStartCommand, (v) => { nodeCommand.value = v ?? '' }, { immediate: true })
watch([site, aliases, nodePort, nodeCommand], markDirty, { deep: true })

function mergeStoreChanges(next: SiteInfo) {
  if (!site.value || !storeBaseline) return
  const prev = storeBaseline as unknown as Record<string, unknown>
  const incoming = next as unknown as Record<string, unknown>
  const local = site.value as unknown as Record<string, unknown>
  for (const key of new Set([...Object.keys(prev), ...Object.keys(incoming)])) {
    if (sameValue(prev[key], incoming[key])) continue
    if (key === 'cloudflare') {
      const before = (prev.cloudflare ?? {}) as Record<string, unknown>
      const after = (next.cloudflare ?? {}) as Record<string, unknown>
      const merged = { ...((local.cloudflare ?? {}) as Record<string, unknown>) }
      for (const sub of new Set([...Object.keys(before), ...Object.keys(after)])) {
        if (!sameValue(before[sub], after[sub])) merged[sub] = after[sub]
      }
      local.cloudflare = merged
    } else {
      local[key] = incoming[key] === undefined ? undefined : JSON.parse(JSON.stringify(incoming[key]))
    }
  }
  normalizeSiteCopy(site.value)
  storeBaseline = cloneSite(next)
}

const storeEntry = computed(() => sitesStore.sites.find(s => s.domain === domain.value))
watch(storeEntry, (next) => {
  // Not loaded yet, site deleted, or a domain switch in progress — load() seeds those cases itself.
  if (!next || !site.value || !storeBaseline) return
  if (storeBaseline.domain !== next.domain || site.value.domain !== next.domain) return
  if (sameValue(storeBaseline, next)) return
  if (dirty.value) mergeStoreChanges(next)
  else seedFromStore(next)
}, { deep: true })

function discard() {
  seedFromStore(storeEntry.value)
}

// ── Folder ─────────────────────────────────────────────────────────────
const showFolderBrowser = ref(false)
const folderState = useFolderState(computed({
  get: () => site.value?.documentRoot ?? '',
  set: () => { /* read-only view of the field */ },
}))

function onFolderPick(path: string) {
  if (site.value) site.value.documentRoot = path
}

async function openFolder() {
  if (!site.value) return
  if (!(await revealFolder(site.value.documentRoot))) ElMessage.info(t('sitesToast.folderPickerDesktopOnly'))
}

// ── Aliases ────────────────────────────────────────────────────────────
const aliasInput = ref('')
const aliasInputVisible = ref(false)
const aliasInputRef = ref<{ focus?: () => void } | null>(null)

function showAliasInput() {
  aliasInputVisible.value = true
  void nextTick(() => aliasInputRef.value?.focus?.())
}
function addAlias() {
  const v = aliasInput.value.trim().toLowerCase()
  if (v && !aliases.value.includes(v)) aliases.value = [...aliases.value, v]
  aliasInput.value = ''
  aliasInputVisible.value = false
}
function removeAlias(a: string) {
  aliases.value = aliases.value.filter(x => x !== a)
}

// ── Runtime ────────────────────────────────────────────────────────────
type Runtime = 'static' | 'php' | 'node'
const selectedRuntime = computed<Runtime>(() => {
  if (site.value?.nodeUpstreamPort && site.value.nodeUpstreamPort > 0) return 'node'
  if (site.value?.phpVersion && site.value.phpVersion !== 'none') return 'php'
  return 'static'
})
const runtimeChoices = computed(() => [
  { id: 'php' as const, title: 'PHP', desc: t('siteEdit.phpDesc') },
  { id: 'static' as const, title: t('sites.runtimeStaticSite'), desc: t('siteEdit.staticDesc') },
  { id: 'node' as const, title: 'Node.js', desc: t('siteEdit.nodeDesc') },
])

function selectRuntime(rt: Runtime) {
  const s = site.value
  if (!s || rt === selectedRuntime.value) return
  if (rt === 'static') {
    s.phpVersion = 'none'
    s.nodeUpstreamPort = 0
  } else if (rt === 'php') {
    const list = siteState.phpVersions ?? []
    s.phpVersion = (list.find(p => p.isDefault) ?? list[0])?.value ?? ''
    s.nodeUpstreamPort = 0
  } else {
    s.phpVersion = 'none'
    s.nodeUpstreamPort = nodePort.value || 3000
  }
}

const phpVersionMissing = computed(() => {
  const v = site.value?.phpVersion
  const list = siteState.phpVersions
  return !!v && v !== 'none' && !!list && list.length > 0 && !list.some(p => p.value === v.split('.').slice(0, 2).join('.'))
})

// ── HTTPS / tunnel ─────────────────────────────────────────────────────
const httpsNeedsSetup = computed(() => {
  const p = siteState.prerequisites
  return !!p && !(p.mkcertBinaryInstalled && p.mkcertCaInstalled)
})

const tunnelConfigured = computed(() => !!(site.value?.cloudflare?.subdomain && site.value.cloudflare.zoneId))
function setTunnel(on: boolean | string | number) {
  if (!site.value?.cloudflare) return
  site.value.cloudflare = { ...site.value.cloudflare, enabled: on === true }
}

// ── Network ────────────────────────────────────────────────────────────
const bindOptions = ref<BindAddressOption[]>([])
const bindLoading = ref(false)

function onBindChange(values: string[]) {
  if (!site.value) return
  site.value.bindAddresses = normalizeBind(values)
  site.value.bindAddress = site.value.bindAddresses[0] ?? '*'
}

async function loadBindOptions() {
  bindLoading.value = true
  try {
    bindOptions.value = await fetchBindAddressOptions()
    for (const current of site.value?.bindAddresses ?? ['*']) {
      if (current && !bindOptions.value.some(o => o.value === current)) {
        bindOptions.value.push({
          value: current,
          label: `${current} (${t('siteEdit.bindUnavailable')})`,
          description: '',
          wildcard: current === '*',
          loopback: false,
        })
      }
    }
  } catch {
    bindOptions.value = []
  } finally {
    bindLoading.value = false
  }
}

// ── Framework + Docker Compose ─────────────────────────────────────────
const detecting = ref(false)
async function detectFramework() {
  if (!site.value) return
  detecting.value = true
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(site.value.domain)}/detect-framework`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (!res.ok) throw new Error((await res.text().catch(() => '')) || `HTTP ${res.status}`)
    const data = await res.json() as { framework?: string }
    if (data.framework) {
      site.value.framework = data.framework
      ElMessage.success(t('siteEditToast.detected', { framework: data.framework }))
    } else {
      ElMessage.info(t('siteEditToast.noFramework'))
    }
  } catch (e) {
    ElMessage.error(t('siteEditToast.detectFailed', { err: errorMessage(e) }))
  } finally {
    detecting.value = false
  }
}

const composeInfo = ref<DockerComposeStatus | null>(null)
const composeLoading = ref(false)
const composeOutput = ref('')

async function runCompose(action: 'up' | 'down' | 'restart' | 'ps') {
  if (!site.value) return
  composeLoading.value = true
  composeOutput.value = ''
  try {
    const fn = { up: composeUp, down: composeDown, restart: composeRestart, ps: composePs }[action]
    const result = await fn(site.value.domain)
    composeOutput.value = result.output || t(result.ok ? 'siteEdit.composeDone' : 'siteEdit.composeFailedShort')
    if (!result.ok) ElMessage.warning(t('siteEditToast.composeNonZero', { action }))
  } catch (e) {
    const msg = errorMessage(e)
    composeOutput.value = msg
    ElMessage.error(t('siteEditToast.composeFailed', { action, err: msg }))
  } finally {
    composeLoading.value = false
  }
}

// ── Node.js process ────────────────────────────────────────────────────
const NODE_PLUGIN_ID = 'nks.wdc.node'
const nodeProcessState = ref(0)
const nodeProcessPid = ref<number | null>(null)
const nodeProcessLoading = ref(false)
const nodePluginReachable = ref(true)
const nodePluginBusy = ref(false)

// 'disabled' = installed but turned off, 'missing' = not installed or not answering.
const nodePlugin = computed<'ok' | 'disabled' | 'missing'>(() => {
  const manifest = pluginsStore.manifests.find(m => m.id === NODE_PLUGIN_ID)
  if (manifest && !manifest.enabled) return 'disabled'
  return nodePluginReachable.value ? 'ok' : 'missing'
})

async function refreshNodeStatus() {
  if (!site.value || selectedRuntime.value !== 'node') return
  try {
    const list = await fetchNodeSites()
    nodePluginReachable.value = true
    const proc = list.find(p => p.domain === site.value!.domain)
    nodeProcessState.value = proc?.state ?? 0
    nodeProcessPid.value = proc?.pid ?? null
  } catch {
    nodePluginReachable.value = false
    nodeProcessState.value = 0
    nodeProcessPid.value = null
  }
}

async function enableNodePlugin() {
  nodePluginBusy.value = true
  try {
    await pluginsStore.toggleEnable(NODE_PLUGIN_ID)
    await refreshNodeStatus()
  } catch (e) {
    ElMessage.error(errorMessage(e))
  } finally {
    nodePluginBusy.value = false
  }
}

async function nodeAction(run: () => Promise<unknown>, failKey: string) {
  if (!site.value) return
  nodeProcessLoading.value = true
  try {
    // start/restart answer with the process state; stop answers without one (= stopped).
    const result = await run() as { state?: number; pid?: number | null } | undefined
    nodeProcessState.value = result?.state ?? 0
    nodeProcessPid.value = result?.pid ?? null
  } catch (e) {
    ElMessage.error(t(failKey, { err: errorMessage(e) }))
  } finally {
    nodeProcessLoading.value = false
  }
}
const startNodeProcess = () => nodeAction(() => startNodeSite(site.value!.domain), 'siteEditToast.nodeStartFailed')
const stopNodeProcess = () => nodeAction(() => stopNodeSite(site.value!.domain), 'siteEditToast.nodeStopFailed')
const restartNodeProcess = () => nodeAction(() => restartNodeSite(site.value!.domain), 'siteEditToast.nodeRestartFailed')

// ── Plugin tabs ────────────────────────────────────────────────────────
// Plugin tabs (SSL, Cloudflare) emit `update:site` with the part of the
// record they edit; it is merged into the working copy and saved with it.
function onPluginSiteUpdate(updated: SiteInfo) {
  if (!site.value) return
  site.value = { ...site.value, ...updated }
  markDirty()
}

// ── Load / save ────────────────────────────────────────────────────────
const history = ref<Array<{ timestamp: string; createdAt?: string; label?: string }>>([])

async function load() {
  loading.value = true
  try {
    await sitesStore.load()
    seedFromStore(sitesStore.sites.find(s => s.domain === domain.value))
    void siteApplyStore.hydrate()
    void siteState.refresh()
    void loadBindOptions()

    try {
      const res = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(domain.value)}/history`, { headers: sitesStore.authHeaders() })
      if (res.ok) history.value = await res.json() as typeof history.value
    } catch { /* optional */ }

    fetchDockerComposeStatus(domain.value)
      .then(s => { composeInfo.value = s.hasCompose ? s : null })
      .catch(() => { composeInfo.value = null })
    void refreshNodeStatus()
  } finally {
    loading.value = false
  }
}

async function save() {
  const s = site.value
  if (!s || saving.value) return
  s.bindAddresses = normalizeBind(s.bindAddresses)
  if (s.bindAddresses.length === 0) {
    ElMessage.warning(t('siteEditToast.bindRequired'))
    return
  }
  if (selectedRuntime.value === 'php' && !s.phpVersion) {
    ElMessage.warning(t('siteEdit.phpRequired'))
    return
  }
  s.bindAddress = s.bindAddresses[0]
  s.aliases = [...aliases.value]
  if (selectedRuntime.value === 'node') {
    s.nodeUpstreamPort = nodePort.value
    s.nodeStartCommand = nodeCommand.value || ''
  } else {
    s.nodeUpstreamPort = 0
    s.nodeStartCommand = ''
  }
  saving.value = true
  try {
    await siteApplyStore.track(s.domain, () => sitesStore.update(s.domain, s))
    ElMessage.success(t('siteEditToast.updated'))
    // The daemon's bind-IP / NIC warnings: Apache would fail to bind at the next start.
    for (const w of sitesStore.lastUpdateWarnings) ElMessage.warning({ message: w, duration: 8000, showClose: true })
    seeding = true
    dirty.value = false
    void nextTick(() => { seeding = false; dirty.value = false })
    void refreshNodeStatus()
  } catch (e) {
    ElMessage.error(t('siteEditToast.updateFailed', { err: errorMessage(e) }))
  } finally {
    saving.value = false
  }
}

// Ctrl+S saves while there is something to save.
function onKeydown(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's' && dirty.value) {
    e.preventDefault()
    void save()
  }
}

async function confirmLeave(): Promise<boolean> {
  if (!dirty.value) return true
  try {
    await ElMessageBox.confirm(t('siteEdit.leaveText'), t('siteEdit.leaveTitle'), {
      type: 'warning',
      confirmButtonText: t('siteEdit.leaveConfirm'),
      cancelButtonText: t('siteEdit.stay'),
    })
    return true
  } catch {
    return false
  }
}
onBeforeRouteLeave(confirmLeave)
onBeforeRouteUpdate((to, from) => (to.params.domain === from.params.domain ? true : confirmLeave()))

async function toggleEnabled() {
  const s = site.value
  if (!s) return
  const enabled = s.enabled === false
  toggling.value = true
  try {
    await siteApplyStore.track(s.domain, () => setSiteEnabled(s.domain, enabled))
    ElMessage.success(t(enabled ? 'sitesToast.enabled' : 'sitesToast.disabled', { domain: s.domain }))
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(t('sitesToast.toggleFailed', { err: errorMessage(e) }))
  } finally {
    toggling.value = false
  }
}

async function rollback(timestamp: string, createdAt?: string) {
  if (!site.value) return
  try {
    await ElMessageBox.confirm(t('toast.restoreConfigConfirm', { when: formatDate(createdAt ?? timestamp) }), t('toast.restoreTitle'), {
      type: 'warning',
      confirmButtonText: t('toast.restoreTitle'),
      cancelButtonText: t('common.cancel'),
    })
  } catch { return }
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(site.value.domain)}/rollback`, {
      method: 'POST',
      headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ timestamp }),
    })
    if (!res.ok) throw new Error((await res.text().catch(() => '')) || `HTTP ${res.status}`)
    ElMessage.success(t('siteEditToast.configRestored'))
    await load()
  } catch (e) {
    ElMessage.error(t('siteEditToast.restoreFailed', { err: errorMessage(e) }))
  }
}

async function confirmDelete() {
  const s = site.value
  if (!s) return
  try {
    await ElMessageBox.confirm(t('sitesToast.deleteConfirm', { domain: s.domain }), t('sites.delete'), {
      type: 'warning',
      confirmButtonText: t('common.delete'),
      cancelButtonText: t('common.cancel'),
      confirmButtonClass: 'el-button--danger',
    })
  } catch {
    return // cancelled
  }
  try {
    await sitesStore.remove(s.domain)
  } catch (e) {
    ElMessage.error(t('sitesToast.deleteFailed', { err: errorMessage(e) }))
    return
  }
  dirty.value = false
  ElMessage.success(t('sitesToast.deleted', { domain: s.domain }))
  void router.push('/sites')
}

function formatDate(s: string | number | undefined | null): string {
  // The daemon returns history timestamps as ISO strings, unix seconds or
  // unix milliseconds, and some as "YYYY-MM-DD HH:mm:ss" (rejected by Safari).
  if (s === null || s === undefined || s === '') return '—'
  let d: Date
  if (typeof s === 'number') d = new Date(s < 1e12 ? s * 1000 : s)
  else d = new Date(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}/.test(s) ? s.replace(' ', 'T') : s)
  return Number.isNaN(d.getTime()) ? String(s) : d.toLocaleString()
}

watch(domain, () => { void load() })
onMounted(() => {
  void load()
  window.addEventListener('keydown', onKeydown)
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<style scoped>
.site-edit-page {
  display: flex;
  flex-direction: column;
  width: 100%;
  min-height: 100%;
  overflow-x: hidden;
}

.page-header {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
  padding: 0 0 var(--wdc-space-4);
  border-bottom: 1px solid var(--wdc-border);
}
.header-left { display: flex; flex-direction: column; align-items: flex-start; gap: var(--wdc-space-1-5); min-width: 0; }
.crumbs { display: flex; gap: var(--wdc-space-2); font-size: 0.87rem; color: var(--wdc-text-2); }
.crumb-link {
  color: var(--wdc-text-2);
  text-decoration: underline;
  text-decoration-color: var(--wdc-border-strong);
  text-underline-offset: 0.2em;
}
.crumb-link:hover { color: var(--wdc-text); text-decoration-color: var(--wdc-accent); }
.crumb-link:focus-visible,
.title-url:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; border-radius: var(--wdc-radius-xs); }
.title-name {
  font-size: 1.728rem;
  line-height: 1.1;
  letter-spacing: -0.028em;
  color: var(--wdc-text);
  overflow-wrap: anywhere;
}
.title-meta { display: flex; flex-wrap: wrap; align-items: center; gap: var(--wdc-space-1) var(--wdc-space-4); }
.title-url {
  color: var(--wdc-accent-ink);
  font-family: var(--wdc-font-mono);
  font-size: 0.87rem;
  text-underline-offset: 0.2em;
  overflow-wrap: anywhere;
}
.header-actions { display: flex; gap: var(--wdc-space-2); }
.header-actions :deep(.el-button + .el-button) { margin-left: 0; }
.header-actions :deep(.el-button > span) { display: inline-flex; align-items: center; gap: var(--wdc-space-1-5); }

.state-box { padding: var(--wdc-space-6) 0; }
.edit-body { flex: 1; padding-top: var(--wdc-space-4); }
.site-apply { margin-bottom: var(--wdc-space-3); }

/* Tabs keep their natural width and sit on the left. */
.site-tabs :deep(.el-tabs__header) { margin-bottom: 0; }
.site-tabs :deep(.el-tabs__nav-wrap) { overflow-x: auto; }
.site-tabs :deep(.el-tabs__item) { padding: 0 var(--wdc-space-4); font-size: 0.93rem; }

.tab-content {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-5) var(--wdc-space-1) var(--wdc-space-5-5);
}
.tab-content.settings { max-width: 880px; }
.tab-wide { max-width: 100%; }
.logs-switch { align-self: flex-start; }

.hint {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-1-5);
  width: 100%;
  margin-top: var(--wdc-space-1-5);
  color: var(--wdc-text-3);
  font-size: 0.8rem;
  line-height: 1.4;
}
.hint-ok { color: var(--wdc-status-running); }
.hint-warn { color: var(--wdc-status-starting); }
.hint.mono { font-family: var(--wdc-font-mono); }

:deep(.el-form-item) { margin-bottom: var(--wdc-space-4); }
:deep(.el-form-item:last-child) { margin-bottom: 0; }
:deep(.el-select) { width: 100%; }
:deep(.el-input-number) { width: 170px; }
.path-input :deep(.el-input__inner) { font-family: var(--wdc-font-mono); font-size: 0.85rem; }
.path-input :deep(.el-input-group__append .el-button) { gap: var(--wdc-space-1-5); }

.sub-label { display: block; margin-bottom: var(--wdc-space-2); color: var(--wdc-text-2); font-size: 0.82rem; font-family: var(--wdc-font-display); font-weight: 500; }

/* Alias chips */
.alias-chips { display: flex; flex-wrap: wrap; align-items: center; gap: var(--wdc-space-2); min-height: 32px; }
.alias-chip { font-family: var(--wdc-font-mono); }
.alias-input { width: 220px; }
.alias-add-btn { border-style: dashed; }

/* Runtime picker */
.runtime-picker { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--wdc-space-3); }
.runtime-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--wdc-space-1);
  padding: var(--wdc-space-3) var(--wdc-space-4);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface);
  color: inherit;
  font-family: inherit;
  text-align: left;
  cursor: pointer;
}
@media (hover: hover) { .runtime-card:hover { background: var(--wdc-hover); } }
.runtime-card.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); box-shadow: inset 0 0 0 1px var(--wdc-accent); }
.runtime-card:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; }
.runtime-card-title { color: var(--wdc-text); font-family: var(--wdc-font-display); font-weight: 600; }
.runtime-card-desc { color: var(--wdc-text-2); font-size: 0.8rem; line-height: 1.4; }
.runtime-extra { margin-top: var(--wdc-space-4); padding-top: var(--wdc-space-4); border-top: 1px solid var(--wdc-border); }

.php-version-grid { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.php-version-btn {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-2);
  min-height: 34px;
  padding: 0 var(--wdc-space-3-5);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface);
  color: var(--wdc-text);
  font-family: var(--wdc-font-mono);
  font-size: 0.87rem;
  cursor: pointer;
}
@media (hover: hover) { .php-version-btn:hover { background: var(--wdc-hover); } }
.php-version-btn.active { border-color: var(--wdc-accent); background: var(--wdc-accent-dim); color: var(--wdc-accent-ink); box-shadow: inset 0 0 0 1px var(--wdc-accent); }
.php-version-btn:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 2px; }
.php-default { color: var(--wdc-text-3); font-family: var(--wdc-font-body); font-size: 0.72rem; }
.php-version-btn.active .php-default { color: var(--wdc-accent-ink); }

.node-fields { display: flex; flex-wrap: wrap; gap: var(--wdc-space-4); }
.field { display: grid; }
.field-grow { flex: 1; min-width: 220px; }
.node-process { margin-top: var(--wdc-space-4); }
.node-process-row { display: flex; align-items: center; flex-wrap: wrap; gap: var(--wdc-space-2-5); }
.node-process-state { color: var(--wdc-text-2); font-size: 0.87rem; }

.inline-callout {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
  margin-top: var(--wdc-space-2-5);
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border: 1px solid var(--wdc-border-strong);
  border-radius: var(--wdc-radius);
  background: var(--wdc-surface-2);
  color: var(--wdc-text-2);
  font-size: 0.82rem;
}
.inline-callout .el-icon { color: var(--wdc-status-starting); }
.inline-callout .el-button { margin-left: auto; }

.toggle-line { display: inline-flex; align-items: center; gap: var(--wdc-space-2-5); cursor: pointer; }
.toggle-label { color: var(--wdc-text); font-weight: 500; }

.port-grid { display: flex; flex-wrap: wrap; gap: var(--wdc-space-4); }

.framework-row { display: flex; gap: var(--wdc-space-2); }
.framework-row :deep(.el-button > span) { display: inline-flex; align-items: center; gap: var(--wdc-space-1-5); }
.compose-block { margin-top: var(--wdc-space-4); padding-top: var(--wdc-space-4); border-top: 1px solid var(--wdc-border); }
.compose-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.compose-actions :deep(.el-button + .el-button) { margin-left: 0; }
.compose-output {
  max-height: 200px;
  margin: var(--wdc-space-3) 0 0;
  padding: var(--wdc-space-3);
  overflow-y: auto;
  border-radius: var(--wdc-radius-sm);
  background: var(--wdc-log-bg);
  color: var(--wdc-log-text);
  font-family: var(--wdc-font-mono);
  font-size: 0.75rem;
  white-space: pre-wrap;
  word-break: break-all;
}

.danger-box {
  padding: var(--wdc-space-4) var(--wdc-space-5);
  border: 1px solid color-mix(in oklab, var(--wdc-status-error) 45%, var(--wdc-border));
  border-radius: var(--wdc-radius-lg);
}
.danger-title { margin-bottom: var(--wdc-space-2); font-size: 0.97rem; color: var(--wdc-status-error); }
.danger-desc { margin: 0 0 var(--wdc-space-3); color: var(--wdc-text-2); font-size: 0.87rem; line-height: 1.5; max-width: 70ch; }

.info-banner {
  display: flex;
  align-items: flex-start;
  gap: var(--wdc-space-2-5);
  padding: var(--wdc-space-3) var(--wdc-space-4);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface-2);
  color: var(--wdc-text-2);
  font-size: 0.87rem;
  line-height: 1.5;
}
.info-banner .el-icon { margin-top: 2px; color: var(--wdc-accent-ink); flex-shrink: 0; }
.history-list { display: flex; flex-direction: column; gap: var(--wdc-space-2); margin: 0; padding: 0; list-style: none; }
.history-row {
  display: grid;
  grid-template-columns: 220px minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-2-5) var(--wdc-space-4);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface);
}
.history-when { color: var(--wdc-text-2); font-size: 0.87rem; }
.history-label { display: flex; align-items: center; gap: var(--wdc-space-2-5); font-size: 0.87rem; }
.version-badge {
  padding: 0 var(--wdc-space-2);
  border-radius: var(--wdc-radius-xs);
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent-ink);
  font-family: var(--wdc-font-mono);
  font-size: 0.75rem;
}

/* Sticky bar at the bottom while there is something to save. */
.save-bar {
  position: sticky;
  bottom: 0;
  z-index: 5;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--wdc-space-2);
  margin-top: auto;
  padding: var(--wdc-space-3) var(--wdc-space-4);
  border-top: 1px solid var(--wdc-border-strong);
  background: var(--wdc-elevated);
  box-shadow: var(--wdc-shadow-pop);
}
.save-bar :deep(.el-button + .el-button) { margin-left: 0; }
.save-text { margin-right: auto; color: var(--wdc-text); font-family: var(--wdc-font-display); font-weight: 600; }

@media (max-width: 900px) {
  .runtime-picker { grid-template-columns: minmax(0, 1fr); }
  .history-row { grid-template-columns: minmax(0, 1fr) auto; }
  .history-when { grid-column: 1 / -1; }
}
</style>
