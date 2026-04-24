<template>
  <div class="site-edit-page">
    <!-- Simple mode: tabless single-page view -->
    <SiteDetailSimple v-if="uiMode.isSimple" :domain="domain" />

    <template v-else>
    <!-- Page header -->
    <div class="page-header">
      <div class="header-left">
        <el-button size="small" text @click="goBack">
          <el-icon><ArrowLeft /></el-icon>
          <span>{{ $t('sites.backToSites') }}</span>
        </el-button>
        <div class="title-block">
          <div class="title-label">{{ $t('sites.editSite') }}</div>
          <div class="title-name">{{ domain }}</div>
        </div>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openInBrowser" :disabled="!site">
          {{ $t('common.open') }}
        </el-button>
        <el-button
          type="primary"
          size="small"
          :loading="saving"
          :disabled="!dirty"
          @click="save"
        >
          {{ $t('common.save') }} &amp; {{ $t('common.apply') }}
        </el-button>
      </div>
    </div>

    <!-- Loading -->
    <div v-if="loading" class="state-box">
      <el-skeleton :rows="10" animated />
    </div>

    <!-- Not found -->
    <div v-else-if="!site" class="state-box">
      <el-empty :description="$t('sites.notFound', { domain })" />
    </div>

    <!-- Content with tabs -->
    <div v-else class="edit-body">
      <el-tabs v-model="activeTab" class="site-tabs">
        <!-- ── General ──────────────────────────── -->
        <el-tab-pane name="general">
          <template #label>
            <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('siteEdit.general') }}</span>
          </template>
          <div class="tab-content">
            <!-- Identity card -->
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Identity</span>
                <span class="edit-card-hint">Domain and document root</span>
              </header>
              <div class="edit-card-body">
                <el-form :model="site" label-position="top" size="default">
                  <el-form-item label="Domain">
                    <el-input :model-value="site.domain" disabled>
                      <template #prepend><el-icon><Link /></el-icon></template>
                    </el-input>
                  </el-form-item>
                  <el-form-item label="Document Root" required>
                    <el-input
                      v-model="site.documentRoot"
                      placeholder="C:\work\htdocs\myapp"
                      class="path-input"
                      @input="markDirty"
                    >
                      <template #append>
                        <el-button
                          class="browse-append-btn"
                          :aria-label="$t('sites.edit.browseDocRoot')"
                          @click="showFolderBrowser = true"
                        >
                          <el-icon><FolderOpened /></el-icon>
                          <span>Browse…</span>
                        </el-button>
                      </template>
                    </el-input>
                    <div class="hint" v-if="site.documentRoot">
                      <el-icon><Check /></el-icon>
                      Apache will serve files from this folder
                    </div>
                  </el-form-item>
                </el-form>
              </div>
            </section>

            <!-- Alias picker card -->
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Aliases</span>
                <span class="edit-card-hint">{{ aliases.length }} additional domain{{ aliases.length === 1 ? '' : 's' }}</span>
              </header>
              <div class="edit-card-body">
                <div class="alias-chips">
                  <el-tag
                    v-for="alias in aliases"
                    :key="alias"
                    class="alias-chip"
                    closable
                    effect="dark"
                    @close="removeAlias(alias)"
                  >
                    {{ alias }}
                  </el-tag>
                  <el-input
                    v-if="aliasInputVisible"
                    ref="aliasInputRef"
                    v-model="aliasInput"
                    size="small"
                    class="alias-input"
                    placeholder="www.myapp.loc"
                    @keydown.enter.prevent="addAlias"
                    @blur="addAlias"
                  />
                  <el-button
                    v-else
                    size="small"
                    class="alias-add-btn"
                    @click="showAliasInput"
                  >
                    + Add alias
                  </el-button>
                </div>
                <div class="hint">
                  Wildcards supported: <code>*.myapp.loc</code> catches every subdomain.
                </div>
              </div>
            </section>

            <!-- Framework detection card -->
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Framework</span>
                <span class="edit-card-hint">Detected automatically or set manually</span>
              </header>
              <div class="edit-card-body">
                <div class="framework-row">
                  <el-input
                    v-model="site.framework"
                    placeholder="wordpress, laravel, nextjs, …"
                    class="framework-input"
                    @input="markDirty"
                  />
                  <el-button size="default" @click="detectFramework" :loading="detecting">
                    <el-icon><Search /></el-icon>
                    <span>Auto-detect</span>
                  </el-button>
                </div>
              </div>
            </section>

            <!-- Docker Compose detection + lifecycle card -->
            <section v-if="composeInfo" class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Docker Compose</span>
                <span class="edit-card-hint">{{ composeInfo.fileName }}</span>
              </header>
              <div class="edit-card-body">
                <div style="display: flex; align-items: center; gap: 8px; flex-wrap: wrap">
                  <el-tag size="small" type="info" effect="plain">{{ composeInfo.fileName }}</el-tag>
                  <el-button size="small" type="success" @click="runCompose('up')" :loading="composeLoading">
                    Up
                  </el-button>
                  <el-button size="small" type="danger" plain @click="runCompose('down')" :loading="composeLoading">
                    Down
                  </el-button>
                  <el-button size="small" @click="runCompose('restart')" :loading="composeLoading">
                    Restart
                  </el-button>
                  <el-button size="small" @click="runCompose('ps')" :loading="composeLoading">
                    Status
                  </el-button>
                </div>
                <div v-if="composeOutput" class="compose-output">
                  <pre>{{ composeOutput }}</pre>
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- ── Runtime ──────────────────────────── -->
        <el-tab-pane name="runtime">
          <template #label>
            <span class="tab-label"><el-icon><Cpu /></el-icon> {{ $t('siteEdit.runtime') }}</span>
          </template>
          <div class="tab-content">
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Runtime</span>
                <span class="edit-card-hint">Choose how this site is served</span>
              </header>
              <div class="edit-card-body">
                <div class="runtime-picker">
                  <button
                    class="runtime-card"
                    :class="{ active: selectedRuntime === 'static' }"
                    @click="selectRuntime('static')"
                    type="button"
                  >
                    <div class="runtime-card-icon runtime-static">HTML</div>
                    <div class="runtime-card-title">Static</div>
                    <div class="runtime-card-desc">Plain HTML / assets. No language runtime.</div>
                  </button>
                  <button
                    class="runtime-card"
                    :class="{ active: selectedRuntime === 'php' }"
                    @click="selectRuntime('php')"
                    type="button"
                  >
                    <div class="runtime-card-icon runtime-php">PHP</div>
                    <div class="runtime-card-title">PHP</div>
                    <div class="runtime-card-desc">Pick an installed PHP version below.</div>
                  </button>
                  <button
                    class="runtime-card"
                    :class="{ active: selectedRuntime === 'node' }"
                    @click="selectRuntime('node')"
                    type="button"
                  >
                    <div class="runtime-card-icon runtime-node">N</div>
                    <div class="runtime-card-title">Node.js</div>
                    <div class="runtime-card-desc">Reverse-proxy to your app's HTTP listener.</div>
                  </button>
                </div>

                <!-- Node.js upstream port + start command (only when Node is selected) -->
                <div v-if="selectedRuntime === 'node'" class="php-version-picker">
                  <label class="sub-label">Upstream port</label>
                  <el-input-number
                    v-model="nodeUpstreamPort"
                    :min="1024"
                    :max="65535"
                    controls-position="right"
                    style="width: 180px"
                    @change="markDirty"
                  />
                  <div class="hint" style="margin-top: 8px">
                    Port your Node.js app listens on. Apache will <code>ProxyPass</code> all requests to it.
                  </div>

                  <label class="sub-label" style="margin-top: 16px">Start command</label>
                  <el-input
                    v-model="nodeStartCommand"
                    placeholder="npm start"
                    clearable
                    style="width: 320px"
                    @input="markDirty"
                  />
                  <div class="hint" style="margin-top: 4px">
                    Command to start your app (e.g. <code>npm start</code>, <code>npm run dev</code>, <code>node server.js</code>).
                    Empty defaults to <code>npm start</code>.
                  </div>

                  <!-- Per-site Node process controls -->
                  <div class="node-process-controls" style="margin-top: 16px">
                    <label class="sub-label">Process</label>
                    <div v-if="!nodePluginAvailable" class="hint" style="margin-top: 4px">
                      <el-icon style="color: var(--el-color-warning)"><WarningFilled /></el-icon>
                      Node.js plugin not loaded in daemon. Apache will still reverse-proxy
                      to <code>localhost:{{ nodeUpstreamPort }}</code>, but you must start
                      the Node process yourself. Install <code>NKS.WebDevConsole.Plugin.Node</code>
                      and restart the daemon to enable per-site process management.
                    </div>
                    <div v-else style="display: flex; align-items: center; gap: 10px; margin-top: 4px">
                      <span
                        class="status-dot"
                        :class="nodeProcessState === 2 ? 'running' : nodeProcessState === 4 ? 'crashed' : 'stopped'"
                      />
                      <span style="font-size: 0.85rem; color: var(--wdc-text-2)">
                        {{ nodeProcessState === 2 ? 'Running' : nodeProcessState === 4 ? 'Crashed' : nodeProcessState === 1 ? 'Starting...' : 'Stopped' }}
                        <template v-if="nodeProcessPid"> (PID {{ nodeProcessPid }})</template>
                      </span>
                      <el-button
                        v-if="nodeProcessState !== 2"
                        size="small"
                        type="success"
                        @click="startNodeProcess"
                        :loading="nodeProcessLoading"
                      >Start</el-button>
                      <el-button
                        v-if="nodeProcessState === 2"
                        size="small"
                        type="danger"
                        @click="stopNodeProcess"
                        :loading="nodeProcessLoading"
                      >{{ $t('common.stop') }}</el-button>
                      <el-button
                        v-if="nodeProcessState === 2"
                        size="small"
                        @click="restartNodeProcess"
                        :loading="nodeProcessLoading"
                      >Restart</el-button>
                    </div>
                  </div>
                </div>

                <!-- PHP version sub-picker (only when PHP is selected) -->
                <div v-if="selectedRuntime === 'php'" class="php-version-picker">
                  <label class="sub-label">PHP Version</label>
                  <div class="php-version-grid">
                    <button
                      v-for="v in phpVersions"
                      :key="v.value"
                      type="button"
                      class="php-version-btn"
                      :class="{ active: site.phpVersion === v.value, 'php-active': v.isActive }"
                      :title="v.isActive ? `${v.label} — runtime-active` : v.label"
                      @click="setPhpVersion(v.value)"
                    >
                      {{ v.label }}
                    </button>
                    <div v-if="phpVersions.length === 0" class="hint">
                      No PHP versions installed. Open the Binaries page to install one.
                    </div>
                  </div>
                </div>
              </div>
            </section>

            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Ports</span>
                <span class="edit-card-hint">Default: 80 (HTTP) and 443 (HTTPS)</span>
              </header>
              <div class="edit-card-body">
                <div class="port-grid">
                  <div class="port-item">
                    <label class="sub-label">HTTP Port</label>
                    <el-input-number
                      v-model="site.httpPort"
                      :min="1"
                      :max="65535"
                      controls-position="right"
                      style="width: 100%"
                      @change="markDirty"
                    />
                  </div>
                  <div class="port-item" v-if="site.sslEnabled">
                    <label class="sub-label">HTTPS Port</label>
                    <el-input-number
                      v-model="site.httpsPort"
                      :min="1"
                      :max="65535"
                      controls-position="right"
                      style="width: 100%"
                      @change="markDirty"
                    />
                  </div>
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- F91.6: plugin-contributed tabs (SSL, Cloudflare, Composer, …).
             Each plugin's UiContribution(slot="site-edit-tabs") renders as
             its own <el-tab-pane> with label/content the plugin supplies.
             Disabling a plugin removes its contribution → tab vanishes. -->
        <PluginSlot
          name="site-edit-tabs"
          :context="{ domain, site, redirectHttps }"
          @update:site="onPluginSiteUpdate"
          @update:redirectHttps="(v: boolean) => redirectHttps = v"
          @dirty="markDirty"
        />

        <!-- ── History ──────────────────────────── -->
        <el-tab-pane v-if="uiMode.isAdvanced" name="history">
          <template #label>
            <span class="tab-label"><el-icon><Clock /></el-icon> History ({{ history.length }})</span>
          </template>
          <div class="tab-content">
            <div class="history-banner">
              <el-icon><InfoFilled /></el-icon>
              <span>
                Rollback points capture this site's vhost + plugin config
                before every save. Restore a previous version if a recent
                change broke something — only config files are reverted,
                the site's document root is left alone.
              </span>
            </div>
            <el-empty v-if="history.length === 0" :description="$t('sites.history.emptyDescription')" :image-size="64" />
            <div v-else class="history-list">
              <div v-for="(h, i) in history" :key="h.timestamp" class="history-row">
                <div class="history-when">
                  <el-icon><Clock /></el-icon>
                  <span>{{ formatDate(h.timestamp) }}</span>
                </div>
                <div class="history-label">
                  <span class="version-badge">v{{ history.length - i }}</span>
                  <span v-if="h.label">{{ h.label }}</span>
                  <span v-else class="version-hint">config snapshot</span>
                </div>
                <el-button size="small" text type="primary" @click="rollback(h.timestamp)">
                  Restore
                </el-button>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- ── Metrics ──────────────────────────── -->
        <el-tab-pane name="metrics">
          <template #label>
            <span class="tab-label"><el-icon><DataLine /></el-icon> {{ $t('siteEdit.metrics') }}</span>
          </template>
          <div class="tab-content">
            <div class="metrics-toolbar">
              <el-button size="small" :loading="metricsLoading" :icon="Refresh" @click="refreshMetrics">
                {{ $t('common.refresh') }}
              </el-button>
              <span v-if="metricsLastRefresh" class="hint metrics-timestamp">
                Last updated {{ metricsAgeDisplay }}
              </span>
            </div>
            <div v-if="!siteMetrics" class="hint" style="padding: 24px 0">
              <span v-if="metricsLoading">Loading metrics...</span>
              <span v-else>No access log data found for this site. Start Apache with the generated vhost and make some requests.</span>
            </div>

            <!-- F74 layout: historical chart FIRST (dominant visual), then
                 compact KPI cards + request-rate sparkline as a summary strip,
                 then the access log detail table at the bottom. Previous
                 order (cards → sparkline → table → chart) buried the chart. -->

            <!-- Historical data section -->
            <div class="historical-section">
              <div class="historical-header">
                <span class="historical-title">Historical</span>
                <div class="historical-controls">
                  <div class="historical-control-item">
                    <label class="historical-label">{{ $t('metrics.historical.dateLabel') }}</label>
                    <el-date-picker
                      v-model="historicalDate"
                      type="date"
                      size="small"
                      :disabled-date="isDateDisabled"
                      value-format="YYYY-MM-DD"
                      format="YYYY-MM-DD"
                      style="width: 150px"
                      @change="onHistoricalParamsChange"
                    />
                  </div>
                  <div class="historical-control-item">
                    <label class="historical-label">{{ $t('metrics.historical.granularityLabel') }}</label>
                    <el-select
                      v-model="historicalGranularity"
                      size="small"
                      style="width: 90px"
                      @change="onHistoricalParamsChange"
                    >
                      <el-option label="1m" value="1m" />
                      <el-option label="5m" value="5m" />
                      <el-option label="15m" value="15m" />
                      <el-option label="1h" value="1h" />
                    </el-select>
                  </div>
                  <el-button
                    size="small"
                    :loading="historicalLoading"
                    :icon="Refresh"
                    @click="loadHistoricalMetrics"
                  >
                    {{ $t('metrics.historical.refresh') }}
                  </el-button>
                  <div class="historical-series-toggles">
                    <el-check-tag
                      v-for="s in historicalSeriesOptions"
                      :key="s.key"
                      :checked="historicalActiveSeries.includes(s.key)"
                      :style="{ '--check-tag-color': s.color }"
                      class="series-toggle"
                      @change="(checked: boolean) => toggleSeries(s.key, checked)"
                    >
                      {{ $t(s.labelKey) }}
                    </el-check-tag>
                  </div>
                </div>
              </div>

              <div class="historical-chart-wrap">
                <div v-if="historicalLoading" class="historical-overlay">
                  <span class="hint">{{ $t('metrics.historical.loading') }}</span>
                </div>
                <div
                  v-if="!historicalLoading && historicalIsEmptyDay"
                  class="historical-overlay historical-empty"
                >
                  {{ $t('metrics.historical.emptyDay') }}
                </div>
                <v-chart
                  :option="historicalChartOption"
                  :autoresize="true"
                  style="width: 100%; height: 220px"
                />
              </div>
            </div>

            <!-- Recent parsed access-log entries. Restored here after the
                 F74 chart-first reorder accidentally dropped the table. -->
            <SiteAccessLogs v-if="site" :domain="site.domain" class="access-log-section" />
          </div>
        </el-tab-pane>

        <!-- ── Error Logs ───────────────────────── -->
        <el-tab-pane name="errors">
          <template #label>
            <span class="tab-label"><el-icon><WarningFilled /></el-icon> {{ $t('siteEdit.errors') }}</span>
          </template>
          <div class="tab-content" style="max-width: 100%">
            <SiteErrorLogs v-if="site" :domain="site.domain" />
          </div>
        </el-tab-pane>

        <!-- ── Per-site Backup (task 29) ─────────── -->
        <el-tab-pane v-if="uiMode.isAdvanced" name="backup">
          <template #label>
            <span class="tab-label"><el-icon><FolderOpened /></el-icon> Backup</span>
          </template>
          <div class="tab-content">
            <div class="history-banner">
              <el-icon><InfoFilled /></el-icon>
              <span>
                Per-site backup snapshots (vhost + optional docroot). Backed
                by the global Backups tab (Zálohy) — filtered to only show
                snapshots that contain this site's files. Global backup
                schedule + content selection lives in
                <el-link type="primary" @click="$router.push('/backups')">Backups</el-link>.
              </span>
            </div>
            <el-empty
              description="Backup list surfaces here after the Backups top-level tab ships (task 14)"
              :image-size="64"
            />
          </div>
        </el-tab-pane>

        <!-- ── Danger ───────────────────────────── -->
        <el-tab-pane v-if="uiMode.isAdvanced" name="danger">
          <template #label>
            <span class="tab-label danger-label"><el-icon><WarningFilled /></el-icon> Danger</span>
          </template>
          <div class="tab-content">
            <!-- Task 28: rename-domain section. Backend endpoint not
                 wired yet — disabled with tooltip explaining the reason
                 until PATCH /api/sites/{domain}/rename is implemented. -->
            <div class="danger-box" style="margin-bottom: 16px">
              <div class="danger-title">
                <el-icon><Setting /></el-icon>
                Rename domain
              </div>
              <div class="danger-desc">
                Changes the site's primary domain. Updates vhost, hosts
                file, SSL cert paths, and Cloudflare tunnel route. Coming
                in a future release.
              </div>
              <el-input
                v-model="renameNewDomain"
                :placeholder="site.domain"
                size="default"
                style="max-width: 320px; margin-bottom: 8px"
                disabled
              />
              <el-tooltip content="Backend PATCH /api/sites/{domain}/rename not yet implemented" placement="top">
                <el-button type="warning" size="default" disabled>
                  Rename to {{ renameNewDomain || '...' }}
                </el-button>
              </el-tooltip>
            </div>

            <div class="danger-box">
              <div class="danger-title">
                <el-icon><WarningFilled /></el-icon>
                Delete this site
              </div>
              <div class="danger-desc">
                Removes the vhost config and hosts file entry. Document root and databases are not touched.
              </div>
              <el-button type="danger" size="default" @click="confirmDelete">
                {{ $t('common.delete') }} {{ site.domain }}
              </el-button>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>

    <!-- Folder browser dialog -->
    <FolderBrowser
      v-model="showFolderBrowser"
      :initial-path="site?.documentRoot"
      @select="onFolderPick"
    />
    </template><!-- end v-else advanced -->
  </div>
