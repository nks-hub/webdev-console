<template>
  <div class="cf-page">
    <!-- Page header -->
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('cloudflare.title') }}</h1>
        <span class="page-subtitle">
          {{ $t('cloudflare.subtitle') }}
        </span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openDocs">
          <el-icon><Link /></el-icon>
          <span>{{ $t('cloudflare.docs') }}</span>
        </el-button>
        <el-tooltip
          v-if="!pluginMissing"
          :disabled="configReady || serviceRunning"
          :content="$t('cloudflare.startNeedsSetup')"
          placement="bottom"
        >
          <span>
            <el-button
              size="small"
              :type="serviceRunning ? 'danger' : 'success'"
              :loading="toggling"
              :disabled="!daemonStore.connected || !configReady"
              @click="toggleTunnel"
            >
              {{ serviceRunning ? $t('cloudflare.stopTunnel') : $t('cloudflare.startTunnel') }}
            </el-button>
          </span>
        </el-tooltip>
      </div>
    </div>

    <LoadingState v-if="initialLoading" :label="$t('cloudflare.connecting')" />

    <PluginMissingState
      v-else-if="pluginMissing"
      plugin-id="nks.wdc.cloudflare"
      :title="$t('cloudflare.pluginMissingTitle')"
      :description="$t('cloudflare.pluginMissingDesc')"
    />

    <template v-else>
    <el-alert
      v-if="pageLoadError"
      type="error"
      :title="pageLoadError"
      :closable="true"
      @close="pageLoadError = ''"
      show-icon
      style="margin: 0 0 var(--wdc-space-4)"
    />

    <!-- Status strip -->
    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'"><CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else /></el-icon>
        <div class="status-body">
          <div class="status-title">
            {{ serviceRunning ? $t('cloudflare.status.tunnelRunning') : (serviceInfo ? $t('cloudflare.status.stopped') : $t('cloudflare.status.unknown')) }}
          </div>
          <div class="status-meta" v-if="serviceRunning && serviceInfo">
            {{ $t('cloudflare.status.pidUptime', { pid: serviceInfo.pid ?? '—', uptime: formatUptime(serviceInfo.uptimeSeconds ?? serviceInfo.uptime) }) }}
          </div>
          <div class="status-meta" v-else-if="!configReady">
            {{ $t('cloudflare.status.finishSetup') }}
          </div>
          <div class="status-meta" v-else-if="!serviceInfo">
            {{ $t('cloudflare.status.serviceInfoUnavailable') }}
          </div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Key /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ tokenState }}</div>
          <div class="status-meta">{{ config.apiToken ? $t('cloudflare.status.tokenStored') : $t('cloudflare.status.tokenMissingHint') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Connection /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ config.tunnelName || $t('cloudflare.status.noTunnel') }}</div>
          <div class="status-meta mono" v-if="config.tunnelId">{{ config.tunnelId.slice(0, 18) }}…</div>
          <div class="status-meta" v-else>{{ $t('cloudflare.status.configureBelow') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Location /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('cloudflare.status.zoneCount', { n: zones.length }) }}</div>
          <div class="status-meta">{{ config.defaultZoneId ? $t('cloudflare.status.defaultSelected') : $t('cloudflare.status.pickDefaultZone') }}</div>
        </div>
      </div>
    </div>

    <!-- Tabs -->
    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- ═══ Setup (one-token flow) ════════════════ -->
      <el-tab-pane name="settings">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('cloudflare.tabs.setup') }}</span>
        </template>

        <div class="tab-content">
          <EditCard :title="$t('cloudflare.steps.step1Title')" :hint="$t('cloudflare.steps.step1Hint')">
              <el-input
                v-model="apiTokenInput"
                type="password"
                show-password
                :placeholder="apiTokenMasked || $t('cloudflare.tokenPlaceholder')"
                class="mono"
              />
              <div class="hint">
                {{ $t('cloudflare.requiredScopes') }}
                <code>Account &gt; Cloudflare Tunnel &gt; Edit</code>,
                <code>Account &gt; Account Settings &gt; Read</code>,
                <code>Zone &gt; Zone &gt; Read</code>,
                <code>Zone &gt; DNS &gt; Edit</code>.
                {{ $t('cloudflare.createTokenAt') }}
                <a href="#" @click.prevent="openTokenPage">dash.cloudflare.com/profile/api-tokens</a>.
              </div>
              <div class="card-actions">
                <el-button
                  type="primary"
                  size="default"
                  :loading="autoSettingUp"
                  :disabled="!apiTokenInput"
                  @click="runAutoSetup"
                >
                  <el-icon><Check /></el-icon>
                  <span>{{ $t('cloudflare.actions.autoSetup') }}</span>
                </el-button>
                <span v-if="autoSetupResult" class="save-status ok">
                  <el-icon><CircleCheckFilled /></el-icon> {{ $t('cloudflare.autoSetupDone', { account: autoSetupResult.account.name, tunnel: autoSetupResult.tunnel.name }) }}
                </span>
                <span v-if="saveStatus && saveStatus.kind === 'err'" class="save-status err">
                  {{ saveStatus.message }}
                </span>
              </div>
              <div class="hint" style="margin-top: var(--wdc-space-3);">
                {{ $t('cloudflare.autoSetupExplain', { tunnel: 'NKS-WDC-Tunnel-…', file: '~/.wdc/cloudflare/config.json' }) }}
              </div>
          </EditCard>

          <EditCard :title="$t('cloudflare.steps.step2Title')" :hint="$t('cloudflare.steps.step2Hint')">
              <el-input
                v-model="config.cloudflaredPath"
                :placeholder="$t('cloudflare.binaryPathPlaceholder')"
                class="mono"
              >
                <template #append>
                  <el-button @click="showFolderBrowser = true">{{ $t('cloudflare.actions.browse') }}</el-button>
                </template>
              </el-input>
              <div class="card-actions">
                <el-button size="small" :loading="savingConfig" @click="saveBinaryPath">
                  {{ $t('cloudflare.savePathBtn') }}
                </el-button>
                <el-button
                  v-if="!config.cloudflaredPath"
                  size="small"
                  type="primary"
                  :loading="installingCloudflared"
                  @click="installCloudflared"
                >
                  {{ $t('cloudflare.installCloudflared') }}
                </el-button>
              </div>
          </EditCard>

          <EditCard :title="$t('cloudflare.steps.subdomainTemplate')" :hint="$t('cloudflare.steps.subdomainTemplateHint')">
              <el-input
                v-model="config.subdomainTemplate"
                class="mono"
                :placeholder="DEFAULT_SUBDOMAIN_TEMPLATE"
              />
              <div class="hint">
                {{ $t('cloudflare.subdomainPlaceholders') }}
                <code>{stem}</code> {{ $t('cloudflare.subdomainStem', { loc: '.loc', local: '.local', test: '.test' }) }},
                <code>{hash}</code> {{ $t('cloudflare.subdomainHash') }},
                <code>{user}</code> {{ $t('cloudflare.subdomainUser') }}.
                {{ $t('cloudflare.subdomainExample', { domain: 'myapp.loc' }) }}
                <code>{{ previewTemplate }}</code>
              </div>
              <div class="default-zone">
                <label class="default-zone-label" for="cf-default-zone">{{ $t('cloudflare.defaultZone') }}</label>
                <el-select
                  id="cf-default-zone"
                  v-model="config.defaultZoneId"
                  :placeholder="config.apiToken ? $t('cloudflare.pickZone') : $t('cloudflare.defaultZoneNeedsToken')"
                  :disabled="!config.apiToken"
                  :loading="loadingZones"
                  filterable
                  class="default-zone-select"
                  @change="saveDefaultZone"
                >
                  <el-option v-for="z in zones" :key="z.id" :label="z.name" :value="z.id" />
                </el-select>
                <div class="hint">{{ $t('cloudflare.defaultZoneHint') }}</div>
              </div>
              <div class="card-actions">
                <el-button size="small" :loading="savingConfig" @click="saveSubdomainTemplate">
                  {{ $t('cloudflare.saveTemplateBtn') }}
                </el-button>
              </div>
          </EditCard>

          <EditCard :title="$t('cloudflare.steps.step3Title')" :hint="$t('cloudflare.steps.step3Hint')">
              <div class="ssl-toggle-row">
                <div class="ssl-toggle-meta">
                  <div class="ssl-toggle-title">
                    {{ serviceRunning ? t('cloudflare.tunnelRunning') : t('cloudflare.tunnelStopped') }}
                  </div>
                  <div class="ssl-toggle-desc">
                    {{ t('cloudflare.tunnelHint') }}
                  </div>
                </div>
                <el-button
                  size="default"
                  :type="serviceRunning ? 'danger' : 'success'"
                  :loading="toggling"
                  :disabled="!configReady"
                  @click="toggleTunnel"
                >
                  {{ serviceRunning ? $t('cloudflare.stopTunnel') : $t('cloudflare.startTunnel') }}
                </el-button>
              </div>
          </EditCard>
        </div>
      </el-tab-pane>

      <!-- ═══ Sites — per-site exposure ═════════════ -->
      <el-tab-pane name="ingress">
        <template #label>
          <span class="tab-label"><el-icon><Share /></el-icon> {{ $t('cloudflare.tabs.exposedSites', { n: exposedSites.length }) }}</span>
        </template>
        <div class="tab-content">
          <EditCard :title="$t('cloudflare.exposedSites')" :hint="$t('cloudflare.exposedSitesHint')">
              <div v-if="exposedSites.length === 0" class="hint">{{ $t('cloudflare.noSitesExposed') }}</div>
              <div v-else class="site-expose-list">
                <div
                  v-for="s in exposedSites"
                  :key="s.domain"
                  class="site-expose-row"
                >
                  <div class="expose-local mono">{{ s.domain }}</div>
                  <el-icon class="arrow"><Right /></el-icon>
                  <div class="expose-public mono">
                    https://{{ s.cloudflare?.subdomain }}.{{ s.cloudflare?.zoneName }}
                  </div>
                  <el-tag
                    size="small"
                    type="success"
                    effect="dark"
                    class="expose-tag"
                  >{{ $t('cloudflare.proxied') }}</el-tag>
                </div>
              </div>

              <div class="card-actions">
                <el-button
                  size="default"
                  type="primary"
                  :loading="syncing"
                  :disabled="exposedSites.length === 0 || !config.tunnelId"
                  @click="syncAllSites"
                >
                  <el-icon><Check /></el-icon>
                  <span>{{ $t('cloudflare.syncAllSites', { n: exposedSites.length }) }}</span>
                </el-button>
                <span v-if="syncStatus" class="save-status" :class="syncStatus.kind">
                  {{ syncStatus.message }}
                </span>
              </div>
              <div class="hint" style="margin-top: var(--wdc-space-2-5);">
                {{ config.tunnelId
                  ? $t('cloudflare.syncExplain', { target: `${config.tunnelId.slice(0, 8)}….cfargotunnel.com` })
                  : $t('cloudflare.syncNeedsTunnel') }}
              </div>
          </EditCard>
        </div>
      </el-tab-pane>

      <!-- ═══ DNS records ════════════════════════════ -->
      <el-tab-pane name="dns">
        <template #label>
          <span class="tab-label"><el-icon><Postcard /></el-icon> {{ $t('cloudflare.tabs.dns') }}</span>
        </template>
        <el-empty v-if="!config.apiToken" :image-size="64" class="cf-dns-gate">
          <template #description>
            <p>{{ $t('cloudflare.dnsNeedsToken') }}</p>
          </template>
          <el-button type="primary" @click="activeTab = 'settings'">{{ $t('cloudflare.goToStep1') }}</el-button>
        </el-empty>
        <div v-else class="tab-content">
          <EditCard :title="$t('cloudflare.dnsRecords')" :hint="activeZone?.name || $t('cloudflare.pickZone')">
              <el-select
                v-model="selectedDnsZoneId"
                :placeholder="$t('cloudflare.zoneSelect')"
                :loading="loadingZones"
                filterable
                @change="loadDns"
              >
                <el-option
                  v-for="z in zones"
                  :key="z.id"
                  :label="z.name"
                  :value="z.id"
                />
              </el-select>

              <el-table
                v-if="dnsRecords.length"
                :data="dnsRecords"
                size="small"
                class="dns-table"
                style="margin-top: var(--wdc-space-3-5)"
              >
                <el-table-column prop="type" :label="$t('cloudflare.type')" width="90">
                  <template #default="{ row }">
                    <el-tag size="small" effect="dark">{{ row.type }}</el-tag>
                  </template>
                </el-table-column>
                <el-table-column prop="name" :label="$t('cloudflare.name')" min-width="180" />
                <el-table-column prop="content" :label="$t('cloudflare.content')" min-width="220">
                  <template #default="{ row }">
                    <span class="mono">{{ row.content }}</span>
                  </template>
                </el-table-column>
                <el-table-column :label="$t('cloudflare.proxied')" width="120" align="center">
                  <template #default="{ row }">
                    <el-tag v-if="row.proxied" size="small" type="warning" effect="dark">{{ $t('cloudflare.proxied') }}</el-tag>
                    <el-tag v-else size="small" effect="plain">{{ $t('cloudflare.dnsOnly') }}</el-tag>
                  </template>
                </el-table-column>
                <el-table-column :label="$t('common.actions')" width="100">
                  <template #default="{ row }">
                    <el-button
                      size="small"
                      type="danger"
                      plain
                      @click="deleteDnsRecord(row)"
                    >
                      {{ $t('common.delete') }}
                    </el-button>
                  </template>
                </el-table-column>
              </el-table>
              <div v-else-if="selectedDnsZoneId && !loadingDns" class="hint">{{ $t('cloudflare.noRecords') }}</div>
          </EditCard>

          <EditCard
            :title="$t('cloudflare.addDnsRecord')"
            :hint="$t('cloudflare.addDnsHint', { target: tunnelCnameTarget })"
          >
            <el-form :inline="true" size="default">
                <el-form-item :label="$t('cloudflare.type')">
                  <el-select v-model="newDns.type" style="width: 100px">
                    <el-option label="CNAME" value="CNAME" />
                    <el-option label="A" value="A" />
                    <el-option label="AAAA" value="AAAA" />
                    <el-option label="TXT" value="TXT" />
                  </el-select>
                </el-form-item>
                <el-form-item :label="$t('cloudflare.name')">
                  <el-input v-model="newDns.name" placeholder="blog" style="width: 180px" />
                </el-form-item>
                <el-form-item :label="$t('cloudflare.content')">
                  <el-input
                    v-model="newDns.content"
                    :placeholder="newDns.type === 'CNAME' ? tunnelCnameTarget : '…'"
                    style="width: 320px"
                    class="mono"
                  />
                </el-form-item>
                <el-form-item>
                  <el-switch v-model="newDns.proxied" :active-text="$t('cloudflare.proxied')" />
                </el-form-item>
                <el-form-item>
                  <el-button type="primary" :loading="creatingDns" @click="createDnsRecord">
                    {{ $t('cloudflare.create') }}
                  </el-button>
                </el-form-item>
              </el-form>
          </EditCard>
        </div>
      </el-tab-pane>
    </el-tabs>

    </template>

    <FolderBrowser
      v-model="showFolderBrowser"
      :initial-path="config.cloudflaredPath || undefined"
      @select="onPickBinary"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Setting, Check, Share, Right, Postcard, Link,
  CircleCheckFilled, CircleClose, Key, Connection, Location,
} from '@element-plus/icons-vue'

