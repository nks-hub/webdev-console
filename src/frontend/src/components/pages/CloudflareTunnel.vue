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
        <el-button
          size="small"
          :type="serviceRunning ? 'danger' : 'success'"
          :loading="toggling"
          :disabled="!daemonStore.connected || !configReady"
          @click="toggleTunnel"
        >
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} tunnel
        </el-button>
      </div>
    </div>

    <LoadingState v-if="initialLoading" :label="$t('cloudflare.connecting')" />

    <template v-else>
    <el-alert
      v-if="pageLoadError"
      type="error"
      :title="pageLoadError"
      :closable="true"
      @close="pageLoadError = ''"
      show-icon
      style="margin: 0 24px 16px"
    />

    <!-- Status strip -->
    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'"><CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else /></el-icon>
        <div class="status-body">
          <div class="status-title">
            {{ serviceRunning ? $t('cloudflare.status.tunnelRunning') : (serviceInfo ? $t('cloudflare.status.stopped') : $t('cloudflare.status.unknown')) }}
          </div>
          <div class="status-meta" v-if="serviceInfo">
            PID {{ serviceInfo.pid ?? '—' }} · uptime {{ formatUptime(serviceInfo.uptime) }}
          </div>
          <div class="status-meta" v-else-if="configReady">
            {{ $t('cloudflare.status.serviceInfoUnavailable') }}
          </div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Key /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ tokenState }}</div>
          <div class="status-meta">{{ config.apiToken ? $t('cloudflare.status.tokenStored') : $t('cloudflare.status.tokenMissing') }}</div>
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
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.steps.step1Title') }}</span>
              <span class="edit-card-hint">{{ $t('cloudflare.steps.step1Hint') }}</span>
            </header>
            <div class="edit-card-body">
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
                  :disabled="!apiTokenInput && !config.apiToken"
                  @click="runAutoSetup"
                >
                  <el-icon><Check /></el-icon>
                  <span>{{ $t('cloudflare.actions.autoSetup') }}</span>
                </el-button>
                <span v-if="autoSetupResult" class="save-status ok">
                  <el-icon><CircleCheckFilled /></el-icon> Account: {{ autoSetupResult.account.name }} · Tunnel: {{ autoSetupResult.tunnel.name }}
                </span>
                <span v-if="saveStatus && saveStatus.kind === 'err'" class="save-status err">
                  {{ saveStatus.message }}
                </span>
              </div>
              <div class="hint" style="margin-top: 12px;">
                {{ $t('cloudflare.autoSetupExplain', { tunnel: 'NKS-WDC-Tunnel-{md5}', file: '~/.wdc/cloudflare/config.json' }) }}
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.steps.step2Title') }}</span>
              <span class="edit-card-hint">{{ $t('cloudflare.steps.step2Hint') }}</span>
            </header>
            <div class="edit-card-body">
              <el-input
                v-model="config.cloudflaredPath"
                placeholder="C:\Users\...\cloudflared.exe"
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
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.steps.subdomainTemplate') }}</span>
              <span class="edit-card-hint">{{ $t('cloudflare.steps.subdomainTemplateHint') }}</span>
            </header>
            <div class="edit-card-body">
              <el-input
                v-model="config.subdomainTemplate"
                class="mono"
                placeholder="{stem}-dev"
              />
              <div class="hint">
                {{ $t('cloudflare.subdomainPlaceholders') }}
                <code>{stem}</code> {{ $t('cloudflare.subdomainStem', { loc: '.loc', local: '.local', test: '.test' }) }},
                <code>{user}</code> {{ $t('cloudflare.subdomainUser') }}.
                {{ $t('cloudflare.subdomainExample', { domain: 'myapp.loc' }) }}
                <code>{{ previewTemplate }}</code>
              </div>
              <div class="card-actions">
                <el-button size="small" :loading="savingConfig" @click="saveSubdomainTemplate">
                  {{ $t('cloudflare.saveTemplateBtn') }}
                </el-button>
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.steps.step3Title') }}</span>
              <span class="edit-card-hint">{{ $t('cloudflare.steps.step3Hint') }}</span>
            </header>
            <div class="edit-card-body">
              <div class="ssl-toggle-row">
                <div class="ssl-toggle-meta">
                  <div class="ssl-toggle-title">
                    {{ serviceRunning ? 'Tunnel is running' : 'Tunnel is stopped' }}
                  </div>
                  <div class="ssl-toggle-desc">
                    Uses the JWT fetched in Step 1 and connects to Cloudflare's edge.
                  </div>
                </div>
                <el-button
                  size="default"
                  :type="serviceRunning ? 'danger' : 'success'"
                  :loading="toggling"
                  :disabled="!configReady"
                  @click="toggleTunnel"
                >
                  {{ serviceRunning ? $t('common.stop') : $t('common.run') }} cloudflared
                </el-button>
              </div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- ═══ Sites — per-site exposure ═════════════ -->
      <el-tab-pane name="ingress">
        <template #label>
          <span class="tab-label"><el-icon><Share /></el-icon> {{ $t('cloudflare.tabs.sites') }} ({{ exposedSites.length }})</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.exposedSites') }}</span>
              <span class="edit-card-hint">{{ $t('cloudflare.exposedSitesHint') }}</span>
            </header>
            <div class="edit-card-body">
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
                  >proxied</el-tag>
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
              <div class="hint" style="margin-top: 10px;">
                Sync will: (1) upsert a proxied CNAME for each site pointing at
                <code>{{ config.tunnelId ? config.tunnelId.slice(0,8)+'…' : 'tunnel' }}.cfargotunnel.com</code>,
                (2) rebuild the tunnel ingress rules with an
                <code>httpHostHeader</code> override per site so Apache matches
                the correct local vhost. Safe to run repeatedly — it's idempotent.
              </div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- ═══ DNS records ════════════════════════════ -->
      <el-tab-pane name="dns">
        <template #label>
          <span class="tab-label"><el-icon><Postcard /></el-icon> {{ $t('cloudflare.tabs.dns') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.dnsRecords') }}</span>
              <span class="edit-card-hint">{{ activeZone?.name || $t('cloudflare.pickZone') }}</span>
            </header>
            <div class="edit-card-body">
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
                style="margin-top: 14px"
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
                <el-table-column :label="$t('cloudflare.proxied')" width="90" align="center">
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
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('cloudflare.addDnsRecord') }}</span>
              <span class="edit-card-hint">{{ $t('cloudflare.addDnsHint', { target: tunnelCnameTarget }) }}</span>
            </header>
            <div class="edit-card-body">
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
            </div>
          </section>
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
  Setting, Check, Share, Delete, Right, Postcard, Link,
  CircleCheckFilled, CircleClose, Key, Connection, Location,
} from '@element-plus/icons-vue'

