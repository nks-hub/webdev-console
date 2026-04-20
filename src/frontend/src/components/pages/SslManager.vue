<template>
  <div class="ssl-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('ssl.title') }}</h1>
        <el-tag v-if="mkcertInstalled" type="success" size="small" effect="plain">{{ $t('ssl.mkcertReady') }}</el-tag>
        <el-tag v-else type="danger" size="small" effect="plain">{{ $t('ssl.mkcertMissing') }}</el-tag>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="installCA" :loading="installingCA" :disabled="!mkcertInstalled">
          {{ $t('ssl.installCa') }}
        </el-button>
        <el-button size="small" @click="loadCerts" :loading="loading">{{ $t('common.refresh') }}</el-button>
        <el-button type="primary" size="small" @click="showGenerateDialog = true" :disabled="!mkcertInstalled">
          + {{ $t('ssl.generate') }}
        </el-button>
      </div>
    </div>

    <div class="page-body">
      <el-alert
        v-if="loadError"
        type="error"
        :title="loadError"
        :closable="true"
        @close="loadError = ''"
        show-icon
        style="margin-bottom: 16px"
      />
      <el-alert
        v-if="!mkcertInstalled && !loadError"
        type="warning"
        title="mkcert not installed"
        description="Install mkcert via Binaries page to enable SSL certificate generation."
        show-icon
        :closable="false"
        style="margin-bottom: 16px"
      />

      <!-- Loading -->
      <LoadingState v-if="loading && certs.length === 0" label="Reading SSL certificates…" />

      <!-- Cert list -->
      <div v-else-if="certs.length > 0" class="cert-list">
        <div v-for="cert in certs" :key="cert.domain" class="cert-card" :class="{ 'cert-expiring': cert.expiring, 'cert-expired': cert.expired, 'cert-orphan': cert.orphan }">
          <div class="cert-main">
            <div class="cert-domain">
              <el-icon class="cert-lock"><Lock /></el-icon>
              <span class="cert-name">{{ cert.domain }}</span>
              <!-- F81: status badges -->
              <el-tag v-if="cert.expired" type="danger" size="small" effect="dark" class="cert-badge">Expired</el-tag>
              <el-tag v-else-if="cert.expiring" type="warning" size="small" effect="dark" class="cert-badge">Expires in {{ cert.daysToExpiry }}d</el-tag>
              <el-tag v-else-if="cert.daysToExpiry !== null && cert.daysToExpiry !== undefined" type="success" size="small" effect="plain" class="cert-badge">Valid {{ cert.daysToExpiry }}d</el-tag>
              <el-tag v-if="cert.orphan" type="info" size="small" effect="plain" class="cert-badge" title="No site with this domain currently exists">Orphan</el-tag>
            </div>
            <div class="cert-meta">
              <span class="cert-meta-item" v-if="cert.aliases?.length">
                Aliases: {{ cert.aliases.join(', ') }}
              </span>
              <span class="cert-meta-item">
                Created: {{ formatDate(cert.createdUtc) }}
              </span>
              <span v-if="cert.notAfterUtc" class="cert-meta-item">
                Expires: {{ formatDate(cert.notAfterUtc) }}
              </span>
              <span v-if="cert.issuer" class="cert-meta-item mono">
                Issuer: {{ cert.issuer }}
              </span>
              <span v-if="cert.fingerprint" class="cert-meta-item mono cert-fingerprint">
                SHA1: {{ cert.fingerprint }}
              </span>
            </div>
            <div class="cert-paths">
              <span class="cert-path">{{ cert.certPath }}</span>
            </div>
          </div>
          <div class="cert-actions">
            <el-button
              size="small"
              type="danger"
              text
              @click="revokeCert(cert.domain)"
              :loading="revoking.has(cert.domain)"
            >
              Revoke
            </el-button>
          </div>
        </div>
      </div>

      <el-empty
        v-else-if="!loading"
        description="No SSL certificates. Generate one for your sites."
        :image-size="64"
      />
    </div>

    <!-- Generate cert dialog -->
    <el-dialog v-model="showGenerateDialog" title="Generate SSL Certificate" width="440px">
      <el-form label-position="top" size="small">
        <el-form-item label="Domain" required>
          <el-select
            v-model="genDomain"
            filterable
            allow-create
            placeholder="Select or type domain"
            style="width: 100%"
          >
            <el-option
              v-for="site in availableSites"
              :key="site"
              :label="site"
              :value="site"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="Aliases (comma-separated)">
          <el-input v-model="genAliases" placeholder="www.myapp.loc, api.myapp.loc" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showGenerateDialog = false">Cancel</el-button>
        <el-button type="primary" :loading="generating" @click="generateCert" :disabled="!genDomain">
          Generate
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import LoadingState from '../shared/LoadingState.vue'
import { ElMessage } from 'element-plus'
import { Lock } from '@element-plus/icons-vue'

interface CertInfo {
  domain: string
  certPath: string
  keyPath: string
  createdUtc: string
  aliases: string[]
  // F81 enrichment emitted by daemon /api/ssl/certs: parsed X.509 metadata
  // + orphan/expiring/expired flags. Optional to keep older daemon payloads
  // working (pre-F81 nodes just render the base fields above).
  notAfterUtc?: string
  issuer?: string
  fingerprint?: string
  daysToExpiry?: number
  expiring?: boolean
  expired?: boolean
  orphan?: boolean
}