</template>

<script setup lang="ts">
// Named export so <keep-alive exclude="SiteEdit"> in App.vue can skip caching
// this page — SiteEdit is parametric by :domain and MUST refresh state on
// every navigation, unlike Dashboard/Sites/Binaries which benefit from cache.
defineOptions({ name: 'SiteEdit' })
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  ArrowLeft, Setting, Cpu, Lock, Clock, WarningFilled,
  FolderOpened, Check, Search, Link, DataLine, Refresh, Grid,
  InfoFilled,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { useUiModeStore } from '../../stores/uiMode'
import { usePluginsStore } from '../../stores/plugins'
import type { SiteInfo, HistoricalMetrics } from '../../api/types'
import FolderBrowser from '../shared/FolderBrowser.vue'
import SiteErrorLogs from './SiteErrorLogs.vue'
import SiteAccessLogs from './SiteAccessLogs.vue'
import SiteComposer from './SiteComposer.vue'
import SiteDetailSimple from './SiteDetailSimple.vue'
import PluginSlot from '../shared/PluginSlot.vue'
import {
  fetchCloudflareZones, fetchCloudflareConfig, suggestCloudflareSubdomain,
  fetchNodeSites, startNodeSite, stopNodeSite, restartNodeSite,
  fetchSiteMetrics, type SiteMetrics,
  fetchDockerComposeStatus, type DockerComposeStatus,
  composeUp, composeDown, composeRestart, composePs,
  getHistoricalMetrics,
  fetchPhpVersions,
  daemonBaseUrl,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent, DataZoomComponent } from 'echarts/components'
