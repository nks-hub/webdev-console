<template>
  <div class="site-edit-page">
    <!-- Page header -->
    <div class="page-header">
      <div class="header-left">
        <el-button size="small" text @click="goBack">
          <el-icon><ArrowLeft /></el-icon>
          <span>Back to Sites</span>
        </el-button>
        <div class="title-block">
          <div class="title-label">Edit Site</div>
          <div class="title-name">{{ domain }}</div>
        </div>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openInBrowser" :disabled="!site">
          Open in Browser
        </el-button>
        <el-button
          type="primary"
          size="small"
          :loading="saving"
          :disabled="!dirty"
          @click="save"
        >
          Save &amp; Apply
        </el-button>
      </div>
    </div>

    <!-- Loading -->
    <div v-if="loading" class="state-box">
      <el-skeleton :rows="10" animated />
    </div>

    <!-- Not found -->
    <div v-else-if="!site" class="state-box">
      <el-empty :description="`Site '${domain}' not found.`" />
    </div>

    <!-- Content with tabs -->
    <div v-else class="edit-body">
      <el-tabs v-model="activeTab" class="site-tabs">
        <!-- ── General ──────────────────────────── -->
        <el-tab-pane name="general">
          <template #label>
            <span class="tab-label"><el-icon><Setting /></el-icon> General</span>
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
                      <template #prepend>🌐</template>
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
                  <el-tag size="small" type="info" effect="plain">🐳 {{ composeInfo.fileName }}</el-tag>
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
            <span class="tab-label"><el-icon><Cpu /></el-icon> Runtime</span>
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
                    <div class="runtime-card-icon runtime-static">⚡</div>
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
                      <span style="color: var(--el-color-warning)">⚠</span>
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
                      >Stop</el-button>
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
                      :key="v"
                      type="button"
                      class="php-version-btn"
                      :class="{ active: site.phpVersion === v }"
                      @click="setPhpVersion(v)"
                    >
                      {{ v }}
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

        <!-- ── Cloudflare Tunnel ────────────────── -->
        <el-tab-pane name="cloudflare">
          <template #label>
            <span class="tab-label"><el-icon><Link /></el-icon> Cloudflare</span>
          </template>
          <div class="tab-content">
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Expose via Cloudflare Tunnel</span>
                <span class="edit-card-hint">Public hostname → this local site</span>
              </header>
              <div class="edit-card-body">
                <div class="ssl-toggle-row">
                  <div class="ssl-toggle-meta">
                    <div class="ssl-toggle-title">Expose this site</div>
                    <div class="ssl-toggle-desc">
                      Enabling creates a proxied CNAME on your Cloudflare zone
                      and adds an ingress rule to the shared tunnel so this
                      site is reachable over HTTPS. Disabling deletes the
                      CNAME and removes the ingress rule, so the public
                      hostname stops resolving. Other exposed sites and the
                      tunnel process itself are not affected — this toggle
                      is strictly per-site.
                    </div>
                  </div>
                  <el-switch
                    v-model="cloudflareEnabled"
                    size="large"
                    @change="onCloudflareToggle"
                  />
                </div>

                <template v-if="cloudflareEnabled">
                  <!-- Read-only status indicator. Start/Stop of the shared
                       cloudflared process lives on the /cloudflare page
                       so users can't accidentally kill every exposed site
                       from a single site's settings. -->
                  <div class="tunnel-status-row tunnel-status-readonly">
                    <div class="tunnel-status-meta">
                      <div class="tunnel-status-title">
                        Shared tunnel status:
                        <span :class="['tunnel-pill', cloudflareRunning ? 'tunnel-pill-on' : 'tunnel-pill-off']">
                          {{ cloudflareRunning ? 'Running' : 'Stopped' }}
                        </span>
                        <span class="tunnel-shared-count" v-if="totalExposedCount > 0">
                          · {{ totalExposedCount }} site{{ totalExposedCount === 1 ? '' : 's' }} exposed
                        </span>
                      </div>
                      <div class="tunnel-status-desc">
                        <template v-if="cloudflareRunning">
                          The <code>cloudflared</code> process is online.
                          Your public URL is ready once you save.
                        </template>
                        <template v-else>
                          The shared tunnel is currently stopped.
                          Start it from the
                          <router-link to="/cloudflare">Cloudflare Tunnel page</router-link>
                          to make your public URL reachable.
                        </template>
                      </div>
                    </div>
                  </div>

                  <el-form label-position="top" size="default" style="margin-top: 20px;">
                    <el-form-item label="Zone (Cloudflare domain)">
                      <el-select
                        v-model="cloudflareZoneId"
                        placeholder="Pick a zone…"
                        :loading="loadingCfZones"
                        filterable
                        style="width: 100%"
                        @visible-change="(v: boolean) => v && cfZones.length === 0 && loadCfZones()"
                        @change="onZoneChange"
                      >
                        <el-option
                          v-for="z in cfZones"
                          :key="z.id"
                          :label="z.name"
                          :value="z.id"
                        />
                      </el-select>
                      <div class="hint" v-if="cfZones.length === 0 && !loadingCfZones">
                        Open the
                        <router-link to="/cloudflare">Cloudflare Tunnel page</router-link>
                        first to enter your API token.
                      </div>
                    </el-form-item>

                    <el-form-item label="Subdomain" v-if="cloudflareZoneId">
                      <div class="cf-subdomain-row">
                        <el-input
                          v-model="cloudflareSubdomain"
                          placeholder="blog"
                          class="mono"
                        />
                        <span class="cf-zone-suffix mono">.{{ selectedCfZoneName }}</span>
                      </div>
                      <div class="hint">
                        Full public URL:
                        <code>https://{{ cloudflareSubdomain || 'subdomain' }}.{{ selectedCfZoneName }}</code>
                      </div>
                    </el-form-item>

                    <el-form-item label="Local service">
                      <el-input
                        v-model="cloudflareLocalService"
                        class="mono"
                        placeholder="localhost:80"
                      >
                        <template #prepend>
                          <el-select
                            v-model="cloudflareProtocol"
                            style="width: 100px"
                          >
                            <el-option label="http://" value="http" />
                            <el-option label="https://" value="https" />
                          </el-select>
                        </template>
                      </el-input>
                      <div class="hint">
                        cloudflared will forward tunnel traffic to this address and
                        send <code>Host: {{ site.domain }}</code> so Apache's vhost matches.
                      </div>
                    </el-form-item>
                  </el-form>
                </template>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- ── SSL ──────────────────────────────── -->
        <el-tab-pane name="ssl">
          <template #label>
            <span class="tab-label"><el-icon><Lock /></el-icon> SSL</span>
          </template>
          <div class="tab-content">
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">HTTPS</span>
                <span class="edit-card-hint">Local certificates via mkcert</span>
              </header>
              <div class="edit-card-body">
                <div class="ssl-toggle-row">
                  <div class="ssl-toggle-meta">
                    <div class="ssl-toggle-title">Enable HTTPS</div>
                    <div class="ssl-toggle-desc">
                      Generates a locally-trusted certificate and binds an HTTPS vhost on port {{ site.httpsPort || 443 }}.
                    </div>
                  </div>
                  <el-switch v-model="site.sslEnabled" size="large" @change="markDirty" />
                </div>

                <div v-if="site.sslEnabled" class="ssl-toggle-row">
                  <div class="ssl-toggle-meta">
                    <div class="ssl-toggle-title">HTTP → HTTPS redirect</div>
                    <div class="ssl-toggle-desc">
                      Automatically redirect plain HTTP requests to the HTTPS version.
                    </div>
                  </div>
                  <el-switch v-model="redirectHttps" size="large" @change="markDirty" />
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- ── History ──────────────────────────── -->
        <el-tab-pane name="history">
          <template #label>
            <span class="tab-label"><el-icon><Clock /></el-icon> History ({{ history.length }})</span>
          </template>
          <div class="tab-content">
            <el-empty v-if="history.length === 0" description="No config history yet." :image-size="64" />
            <div v-else class="history-list">
              <div v-for="(h, i) in history" :key="h.timestamp" class="history-row">
                <div class="history-when">
                  <el-icon><Clock /></el-icon>
                  <span>{{ formatDate(h.timestamp) }}</span>
                </div>
                <div class="history-label">{{ h.label ?? `Version ${history.length - i}` }}</div>
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
            <span class="tab-label"><el-icon><DataLine /></el-icon> Metrics</span>
          </template>
          <div class="tab-content">
            <div v-if="!siteMetrics" class="hint" style="padding: 24px 0">
              <span v-if="metricsLoading">Loading metrics...</span>
              <span v-else>No access log data found for this site. Start Apache with the generated vhost and make some requests.</span>
            </div>
            <div v-else class="metrics-grid">
              <div class="metric-card">
                <div class="metric-value">{{ formatNumber(siteMetrics.accessLog?.requestCount ?? 0) }}</div>
                <div class="metric-label">Total Requests</div>
              </div>
              <div class="metric-card">
                <div class="metric-value">{{ formatSize(siteMetrics.accessLog?.sizeBytes ?? 0) }}</div>
                <div class="metric-label">Access Log Size</div>
              </div>
              <div class="metric-card">
                <div class="metric-value">{{ formatAge(siteMetrics.accessLog?.lastWriteUtc) }}</div>
                <div class="metric-label">Last Request</div>
              </div>
            </div>
            <div v-if="siteMetrics?.accessLog" class="hint" style="margin-top: 12px">
              <code>{{ siteMetrics.accessLog.path }}</code>
            </div>
          </div>
        </el-tab-pane>

        <!-- ── Danger ───────────────────────────── -->
        <el-tab-pane name="danger">
          <template #label>
            <span class="tab-label danger-label"><el-icon><WarningFilled /></el-icon> Danger</span>
          </template>
          <div class="tab-content">
            <div class="danger-box">
              <div class="danger-title">
                <el-icon><WarningFilled /></el-icon>
                Delete this site
              </div>
              <div class="danger-desc">
                Removes the vhost config and hosts file entry. Document root and databases are not touched.
              </div>
              <el-button type="danger" size="default" @click="confirmDelete">
                Delete {{ site.domain }}
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
  </div>