const { t } = useI18n()
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import FolderBrowser from '../shared/FolderBrowser.vue'
import LoadingState from '../shared/LoadingState.vue'
import {
  fetchCloudflareConfig, saveCloudflareConfig,
  verifyCloudflareToken, fetchCloudflareZones,
  fetchCloudflareDns, createCloudflareDns, deleteCloudflareDns,
  fetchCloudflareTunnels, fetchCloudflareTunnelConfig,
  updateCloudflareTunnelIngress, cloudflareAutoSetup, cloudflareSync,
  type CloudflareConfig, type CfIngressRule, type CloudflareAutoSetupResult,
} from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import type { SiteInfo } from '../../api/types'

defineOptions({ name: 'CloudflareTunnel' })

const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()

const activeTab = ref<'settings' | 'ingress' | 'dns'>('settings')

// ── Config state ─────────────────────────────────────────────────────
const config = reactive<CloudflareConfig>({})
const pageLoadError = ref('')
const initialLoading = ref(true)
const apiTokenInput = ref('')
const tunnelTokenInput = ref('')

const apiTokenMasked = computed(() => config.apiToken || '')
const tunnelTokenMasked = computed(() => config.tunnelToken || '')

const savingConfig = ref(false)
const saveStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

async function loadConfig() {
  pageLoadError.value = ''
  try {
    const res = await fetchCloudflareConfig()
    Object.assign(config, res)
  } catch (e) {
    pageLoadError.value = `Failed to load Cloudflare config: ${errorMessage(e)}`
    ElMessage.error(pageLoadError.value)
  }
}

async function saveConfig() {
  savingConfig.value = true
  saveStatus.value = null
  try {
    const body: Partial<CloudflareConfig> = {
      cloudflaredPath: config.cloudflaredPath || null,
      tunnelName: config.tunnelName || null,
      tunnelId: config.tunnelId || null,
      accountId: config.accountId || null,
      defaultZoneId: config.defaultZoneId || null,
    }
    // Only send secret fields when the user actually typed something new
    if (apiTokenInput.value) body.apiToken = apiTokenInput.value
    if (tunnelTokenInput.value) body.tunnelToken = tunnelTokenInput.value

    const res = await saveCloudflareConfig(body)
    Object.assign(config, res)
    apiTokenInput.value = ''
    tunnelTokenInput.value = ''
    saveStatus.value = { kind: 'ok', message: 'Settings saved' }
    ElMessage.success('Cloudflare settings saved')
  } catch (e) {
    saveStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(`Save failed: ${errorMessage(e)}`)
  } finally {
    savingConfig.value = false
  }
}