import VChart from 'vue-echarts'

use([CanvasRenderer, LineChart, GridComponent, TooltipComponent, LegendComponent, DataZoomComponent])

const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()
const uiMode = useUiModeStore()
const pluginsStore = usePluginsStore()

const domain = computed(() => String(route.params.domain || ''))

const site = ref<SiteInfo | null>(null)
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)
const activeTab = ref('general')
// Same per-version option shape as Sites.vue wizard — keeps the detail
// page and the wizard aligned. `value` is the backend-persisted
// majorMinor; `label` shows the full patch + active flag.
interface PhpVersionOption { value: string; label: string; isActive: boolean }
const phpVersions = ref<PhpVersionOption[]>([])
const history = ref<Array<{ timestamp: string; label?: string }>>([])
// Task 28: Danger-tab rename input. Kept reactive so a future rename
// backend can wire up immediately without a template change.
const renameNewDomain = ref('')
const composeInfo = ref<DockerComposeStatus | null>(null)
const composeLoading = ref(false)
const composeOutput = ref('')

async function runCompose(action: 'up' | 'down' | 'restart' | 'ps') {
  if (!site.value) return
  composeLoading.value = true
  composeOutput.value = ''
  try {
    const fn = action === 'up' ? composeUp
      : action === 'down' ? composeDown
      : action === 'restart' ? composeRestart
      : composePs
    const result = await fn(site.value.domain)
    composeOutput.value = result.output || (result.ok ? 'Done' : 'Failed')
    if (!result.ok) ElMessage.warning(`Compose ${action} returned non-zero exit code`)
  } catch (e) {
    const msg = errorMessage(e)
    composeOutput.value = msg
    ElMessage.error(`Compose ${action} failed: ${msg}`)
  } finally {
    composeLoading.value = false
  }
}
const redirectHttps = ref(true)

// ── Alias chip picker ──────────────────────────────────────────────────
const aliases = ref<string[]>([])
const aliasInput = ref('')
const aliasInputVisible = ref(false)
// Same pattern as CommandPalette.inputRef — only focus() is ever called.
const aliasInputRef = ref<{ focus?: () => void } | null>(null)
watch(() => site.value?.aliases, (al) => { aliases.value = [...(al ?? [])] }, { immediate: true })

function showAliasInput() {
  aliasInputVisible.value = true
  void nextTick(() => aliasInputRef.value?.focus?.())
}

function addAlias() {
  const v = aliasInput.value.trim()
  if (v && !aliases.value.includes(v)) {
    aliases.value.push(v)
    markDirty()
  }
  aliasInput.value = ''
  aliasInputVisible.value = false
}

function removeAlias(a: string) {
  aliases.value = aliases.value.filter(x => x !== a)
  markDirty()
}

// ── Runtime picker ─────────────────────────────────────────────────────
const nodeUpstreamPort = ref(3000)
const nodeStartCommand = ref('')