</template>

<script setup lang="ts">
// Named export so <keep-alive exclude="SiteEdit"> in App.vue can skip caching
// this page — SiteEdit is parametric by :domain and MUST refresh state on
// every navigation, unlike Dashboard/Sites/Binaries which benefit from cache.
defineOptions({ name: 'SiteEdit' })
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  ArrowLeft, Setting, Cpu, Lock, Clock, WarningFilled,
  FolderOpened, Check, Search, Link, DataLine,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import type { SiteInfo } from '../../api/types'
import FolderBrowser from '../shared/FolderBrowser.vue'
import {
  fetchCloudflareZones, fetchCloudflareConfig, suggestCloudflareSubdomain,
  fetchNodeSites, startNodeSite, stopNodeSite, restartNodeSite,
  fetchSiteMetrics, type SiteMetrics,
  fetchDockerComposeStatus, type DockerComposeStatus,
  composeUp, composeDown, composeRestart, composePs,
} from '../../api/daemon'

const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()

const domain = computed(() => String(route.params.domain || ''))

const site = ref<SiteInfo | null>(null)
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)
const activeTab = ref('general')
const phpVersions = ref<string[]>([])
const history = ref<Array<{ timestamp: string; label?: string }>>([])
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
  } catch (e: any) {
    composeOutput.value = e.message
    ElMessage.error(`Compose ${action} failed: ${e.message}`)
  } finally {
    composeLoading.value = false
  }
}
const redirectHttps = ref(true)

