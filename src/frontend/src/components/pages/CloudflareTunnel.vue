<template>
  <div class="cf-page">
    <!-- Page header -->
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">Cloudflare Tunnel</h1>
        <span class="page-subtitle">
          Expose local sites to the internet via an encrypted tunnel
        </span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openDocs">
          <el-icon><Link /></el-icon>
          <span>Docs</span>
        </el-button>
        <el-button
          size="small"
          :type="serviceRunning ? 'danger' : 'success'"
          :loading="toggling"
          :disabled="!daemonStore.connected || !configReady"
          @click="toggleTunnel"
        >
          {{ serviceRunning ? 'Stop tunnel' : 'Start tunnel' }}
        </el-button>
      </div>
    </div>

    <!-- Status strip -->
    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <div class="status-icon">{{ serviceRunning ? '●' : '○' }}</div>
        <div class="status-body">
          <div class="status-title">
            {{ serviceRunning ? 'Tunnel running' : (serviceInfo ? 'Stopped' : 'Unknown') }}
          </div>
          <div class="status-meta" v-if="serviceInfo">
            PID {{ serviceInfo.pid ?? '—' }} · uptime {{ formatUptime(serviceInfo.uptime) }}
          </div>
        </div>
      </div>
      <div class="status-card">
        <div class="status-icon">🔑</div>
        <div class="status-body">
          <div class="status-title">{{ tokenState }}</div>
          <div class="status-meta">{{ config.apiToken ? 'API token stored' : 'API token missing' }}</div>
        </div>
      </div>
      <div class="status-card">
        <div class="status-icon">🌐</div>
        <div class="status-body">
          <div class="status-title">{{ config.tunnelName || 'No tunnel selected' }}</div>
          <div class="status-meta mono" v-if="config.tunnelId">{{ config.tunnelId.slice(0, 18) }}…</div>
          <div class="status-meta" v-else>Configure below</div>
        </div>
      </div>
      <div class="status-card">
        <div class="status-icon">📍</div>
        <div class="status-body">
          <div class="status-title">{{ zones.length }} zone{{ zones.length === 1 ? '' : 's' }}</div>
          <div class="status-meta">{{ config.defaultZoneId ? 'Default selected' : 'Pick default zone' }}</div>
        </div>
      </div>
    </div>

    <!-- Tabs -->
    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- ═══ Settings ═══════════════════════════════ -->
      <el-tab-pane name="settings">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> Settings</span>
        </template>

        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">cloudflared binary</span>
              <span class="edit-card-hint">Path to the cloudflared executable</span>
            </header>
            <div class="edit-card-body">
              <el-input
                v-model="config.cloudflaredPath"
                placeholder="C:\Users\...\cloudflared.exe"
                class="mono"
              >
                <template #append>
                  <el-button @click="showFolderBrowser = true">Browse…</el-button>
                </template>
              </el-input>
              <div class="hint">
                Auto-detected from <code>~/.wdc/binaries/cloudflared/</code> and
                <code>~/Downloads/FlyEnv-Data/app/cloudflared/</code> at daemon start.
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">API token</span>
              <span class="edit-card-hint">
                Required scopes: Account &gt; Cloudflare Tunnel &gt; Edit,
                Zone &gt; DNS &gt; Edit
              </span>
            </header>
            <div class="edit-card-body">
              <el-input
                v-model="apiTokenInput"
                type="password"
                show-password
                :placeholder="apiTokenMasked || 'cfut_…'"
                class="mono"
              />
              <div class="card-actions">
                <el-button size="small" :loading="verifying" @click="verifyToken">
                  Verify token
                </el-button>
                <span class="token-status" :class="`token-${tokenVerdict}`">{{ tokenVerdictLabel }}</span>
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">Account &amp; tunnel identifiers</span>
              <span class="edit-card-hint">Account ID + default tunnel to operate on</span>
            </header>
            <div class="edit-card-body">
              <el-form label-position="top" size="default">
                <el-form-item label="Account ID">
                  <el-input v-model="config.accountId" placeholder="68888717e58fbd8335fc688db778e311" class="mono" />
                </el-form-item>
                <el-form-item label="Tunnel ID">
                  <div class="row-gap">
                    <el-input v-model="config.tunnelId" placeholder="0eed0b1d-53c1-4a2b-a81e-5a7f435baa79" class="mono" />
                    <el-button size="default" :loading="loadingTunnels" @click="loadTunnels">
                      Load tunnels
                    </el-button>
                  </div>
                  <div v-if="tunnels.length" class="tunnel-picker">
                    <div
                      v-for="t in tunnels"
                      :key="t.id"
                      class="tunnel-row"
                      :class="{ 'tunnel-row-active': config.tunnelId === t.id }"
                      @click="selectTunnel(t)"
                    >
                      <div class="tunnel-name">{{ t.name }}</div>
                      <div class="tunnel-id mono">{{ t.id }}</div>
                      <el-tag
                        size="small"
                        :type="t.status === 'healthy' ? 'success' : 'info'"
                        effect="dark"
                      >{{ t.status || 'unknown' }}</el-tag>
                    </div>
                  </div>
                </el-form-item>
                <el-form-item label="Tunnel name (label only)">
                  <el-input v-model="config.tunnelName" placeholder="flyenv-local" />
                </el-form-item>
                <el-form-item label="Tunnel token (JWT)">
                  <el-input
                    v-model="tunnelTokenInput"
                    type="password"
                    show-password
                    :placeholder="tunnelTokenMasked || 'eyJhIjoi…'"
                    class="mono"
                  />
                  <div class="hint">
                    Used by <code>cloudflared tunnel run --token</code>. Copy from the
                    tunnel's Install Connector page in the Cloudflare dashboard.
                  </div>
                </el-form-item>
                <el-form-item label="Default zone">
                  <el-select
                    v-model="config.defaultZoneId"
                    placeholder="Pick a zone…"
                    :loading="loadingZones"
                    filterable
                    style="width: 100%"
                    @visible-change="(v: boolean) => v && !zones.length && loadZones()"
                  >
                    <el-option
                      v-for="z in zones"
                      :key="z.id"
                      :label="`${z.name} (${z.status})`"
                      :value="z.id"
                    />
                  </el-select>
                </el-form-item>
              </el-form>
            </div>
          </section>

          <div class="save-row">
            <el-button
              type="primary"
              size="default"
              :loading="savingConfig"
              @click="saveConfig"
            >
              <el-icon><Check /></el-icon>
              <span>Save settings</span>
            </el-button>
            <span v-if="saveStatus" class="save-status" :class="saveStatus.kind">
              {{ saveStatus.message }}
            </span>
          </div>
        </div>
      </el-tab-pane>

      <!-- ═══ Ingress — per-site mapping ═════════════ -->
      <el-tab-pane name="ingress">
        <template #label>
          <span class="tab-label"><el-icon><Share /></el-icon> Ingress ({{ ingressRules.length }})</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">Per-site ingress rules</span>
              <span class="edit-card-hint">
                Map public hostnames → local services. A final catch-all 404 is added automatically.
              </span>
            </header>
            <div class="edit-card-body">
              <div class="ingress-list" v-if="ingressRules.length">
                <div
                  v-for="(rule, i) in ingressRules"
                  :key="i"
                  class="ingress-row"
                >
                  <el-input
                    v-model="rule.hostname"
                    placeholder="blog.nks-dev.cz"
                    class="mono ingress-host"
                  />
                  <el-icon class="arrow"><Right /></el-icon>
                  <el-input
                    v-model="rule.service"
                    placeholder="http://localhost:80"
                    class="mono ingress-service"
                  />
                  <el-button
                    size="small"
                    type="danger"
                    plain
                    @click="removeRule(i)"
                  >
                    <el-icon><Delete /></el-icon>
                  </el-button>
                </div>
              </div>
              <div v-else class="ingress-empty">
                No ingress rules yet. Click a site below to auto-add one.
              </div>

              <div class="card-actions">
                <el-button size="small" @click="addEmptyRule">+ Add rule</el-button>
                <el-button size="small" type="primary" :loading="loadingTunnelConfig" @click="loadIngress">
                  Reload from Cloudflare
                </el-button>
                <el-button
                  size="small"
                  type="success"
                  :loading="applyingIngress"
                  :disabled="!config.tunnelId || ingressRules.length === 0"
                  @click="applyIngress"
                >
                  Apply to tunnel
                </el-button>
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">Quick-add from sites</span>
              <span class="edit-card-hint">One click to expose a local site through the tunnel</span>
            </header>
            <div class="edit-card-body">
              <div v-if="sitesStore.sites.length === 0" class="hint">
                No sites configured. Add a site on the Sites page first.
              </div>
              <div v-else class="site-pick-grid">
                <button
                  v-for="site in sitesStore.sites"
                  :key="site.domain"
                  type="button"
                  class="site-pick-card"
                  @click="addRuleFromSite(site)"
                >
                  <div class="site-pick-domain">{{ site.domain }}</div>
                  <div class="site-pick-meta">
                    localhost:{{ site.httpPort || 80 }}
                    <span v-if="site.framework"> · {{ site.framework }}</span>
                  </div>
                </button>
              </div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- ═══ DNS records ════════════════════════════ -->
      <el-tab-pane name="dns">
        <template #label>
          <span class="tab-label"><el-icon><Postcard /></el-icon> DNS</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">DNS records</span>
              <span class="edit-card-hint">{{ activeZone?.name || 'Pick a zone' }}</span>
            </header>
            <div class="edit-card-body">
              <el-select
                v-model="selectedDnsZoneId"
                placeholder="Zone"
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
                <el-table-column prop="type" label="Type" width="90">
                  <template #default="{ row }">
                    <el-tag size="small" effect="dark">{{ row.type }}</el-tag>
                  </template>
                </el-table-column>
                <el-table-column prop="name" label="Name" min-width="180" />
                <el-table-column prop="content" label="Content" min-width="220">
                  <template #default="{ row }">
                    <span class="mono">{{ row.content }}</span>
                  </template>
                </el-table-column>
                <el-table-column label="Proxied" width="90" align="center">
                  <template #default="{ row }">
                    <el-tag v-if="row.proxied" size="small" type="warning" effect="dark">🟠</el-tag>
                    <el-tag v-else size="small" effect="plain">DNS only</el-tag>
                  </template>
                </el-table-column>
                <el-table-column label="Actions" width="100">
                  <template #default="{ row }">
                    <el-button
                      size="small"
                      type="danger"
                      plain
                      @click="deleteDnsRecord(row)"
                    >
                      Delete
                    </el-button>
                  </template>
                </el-table-column>
              </el-table>
              <div v-else-if="selectedDnsZoneId && !loadingDns" class="hint">
                No records in this zone.
              </div>
            </div>
          </section>

          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">Add DNS record</span>
              <span class="edit-card-hint">
                CNAME to <code>{{ tunnelCnameTarget }}</code> routes traffic through this tunnel
              </span>
            </header>
            <div class="edit-card-body">
              <el-form :inline="true" size="default">
                <el-form-item label="Type">
                  <el-select v-model="newDns.type" style="width: 100px">
                    <el-option label="CNAME" value="CNAME" />
                    <el-option label="A" value="A" />
                    <el-option label="AAAA" value="AAAA" />
                    <el-option label="TXT" value="TXT" />
                  </el-select>
                </el-form-item>
                <el-form-item label="Name">
                  <el-input v-model="newDns.name" placeholder="blog" style="width: 180px" />
                </el-form-item>
                <el-form-item label="Content">
                  <el-input
                    v-model="newDns.content"
                    :placeholder="newDns.type === 'CNAME' ? tunnelCnameTarget : '…'"
                    style="width: 320px"
                    class="mono"
                  />
                </el-form-item>
                <el-form-item>
                  <el-switch v-model="newDns.proxied" active-text="Proxied" />
                </el-form-item>
                <el-form-item>
                  <el-button type="primary" :loading="creatingDns" @click="createDnsRecord">
                    Create
                  </el-button>
                </el-form-item>
              </el-form>
            </div>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>

    <FolderBrowser
      v-model="showFolderBrowser"
      :initial-path="config.cloudflaredPath || undefined"
      @select="onPickBinary"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  Setting, Check, Share, Delete, Right, Postcard, Link,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import FolderBrowser from '../shared/FolderBrowser.vue'
