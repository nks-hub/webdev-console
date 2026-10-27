<template>
  <div class="composer-manager">
    <div class="page-header">
      <div class="header-title-block">
        <h1 class="page-title">{{ $t('composer.title') }}</h1>
        <p class="page-subtitle">{{ $t('composer.subtitle') }}</p>
      </div>
      <div class="header-actions">
        <el-button size="small" :loading="loading" @click="loadAll">{{ $t('common.refresh') }}</el-button>
      </div>
    </div>

    <div class="page-body">
      <PluginMissingState
        v-if="pluginState !== 'ok' && pluginState !== 'unknown'"
        plugin-id="nks.wdc.composer"
        :title="$t('composer.pluginMissingTitle')"
        :description="$t('composer.pluginMissingDesc')"
        @enabled="loadAll"
      />

      <!-- Section 1: Composer version -->
      <EditCard v-else :title="$t('composer.version')">
        <div v-if="versionLoading">
          <el-skeleton :rows="2" animated />
        </div>
        <el-descriptions v-else-if="composerVersion || composerPath" :column="2" border size="small">
          <el-descriptions-item :label="$t('composer.versionLabel')">
            <el-tag v-if="composerVersion" type="success" size="small" class="mono">{{ composerVersion }}</el-tag>
            <span v-else class="text-muted">{{ $t('composer.versionUnknown') }}</span>
          </el-descriptions-item>
          <el-descriptions-item :label="$t('composer.pathLabel')">
            <span class="mono text-muted">{{ composerPath || '—' }}</span>
          </el-descriptions-item>
        </el-descriptions>
        <div v-else class="composer-not-installed">
          <el-icon :size="40" color="var(--el-color-warning)"><WarningFilled /></el-icon>
          <h3>{{ t('composer.notInstalledTitle') }}</h3>
          <p class="text-muted">{{ t('composer.notInstalledHint') }}</p>
          <el-button type="primary" :loading="installing" @click="selfInstall">
            {{ t('composer.installNow') }}
          </el-button>
          <p v-if="installError" class="install-error" role="alert">{{ installError }}</p>
        </div>
        <p v-if="!versionLoading && composerPath && !composerVersion && versionError" class="version-error text-muted">
          {{ $t('composer.versionProbeFailed', { err: versionError }) }}
        </p>
      </EditCard>

      <!-- Section 2: Sites with Composer -->
      <EditCard
        :title="$t('composer.sitesWithComposer')"
        :hint="sitesWithComposer.length > 0 ? String(sitesWithComposer.length) : ''"
        flush-body
      >
        <div v-if="loading" class="cm-loading">
          <el-skeleton :rows="4" animated />
        </div>

        <el-table
          v-else
          :data="sitesWithComposer"
          size="small"
          style="width: 100%"
          :empty-text="allSitesScanned ? $t('composer.noSites') : $t('common.loading')"
        >
          <el-table-column :label="$t('sites.domain')" prop="domain" min-width="180">
            <template #default="{ row }">
              <span class="mono">{{ row.domain }}</span>
            </template>
          </el-table-column>

          <el-table-column label="composer.lock" min-width="160" align="center">
            <template #default="{ row }">
              <el-tag v-if="row.status?.hasLock" type="success" size="small" :aria-label="$t('composer.lockPresent')">
                <el-icon><Check /></el-icon>
              </el-tag>
              <el-tag v-else type="warning" size="small">{{ $t('composer.missingLock') }}</el-tag>
            </template>
          </el-table-column>

          <el-table-column :label="$t('sites.framework')" width="130">
            <template #default="{ row }">
              <el-tag v-if="row.status?.framework" size="small" type="info">{{ row.status.framework }}</el-tag>
              <span v-else class="text-muted">—</span>
            </template>
          </el-table-column>

          <el-table-column :label="$t('common.actions')" width="100" align="right">
            <template #default="{ row }">
              <el-button
                size="small"
                type="primary"
                plain
                @click="goToSitePackages(row.domain)"
              >
                {{ $t('common.open') }}
              </el-button>
            </template>
          </el-table-column>
        </el-table>

        <div v-if="loading && scanProgress > 0" class="scan-progress">
          <el-progress :percentage="scanProgress" :format="() => `${scannedCount}/${sites.length}`" />
        </div>
      </EditCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Check, WarningFilled } from '@element-plus/icons-vue'
import { useI18n } from 'vue-i18n'
import { useSitesStore } from '../../stores/sites'
import { usePluginsStore } from '../../stores/plugins'
import { composerStatus, daemonAuthHeaders as authHeaders, daemonBaseUrl } from '../../api/daemon'
import type { ComposerStatus } from '../../api/types'
import EditCard from '../shared/EditCard.vue'
import PluginMissingState from '../shared/PluginMissingState.vue'

const { t } = useI18n()

const router = useRouter()
const sitesStore = useSitesStore()
const pluginsStore = usePluginsStore()

interface SiteComposerRow {
  domain: string
  status: ComposerStatus | null
  error: boolean
}