const selectedRuntime = computed<'static' | 'php' | 'node'>(() => {
  if (site.value?.nodeUpstreamPort && site.value.nodeUpstreamPort > 0) return 'node'
  if (site.value?.phpVersion && site.value.phpVersion !== 'none') return 'php'
  return 'static'
})

watch(() => site.value?.nodeUpstreamPort, (v) => {
  if (typeof v === 'number' && v > 0) nodeUpstreamPort.value = v
}, { immediate: true })

watch(() => site.value?.nodeStartCommand, (v) => {
  nodeStartCommand.value = v ?? ''
}, { immediate: true })

// ── Node.js process control ───────────────────────────────────────────
const nodeProcessState = ref(0)
const nodeProcessPid = ref<number | null>(null)
const nodeProcessLoading = ref(false)
// True when the daemon has the Node plugin loaded. False means /api/node/sites
// returned an error or the plugin DLL isn't present — we hide the process
// controls and show an explanatory message so the user knows why.
const nodePluginAvailable = ref(true)

async function refreshNodeStatus() {
  if (!site.value || selectedRuntime.value !== 'node') return
  try {
    const list = await fetchNodeSites()
    nodePluginAvailable.value = true
    const proc = list.find(p => p.domain === site.value!.domain)
    nodeProcessState.value = proc?.state ?? 0
    nodeProcessPid.value = proc?.pid ?? null
  } catch {
    // Plugin not loaded or daemon unreachable — disable controls instead
    // of leaving a blank/misleading status pill.
    nodePluginAvailable.value = false
    nodeProcessState.value = 0
    nodeProcessPid.value = null
  }
}

async function startNodeProcess() {
  if (!site.value) return
  nodeProcessLoading.value = true
  try {
    const result = await startNodeSite(site.value.domain)
    nodeProcessState.value = result.state
    nodeProcessPid.value = result.pid
  } catch (e) {
    ElMessage.error(`Failed to start Node: ${errorMessage(e)}`)
  } finally {
    nodeProcessLoading.value = false
  }
}

async function stopNodeProcess() {
  if (!site.value) return
  nodeProcessLoading.value = true
  try {
    await stopNodeSite(site.value.domain)
    nodeProcessState.value = 0
    nodeProcessPid.value = null
  } catch (e) {
    ElMessage.error(`Failed to stop Node: ${errorMessage(e)}`)
  } finally {
    nodeProcessLoading.value = false
  }
}

async function restartNodeProcess() {
  if (!site.value) return
  nodeProcessLoading.value = true
  try {
    const result = await restartNodeSite(site.value.domain)
    nodeProcessState.value = result.state
    nodeProcessPid.value = result.pid
  } catch (e) {
    ElMessage.error(`Failed to restart Node: ${errorMessage(e)}`)
  } finally {
    nodeProcessLoading.value = false
  }
}

function selectRuntime(rt: 'static' | 'php' | 'node') {
  if (!site.value) return
  if (rt === 'static') {
    site.value.phpVersion = 'none'
    site.value.nodeUpstreamPort = 0
  } else if (rt === 'php') {
    // Fall back to empty rather than a fabricated '8.4' — the dropdown's
    // empty-state hint ("no PHP installed — go to Binaries") should
    // then guide the user instead of silently storing a broken value.
    site.value.phpVersion =
      phpVersions.value.find(p => p.isActive)?.value
      ?? phpVersions.value[0]?.value
      ?? ''
    site.value.nodeUpstreamPort = 0
  } else if (rt === 'node') {
    site.value.phpVersion = 'none'
    site.value.nodeUpstreamPort = nodeUpstreamPort.value || 3000
  }
  markDirty()
}

function setPhpVersion(v: string) {
  if (!site.value) return
  site.value.phpVersion = v
  markDirty()
}

// ── Folder browser ─────────────────────────────────────────────────────
const showFolderBrowser = ref(false)

function onFolderPick(path: string) {
  if (!site.value) return
  site.value.documentRoot = path
  markDirty()
}

// ── Cloudflare Tunnel per-site ─────────────────────────────────────────
const cloudflareEnabled = ref(false)
const cloudflareSubdomain = ref('')
const cloudflareZoneId = ref('')
const cloudflareZoneName = ref('')
const cloudflareLocalService = ref('localhost:80')
const cloudflareProtocol = ref<'http' | 'https'>('http')
// Same minimal shape as CloudflareTunnel's CfZone — kept inline to avoid
// coupling page files together for a 2-field type.
const cfZones = ref<{ id: string; name: string }[]>([])
const loadingCfZones = ref(false)
const cfSubdomainTemplate = ref('{stem}-dev')

const selectedCfZoneName = computed(() => {
  const z = cfZones.value.find(x => x.id === cloudflareZoneId.value)
  return z?.name ?? cloudflareZoneName.value ?? ''
})

watch(() => site.value?.cloudflare, (cf) => {
  if (!cf) return
  cloudflareEnabled.value = cf.enabled ?? false
  cloudflareSubdomain.value = cf.subdomain ?? ''
  cloudflareZoneId.value = cf.zoneId ?? ''
  cloudflareZoneName.value = cf.zoneName ?? ''
  cloudflareLocalService.value = cf.localService ?? 'localhost:80'
  cloudflareProtocol.value = (cf.protocol ?? 'http') as 'http' | 'https'
}, { immediate: true })

async function loadCfZones() {
  loadingCfZones.value = true
  try {
    const res = await fetchCloudflareZones()
    cfZones.value = res?.result ?? []
  } catch (e) {
    ElMessage.error(`Cannot load Cloudflare zones: ${errorMessage(e)}. Open the Cloudflare Tunnel page first to configure the API token.`)
  } finally {
    loadingCfZones.value = false
  }
}

// Load subdomain template from global Cloudflare config on mount so the
// auto-fill uses whatever prefix/suffix the user has configured.
async function loadCfSubdomainTemplate() {
  try {
    const cfg = await fetchCloudflareConfig()
    if (cfg?.subdomainTemplate) cfSubdomainTemplate.value = cfg.subdomainTemplate
  } catch { /* plugin not loaded — keep default */ }
}

/**
 * Render the subdomain template with the current site. Supports:
 *   {stem}  — local domain stripped of .loc/.local/.test
 *   {user}  — lowercased guess at the username (falls back to empty)
 * Keeps the template simple on purpose: users who need something fancier
 * can just edit the generated value in the subdomain input field.
 */
function renderSubdomainTemplate(): string {
  if (!site.value) return ''
  const stem = site.value.domain.replace(/\.(loc|local|test)$/i, '')
  // No reliable OS username source in a vanilla browser — Electron main
  // could expose it via preload, but defaulting to empty avoids a hard
  // dependency and keeps the default template ({stem}-dev) unambiguous.
  const user = ''
  return cfSubdomainTemplate.value
    .replace('{stem}', stem)
    .replace('{user}', user)
    // Collapse stray double dashes from empty placeholders and strip
    // leading/trailing ones so "{user}-{stem}" with empty user doesn't
    // produce "-myapp".
    .replace(/-+/g, '-')
    .replace(/^-+|-+$/g, '')
}

async function onCloudflareToggle(v: boolean) {
  // Disabling needs user confirmation — save flow will delete the CNAME
  // from Cloudflare and strip the ingress rule, which means the public
  // URL stops resolving immediately after the next save.
  if (!v && cloudflareSubdomain.value && cloudflareZoneName.value) {
    try {
      await ElMessageBox.confirm(
        `Disable Cloudflare Tunnel for ${site.value?.domain}? The public URL https://${cloudflareSubdomain.value}.${cloudflareZoneName.value} will be taken offline when you click Save.`,
        'Disable tunnel for this site',
        {
          confirmButtonText: 'Disable',
          cancelButtonText: 'Cancel',
          type: 'warning',
        },
      )
    } catch {
      // User cancelled — flip the switch back on
      cloudflareEnabled.value = true
      return
    }
  }

  markDirty()
  if (v && cfZones.value.length === 0) void loadCfZones()
  // Auto-fill the subdomain from the backend-rendered template (which
  // knows the install salt and computes a deterministic 6-char hash)
  // only if the user hasn't already typed something. Falls back to a
  // client-side render if the backend endpoint is unavailable.
  if (v && !cloudflareSubdomain.value && site.value) {
    try {
      const res = await suggestCloudflareSubdomain(site.value.domain)
      if (res.suggestion) cloudflareSubdomain.value = res.suggestion
      else cloudflareSubdomain.value = renderSubdomainTemplate()
    } catch {
      cloudflareSubdomain.value = renderSubdomainTemplate()
    }
  }
  // Auto-fill local service from the site's HTTP port
  if (v && site.value) {
    const port = site.value.httpPort || 80
    cloudflareLocalService.value = `localhost:${port}`
  }
}