import {
  fetchCloudflareConfig, saveCloudflareConfig,
  verifyCloudflareToken, fetchCloudflareZones,
  fetchCloudflareDns, createCloudflareDns, deleteCloudflareDns,
  fetchCloudflareTunnels, fetchCloudflareTunnelConfig,
  updateCloudflareTunnelIngress,
  type CloudflareConfig, type CfIngressRule,
} from '../../api/daemon'
import type { SiteInfo } from '../../api/types'

defineOptions({ name: 'CloudflareTunnel' })

const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()

const activeTab = ref<'settings' | 'ingress' | 'dns'>('settings')

// ── Config state ─────────────────────────────────────────────────────
const config = reactive<CloudflareConfig>({})
const apiTokenInput = ref('')
const tunnelTokenInput = ref('')

const apiTokenMasked = computed(() => config.apiToken || '')
const tunnelTokenMasked = computed(() => config.tunnelToken || '')

const savingConfig = ref(false)
const saveStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

async function loadConfig() {
  try {
    const res = await fetchCloudflareConfig()
    Object.assign(config, res)
  } catch (e: any) {
    ElMessage.error(`Failed to load Cloudflare config: ${e.message}`)
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
  } catch (e: any) {
    saveStatus.value = { kind: 'err', message: e.message }
    ElMessage.error(`Save failed: ${e.message}`)
  } finally {
    savingConfig.value = false
  }
}