// ── Alias chip picker ──────────────────────────────────────────────────
const aliases = ref<string[]>([])
const aliasInput = ref('')
const aliasInputVisible = ref(false)
const aliasInputRef = ref<any>(null)
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
  } catch (e: any) {
    ElMessage.error(`Failed to start Node: ${e.message}`)
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
  } catch (e: any) {
    ElMessage.error(`Failed to stop Node: ${e.message}`)
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
  } catch (e: any) {
    ElMessage.error(`Failed to restart Node: ${e.message}`)
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
    site.value.phpVersion = phpVersions.value[0] ?? '8.4'
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
const cfZones = ref<any[]>([])
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
  } catch (e: any) {
    ElMessage.error(`Cannot load Cloudflare zones: ${e.message}. Open the Cloudflare Tunnel page first to configure the API token.`)
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
  const svc = daemonStore.services.find((s: any) => s.id === 'cloudflare')
  return svc?.state === 2 || svc?.status === 'running'
})
const totalExposedCount = computed(() =>
  sitesStore.sites.filter((s: any) => s.cloudflare?.enabled).length
)

// ── Auto-detect framework ──────────────────────────────────────────────
const detecting = ref(false)
async function detectFramework() {
  if (!site.value) return
  detecting.value = true
  try {
    const res = await fetch(`${daemonBase()}/api/sites/${site.value.domain}/detect-framework`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const data = await res.json()
    if (data.framework) {
      site.value.framework = data.framework
      ElMessage.success(`Detected: ${data.framework}`)
      markDirty()
    } else {
      ElMessage.info('No framework detected')
    }
  } catch (e: any) {
    ElMessage.error(`Detection failed: ${e.message}`)
  } finally {
    detecting.value = false
  }
}

function markDirty() { dirty.value = true }

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  if (urlPort && /^\d+$/.test(urlPort)) return `http://localhost:${urlPort}`
  const p = (window as any).daemonApi?.getPort?.()
  return `http://localhost:${typeof p === 'number' ? p : 5199}`
}