function onZoneChange(zoneId: string) {
  const z = cfZones.value.find(x => x.id === zoneId)
  cloudflareZoneName.value = z?.name ?? ''
  markDirty()
}

// ── cloudflared service status (read-only indicator only) ──────────
// The Start/Stop button lives on the /cloudflare page so per-site edits
// can't accidentally kill other exposed sites.
const cloudflareRunning = computed(() => {
  const svc = daemonStore.services.find(s => s.id === 'cloudflare')
  return svc?.state === 2 || svc?.status === 'running'
})
const totalExposedCount = computed(() =>
  sitesStore.sites.filter(s => s.cloudflare?.enabled).length
)

// ── Auto-detect framework ──────────────────────────────────────────────
const detecting = ref(false)
async function detectFramework() {
  if (!site.value) return
  detecting.value = true
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/${site.value.domain}/detect-framework`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (!res.ok) throw new Error((await res.text().catch(() => '')) || `HTTP ${res.status}`)
    const data = await res.json()
    if (data.framework) {
      site.value.framework = data.framework
      ElMessage.success(`Detected: ${data.framework}`)
      markDirty()
    } else {
      ElMessage.info('No framework detected')
    }
  } catch (e) {
    ElMessage.error(`Detection failed: ${errorMessage(e)}`)
  } finally {
    detecting.value = false
  }
}

function markDirty() { dirty.value = true }

// F91.6: plugin-contributed tabs (SSL, Cloudflare) emit `update:site` when
// they edit a subset of the site record (e.g. sslEnabled, cloudflare.*).
// We merge the incoming partial into our reactive `site.value` so the save
// path + the rest of the UI observe the change immediately.
function onPluginSiteUpdate(updated: SiteInfo) {
  if (!site.value) return
  site.value = { ...site.value, ...updated }
  markDirty()
}

async function load() {
  loading.value = true
  try {
    await sitesStore.load()
    const found = sitesStore.sites.find(s => s.domain === domain.value)
    site.value = found ? { ...found, aliases: [...(found.aliases ?? [])] } : null
    dirty.value = false

    // PHP versions — authoritative list from daemon. Leave the array
    // empty on failure so the template's "no versions installed" hint
    // renders instead of lying about versions the user doesn't actually
    // have. The old fallback ['8.4','8.3','8.2'] hid the bug where the
    // daemon lost the PHP plugin state at boot — now the user sees the
    // real empty state and knows to install via the Binaries page.
    try {
      const versions = await fetchPhpVersions()
      phpVersions.value = versions.map(v => {
        const mm = v.majorMinor || v.version.split('.').slice(0, 2).join('.') || v.version
        return {
          value: mm,
          label: `PHP ${v.version}${v.isActive ? ' (active)' : ''}`,
          isActive: !!v.isActive,
        }
      })
    } catch { phpVersions.value = [] }

    // history
    try {
      const res = await fetch(`${daemonBaseUrl()}/api/sites/${domain.value}/history`, {
        headers: sitesStore.authHeaders(),
      })
      if (res.ok) history.value = await res.json() as Array<{ timestamp: string; label?: string }>
    } catch { /* optional */ }

    // Docker Compose detection (non-blocking)
    fetchDockerComposeStatus(domain.value)
      .then(s => { composeInfo.value = s.hasCompose ? s : null })
      .catch(() => { composeInfo.value = null })

    // Node process status (non-blocking)
    refreshNodeStatus()
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!site.value) return
  saving.value = true
  try {
    // Commit aliases from chip picker (no more comma-separated string)
    site.value.aliases = [...aliases.value]
    // Commit Node.js proxy port + start command from runtime tab
    if (selectedRuntime.value === 'node') {
      site.value.nodeUpstreamPort = nodeUpstreamPort.value
      site.value.nodeStartCommand = nodeStartCommand.value || ''
    } else {
      site.value.nodeUpstreamPort = 0
      site.value.nodeStartCommand = ''
    }
    // Commit Cloudflare sub-config from the dedicated tab. When disabled we
    // still send the object so the daemon can clear any previous ingress
    // rule on apply rather than leaving a stale CNAME pointing here.
    site.value.cloudflare = {
      enabled: cloudflareEnabled.value,
      subdomain: cloudflareSubdomain.value,
      zoneId: cloudflareZoneId.value,
      zoneName: cloudflareZoneName.value,
      localService: cloudflareLocalService.value,
      protocol: cloudflareProtocol.value,
    }
    await sitesStore.update(site.value.domain, site.value)
    ElMessage.success('Site updated')
    dirty.value = false
    // After apply, the orchestrator may have started/stopped a Node process.
    // Refresh the status pill so the UI reflects the current state.
    if (selectedRuntime.value === 'node') {
      void refreshNodeStatus()
    }
  } catch (e) {
    ElMessage.error(`Update failed: ${errorMessage(e)}`)
  } finally {
    saving.value = false
  }
}

async function rollback(timestamp: string) {
  if (!site.value) return
  try {
    await ElMessageBox.confirm(`Restore config from ${formatDate(timestamp)}?`, 'Restore', {
      type: 'warning',
      confirmButtonText: 'Restore',
    })
  } catch { return }
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/${site.value.domain}/rollback`, {
      method: 'POST',
      headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ timestamp }),
    })
    if (!res.ok) throw new Error((await res.text().catch(() => '')) || `HTTP ${res.status}`)
    ElMessage.success('Config restored')
    await load()
  } catch (e) {
    ElMessage.error(`Restore failed: ${errorMessage(e)}`)
  }
}

async function confirmDelete() {
  if (!site.value) return
  try {
    await ElMessageBox.confirm(
      `Delete site "${site.value.domain}"? This cannot be undone.`,
      'Confirm deletion',
      { type: 'warning', confirmButtonText: 'Delete', confirmButtonClass: 'el-button--danger' }
    )
    await sitesStore.remove(site.value.domain)
    ElMessage.success('Site deleted')
    router.push('/sites')
  } catch { /* user cancelled */ }
}

function openInBrowser() {
  if (!site.value) return
  const s = site.value
  const proto = s.sslEnabled ? 'https' : 'http'
  const port = s.sslEnabled ? (s.httpsPort || 443) : (s.httpPort || 80)
  const portSuffix = (s.sslEnabled && port === 443) || (!s.sslEnabled && port === 80) ? '' : `:${port}`
  window.open(`${proto}://${s.domain}${portSuffix}`, '_blank')
}

// ── Site metrics (Phase 11 performance monitoring) ────────────────────
const siteMetrics = ref<SiteMetrics | null>(null)
const metricsLoading = ref(false)
const metricsLastRefresh = ref<string | null>(null)
// Ticker that nudges Vue to re-render `formatAge(metricsLastRefresh)` every
// 5s so the "Last updated Xs ago" label doesn't freeze while the tab is open.
// Without this, formatAge only recomputes on refresh and users see stale
// relative-time text until they click Refresh or switch tabs.
const metricsTick = ref(0)
let metricsTickTimer: ReturnType<typeof setInterval> | null = null
let metricsPollTimer: ReturnType<typeof setInterval> | null = null

// Request-rate ring buffer — 60 samples at 5s interval = 5-minute rolling
// window. Each entry is requests-per-minute computed from the delta between
// consecutive raw-count polls. Phase 11 timeseries chart reads this directly.
const REQUEST_RATE_BUFFER_SIZE = 60
const REQUEST_RATE_POLL_INTERVAL_MS = 5000
const requestRateHistory = ref<number[]>([])
let lastRequestCount: number | null = null
let lastPollTimestamp: number | null = null

const currentRequestRate = computed(() => {
  const h = requestRateHistory.value
  return h.length > 0 ? Math.round(h[h.length - 1]) : 0
})