// ── Token verify ─────────────────────────────────────────────────────
const verifying = ref(false)
const tokenVerdict = ref<'unknown' | 'ok' | 'fail'>('unknown')
const tokenVerdictLabel = computed(() => {
  if (tokenVerdict.value === 'ok') return '✓ Token valid'
  if (tokenVerdict.value === 'fail') return '✗ Token invalid'
  return ''
})
const tokenState = computed(() => {
  if (tokenVerdict.value === 'ok') return 'Token verified'
  if (tokenVerdict.value === 'fail') return 'Token invalid'
  return config.apiToken ? 'Token stored (not verified)' : 'No token'
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
    if (res?.success) ElMessage.success('Cloudflare API token is valid')
    else ElMessage.error('Token rejected by Cloudflare')
  } catch (e: any) {
    tokenVerdict.value = 'fail'
    ElMessage.error(`Verify failed: ${e.message}`)
  } finally {
    verifying.value = false
  }
}

// ── Zones ────────────────────────────────────────────────────────────
const zones = ref<any[]>([])
const loadingZones = ref(false)

async function loadZones() {
  loadingZones.value = true
  try {
    const res = await fetchCloudflareZones()
    zones.value = res?.result ?? []
  } catch (e: any) {
    ElMessage.error(`List zones failed: ${e.message}`)
  } finally {
    loadingZones.value = false
  }
}