async function saveBinaryPath() {
  savingConfig.value = true
  try {
    await saveCloudflareConfig({ cloudflaredPath: config.cloudflaredPath || null })
    ElMessage.success('Binary path saved')
  } catch (e) {
    ElMessage.error(`Save failed: ${errorMessage(e)}`)
  } finally {
    savingConfig.value = false
  }
}

async function saveSubdomainTemplate() {
  savingConfig.value = true
  try {
    await saveCloudflareConfig({ subdomainTemplate: config.subdomainTemplate || '{stem}-dev' })
    ElMessage.success('Subdomain template saved')
  } catch (e) {
    ElMessage.error(`Save failed: ${errorMessage(e)}`)
  } finally {
    savingConfig.value = false
  }
}

// Live preview of how the template resolves for the stem "myapp".
// {hash} is rendered as a fixed example — the real value comes from
// backend where the install salt lives, but that's fine for a preview.
const previewTemplate = computed(() => {
  const t = config.subdomainTemplate || '{stem}-{hash}'
  return t
    .replace('{stem}', 'myapp')
    .replace('{hash}', 'bffa44')
    .replace('{user}', 'me')
    .replace(/-+/g, '-')
    .replace(/^-+|-+$/g, '')
})

// ── Auto-setup (one-token flow) ──────────────────────────────────────
const autoSettingUp = ref(false)
const autoSetupResult = ref<CloudflareAutoSetupResult | null>(null)

async function runAutoSetup() {
  autoSettingUp.value = true
  saveStatus.value = null
  try {
    // Use typed input if present, fall back to the already-stored masked token
    // (empty string — only works if token was persisted earlier and user just
    // wants to re-run setup without re-typing)
    const token = apiTokenInput.value || ''
    if (!token) {
      ElMessage.warning('Paste your Cloudflare API token first')
      return
    }
    autoSetupResult.value = await cloudflareAutoSetup(token)
    apiTokenInput.value = ''
    await loadConfig() // reload to see redacted token + populated account/tunnel IDs
    tokenVerdict.value = 'ok'
    ElMessage.success(`Auto-setup complete: tunnel "${autoSetupResult.value.tunnel.name}"`)
    void loadZones()
  } catch (e) {
    saveStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(`Auto-setup failed: ${errorMessage(e)}`)
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
    syncStatus.value = {
      kind: 'ok',
      message: `Synced ${res.synced} site${res.synced === 1 ? '' : 's'} to Cloudflare`,
    }
    ElMessage.success(`Synced ${res.synced} sites`)
  } catch (e) {
    syncStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(`Sync failed: ${errorMessage(e)}`)
  } finally {
    syncing.value = false
  }
}

// ── Token verify ─────────────────────────────────────────────────────
const verifying = ref(false)
const tokenVerdict = ref<'unknown' | 'ok' | 'fail'>('unknown')
const tokenVerdictLabel = computed(() => {
  if (tokenVerdict.value === 'ok') return 'Token valid'
  if (tokenVerdict.value === 'fail') return 'Token invalid'
  return ''
})
const tokenState = computed(() => {
  if (tokenVerdict.value === 'ok') return t('cloudflare.status.tokenVerified')
  if (tokenVerdict.value === 'fail') return t('cloudflare.status.tokenInvalid')
  return config.apiToken ? t('cloudflare.status.tokenStoredUnverified') : t('cloudflare.status.tokenMissing')
})

async function verifyToken() {
  verifying.value = true
  try {
    // Save first if user typed a new token
    if (apiTokenInput.value) {
      await saveCloudflareConfig({ apiToken: apiTokenInput.value })
      apiTokenInput.value = ''
      await loadConfig()
    }
    const res = await verifyCloudflareToken()
    tokenVerdict.value = res?.success ? 'ok' : 'fail'
    if (res?.success) {
      ElMessage.success('Cloudflare API token is valid')
    } else {
      // Cloudflare returns { success: false, errors: [{ code, message }, ...] }
      // Surface the actual rejection reason instead of a generic toast so the
      // user knows whether to regenerate the token, fix scopes, or check quota.
      const errMsg = Array.isArray(res?.errors) && res.errors.length > 0
        ? res.errors.map(e => e?.message || e?.code || String(e)).join('; ')
        : 'Token rejected by Cloudflare'
      ElMessage.error(errMsg)
    }
  } catch (e) {
    tokenVerdict.value = 'fail'
    ElMessage.error(`Verify failed: ${errorMessage(e)}`)
  } finally {
    verifying.value = false
  }
}

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
    ElMessage.error(`List zones failed: ${errorMessage(e)}`)
  } finally {
    loadingZones.value = false
  }
}