async function refreshMetrics() {
  if (!site.value) return
  // Re-entrancy guard: if a previous poll is still in-flight (e.g. slow
  // daemon), skip this tick instead of racing with ourselves on the ring
  // buffer. Without this guard two concurrent refreshes could both read
  // `lastRequestCount`, compute deltas against the same baseline, and
  // push duplicate/skewed samples that distort the timeseries.
  if (metricsLoading.value) return
  metricsLoading.value = true
  try {
    const m = await fetchSiteMetrics(site.value.domain)
    siteMetrics.value = m.hasMetrics ? m : null
    metricsLastRefresh.value = new Date().toISOString()

    // Populate the ring buffer with the delta since the previous sample,
    // normalized to requests-per-minute. The first sample primes the counter
    // so we don't push a bogus 0 spike on first load.
    const count = m?.accessLog?.requestCount ?? 0
    const now = Date.now()
    if (lastRequestCount !== null && lastPollTimestamp !== null && now > lastPollTimestamp) {
      const deltaCount = Math.max(0, count - lastRequestCount)
      const deltaSeconds = (now - lastPollTimestamp) / 1000
      const requestsPerMin = deltaSeconds > 0 ? (deltaCount * 60) / deltaSeconds : 0
      requestRateHistory.value.push(requestsPerMin)
      if (requestRateHistory.value.length > REQUEST_RATE_BUFFER_SIZE) {
        requestRateHistory.value.shift()
      }
    }
    lastRequestCount = count
    lastPollTimestamp = now
  } catch {
    // Transient network errors shouldn't wipe the metrics display — keep
    // the last good `siteMetrics` visible and just skip the sample.
    // Only clear on permanent failures (which we can't distinguish here,
    // so we rely on the user clicking Refresh manually to reset).
  }
  finally { metricsLoading.value = false }
}

function startMetricsTicker() {
  if (metricsTickTimer) return
  metricsTickTimer = setInterval(() => { metricsTick.value++ }, 5000)
  // Auto-poll the metrics endpoint every 5s to feed the ring buffer.
  // Kept separate from the tick timer so a future change to the display
  // cadence doesn't accidentally break timeseries accuracy.
  if (!metricsPollTimer) {
    metricsPollTimer = setInterval(() => { void refreshMetrics() }, REQUEST_RATE_POLL_INTERVAL_MS)
  }
}
function stopMetricsTicker() {
  if (metricsTickTimer) { clearInterval(metricsTickTimer); metricsTickTimer = null }
  if (metricsPollTimer) { clearInterval(metricsPollTimer); metricsPollTimer = null }
}