const activeZone = computed(() =>
  zones.value.find(z => z.id === selectedDnsZoneId.value)
)

// ── Tunnels ──────────────────────────────────────────────────────────
const tunnels = ref<any[]>([])
const loadingTunnels = ref(false)

async function loadTunnels() {
  loadingTunnels.value = true
  try {
    const res = await fetchCloudflareTunnels()
    tunnels.value = res?.result ?? []
  } catch (e: any) {
    ElMessage.error(`List tunnels failed: ${e.message}`)
  } finally {
    loadingTunnels.value = false
  }
}

function selectTunnel(t: any) {
  config.tunnelId = t.id
  config.tunnelName = t.name
}

// ── Service status (from daemon store) ───────────────────────────────
const serviceInfo = computed(() =>
  daemonStore.services.find((s: any) => s.id === 'cloudflare')
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
  } catch (e: any) {
    ElMessage.error(`${serviceRunning.value ? 'Stop' : 'Start'} failed: ${e.message}`)
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
      .filter((r: any) => r.hostname) // strip catch-all 404 rule
      .map((r: any) => ({ hostname: r.hostname, service: r.service }))
  } catch (e: any) {
    ElMessage.error(`Load ingress failed: ${e.message}`)
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
  ElMessage.success(`Added rule: ${hostname} → ${service}`)
}

async function applyIngress() {
  if (!config.tunnelId) return
  applyingIngress.value = true
  try {
    const rules = ingressRules.value.filter(r => r.hostname && r.service)
    await updateCloudflareTunnelIngress(config.tunnelId, rules)
    ElMessage.success(`Applied ${rules.length} ingress rule${rules.length === 1 ? '' : 's'} to tunnel`)
  } catch (e: any) {
    ElMessage.error(`Apply failed: ${e.message}`)
  } finally {
    applyingIngress.value = false
  }
}

// ── DNS records ──────────────────────────────────────────────────────
const selectedDnsZoneId = ref<string>('')
const dnsRecords = ref<any[]>([])
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
  } catch (e: any) {
    ElMessage.error(`Load DNS failed: ${e.message}`)
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
  } catch (e: any) {
    ElMessage.error(`Create failed: ${e.message}`)
  } finally {
    creatingDns.value = false
  }
}

async function deleteDnsRecord(row: any) {
  try {
    await ElMessageBox.confirm(
      `Delete ${row.type} record "${row.name}"?`,
      'Confirm delete',
      { type: 'warning', confirmButtonText: 'Delete' }
    )
    await deleteCloudflareDns(selectedDnsZoneId.value, row.id)
    ElMessage.success('Record deleted')
    await loadDns()
  } catch { /* cancelled or error shown */ }
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

function formatUptime(secs: number): string {
  if (!secs || secs < 1) return '—'
  const h = Math.floor(secs / 3600)
  const m = Math.floor((secs % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

onMounted(async () => {
  await loadConfig()
  if (sitesStore.sites.length === 0) void sitesStore.load()
  if (config.apiToken) {
    void loadZones()
    if (config.defaultZoneId) {
      selectedDnsZoneId.value = config.defaultZoneId
      void loadDns()
    }
  }
})
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
  max-width: 1100px;
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
</style>
