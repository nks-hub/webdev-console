<template>
  <div class="ssl-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('ssl.title') }}</h1>
        <template v-if="loaded && pluginLoaded && !pluginDisabled">
          <el-tag v-if="mkcertInstalled" type="success" size="small" effect="plain">{{ $t('ssl.mkcertReady') }}</el-tag>
          <el-tag v-else type="danger" size="small" effect="plain">{{ $t('ssl.mkcertMissing') }}</el-tag>
          <el-tag v-if="mkcertInstalled && caInstalled" type="success" size="small" effect="plain">{{ $t('ssl.caTrusted') }}</el-tag>
          <el-tag v-else-if="mkcertInstalled" type="warning" size="small" effect="plain">{{ $t('ssl.caNotTrusted') }}</el-tag>
        </template>
      </div>
      <div class="header-actions">
        <el-button
          v-if="mkcertInstalled && !caInstalled && pluginLoaded && !pluginDisabled"
          size="small"
          @click="installCA"
          :loading="installingCA"
        >
          {{ $t('ssl.installCa') }}
        </el-button>
        <el-button size="small" @click="loadCerts" :loading="loading">{{ $t('common.refresh') }}</el-button>
        <el-button
          v-if="pluginLoaded && !pluginDisabled"
          type="primary"
          size="small"
          :icon="Plus"
          @click="showGenerateDialog = true"
          :disabled="!mkcertInstalled"
        >
          {{ $t('ssl.generate') }}
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
        style="margin-bottom: var(--wdc-space-4)"
      />

      <PluginMissingState
        v-if="loaded && (!pluginLoaded || pluginDisabled)"
        plugin-id="nks.wdc.ssl"
        :title="$t('ssl.pluginMissingTitle')"
        :description="$t('ssl.pluginMissingDesc')"
        @enabled="loadCerts"
      />

      <template v-else>
        <el-alert
          v-if="loaded && !mkcertInstalled && !loadError"
          type="warning"
          :title="$t('ssl.mkcertNotInstalledTitle')"
          show-icon
          :closable="false"
          style="margin-bottom: var(--wdc-space-4)"
        >
          <p class="alert-text">{{ $t('ssl.mkcertNotInstalledDesc') }}</p>
          <el-button size="small" type="primary" :loading="installingMkcert" @click="installMkcert">
            {{ $t('ssl.installMkcert') }}
          </el-button>
        </el-alert>

        <!-- Loading -->
        <LoadingState v-if="loading && certs.length === 0" :label="$t('ssl.loading')" />

        <!-- Cert list -->
        <div v-else-if="certs.length > 0" class="cert-list">
          <div v-for="cert in certs" :key="cert.domain" class="cert-card" :class="{ 'cert-expiring': cert.expiring, 'cert-expired': cert.expired, 'cert-orphan': cert.orphan }">
            <div class="cert-main">
              <div class="cert-domain">
                <el-icon class="cert-lock"><Lock /></el-icon>
                <span class="cert-name">{{ cert.domain }}</span>
                <!-- F81: status badges -->
                <el-tag v-if="cert.expired" type="danger" size="small" effect="dark" class="cert-badge">{{ $t('ssl.statusExpired') }}</el-tag>
                <el-tag v-else-if="cert.expiring" type="warning" size="small" effect="dark" class="cert-badge">{{ $t('ssl.statusExpiring', { days: cert.daysToExpiry }, cert.daysToExpiry ?? 0) }}</el-tag>
                <el-tag v-else-if="cert.daysToExpiry !== null && cert.daysToExpiry !== undefined" type="success" size="small" effect="plain" class="cert-badge">{{ $t('ssl.statusValid', { days: cert.daysToExpiry }, cert.daysToExpiry) }}</el-tag>
                <el-tag v-if="cert.orphan" type="info" size="small" effect="plain" class="cert-badge" :title="$t('ssl.orphanHint')">{{ $t('ssl.orphan') }}</el-tag>
              </div>
              <div class="cert-meta">
                <span class="cert-meta-item" v-if="cert.aliases?.length">
                  {{ $t('ssl.metaAliases') }}: {{ cert.aliases.join(', ') }}
                </span>
                <span class="cert-meta-item">
                  {{ $t('ssl.metaCreated') }}: {{ formatDate(cert.createdUtc) }}
                </span>
                <span v-if="cert.notAfterUtc" class="cert-meta-item">
                  {{ $t('ssl.metaExpires') }}: {{ formatDate(cert.notAfterUtc) }}
                </span>
                <span v-if="cert.issuer" class="cert-meta-item mono">
                  {{ $t('ssl.metaIssuer') }}: {{ cert.issuer }}
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
                {{ $t('ssl.revoke') }}
              </el-button>
            </div>
          </div>
        </div>

        <el-empty
          v-else-if="!loading"
          :description="$t('ssl.noCerts')"
          :image-size="64"
        />
      </template>
    </div>

    <!-- Generate cert dialog -->
    <el-dialog v-model="showGenerateDialog" :title="$t('ssl.dialog.title')" width="440px">
      <el-form label-position="top" size="small">
        <el-form-item :label="$t('ssl.dialog.domain')" required>
          <el-select
            v-model="genDomain"
            filterable
            allow-create
            :placeholder="$t('ssl.dialog.domainPlaceholder')"
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
        <el-form-item :label="$t('ssl.dialog.aliases')">
          <el-input v-model="genAliases" :placeholder="$t('ssl.dialog.aliasesPlaceholder')" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showGenerateDialog = false">{{ $t('ssl.dialog.cancel') }}</el-button>
        <el-button type="primary" :loading="generating" @click="generateCert" :disabled="!genDomain">
          {{ $t('ssl.dialog.generateBtn') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import LoadingState from '../shared/LoadingState.vue'
import PluginMissingState from '../shared/PluginMissingState.vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { Lock, Plus } from '@element-plus/icons-vue'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders, fetchLatestBinary, fetchSites, installBinary } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { installErrorMessage } from '../../utils/binaries'
import { usePluginsStore } from '../../stores/plugins'

const { t, locale } = useI18n()

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
const loaded = ref(false)
const pluginLoaded = ref(true)
const mkcertInstalled = ref(false)
const caInstalled = ref(false)
const loading = ref(false)
const loadError = ref('')
const installingCA = ref(false)
const installingMkcert = ref(false)
const generating = ref(false)
const revoking = ref(new Set<string>())
const showGenerateDialog = ref(false)
const genDomain = ref('')
const genAliases = ref('')
const availableSites = ref<string[]>([])
const pluginsStore = usePluginsStore()
const pluginDisabled = computed(() => pluginsStore.manifests.some(p => p.id === 'nks.wdc.ssl' && !p.enabled))

/** Czech/English sentence for a failed SSL call; daemon `code` first, raw text as fallback. */
function sslError(data: { code?: string; message?: string } | null, fallbackKey: string): string {
  const known = ['plugin_not_loaded', 'ca_install_failed', 'generate_failed', 'revoke_failed', 'not_found']
  if (data?.code && known.includes(data.code)) {
    return t(`ssl.error.${data.code}`, { detail: data.message ?? '' })
  }
  return t(fallbackKey, { err: data?.message ?? '' })
}

async function loadCerts() {
  loading.value = true
  loadError.value = ''
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/ssl/certs`, { headers: authHeaders() })
    if (r.ok) {
      const data = await r.json()
      certs.value = data.certs ?? []
      pluginLoaded.value = data.pluginLoaded ?? true
      mkcertInstalled.value = data.mkcertInstalled ?? false
      caInstalled.value = data.caInstalled ?? false
      loaded.value = true
    } else {
      loadError.value = t('ssl.loadFailed', { status: r.status })
    }
  } catch (e) {
    loadError.value = t('ssl.daemonUnreachable', { err: errorMessage(e) })
  } finally { loading.value = false }
}

async function loadSites() {
  try {
    const sites = await fetchSites()
    availableSites.value = sites.map(s => s.domain)
  } catch { /* skip */ }
}

async function installMkcert() {
  installingMkcert.value = true
  let version = ''
  try {
    version = (await fetchLatestBinary('mkcert')).version
    await installBinary('mkcert', version)
    ElMessage.success(t('ssl.mkcertInstalledToast', { version }))
    await loadCerts()
  } catch (e) {
    ElMessage.error({ message: installErrorMessage((k, n) => t(k, n ?? {}), e, 'mkcert', version || '?'), duration: 8000, showClose: true })
  } finally {
    installingMkcert.value = false
  }
}

async function installCA() {
  installingCA.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/ssl/install-ca`, {
      method: 'POST',
      headers: authHeaders(),
    })
    const data = await r.json().catch(() => null)
    if (data?.ok) {
      ElMessage.success(t('ssl.caInstalled'))
      await loadCerts()
    } else {
      ElMessage.error(sslError(data, 'ssl.caInstallFailed'))
    }
  } catch (e) {
    ElMessage.error(t('ssl.caInstallFailed', { err: errorMessage(e) }))
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
    const r = await fetch(`${daemonBaseUrl()}/api/ssl/generate`, {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ domain: genDomain.value, aliases }),
    })
    const data = await r.json().catch(() => null)
    if (data?.ok) {
      ElMessage.success(t('toast.certGenerated', { domain: genDomain.value }))
      showGenerateDialog.value = false
      genDomain.value = ''
      genAliases.value = ''
      await loadCerts()
    } else {
      ElMessage.error(sslError(data, 'ssl.generateFailed'))
    }
  } catch (e) {
    ElMessage.error(t('ssl.generateFailed', { err: errorMessage(e) }))
  } finally {
    generating.value = false
  }
}

