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
      <!-- Section 1: Composer version -->
      <el-card class="cm-card" shadow="never">
        <template #header>
          <span class="cm-section-title">{{ $t('composer.version') }}</span>
        </template>

        <div v-if="versionLoading" style="padding: 8px 0">
          <el-skeleton :rows="2" animated />
        </div>
        <el-descriptions v-else-if="composerVersion" :column="2" border size="small">
          <el-descriptions-item label="Version">
            <el-tag type="success" size="small" class="mono">{{ composerVersion }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="Path">
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
          <p v-if="installError" class="install-error">{{ installError }}</p>
        </div>
      </el-card>

      <!-- Section 2: Sites with Composer -->
      <el-card class="cm-card" shadow="never">
        <template #header>
          <div class="cm-card-header">
            <span class="cm-section-title">{{ $t('composer.sitesWithComposer') }}</span>
            <el-tag v-if="sitesWithComposer.length > 0" type="info" size="small">{{ sitesWithComposer.length }}</el-tag>
          </div>
        </template>

        <div v-if="loading" style="padding: 8px 0">
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

          <el-table-column label="composer.json" width="120" align="center">
            <template #default="{ row }">
              <el-tag v-if="row.status?.hasComposerJson" type="success" size="small">
                <el-icon><Check /></el-icon>
              </el-tag>
              <el-tag v-else type="info" size="small">—</el-tag>
            </template>
          </el-table-column>

          <el-table-column label="composer.lock" width="120" align="center">
            <template #default="{ row }">
              <el-tag v-if="row.status?.hasLock" type="success" size="small">
                <el-icon><Check /></el-icon>
              </el-tag>
              <el-tag v-else-if="row.status?.hasComposerJson" type="warning" size="small">missing</el-tag>
              <el-tag v-else type="info" size="small">—</el-tag>
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
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { Check, WarningFilled } from '@element-plus/icons-vue'
import { useI18n } from 'vue-i18n'
import { useSitesStore } from '../../stores/sites'
import { composerStatus } from '../../api/daemon'
import type { ComposerStatus } from '../../api/types'

const { t } = useI18n()

const router = useRouter()
const sitesStore = useSitesStore()

interface SiteComposerRow {
  domain: string
  status: ComposerStatus | null
  error: boolean
}

const loading = ref(false)
const versionLoading = ref(false)
const composerVersion = ref<string | null>(null)
const composerPath = ref<string | null>(null)
const installing = ref(false)
const installError = ref<string | null>(null)

const siteRows = ref<SiteComposerRow[]>([])
const scannedCount = ref(0)
const allSitesScanned = ref(false)

const sites = computed(() => sitesStore.sites)

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
    const r = await fetch(`${getBase()}/api/composer/self-install`, {
      method: 'POST',
      headers: authHeaders(),
    })
    if (!r.ok) {
      const body = await r.json().catch(() => ({}))
      throw new Error((body as any).detail || `HTTP ${r.status}`)
    }
    await loadComposerVersion()
  } catch (e: any) {
    installError.value = e.message ?? String(e)
  } finally {
    installing.value = false
  }
}

async function loadComposerVersion(): Promise<void> {
  versionLoading.value = true
  try {
    const res = await fetch(`${getBase()}/api/composer/version`, {
      headers: authHeaders(),
    })
    if (res.ok) {
      const data = await res.json() as { version?: string; path?: string }
      composerVersion.value = data.version ?? null
      composerPath.value = data.path ?? null
    }
  } catch {
    // endpoint may not exist yet — silent, show empty state
  } finally {
    versionLoading.value = false
  }
}

function getBase(): string {
  const preloadPort = (window as any).daemonApi?.getPort?.()
  if (typeof preloadPort === 'number' && preloadPort > 0) {
    return `http://localhost:${preloadPort}`
  }
  const urlPort = new URLSearchParams(window.location.search).get('port')
  if (urlPort && /^\d+$/.test(urlPort)) {
    return `http://localhost:${parseInt(urlPort, 10)}`
  }
  return 'http://localhost:5199'
}

function authHeaders(): Record<string, string> {
  const preloadToken = (window as any).daemonApi?.getToken?.() || ''
  const urlToken = new URLSearchParams(window.location.search).get('token') || ''
  const token = preloadToken || urlToken
  const h: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) h['Authorization'] = `Bearer ${token}`
  return h
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

  // Sort: sites with composer.json first, then alphabetically
  results.sort((a, b) => {
    const aHas = a.status?.hasComposerJson ? 1 : 0
    const bHas = b.status?.hasComposerJson ? 1 : 0
    if (bHas !== aHas) return bHas - aHas
    return a.domain.localeCompare(b.domain)
  })

  siteRows.value = results
  allSitesScanned.value = true
  loading.value = false
}

async function loadAll(): Promise<void> {
  if (sites.value.length === 0) {
    await sitesStore.load()
  }
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
  padding: 24px 28px 16px;
  flex-shrink: 0;
}

.header-title-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.page-title {
  font-size: 1.35rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0;
}

.page-subtitle {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.header-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.page-body {
  flex: 1;
  overflow-y: auto;
  padding: 0 28px 28px;
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.cm-card {
  border-radius: 10px;
}

.cm-card-header {
  display: flex;
  align-items: center;
  gap: 10px;
}

.cm-section-title {
  font-weight: 600;
  font-size: 0.9rem;
}

.mono {
  font-family: monospace;
  font-size: 12px;
}

.text-muted {
  color: var(--el-text-color-secondary);
}

.scan-progress {
  margin-top: 12px;
  padding: 0 4px;
}

.composer-not-installed {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 32px 16px;
  text-align: center;
}
.composer-not-installed h3 { margin: 4px 0 0; font-size: 1rem; font-weight: 600; }
.composer-not-installed p { margin: 0; max-width: 420px; font-size: 0.85rem; }
.install-error { color: var(--el-color-danger); }
</style>