const activeZone = computed(() =>
  zones.value.find(z => z.id === selectedDnsZoneId.value)
)

// ── Tunnels ──────────────────────────────────────────────────────────
// Cloudflare tunnels list entry — only id/name are read from the UI.
interface CfTunnel {
  id: string
  name: string
}
const tunnels = ref<CfTunnel[]>([])
const loadingTunnels = ref(false)

async function loadTunnels() {
  loadingTunnels.value = true
  try {
    const res = await fetchCloudflareTunnels()
    tunnels.value = res?.result ?? []
  } catch (e) {
    ElMessage.error(`List tunnels failed: ${errorMessage(e)}`)
  } finally {
    loadingTunnels.value = false
  }
}

function selectTunnel(t: CfTunnel) {
  config.tunnelId = t.id
  config.tunnelName = t.name
}

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
    ElMessage.error(`${serviceRunning.value ? 'Stop' : 'Start'} failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

// ── Ingress rules ────────────────────────────────────────────────────
const ingressRules = ref<CfIngressRule[]>([])
const loadingTunnelConfig = ref(false)
const applyingIngress = ref(false)

async function loadIngress() {
  if (!config.tunnelId) {
    ElMessage.warning('Configure tunnel ID first')
    return
  }
  loadingTunnelConfig.value = true
  try {
    const res = await fetchCloudflareTunnelConfig(config.tunnelId)
    const cfg = res?.result?.config?.ingress ?? []
    ingressRules.value = cfg
      .filter(r => typeof r.hostname === 'string' && r.hostname.length > 0) // strip catch-all 404 rule
      .map(r => ({ hostname: r.hostname ?? '', service: r.service ?? '' }))
  } catch (e) {
    ElMessage.error(`Load ingress failed: ${errorMessage(e)}`)
  } finally {
    loadingTunnelConfig.value = false
  }
}

function addEmptyRule() {
  ingressRules.value.push({ hostname: '', service: 'http://localhost:80' })
}

function removeRule(i: number) {
  ingressRules.value.splice(i, 1)
}

function addRuleFromSite(site: SiteInfo) {
  const zoneDomain = zones.value.find(z => z.id === config.defaultZoneId)?.name ?? ''
  const localStem = site.domain.replace(/\.(loc|local|test)$/i, '')
  const hostname = zoneDomain ? `${localStem}.${zoneDomain}` : site.domain
  const port = site.httpPort || 80
  const service = `http://localhost:${port}`
  // Dedupe by hostname
  const existing = ingressRules.value.findIndex(r => r.hostname === hostname)
  if (existing >= 0) {
    ingressRules.value[existing].service = service
  } else {
    ingressRules.value.push({ hostname, service })
  }
  ElMessage.success(`Added rule: ${hostname} -> ${service}`)
}

async function applyIngress() {
  if (!config.tunnelId) return
  applyingIngress.value = true
  try {
    const rules = ingressRules.value.filter(r => r.hostname && r.service)
    await updateCloudflareTunnelIngress(config.tunnelId, rules)
    ElMessage.success(`Applied ${rules.length} ingress rule${rules.length === 1 ? '' : 's'} to tunnel`)
  } catch (e) {
    ElMessage.error(`Apply failed: ${errorMessage(e)}`)
  } finally {
    applyingIngress.value = false
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
    ElMessage.error(`Load DNS failed: ${errorMessage(e)}`)
  } finally {
    loadingDns.value = false
  }
}

async function createDnsRecord() {
  if (!selectedDnsZoneId.value) {
    ElMessage.warning('Pick a zone first')
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
    ElMessage.success(`${newDns.type} ${newDns.name} created`)
    newDns.name = ''
    newDns.content = ''
    await loadDns()
  } catch (e) {
    ElMessage.error(`Create failed: ${errorMessage(e)}`)
  } finally {
    creatingDns.value = false
  }
}