function formatNumber(n: number): string {
  return n.toLocaleString()
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function formatAge(iso?: string | null): string {
  if (!iso) return '—'
  const age = Date.now() - new Date(iso).getTime()
  const s = Math.floor(age / 1000)
  if (s < 60) return `${s}s ago`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ago`
  return `${Math.floor(h / 24)}d ago`
}

function goBack() {
  router.push('/sites')
}

function formatDate(s: string | number | undefined | null): string {
  // Task 18: robust timestamp parsing — daemon returns history timestamps
  // as either ISO-8601 strings, numeric unix seconds, or numeric unix
  // milliseconds. Previous `new Date(s).toLocaleString()` produced
  // "Invalid Date" whenever the value arrived as a number or as a
  // space-separated "YYYY-MM-DD HH:mm:ss" (Safari rejects the latter).
  if (s === null || s === undefined || s === '') return '—'
  let d: Date
  if (typeof s === 'number') {
    // Heuristic: unix seconds if under year-3000 threshold, else ms
    d = new Date(s < 1e12 ? s * 1000 : s)
  } else {
    // Replace space with 'T' to make ISO-ish strings parse everywhere
    const normalized = /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}/.test(s)
      ? s.replace(' ', 'T')
      : s
    d = new Date(normalized)
  }
  if (Number.isNaN(d.getTime())) return String(s)
  return d.toLocaleString()
}

// Reactive display of "Last updated Xs ago" — depends on metricsTick so Vue
// re-renders it on the 5s tick without any manual string interpolation.
const metricsAgeDisplay = computed(() => {
  void metricsTick.value // register dependency
  return formatAge(metricsLastRefresh.value)
})

// ── Historical metrics (Phase 7.2) ────────────────────────────────────

function todayString(): string {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const historicalDate = ref<string>(todayString())
const historicalGranularity = ref<string>('5m')
const historicalLoading = ref(false)
const historicalData = ref<HistoricalMetrics | null>(null)

const historicalSeriesOptions = [
  { key: 'requests', labelKey: 'metrics.historical.seriesRequests', color: '#6366f1' },
  { key: 'bytes',    labelKey: 'metrics.historical.seriesBytes',    color: '#10b981' },
  { key: 'errors',   labelKey: 'metrics.historical.seriesErrors',   color: '#ef4444' },
] as const

type SeriesKey = typeof historicalSeriesOptions[number]['key']

const historicalActiveSeries = ref<SeriesKey[]>(['requests'])

function toggleSeries(key: SeriesKey, checked: boolean) {
  if (checked) {
    if (!historicalActiveSeries.value.includes(key)) {
      historicalActiveSeries.value = [...historicalActiveSeries.value, key]
    }
  } else {
    historicalActiveSeries.value = historicalActiveSeries.value.filter(k => k !== key)
  }
}

function isDateDisabled(date: Date): boolean {
  const today = new Date()
  today.setHours(23, 59, 59, 999)
  return date > today
}

async function loadHistoricalMetrics() {
  if (!site.value) return
  historicalLoading.value = true
  try {
    historicalData.value = await getHistoricalMetrics(site.value.domain, {
      date: historicalDate.value,
      granularity: historicalGranularity.value,
    })
  } catch (e) {
    ElMessage.error(`Historical metrics failed: ${errorMessage(e)}`)
    historicalData.value = null
  } finally {
    historicalLoading.value = false
  }
}

function onHistoricalParamsChange() {
  void loadHistoricalMetrics()
}

const historicalIsEmptyDay = computed(() => {
  if (!historicalData.value) return false
  return historicalData.value.series.every(s => s.data.every(p => p.value === 0))
})

const SERIES_COLORS: Record<SeriesKey, string> = {
  requests: '#6366f1',
  bytes:    '#10b981',
  errors:   '#ef4444',
}

const historicalChartOption = computed(() => {
  const data = historicalData.value
  const activeSeries = historicalActiveSeries.value

  // Build xAxis labels from the first available series, or an empty array
  const firstSeries = data?.series[0]
  const xLabels: string[] = firstSeries
    ? firstSeries.data.map(p => {
        try {
          const d = new Date(p.ts)
          return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
        } catch {
          return p.ts
        }
      })
    : []

  const seriesDef = historicalSeriesOptions
    .filter(s => activeSeries.includes(s.key))
    .map(s => {
      const found = data?.series.find(sr => sr.name === s.key)
      const values: number[] = found ? found.data.map(p => p.value) : xLabels.map(() => 0)
      const color = SERIES_COLORS[s.key]
      return {
        name: s.key,
        type: 'line' as const,
        data: values,
        smooth: 0.2,
        symbol: 'none',
        lineStyle: { color, width: 1.5 },
        areaStyle: {
          color: {
            type: 'linear' as const,
            x: 0, y: 0, x2: 0, y2: 1,
            colorStops: [
              { offset: 0, color: color + '22' },
              { offset: 1, color: color + '03' },
            ],
          },
        },
      }
    })

  return {
    animation: false,
    tooltip: {
      trigger: 'axis',
      backgroundColor: '#1c1e2a',
      borderColor: 'rgba(255,255,255,0.12)',
      textStyle: { color: '#eceef6', fontSize: 11 },
    },
    legend: { show: false },
    grid: { top: 12, right: 16, bottom: 40, left: 48 },
    dataZoom: [
      { type: 'inside', start: 0, end: 100 },
      { type: 'slider', height: 20, bottom: 4, borderColor: 'transparent', fillerColor: 'rgba(99,102,241,0.12)' },
    ],
    xAxis: {
      type: 'category',
      data: xLabels,
      boundaryGap: false,
      axisLabel: {
        fontSize: 10,
        color: '#8b8fa8',
        interval: Math.max(0, Math.floor(xLabels.length / 12) - 1),
      },
      axisLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } },
      splitLine: { show: false },
    },
    yAxis: {
      type: 'value',
      min: 0,
      splitNumber: 3,
      axisLabel: {
        fontSize: 10,
        color: '#8b8fa8',
        formatter: (v: number) => v >= 1000 ? `${(v / 1000).toFixed(0)}k` : String(Math.round(v)),
      },
      splitLine: {
        lineStyle: { color: 'rgba(255,255,255,0.05)', type: 'dashed' },
      },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series: seriesDef,
  }
})

watch(domain, () => { void load() })

// Simple mode: redirect to 'general' if the active tab is one that is
// hidden in simple mode (e.g. a user deep-links to #danger or #history).
const ADVANCED_TABS = ['history', 'danger'] as const
watch(
  [() => uiMode.isSimple, activeTab],
  ([simple, tab]) => {
    if (simple && ADVANCED_TABS.includes(tab as typeof ADVANCED_TABS[number])) {
      activeTab.value = 'general'
    }
  },
  { immediate: true },
)

watch(activeTab, (tab) => {
  if (tab === 'metrics') {
    void refreshMetrics()
    startMetricsTicker()
    if (!historicalData.value) void loadHistoricalMetrics()
  } else {
    stopMetricsTicker()
  }
})
onMounted(() => {
  void load()
  void loadCfSubdomainTemplate()
  // If the page mounts with the Metrics tab already active (state restore
  // after reload), the activeTab watcher won't fire because the value hasn't
  // changed — so start the ticker and fetch metrics explicitly here.
  if (activeTab.value === 'metrics') {
    void refreshMetrics()
    startMetricsTicker()
    void loadHistoricalMetrics()
  }
})
onBeforeUnmount(() => {
  stopMetricsTicker()
})
</script>

<style scoped>
.site-edit-page {
  display: flex;
  flex-direction: column;
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 20px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  flex-shrink: 0;
}
.header-left {
  display: flex;
  align-items: center;
  gap: 14px;
}
.title-block {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.title-label {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--wdc-text-3);
}
.title-name {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.header-actions {
  display: flex;
  gap: 8px;
}

.state-box {
  padding: 32px 24px;
}

.edit-body {
  flex: 1;
  padding: 0 20px;
}

.site-tabs {
  margin-top: 8px;
}

/* F91.8: full-width, responsive tab bar. Each tab claims equal horizontal
   space with flex:1 so the header fills the whole row instead of hugging
   the left edge. The :deep() selector is required because the actual
   <el-tabs__*> elements live outside scoped-CSS reach.
   Scrollable fallback kicks in under 640px so narrow windows don't wrap
   into a 5-line tab header — horizontal scroll is the lesser evil. */
.site-tabs :deep(.el-tabs__header) { margin-bottom: 0; }
.site-tabs :deep(.el-tabs__nav-wrap) { width: 100%; padding: 0 4px; }
.site-tabs :deep(.el-tabs__nav-wrap::after) { background: var(--wdc-border); }
.site-tabs :deep(.el-tabs__nav) {
  display: flex;
  width: 100%;
  float: none; /* override Element Plus default float layout */
}
.site-tabs :deep(.el-tabs__active-bar) { /* keep the indicator aligned under full-width items */
  bottom: 0;
}
.site-tabs :deep(.el-tabs__item) {
  flex: 1 1 0;
  min-width: 0;
  padding: 0 6px;
  text-align: center;
  justify-content: center;
  display: inline-flex;
  align-items: center;
}
@media (max-width: 640px) {
  .site-tabs :deep(.el-tabs__nav-wrap) { overflow-x: auto; }
  .site-tabs :deep(.el-tabs__nav) {
    width: max-content;
    min-width: 100%;
  }
  .site-tabs :deep(.el-tabs__item) {
    flex: 0 0 auto;
    padding: 0 14px;
  }
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 0.88rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  min-width: 0;
}
.tab-label.danger-label {
  color: var(--wdc-status-error);
}

.tab-content {
  padding: 24px 4px 28px;
  display: flex;
  flex-direction: column;
  gap: 18px;
  max-width: 100%; /* F91.8: tab content fills container — no 960px cap */
  box-sizing: border-box;
}

/* Two-column form — collapses to single column under 800px so labels +
   inputs don't squash to unreadable widths on narrow windows. */
.two-col-form {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 16px 24px;
  max-width: 100%;
}
.two-col-form .el-form-item {
  margin-bottom: 0;
  min-width: 0; /* allow ellipsis instead of overflow */
}

/* Metrics subtab toolbar + historical chart: wrap instead of horizontal
   scroll when container narrows. */
.metrics-toolbar {
  flex-wrap: wrap;
  gap: 10px;
}
.historical-controls {
  flex-wrap: wrap;
  gap: 10px 16px;
}

/* SiteEdit page shell: cap total width but let content reflow below
   1100px. Previously a fixed 1000+ layout meant the window scrollbar
   appeared on any laptop-size screen. */
.site-edit-page {
  width: 100%;
  max-width: 100%;
  overflow-x: hidden;
}

@media (max-width: 900px) {
  .tab-content { padding: 16px 2px 20px; gap: 14px; }
  .edit-card-header { flex-wrap: wrap; gap: 6px; padding: 12px 14px; }
}

.hint {
  margin-top: 6px;
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  display: flex;
  align-items: center;
  gap: 6px;
}
.hint code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface-2);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--wdc-accent);
  font-size: 0.76rem;
}

/* ─── Card sections ──────────────────────────────────────────────────── */
.edit-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}
.edit-card-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  padding: 14px 20px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
}
.edit-card-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}
.edit-card-hint {
  font-size: 0.75rem;
  color: var(--wdc-text-3);
  font-weight: 500;
}
.edit-card-body {
  padding: 20px;
}

/* ─── Path input + Browse ────────────────────────────────────────────── */
.path-input {
  width: 100%;
  max-width: 100%;
  box-sizing: border-box;
}
.path-input :deep(.el-input__wrapper) {
  min-width: 0;
  flex: 1 1 0%;
}
.path-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.85rem;
  min-width: 0;
}
.path-input :deep(.el-input-group__append) {
  padding: 0 !important;
  background: var(--wdc-surface-2) !important;
  border-color: var(--wdc-border) !important;
  flex-shrink: 0;
}
.browse-append-btn {
  border: none !important;
  background: transparent !important;
  color: var(--wdc-accent) !important;
  font-weight: 700 !important;
  height: 100% !important;
  padding: 0 14px !important;
  box-shadow: none !important;
  white-space: nowrap;
  /* Icon + label sit flush; earlier CSS only gave the button 12px side
     padding + no inner gap, so at the default 13-14px system font the
     icon collided with the trailing ellipsis and pushed "Browse…" past
     the el-input-group__append clip. 14px padding + 6px flex gap holds
     the full label inside every Element Plus theme. */
  display: inline-flex !important;
  align-items: center;
  gap: 6px;
  min-width: fit-content;
}
.browse-append-btn > span {
  display: inline-block;
}
.browse-append-btn:hover {
  background: var(--wdc-accent-dim) !important;
}

/* ─── Form overflow guard ─────────────────────────────────────────────── */
.edit-card-body :deep(.el-form) {
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
}
.edit-card-body :deep(.el-form-item) {
  min-width: 0;
}
.edit-card-body :deep(.el-form-item__content) {
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
  overflow: hidden;
}
.edit-card-body :deep(.el-input-group) {
  display: flex;
  width: 100%;
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
  overflow: hidden;
}
.edit-card-body :deep(.el-input-group__prepend),
.edit-card-body :deep(.el-input-group__append) {
  flex-shrink: 0;
  max-width: 40%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  box-sizing: border-box;
}
.edit-card-body :deep(.el-input-group .el-input__wrapper) {
  flex: 1 1 0%;
  min-width: 0;
  max-width: 100%;
  box-sizing: border-box;
}

@media (max-width: 900px) {
  .edit-card-body :deep(.el-form--label-top .el-form-item) {
    flex-direction: column;
  }
}

/* ─── Alias chip picker ──────────────────────────────────────────────── */
.alias-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  min-height: 32px;
}
.alias-chip {
  font-family: 'JetBrains Mono', monospace !important;
  font-size: 0.78rem !important;
  font-weight: 600 !important;
  padding: 4px 10px !important;
  height: 28px !important;
  background: var(--wdc-accent) !important;
  border-color: var(--wdc-accent) !important;
  color: var(--wdc-bg) !important;
}
.alias-input {
  width: 220px;
}
.alias-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
}
.alias-add-btn {
  font-weight: 600 !important;
  border-style: dashed !important;
}

/* ─── Framework detect row ───────────────────────────────────────────── */
.framework-row {
  display: flex;
  gap: 8px;
  align-items: stretch;
}
.framework-input {
  flex: 1;
}

/* ─── Runtime picker cards ───────────────────────────────────────────── */
.runtime-picker {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 12px;
}
.runtime-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  padding: 18px 20px;
  background: var(--wdc-surface-2);
  border: 2px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  cursor: pointer;
  transition: border-color 0.12s, background 0.12s, transform 0.08s;
  text-align: left;
  font-family: inherit;
}
.runtime-card:hover:not(:disabled) {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-elevated);
}
.runtime-card.active {
  border-color: var(--wdc-accent);
  background: var(--wdc-accent-dim);
  box-shadow: 0 0 0 3px var(--wdc-accent-glow);
}
.runtime-card:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.runtime-card-icon {
  width: 44px;
  height: 44px;
  border-radius: var(--wdc-radius-sm);
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 800;
  font-size: 1.1rem;
  font-family: 'JetBrains Mono', monospace;
  margin-bottom: 4px;
}
.runtime-card-icon.runtime-static {
  background: var(--wdc-elevated);
  color: var(--wdc-text);
}
.runtime-card-icon.runtime-php {
  background: #4f5b93;
  color: #ffffff;
}
.runtime-card-icon.runtime-node {
  background: #3c873a;
  color: #ffffff;
}
.runtime-card-title {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.runtime-card-desc {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  line-height: 1.4;
}

/* ─── Node process status dot ───────────────────────────────────────── */
.status-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--wdc-text-3);
}
.status-dot.running { background: var(--el-color-success); }
.status-dot.crashed { background: var(--el-color-danger); }
.status-dot.stopped { background: var(--wdc-text-3); }

/* ─── PHP version sub-picker ─────────────────────────────────────────── */
.php-version-picker {
  margin-top: 20px;
  padding-top: 20px;
  border-top: 1px solid var(--wdc-border);
}
.sub-label {
  display: block;
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-2);
  margin-bottom: 10px;
}
.php-version-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.php-version-btn {
  min-width: 76px;
  padding: 10px 18px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  color: var(--wdc-text-2);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.92rem;
  font-weight: 700;
  cursor: pointer;
  transition: all 0.12s;
}
.php-version-btn:hover {
  border-color: var(--wdc-border-strong);
  color: var(--wdc-text);
}
.php-version-btn.active {
  background: #4f5b93;
  border-color: #4f5b93;
  color: #ffffff;
}

/* ─── Port grid ──────────────────────────────────────────────────────── */
.port-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  max-width: 520px;
}
.port-item {
  display: flex;
  flex-direction: column;
}

/* ─── Tunnel status row (inside Cloudflare tab) ──────────────────────── */
.tunnel-status-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  padding: 14px 18px;
  margin-top: 16px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.tunnel-status-meta { flex: 1; }
.tunnel-status-title {
  font-size: 0.95rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin-bottom: 4px;
}
.tunnel-pill {
  display: inline-block;
  padding: 2px 10px;
  border-radius: 10px;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.05em;
  margin-left: 6px;
}
.tunnel-pill-on {
  background: var(--wdc-status-running);
  color: var(--wdc-bg);
}
.tunnel-pill-off {
  background: var(--wdc-surface);
  color: var(--wdc-text-3);
  border: 1px solid var(--wdc-border);
}
.tunnel-shared-count {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  margin-left: 2px;
}
.tunnel-status-desc {
  font-size: 0.8rem;
  color: var(--wdc-text-3);
  line-height: 1.5;
}
.tunnel-status-desc code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--wdc-accent);
  font-size: 0.76rem;
}

.cf-subdomain-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.cf-zone-suffix {
  font-size: 0.85rem;
  color: var(--wdc-text-3);
  white-space: nowrap;
}

/* ─── SSL toggle rows ────────────────────────────────────────────────── */
.ssl-toggle-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 24px;
  padding: 14px 0;
}
.ssl-toggle-row + .ssl-toggle-row {
  border-top: 1px solid var(--wdc-border);
}
.ssl-toggle-meta {
  flex: 1;
}
.ssl-toggle-title {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--wdc-text);
  margin-bottom: 4px;
}
.ssl-toggle-desc {
  font-size: 0.8rem;
  color: var(--wdc-text-3);
  line-height: 1.4;
}

.history-banner {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 12px 14px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  font-size: 0.82rem;
  color: var(--wdc-text-2);
  line-height: 1.5;
  margin-bottom: 8px;
}
.history-banner .el-icon {
  color: var(--wdc-accent);
  margin-top: 2px;
  flex-shrink: 0;
}
.history-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  /* Task 18: full-width (was max-width 720px). */
}
.history-row {
  display: grid;
  grid-template-columns: 220px 1fr auto;
  align-items: center;
  gap: 16px;
  padding: 12px 16px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.history-when {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 0.82rem;
  color: var(--wdc-text-2);
}
.history-label {
  font-size: 0.85rem;
  color: var(--wdc-text);
  display: flex;
  align-items: center;
  gap: 10px;
}
.version-badge {
  background: var(--wdc-accent);
  color: white;
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 0.72rem;
  font-weight: 700;
  font-family: 'JetBrains Mono', monospace;
}
.version-hint {
  color: var(--wdc-text-3);
  font-style: italic;
  font-size: 0.78rem;
}

/* ─── Metrics cards ──────────────────────────────────────────────────── */
.metrics-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}
.metrics-timestamp {
  font-size: 0.8rem;
  color: var(--wdc-text-3);
}
.metrics-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 12px;
  margin-bottom: 16px;
}
.metrics-chart-wrap {
  margin-top: 18px;
  background: var(--wdc-surface-2);
  border-radius: var(--wdc-radius-sm);
  padding: 12px 16px;
}
.metrics-chart-label {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  margin-bottom: 8px;
}
.table-responsive {
  overflow-x: auto;
}
.metric-card {
  background: var(--wdc-surface-2);
  border-radius: var(--wdc-radius-sm);
  padding: 20px;
  text-align: center;
}
.metric-value {
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--wdc-text);
  font-family: 'JetBrains Mono', monospace;
}
.metric-label {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  margin-top: 4px;
}

/* ─── Compose output ─────────────────────────────────────────────────── */
.compose-output {
  margin-top: 12px;
  background: var(--wdc-surface-2);
  border-radius: var(--wdc-radius-sm);
  padding: 12px;
  max-height: 200px;
  overflow-y: auto;
}
.compose-output pre {
  margin: 0;
  font-size: 0.75rem;
  font-family: 'JetBrains Mono', monospace;
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--wdc-text-2);
}

.danger-box {
  padding: 24px 28px;
  background: rgba(239, 68, 68, 0.06);
  border: 2px solid rgba(239, 68, 68, 0.32);
  border-radius: var(--wdc-radius);
  max-width: 640px;
}
.danger-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-status-error);
  margin-bottom: 8px;
}
.danger-desc {
  font-size: 0.85rem;
  color: var(--wdc-text-2);
  margin-bottom: 14px;
}

/* ─── Historical metrics section ─────────────────────────────────────── */
.historical-section {
  margin-top: 24px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}
.historical-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px;
  padding: 12px 16px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
}
.historical-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
  flex-shrink: 0;
}
.historical-controls {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
}
.historical-control-item {
  display: flex;
  align-items: center;
  gap: 6px;
}
.historical-label {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--wdc-text-3);
  white-space: nowrap;
}
.historical-series-toggles {
  display: flex;
  gap: 6px;
  align-items: center;
}
.series-toggle {
  font-size: 0.72rem !important;
  font-weight: 600 !important;
  padding: 2px 8px !important;
  border-radius: 4px !important;
  cursor: pointer;
}
.historical-chart-wrap {
  position: relative;
  padding: 12px 8px 4px;
}
.historical-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  pointer-events: none;
  z-index: 1;
  background: transparent;
}
.historical-empty {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  font-style: italic;
}
</style>