async function revokeCert(domain: string) {
  try {
    await ElMessageBox.confirm(
      t('ssl.revokeConfirmMessage', { domain }),
      t('ssl.revokeConfirmTitle'),
      { type: 'warning', confirmButtonText: t('ssl.revoke'), cancelButtonText: t('common.cancel') },
    )
  } catch {
    return
  }
  revoking.value.add(domain)
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/ssl/certs/${encodeURIComponent(domain)}`, {
      method: 'DELETE',
      headers: authHeaders(),
    })
    const data = await r.json().catch(() => null)
    if (data?.ok) {
      ElMessage.success(t('toast.certRevoked', { domain }))
      await loadCerts()
    } else {
      ElMessage.error(sslError(data, 'ssl.revokeFailed'))
    }
  } catch (e) {
    ElMessage.error(t('ssl.revokeFailed', { err: errorMessage(e) }))
  } finally {
    revoking.value.delete(domain)
  }
}

function formatDate(iso: string): string {
  try { return new Date(iso).toLocaleDateString(locale.value) } catch { return iso }
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
  padding: 0 0 var(--wdc-space-4);
  margin-bottom: var(--wdc-space-4-5);
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
}

.header-left {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2-5);
}

.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  letter-spacing: -0.02em;
  margin: 0;
}

.header-actions { display: flex; align-items: center; gap: var(--wdc-space-2); }

.page-body { padding: 0 0 var(--wdc-space-5); }

.alert-text {
  margin: 0 0 var(--wdc-space-2);
}

.cert-list {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2);
}

.cert-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-4) var(--wdc-space-4-5);
  transition: border-color 0.15s;
}

.cert-card:hover { border-color: var(--wdc-border-strong); }

.cert-main { flex: 1; min-width: 0; }

.cert-domain {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  margin-bottom: var(--wdc-space-1);
}

.cert-lock { font-size: 1rem; }

.cert-name {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--wdc-text);
}

.cert-meta {
  display: flex;
  flex-wrap: wrap;
  gap: var(--wdc-space-1) var(--wdc-space-4);
  margin-bottom: var(--wdc-space-1);
}

.cert-meta-item {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
}

.cert-paths {
  margin-top: var(--wdc-space-0-5);
}

.cert-path {
  font-size: 0.72rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
}

.cert-actions { flex-shrink: 0; margin-left: var(--wdc-space-4); }

/* F81 expiry / orphan state styling. The border hints at severity so a
   quick glance across many certs highlights what needs attention. */
.cert-card.cert-expiring { border-left: 3px solid var(--wdc-warning); }
.cert-card.cert-expired { border-left: 3px solid var(--wdc-status-error); }
.cert-card.cert-orphan { opacity: 0.7; }
.cert-badge { margin-left: var(--wdc-space-2); }
.cert-fingerprint { font-size: 0.7rem; opacity: 0.7; word-break: break-all; }
</style>