async function deleteDnsRecord(row: CfDnsRecord) {
  try {
    await ElMessageBox.confirm(
      `Delete ${row.type} record "${row.name}"?`,
      'Confirm delete',
      { type: 'warning', confirmButtonText: 'Delete' }
    )
  } catch {
    // ElMessageBox rejects with 'cancel'/'close' when dismissed — that's fine.
    return
  }
  try {
    await deleteCloudflareDns(selectedDnsZoneId.value, row.id)
    ElMessage.success('Record deleted')
    await loadDns()
  } catch (e) {
    ElMessage.error(`Delete failed: ${errorMessage(e)}`)
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

function formatUptime(secs: number | string | undefined): string {
  if (typeof secs !== 'number') secs = Number(secs) || 0
  if (!secs || secs < 1) return '—'
  const h = Math.floor(secs / 3600)
  const m = Math.floor((secs % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

onMounted(async () => {
  await loadConfig()
  initialLoading.value = false
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
  padding: 0;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px 14px;
  border-bottom: 1px solid var(--wdc-border);
}
.header-left { display: flex; flex-direction: column; gap: 2px; }
.page-title {
  font-size: 1.25rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0;
}
.page-subtitle {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.header-actions { display: flex; gap: 8px; }

.status-strip {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
  padding: 18px 24px 4px;
}
.status-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
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
  padding: 16px 24px;
}

.tab-content {
  display: flex;
  flex-direction: column;
  gap: 16px;
  /* F91.18 / task 37: removed max-width: 1100px — Cloudflare page is a
     first-class top-level view that should use the full content-area
     width, matching Services/Sites/Databases pages. */
}

.edit-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}
.edit-card-header {
  padding: 14px 20px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
  display: flex;
  justify-content: space-between;
  align-items: baseline;
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
}
.edit-card-body {
  padding: 18px 20px;
}

.hint {
  margin-top: 6px;
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}
.hint code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface-2);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--wdc-accent);
}

.card-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-top: 12px;
}
.token-status {
  font-size: 0.8rem;
  font-weight: 600;
  margin-left: 6px;
}
.token-ok { color: var(--wdc-status-running); }
.token-fail { color: var(--wdc-status-error); }

.row-gap {
  display: flex;
  gap: 8px;
}

.tunnel-picker {
  margin-top: 10px;
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  overflow: hidden;
}
.tunnel-row {
  display: grid;
  grid-template-columns: 180px 1fr auto;
  align-items: center;
  gap: 12px;
  padding: 10px 14px;
  cursor: pointer;
  border-bottom: 1px solid var(--wdc-border);
  transition: background 0.1s;
}
.tunnel-row:last-child { border-bottom: none; }
.tunnel-row:hover { background: var(--wdc-hover); }
.tunnel-row-active {
  background: var(--wdc-accent-dim);
  border-left: 3px solid var(--wdc-accent);
}
.tunnel-name {
  font-size: 0.88rem;
  font-weight: 600;
  color: var(--wdc-text);
}
.tunnel-id {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
}

.save-row {
  display: flex;
  align-items: center;
  gap: 14px;
  padding-top: 4px;
}
.save-status { font-size: 0.82rem; font-weight: 600; }
.save-status.ok { color: var(--wdc-status-running); }
.save-status.err { color: var(--wdc-status-error); }

/* Ingress rules ─────────────────────────────────────────────────── */
.ingress-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.ingress-row {
  display: grid;
  grid-template-columns: 1fr 24px 1fr auto;
  align-items: center;
  gap: 10px;
}
.ingress-host :deep(.el-input__inner),
.ingress-service :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
}
.arrow {
  font-size: 1.1rem;
  color: var(--wdc-text-3);
  text-align: center;
}
.ingress-empty {
  padding: 16px 0;
  font-size: 0.85rem;
  color: var(--wdc-text-3);
  text-align: center;
}

.site-pick-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
}
.site-pick-card {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  padding: 12px 14px;
  text-align: left;
  font-family: inherit;
  cursor: pointer;
  transition: all 0.12s;
}
.site-pick-card:hover {
  border-color: var(--wdc-accent);
  background: var(--wdc-elevated);
  transform: translateY(-1px);
}
.site-pick-domain {
  font-size: 0.92rem;
  font-weight: 700;
  color: var(--wdc-text);
  font-family: 'JetBrains Mono', monospace;
}
.site-pick-meta {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  margin-top: 4px;
}

.dns-table :deep(th) {
  background: var(--wdc-surface-2) !important;
}

/* Exposed sites list (Sites tab) ─────────────────────────────────── */
.site-expose-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 16px;
}
.site-expose-row {
  display: grid;
  grid-template-columns: 1fr 24px 1fr auto;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
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