const loading = ref(false)
const versionLoading = ref(false)
const composerVersion = ref<string | null>(null)
const composerPath = ref<string | null>(null)
const versionError = ref<string | null>(null)
const installing = ref(false)
const installError = ref<string | null>(null)

const siteRows = ref<SiteComposerRow[]>([])
const scannedCount = ref(0)
const allSitesScanned = ref(false)

const sites = computed(() => sitesStore.sites)

// Without the Composer plugin the daemon cannot run composer at all, so a
// downloaded phar would never show up as installed; say what is missing.
const pluginState = computed<'unknown' | 'ok' | 'missing' | 'disabled'>(() => {
  if (pluginsStore.manifests.length === 0) return 'unknown'
  const manifest = pluginsStore.manifests.find(p => p.id === 'nks.wdc.composer')
  if (!manifest) return 'missing'
  return manifest.enabled ? 'ok' : 'disabled'
})

const sitesWithComposer = computed(() =>
  siteRows.value.filter(r => r.status?.hasComposerJson)
)

const scanProgress = computed(() => {
  if (sites.value.length === 0) return 0
  return Math.round((scannedCount.value / sites.value.length) * 100)
})

async function selfInstall(): Promise<void> {
  installing.value = true
  installError.value = null
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/composer/self-install`, {
      method: 'POST',
      headers: authHeaders(),
    })
    const body = await r.json().catch(() => ({})) as { detail?: string; path?: string }
    if (!r.ok) throw new Error(body.detail || `HTTP ${r.status}`)
    await loadComposerVersion()
    // The phar is on disk even when the version probe cannot run it yet.
    if (!composerPath.value && body.path) composerPath.value = body.path
    ElMessage.success(t('composer.installedToast', { version: composerVersion.value || t('composer.versionUnknown') }))
  } catch (e) {
    installError.value = t('composer.installFailed', { err: e instanceof Error ? e.message : String(e) })
  } finally {
    installing.value = false
  }
}

async function loadComposerVersion(): Promise<void> {
  versionLoading.value = true
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/composer/version`, {
      headers: authHeaders(),
    })
    if (res.ok) {
      const data = await res.json() as { version?: string; path?: string; lastError?: string | null }
      composerVersion.value = data.version ?? null
      composerPath.value = data.path ?? null
      versionError.value = data.lastError ?? null
    }
  } catch {
    // endpoint may not exist yet — silent, show empty state
  } finally {
    versionLoading.value = false
  }
}

async function scanSites(): Promise<void> {
  loading.value = true
  scannedCount.value = 0
  allSitesScanned.value = false
  siteRows.value = []

  const siteList = sites.value.slice()
  if (siteList.length === 0) {
    loading.value = false
    allSitesScanned.value = true
    return
  }

  const results: SiteComposerRow[] = []

  await Promise.allSettled(
    siteList.map(async (site) => {
      let status: ComposerStatus | null = null
      let error = false
      try {
        status = await composerStatus(site.domain)
      } catch {
        error = true
      }
      results.push({ domain: site.domain, status, error })
      scannedCount.value += 1
    })
  )

  results.sort((a, b) => a.domain.localeCompare(b.domain))

  siteRows.value = results
  allSitesScanned.value = true
  loading.value = false
}

async function loadAll(): Promise<void> {
  if (sites.value.length === 0) {
    await sitesStore.load()
  }
  if (pluginsStore.manifests.length === 0) void pluginsStore.loadAll()
  await Promise.all([loadComposerVersion(), scanSites()])
}

function goToSitePackages(domain: string): void {
  void router.push(`/sites/${encodeURIComponent(domain)}/edit?tab=composer`)
}

onMounted(() => {
  void loadAll()
})
</script>

<style scoped>
.composer-manager {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.page-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  padding: 0 0 var(--wdc-space-4);
  flex-shrink: 0;
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
}

.header-title-block {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1);
}

.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  margin: 0;
  letter-spacing: -0.02em;
}

.page-subtitle {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.header-actions {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: center;
}

.page-body {
  flex: 1;
  overflow-y: auto;
  padding: var(--wdc-space-4) 0;
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4);
}

.cm-loading {
  padding: var(--wdc-space-4);
}

.mono {
  font-family: monospace;
  font-size: 12px;
}

.text-muted {
  color: var(--wdc-text-3);
}

.version-error {
  margin: var(--wdc-space-2) 0 0;
  font-size: 0.8rem;
}

.scan-progress {
  margin: var(--wdc-space-3) var(--wdc-space-4);
}

.composer-not-installed {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--wdc-space-2-5);
  padding: var(--wdc-space-6) var(--wdc-space-4);
  text-align: center;
}
.composer-not-installed h3 { margin: var(--wdc-space-1) 0 0; font-size: 1rem; font-weight: 600; }
.composer-not-installed p { margin: 0; max-width: 420px; font-size: 0.85rem; }
.install-error { color: var(--wdc-status-error); }
</style>