const certs = ref<CertInfo[]>([])
const mkcertInstalled = ref(false)
const loading = ref(false)
const loadError = ref('')
const installingCA = ref(false)
const generating = ref(false)
const revoking = ref(new Set<string>())
const showGenerateDialog = ref(false)
const genDomain = ref('')
const genAliases = ref('')
const availableSites = ref<string[]>([])

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = (window as any).daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

function authHeaders(): Record<string, string> {
  const urlToken = new URLSearchParams(window.location.search).get('token')
  const token = (window as any).daemonApi?.getToken?.() || urlToken || ''
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) headers['Authorization'] = `Bearer ${token}`
  return headers
}

async function loadCerts() {
  loading.value = true
  loadError.value = ''
  try {
    const r = await fetch(`${daemonBase()}/api/ssl/certs`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      certs.value = data.certs ?? []
      mkcertInstalled.value = data.mkcertInstalled ?? false
    } else {
      loadError.value = `Failed to load certificates: HTTP ${r.status}`
    }
  } catch (e: any) {
    loadError.value = `Cannot connect to daemon: ${e?.message || e}`
  } finally { loading.value = false }
}

async function loadSites() {
  try {
    const r = await fetch(`${daemonBase()}/api/sites`, { headers: authHeaders() })
    if (r.ok) {
      const sites = await r.json()
      availableSites.value = sites.map((s: any) => s.domain)
    }
  } catch { /* skip */ }
}

async function installCA() {
  installingCA.value = true
  try {
    const r = await fetch(`${daemonBase()}/api/ssl/install-ca`, {
      method: 'POST',
      headers: authHeaders(),
    })
    const data = await r.json()
    if (data.ok) ElMessage.success('CA installed successfully')
    else ElMessage.error(data.message || 'CA install failed')
  } catch (e: any) {
    ElMessage.error(`CA install failed: ${e?.message || e}`)
  } finally {
    installingCA.value = false
  }
}

async function generateCert() {
  if (!genDomain.value) return
  generating.value = true
  try {
    const aliases = genAliases.value
      ? genAliases.value.split(',').map(s => s.trim()).filter(Boolean)
      : []
    const r = await fetch(`${daemonBase()}/api/ssl/generate`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ domain: genDomain.value, aliases }),
    })
    const data = await r.json()
    if (data.ok) {
      ElMessage.success(`Certificate generated for ${genDomain.value}`)
      showGenerateDialog.value = false
      genDomain.value = ''
      genAliases.value = ''
      await loadCerts()
    } else {
      ElMessage.error(data.message || 'Generation failed')
    }
  } catch (e: any) {
    ElMessage.error(`Generation failed: ${e?.message || e}`)
  } finally {
    generating.value = false
  }
}

async function revokeCert(domain: string) {
  revoking.value.add(domain)
  try {
    const r = await fetch(`${daemonBase()}/api/ssl/certs/${domain}`, {
      method: 'DELETE',
      headers: authHeaders(),
    })
    const data = await r.json()
    if (data.ok) {
      ElMessage.success(`Certificate for ${domain} revoked`)
      await loadCerts()
    } else {
      ElMessage.error(data.message || 'Revoke failed')
    }
  } catch (e: any) {
    ElMessage.error(`Revoke failed: ${e?.message || e}`)
  } finally {
    revoking.value.delete(domain)
  }
}

function formatDate(iso: string): string {
  try { return new Date(iso).toLocaleDateString() } catch { return iso }
}

onMounted(() => {
  void loadCerts()
  void loadSites()
})
</script>

<style scoped>
.ssl-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 24px 24px 0;
  margin-bottom: 20px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.header-actions { display: flex; align-items: center; gap: 8px; }

.page-body { padding: 0 24px 24px; }

.cert-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.cert-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 16px 20px;
  transition: border-color 0.15s;
}

.cert-card:hover { border-color: var(--wdc-border-strong); }

.cert-main { flex: 1; min-width: 0; }

.cert-domain {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.cert-lock { font-size: 1rem; }

.cert-name {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--wdc-text);
}

.cert-meta {
  display: flex;
  gap: 16px;
  margin-bottom: 4px;
}

.cert-meta-item {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
}

.cert-paths {
  margin-top: 2px;
}

.cert-path {
  font-size: 0.72rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
}

.cert-actions { flex-shrink: 0; margin-left: 16px; }

/* F81 expiry / orphan state styling. The border hints at severity so a
   quick glance across many certs highlights what needs attention. */
.cert-card.cert-expiring { border-left: 3px solid var(--wdc-warning, #f5a623); }
.cert-card.cert-expired { border-left: 3px solid var(--wdc-danger, #e74c3c); background: rgba(231, 76, 60, 0.04); }
.cert-card.cert-orphan { opacity: 0.7; }
.cert-badge { margin-left: 8px; }
.cert-fingerprint { font-size: 0.7rem; opacity: 0.7; word-break: break-all; }
</style>