const { t } = useI18n()
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import FolderBrowser from '../shared/FolderBrowser.vue'
import LoadingState from '../shared/LoadingState.vue'
import EditCard from '../shared/EditCard.vue'
import PluginMissingState from '../shared/PluginMissingState.vue'
import {
  fetchCloudflareConfig, saveCloudflareConfig,
  verifyCloudflareToken, fetchCloudflareZones,
  fetchCloudflareDns, createCloudflareDns, deleteCloudflareDns,
  cloudflareAutoSetup, cloudflareSync, fetchLatestBinary, installBinary,
  DaemonError,
  type CloudflareConfig, type CloudflareAutoSetupResult,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { installErrorMessage } from '../../utils/binaries'

defineOptions({ name: 'CloudflareTunnel' })

// Same default as the Cloudflare plugin's CloudflareConfig.SubdomainTemplate.
const DEFAULT_SUBDOMAIN_TEMPLATE = '{stem}-{hash}'

const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()

const activeTab = ref<'settings' | 'ingress' | 'dns'>('settings')

// ── Config state ─────────────────────────────────────────────────────
const config = reactive<CloudflareConfig>({})
const pageLoadError = ref('')
const initialLoading = ref(true)
const apiTokenInput = ref('')
// The daemon answers 404 on every /api/cloudflare route without the plugin.
const pluginMissing = ref(false)

const apiTokenMasked = computed(() => config.apiToken || '')

const savingConfig = ref(false)
const saveStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

async function loadConfig() {
  pageLoadError.value = ''
  try {
    const res = await fetchCloudflareConfig()
    Object.assign(config, res)
    pluginMissing.value = false
  } catch (e) {
    if (e instanceof DaemonError && e.status === 404) pluginMissing.value = true
    else pageLoadError.value = t('cloudflare.loadFailed', { err: errorMessage(e) })
  }
}

async function saveBinaryPath() {
  savingConfig.value = true
  try {
    await saveCloudflareConfig({ cloudflaredPath: config.cloudflaredPath || null })
    ElMessage.success(t('cloudflare.toast.binaryPathSaved'))
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.saveFailed', { err: errorMessage(e) }))
  } finally {
    savingConfig.value = false
  }
}

async function saveSubdomainTemplate() {
  savingConfig.value = true
  try {
    await saveCloudflareConfig({ subdomainTemplate: config.subdomainTemplate || DEFAULT_SUBDOMAIN_TEMPLATE })
    ElMessage.success(t('cloudflare.toast.subdomainTemplateSaved'))
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.saveFailed', { err: errorMessage(e) }))
  } finally {
    savingConfig.value = false
  }
}

// A new site created with the tunnel on takes its zone from here; nothing else sets it.
async function saveDefaultZone(zoneId: string) {
  try {
    await saveCloudflareConfig({ defaultZoneId: zoneId || null })
    ElMessage.success(t('cloudflare.toast.defaultZoneSaved'))
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.saveFailed', { err: errorMessage(e) }))
  }
}

// Live preview of how the template resolves for the stem "myapp".
// {hash} is rendered as a fixed example — the real value comes from
// backend where the install salt lives, but that's fine for a preview.
const previewTemplate = computed(() => {
  const t = config.subdomainTemplate || DEFAULT_SUBDOMAIN_TEMPLATE
  const sub = t
    .replace('{stem}', 'myapp')
    .replace('{hash}', 'bffa44')
    .replace('{user}', 'me')
    .replace(/-+/g, '-')
    .replace(/^-+|-+$/g, '')
  const zone = zones.value.find(z => z.id === config.defaultZoneId)?.name
  return zone ? `${sub}.${zone}` : sub
})

// ── Auto-setup (one-token flow) ──────────────────────────────────────
const autoSettingUp = ref(false)
const autoSetupResult = ref<CloudflareAutoSetupResult | null>(null)

async function runAutoSetup() {
  autoSettingUp.value = true
  saveStatus.value = null
  try {
    // The stored token is redacted, so setup always needs a freshly pasted one;
    // the button stays disabled until the field has text.
    const token = apiTokenInput.value || ''
    if (!token) {
      ElMessage.warning(t('cloudflare.toast.pasteTokenFirst'))
      return
    }
    autoSetupResult.value = await cloudflareAutoSetup(token)
    apiTokenInput.value = ''
    await loadConfig() // reload to see redacted token + populated account/tunnel IDs
    tokenVerdict.value = 'ok'
    ElMessage.success(t('cloudflare.toast.autoSetupComplete', { name: autoSetupResult.value.tunnel.name }))
    void loadZones()
  } catch (e) {
    saveStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(t('cloudflare.toast.autoSetupFailed', { err: errorMessage(e) }))
  } finally {
    autoSettingUp.value = false
  }
}

function openTokenPage() {
  window.open('https://dash.cloudflare.com/profile/api-tokens', '_blank')
}

// ── Per-site exposed sites (from site configs) ───────────────────────
const exposedSites = computed(() =>
  sitesStore.sites.filter(s =>
    s.cloudflare?.enabled && s.cloudflare?.subdomain && s.cloudflare?.zoneName
  )
)

const syncing = ref(false)
const syncStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

async function syncAllSites() {
  syncing.value = true
  syncStatus.value = null
  try {
    const res = await cloudflareSync()
    syncStatus.value = { kind: 'ok', message: t('cloudflare.syncedSites', { n: res.synced }, res.synced) }
    ElMessage.success(t('cloudflare.toast.synced', { n: res.synced }))
  } catch (e) {
    syncStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(t('cloudflare.toast.syncFailed', { err: errorMessage(e) }))
  } finally {
    syncing.value = false
  }
}

// ── Token verify ─────────────────────────────────────────────────────
const tokenVerdict = ref<'unknown' | 'ok' | 'fail'>('unknown')
const tokenState = computed(() => {
  if (tokenVerdict.value === 'ok') return t('cloudflare.status.tokenVerified')
  if (tokenVerdict.value === 'fail') return t('cloudflare.status.tokenInvalid')
  return config.apiToken ? t('cloudflare.status.tokenStoredUnverified') : t('cloudflare.status.tokenMissing')
})

// ── Zones ────────────────────────────────────────────────────────────
// Minimal Cloudflare zone shape — we only display id/name, upstream
// response has many more fields we don't bind.
interface CfZone {
  id: string
  name: string
}
const zones = ref<CfZone[]>([])
const loadingZones = ref(false)

async function loadZones() {
  loadingZones.value = true
  try {
    const res = await fetchCloudflareZones()
    zones.value = res?.result ?? []
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.listZonesFailed', { err: errorMessage(e) }))
  } finally {
    loadingZones.value = false
  }
}

const activeZone = computed(() =>
  zones.value.find(z => z.id === selectedDnsZoneId.value)
)

// ── Service status (from daemon store) ───────────────────────────────
const serviceInfo = computed(() =>
  daemonStore.services.find(s => s.id === 'cloudflare')
)
const serviceRunning = computed(() =>
  serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running'
)
const configReady = computed(() =>
  !!(config.cloudflaredPath && config.tunnelToken)
)

const toggling = ref(false)
async function toggleTunnel() {
  toggling.value = true
  try {
    const action = serviceRunning.value ? 'stop' : 'start'
    const { startService, stopService } = await import('../../api/daemon')
    if (action === 'start') await startService('cloudflare')
    else await stopService('cloudflare')
  } catch (e) {
    ElMessage.error(t(serviceRunning.value ? 'cloudflare.toast.stopFailed' : 'cloudflare.toast.startFailed', { err: errorMessage(e) }))
  } finally {
    toggling.value = false
  }
}

// ── DNS records ──────────────────────────────────────────────────────
// Minimal shape of the Cloudflare /zones/:id/dns_records entries the
// table template reads. The upstream response has many more fields
// (ttl, meta, locked, created_on, …) but pinning just the ones we
// actually render catches typos without typing the whole v4 schema.
interface CfDnsRecord {
  id: string
  type: string
  name: string
  content: string
  proxied: boolean
}
const selectedDnsZoneId = ref<string>('')
const dnsRecords = ref<CfDnsRecord[]>([])
const loadingDns = ref(false)
const creatingDns = ref(false)
const newDns = reactive<{ type: string; name: string; content: string; proxied: boolean }>({
  type: 'CNAME',
  name: '',
  content: '',
  proxied: true,
})

const tunnelCnameTarget = computed(() =>
  config.tunnelId ? `${config.tunnelId}.cfargotunnel.com` : '<tunnel-id>.cfargotunnel.com'
)

async function loadDns() {
  if (!selectedDnsZoneId.value) return
  loadingDns.value = true
  try {
    const res = await fetchCloudflareDns(selectedDnsZoneId.value)
    dnsRecords.value = res?.result ?? []
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.loadDnsFailed', { err: errorMessage(e) }))
  } finally {
    loadingDns.value = false
  }
}

async function createDnsRecord() {
  if (!selectedDnsZoneId.value) {
    ElMessage.warning(t('cloudflare.toast.pickZoneFirst'))
    return
  }
  creatingDns.value = true
  try {
    const content = newDns.content || (newDns.type === 'CNAME' ? tunnelCnameTarget.value : '')
    await createCloudflareDns(selectedDnsZoneId.value, {
      type: newDns.type,
      name: newDns.name,
      content,
      proxied: newDns.proxied,
    })
    ElMessage.success(t('cloudflare.toast.dnsCreated', { type: newDns.type, name: newDns.name }))
    newDns.name = ''
    newDns.content = ''
    await loadDns()
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.dnsCreateFailed', { err: errorMessage(e) }))
  } finally {
    creatingDns.value = false
  }
}

async function deleteDnsRecord(row: CfDnsRecord) {
  try {
    await ElMessageBox.confirm(
      t('cloudflare.toast.dnsDeleteConfirmMessage', { type: row.type, name: row.name }),
      t('cloudflare.toast.dnsDeleteConfirmTitle'),
      { type: 'warning', confirmButtonText: t('cloudflare.toast.dnsDeleteBtn'), cancelButtonText: t('common.cancel') }
    )
  } catch {
    // ElMessageBox rejects with 'cancel'/'close' when dismissed — that's fine.
    return
  }
  try {
    await deleteCloudflareDns(selectedDnsZoneId.value, row.id)
    ElMessage.success(t('cloudflare.toast.dnsDeleted'))
    await loadDns()
  } catch (e) {
    ElMessage.error(t('cloudflare.toast.dnsDeleteFailed', { err: errorMessage(e) }))
  }
}

// ── cloudflared binary ───────────────────────────────────────────────
const installingCloudflared = ref(false)

async function installCloudflared() {
  installingCloudflared.value = true
  let version = ''
  try {
    version = (await fetchLatestBinary('cloudflared')).version
    const installed = await installBinary('cloudflared', version) as { executable?: string | null }
    if (installed?.executable) {
      config.cloudflaredPath = installed.executable
      await saveCloudflareConfig({ cloudflaredPath: installed.executable })
    }
    ElMessage.success(t('cloudflare.toast.cloudflaredInstalled', { version }))
  } catch (e) {
    ElMessage.error({ message: installErrorMessage((k, n) => t(k, n ?? {}), e, 'cloudflared', version || '?'), duration: 8000, showClose: true })
  } finally {
    installingCloudflared.value = false
  }
}

// ── Folder browser for binary path ───────────────────────────────────
const showFolderBrowser = ref(false)
function onPickBinary(path: string) {
  config.cloudflaredPath = path
}

function openDocs() {
  window.open(
    'https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/',
    '_blank'
  )
}

// Seconds, or the daemon's TimeSpan string "[d.]hh:mm:ss[.fffffff]".
function formatUptime(value: number | string | undefined): string {
  let secs = typeof value === 'number' ? value : 0
  if (typeof value === 'string') {
    const m = value.match(/^(?:(\d+)\.)?(\d+):(\d+):(\d+)/)
    secs = m ? Number(m[1] ?? 0) * 86400 + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]) : Number(value) || 0
  }
  if (!secs || secs < 1) return '—'
  const h = Math.floor(secs / 3600)
  const m = Math.floor((secs % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

onMounted(async () => {
  await loadConfig()
  initialLoading.value = false
  if (pluginMissing.value) return
  if (sitesStore.sites.length === 0) void sitesStore.load()
  if (config.apiToken) {
    void loadZones()
    if (config.defaultZoneId) {
      selectedDnsZoneId.value = config.defaultZoneId
      void loadDns()
    }
    // Task 37: auto-verify stored token on mount so "Token stored (not
    // verified)" doesn't linger. Silent background probe — failures stay
    // visible via tokenVerdict badge, user can re-verify manually.
    void autoVerifyToken()
  }
})

async function autoVerifyToken() {
  try {
    const res = await verifyCloudflareToken()
    tokenVerdict.value = res?.success ? 'ok' : 'fail'
  } catch {
    tokenVerdict.value = 'fail'
  }
}
</script>

<style scoped>
.cf-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 0 var(--wdc-space-4);
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
}
.header-left { display: flex; flex-direction: column; gap: 2px; }
.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0;
  letter-spacing: -0.02em;
}
.page-subtitle {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.header-actions { display: flex; gap: 8px; }

.status-strip {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-4-5) 0 var(--wdc-space-1);
}
.status-card {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3-5);
  padding: var(--wdc-space-4-5) var(--wdc-space-4-5);
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
}
.status-card.status-active {
  border-color: var(--wdc-status-running);
}
.status-icon {
  font-size: 1.4rem;
  width: 30px;
  text-align: center;
  color: var(--wdc-text-3);
}
.status-active .status-icon {
  color: var(--wdc-status-running);
}
.status-body {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.status-title {
  font-size: 0.92rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.status-meta {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}
.mono { font-family: 'JetBrains Mono', monospace; }

.cf-tabs {
  padding: var(--wdc-space-4) 0;
}

.tab-content {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4);
  /* F91.18 / task 37: removed max-width: 1100px — Cloudflare page is a
     first-class top-level view that should use the full content-area
     width, matching Services/Sites/Databases pages. */
}

/* .edit-card CSS lives in EditCard shared primitive (plan §6). */

.hint {
  margin-top: var(--wdc-space-1-5);
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.hint code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  padding: 1px var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  color: var(--wdc-accent);
}

.card-actions {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: center;
  margin-top: var(--wdc-space-3);
}
.default-zone { margin-top: var(--wdc-space-3-5); }
.default-zone-label {
  display: block;
  margin-bottom: var(--wdc-space-1-5);
  font-size: 0.82rem;
  font-weight: 600;
  color: var(--wdc-text-2);
}
.default-zone-select { width: 320px; max-width: 100%; }
.save-status { font-size: 0.82rem; font-weight: 600; }
.save-status.ok { color: var(--wdc-status-running); }
.save-status.err { color: var(--wdc-status-error); }

.arrow {
  font-size: 1.1rem;
  color: var(--wdc-text-3);
  text-align: center;
}
.dns-table :deep(th) {
  background: var(--wdc-surface-2) !important;
}

/* Exposed sites list (Sites tab) ─────────────────────────────────── */
.site-expose-list {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2);
  margin-bottom: var(--wdc-space-4);
}
.site-expose-row {
  display: grid;
  grid-template-columns: 1fr 24px 1fr auto;
  align-items: center;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-3) var(--wdc-space-4);
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.expose-local {
  font-size: 0.88rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.expose-public {
  font-size: 0.82rem;
  color: var(--wdc-accent);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.expose-tag {
  flex-shrink: 0;
}
</style>