async function load() {
  loading.value = true
  try {
    await sitesStore.load()
    const found = sitesStore.sites.find(s => s.domain === domain.value)
    site.value = found ? { ...found, aliases: [...(found.aliases ?? [])] } : null
    dirty.value = false

    // php versions
    try {
      const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: sitesStore.authHeaders() })
      if (r.ok) {
        const versions = await r.json()
        phpVersions.value = versions.map((v: any) => v.majorMinor || v.version?.split('.').slice(0, 2).join('.') || v.version)
      }
    } catch { phpVersions.value = ['8.4', '8.3', '8.2'] }

    // history
    try {
      const res = await fetch(`${daemonBase()}/api/sites/${domain.value}/history`, {
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
  } catch (e: any) {
    ElMessage.error(`Update failed: ${e.message}`)
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
    const res = await fetch(`${daemonBase()}/api/sites/${site.value.domain}/rollback`, {
      method: 'POST',
      headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ timestamp }),
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    ElMessage.success('Config restored')
    await load()
  } catch (e: any) {
    ElMessage.error(`Restore failed: ${e.message}`)
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

async function refreshMetrics() {
  if (!site.value) return
  metricsLoading.value = true
  try {
    const m = await fetchSiteMetrics(site.value.domain)
    siteMetrics.value = m.hasMetrics ? m : null
  } catch { siteMetrics.value = null }
  finally { metricsLoading.value = false }
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

function formatDate(s: string): string {
  try { return new Date(s).toLocaleString() } catch { return s }
}

watch(domain, () => { void load() })
watch(activeTab, (tab) => { if (tab === 'metrics') void refreshMetrics() })
onMounted(() => {
  void load()
  void loadCfSubdomainTemplate()
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

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 0.88rem;
}
.tab-label.danger-label {
  color: var(--wdc-status-error);
}

.tab-content {
  padding: 24px 4px 28px;
  display: flex;
  flex-direction: column;
  gap: 18px;
  max-width: 960px;
}

.two-col-form {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px 24px;
  max-width: 880px;
}
.two-col-form .el-form-item {
  margin-bottom: 0;
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
}
.path-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.85rem;
}
.path-input :deep(.el-input-group__append) {
  padding: 0 !important;
  background: var(--wdc-surface-2) !important;
  border-color: var(--wdc-border) !important;
}
.browse-append-btn {
  border: none !important;
  background: transparent !important;
  color: var(--wdc-accent) !important;
  font-weight: 700 !important;
  height: 100% !important;
  padding: 0 16px !important;
  box-shadow: none !important;
}
.browse-append-btn:hover {
  background: var(--wdc-accent-dim) !important;
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

.history-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 720px;
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
}

/* ─── Metrics cards ──────────────────────────────────────────────────── */
.metrics-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
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
</style>
