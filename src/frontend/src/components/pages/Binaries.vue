<template>
  <div class="binaries-page">
    <!-- Grid view: one card per managed runtime -->
    <template v-if="!selectedApp">
      <div class="page-header">
        <div class="header-title-block">
          <h1 class="page-title">Binaries</h1>
          <p class="page-subtitle">
            {{ moduleCards.length }} modules ·
            {{ installed.length }} installed ·
            {{ totalAvailable }} available
          </p>
        </div>
        <div class="header-actions">
          <el-input
            v-model="gridSearch"
            placeholder="Filter modules…"
            clearable
            size="small"
            style="width: 220px"
            prefix-icon="Search"
          />
          <el-button size="small" :loading="loading" @click="refresh">Refresh</el-button>
        </div>
      </div>

      <div v-if="loading" class="page-body-pad">
        <el-skeleton :rows="5" animated />
      </div>

      <div v-else-if="filteredModules.length === 0" class="page-body-pad">
        <el-empty :description="gridSearch ? `No modules matching \u2018${gridSearch}\u2019` : 'No catalog entries. Check daemon connection.'" :image-size="80" />
      </div>

      <div v-else class="bin-grid page-body-pad">
        <div
          v-for="card in filteredModules"
          :key="card.app"
          class="bin-card"
          :class="{ 'bin-card--has-installed': card.installedCount > 0 }"
          @click="selectedApp = card.app"
        >
          <div class="bin-card-header">
            <ServiceIcon :service="card.app" :active="card.installedCount > 0" />
            <div class="bin-card-title">
              <span class="bin-card-name">{{ card.app }}</span>
              <span class="bin-card-latest mono">latest v{{ card.latest }}</span>
            </div>
          </div>
          <div class="bin-card-metrics">
            <div class="metric">
              <span class="metric-num mono">{{ card.installedCount }}</span>
              <span class="metric-label">installed</span>
            </div>
            <div class="metric">
              <span class="metric-num mono">{{ card.available }}</span>
              <span class="metric-label">available</span>
            </div>
            <div class="metric" v-if="card.defaultVersion">
              <span class="metric-num mono">{{ card.defaultVersion }}</span>
              <span class="metric-label">default</span>
            </div>
          </div>
          <div class="bin-card-actions">
            <el-button size="small" type="primary" plain class="bin-open-btn">
              Manage versions &rarr;
            </el-button>
          </div>
        </div>
      </div>
    </template>

    <!-- Detail view: merged installed + catalog table for a single module -->
    <template v-else>
      <div class="page-header">
        <div class="header-title-block">
          <el-button size="small" text class="back-btn" @click="selectedApp = null">
            &larr; Back
          </el-button>
          <h1 class="page-title page-title-detail">
            <ServiceIcon :service="selectedApp" :active="true" />
            <span>{{ selectedApp }}</span>
          </h1>
          <p class="page-subtitle">
            {{ detailRows.filter(r => r.installed).length }} of {{ detailRows.length }} versions installed
          </p>
        </div>
        <div class="header-actions">
          <el-button size="small" :loading="loading" @click="refresh">Refresh</el-button>
        </div>
      </div>

      <div class="page-body-pad">
        <el-table
          v-if="detailRows.length > 0"
          :data="detailRows"
          class="bin-detail-table"
          stripe
          row-key="version"
        >
          <el-table-column label="Version" prop="version" min-width="140">
            <template #default="{ row }">
              <span class="mono col-version">{{ row.version }}</span>
              <el-tag v-if="row.isDefault" size="small" type="success" effect="plain" class="col-tag">default</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="Platforms" min-width="180">
            <template #default="{ row }">
              <el-tag
                v-for="p in row.platforms"
                :key="p"
                size="small"
                effect="plain"
                class="platform-tag mono"
              >{{ p }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="Source" width="140">
            <template #default="{ row }">
              <span class="mono col-muted">{{ row.source || '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column label="Status" width="120">
            <template #default="{ row }">
              <el-tag
                v-if="row.installed"
                size="small"
                type="success"
                effect="plain"
              >installed</el-tag>
              <el-tag
                v-else
                size="small"
                type="info"
                effect="plain"
              >available</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="Actions" width="200" align="right">
            <template #default="{ row }">
              <el-button
                v-if="!row.installed"
                size="small"
                type="primary"
                plain
                :loading="installing.has(`${selectedApp}-${row.version}`)"
                @click="install(selectedApp!, row.version)"
              >
                Install
              </el-button>
              <el-button
                v-else
                size="small"
                type="danger"
                plain
                :loading="uninstalling.has(`${selectedApp}-${row.version}`)"
                @click="uninstall(selectedApp!, row.version)"
              >
                Remove
              </el-button>
            </template>
          </el-table-column>
        </el-table>

        <el-empty v-else description="No versions in catalog for this module." :image-size="80" />
      </div>
    </template>

    <!-- Install progress dialog -->
    <el-dialog v-model="progressVisible" title="Installing binary" width="420px" :close-on-click-modal="false">
      <div class="progress-content">
        <p class="progress-msg">{{ progressMessage }}</p>
        <el-progress :percentage="progressPercent" :status="progressError ? 'exception' : (progressDone ? 'success' : undefined)" />
      </div>
      <template #footer>
        <el-button @click="progressVisible = false" :disabled="!progressDone && !progressError">
          {{ progressDone ? 'Done' : progressError ? 'Close' : 'Installing…' }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import {
  fetchBinaryCatalog,
  fetchInstalledBinaries,
  installBinary,
  uninstallBinary,
} from '../../api/daemon'
import type { BinaryRelease, InstalledBinary } from '../../api/types'
import ServiceIcon from '../shared/ServiceIcon.vue'

const loading = ref(false)
const catalog = ref<Record<string, BinaryRelease[]>>({})
const installed = ref<InstalledBinary[]>([])
const installing = ref<Set<string>>(new Set())
const uninstalling = ref<Set<string>>(new Set())

const gridSearch = ref('')
const selectedApp = ref<string | null>(null)

// Progress dialog
const progressVisible = ref(false)
const progressMessage = ref('')
const progressPercent = ref(0)
const progressDone = ref(false)
const progressError = ref(false)

type ModuleCard = {
  app: string
  latest: string
  available: number
  installedCount: number
  defaultVersion: string | null
}

type DetailRow = {
  version: string
  /** De-duped list of available OS/arch combos, e.g. ["windows/x64", "linux/x64"] */
  platforms: string[]
  /** Upstream source label (e.g. "github", "apachelounge") */
  source?: string
  installed: boolean
  isDefault: boolean
}

// Semver descending sort so "latest" always wins and the detail table
// shows newest at the top.
//
// Previously we naively split on /[.-]/ which turned `2.11.0-beta.1` into
// `["2", "11", "0", "beta", "1"]` — `parseInt("beta")` returns NaN → the
// function fell through to `localeCompare`, so caddy pre-releases were
// sorted AFTER stable in the detail table (wrong per semver).
//
// New logic:
//   1. Split off the pre-release segment on the first dash (`.patch-pre`).
//   2. Compare numeric major.minor.patch parts first — if any differ, done.
//   3. If all numerics equal, a release WITHOUT a pre-release beats one
//      WITH (per semver spec: `1.0.0 > 1.0.0-rc.1`).
//   4. If both have pre-release, compare the pre-release strings lexically.
function versionGreater(a: string, b: string): number {
  const [aMain, aPre = ''] = a.split('-', 2)
  const [bMain, bPre = ''] = b.split('-', 2)

  const pa = aMain.split('.').map(s => parseInt(s, 10) || 0)
  const pb = bMain.split('.').map(s => parseInt(s, 10) || 0)

  const len = Math.max(pa.length, pb.length)
  for (let i = 0; i < len; i++) {
    const av = pa[i] ?? 0
    const bv = pb[i] ?? 0
    if (av !== bv) return bv - av
  }

  // Numerics equal. Stable (no pre-release) outranks pre-release.
  if (!aPre && bPre) return -1  // a comes first (higher)
  if (aPre && !bPre) return 1   // b comes first (higher)
  if (!aPre && !bPre) return 0

  // Both have pre-release — descending lexical (beta.2 > beta.1 > alpha.1).
  return bPre.localeCompare(aPre)
}

const moduleCards = computed<ModuleCard[]>(() => {
  const apps = new Set<string>([
    ...Object.keys(catalog.value),
    ...installed.value.map(b => b.app),
  ])
  return Array.from(apps).map(app => {
    const releases = catalog.value[app] ?? []
    const sortedReleases = [...releases].sort((a, b) => versionGreater(a.version, b.version))
    const installedForApp = installed.value.filter(b => b.app === app)
    const def = installedForApp.find(b => b.isDefault)
    return {
      app,
      latest: sortedReleases[0]?.version ?? installedForApp[0]?.version ?? '—',
      available: releases.length,
      installedCount: installedForApp.length,
      defaultVersion: def?.version ?? null,
    }
  }).sort((a, b) => a.app.localeCompare(b.app))
})

const totalAvailable = computed(() =>
  Object.values(catalog.value).reduce((s, arr) => s + arr.length, 0)
)

const filteredModules = computed(() => {
  const q = gridSearch.value.toLowerCase().trim()
  if (!q) return moduleCards.value
  return moduleCards.value.filter(c => c.app.toLowerCase().includes(q))
})

// Build the merged table rows for the selected module: one row per unique
// version, collecting every platform variant (os/arch) from the catalog so
// users can see all 6 cloudflared targets under a single 2026.3.0 row. Also
// annotates installed/default state from the installed list so Install and
// Remove buttons point at the right version. Installed versions missing
// from the catalog (manually dropped binaries) still render so users can
// Remove them.
const detailRows = computed<DetailRow[]>(() => {
  if (!selectedApp.value) return []
  const app = selectedApp.value
  const releases = catalog.value[app] ?? []
  const installedForApp = installed.value.filter(b => b.app === app)

  const byVersion = new Map<string, DetailRow>()
  for (const r of releases) {
    const existing = byVersion.get(r.version)
    const platform = `${r.os}/${r.arch}`
    if (existing) {
      if (!existing.platforms.includes(platform)) existing.platforms.push(platform)
    } else {
      byVersion.set(r.version, {
        version: r.version,
        platforms: [platform],
        source: r.source,
        installed: false,
        isDefault: false,
      })
    }
  }
  for (const i of installedForApp) {
    const row = byVersion.get(i.version) ?? {
      version: i.version,
      platforms: [],
      source: undefined,
      installed: true,
      isDefault: !!i.isDefault,
    }
    row.installed = true
    row.isDefault = !!i.isDefault
    byVersion.set(i.version, row)
  }

  return Array.from(byVersion.values()).sort((a, b) => versionGreater(a.version, b.version))
})

async function refresh() {
  loading.value = true
  try {
    const [cat, inst] = await Promise.all([
      fetchBinaryCatalog(),
      fetchInstalledBinaries(),
    ])
    catalog.value = cat
    installed.value = inst
  } catch (e: any) {
    ElMessage.error(`Failed to load: ${e.message}`)
  } finally {
    loading.value = false
  }
}

async function install(app: string, version: string) {
  const key = `${app}-${version}`
  installing.value.add(key)
  progressVisible.value = true
  progressMessage.value = `Installing ${app} ${version}…`
  progressPercent.value = 10
  progressDone.value = false
  progressError.value = false

  try {
    progressPercent.value = 30
    const result = await installBinary(app, version)
    progressPercent.value = 90

    if (result.ok) {
      progressPercent.value = 100
      progressDone.value = true
      progressMessage.value = `${app} ${version} installed successfully`
      ElMessage.success(`${app} ${version} installed`)
      await refresh()
    } else {
      throw new Error(result.message ?? 'Installation failed')
    }
  } catch (e: any) {
    progressError.value = true
    progressMessage.value = `Error: ${e.message}`
    ElMessage.error(`Install failed: ${e.message}`)
  } finally {
    installing.value.delete(key)
  }
}

async function uninstall(app: string, version: string) {
  const key = `${app}-${version}`
  uninstalling.value.add(key)
  try {
    await uninstallBinary(app, version)
    ElMessage.success(`${app} ${version} removed`)
    await refresh()
  } catch (e: any) {
    ElMessage.error(`Remove failed: ${e.message}`)
  } finally {
    uninstalling.value.delete(key)
  }
}

onMounted(() => { void refresh() })
</script>

<style scoped>
.binaries-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px 0;
  margin-bottom: 16px;
  gap: 12px;
}

.header-title-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: 0.01em;
  margin: 0;
}

.page-title-detail {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-subtitle {
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.back-btn {
  padding: 2px 6px !important;
  height: auto !important;
  font-size: 0.76rem !important;
}

.page-body-pad {
  padding: 0 24px 24px;
}

/* Grid of module cards */
.bin-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 14px;
}

.bin-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-left: 3px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 16px 18px;
  display: flex;
  flex-direction: column;
  gap: 14px;
  cursor: pointer;
  transition: border-color 0.12s, background 0.12s;
}

.bin-card--has-installed {
  border-left-color: var(--wdc-accent);
}

.bin-card:hover {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-surface-2);
}
.bin-card--has-installed:hover {
  border-left-color: var(--wdc-accent);
}

.bin-card-header {
  display: flex;
  align-items: center;
  gap: 12px;
}

.bin-card-title {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.bin-card-name {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
  text-transform: capitalize;
  letter-spacing: 0.005em;
}

.bin-card-latest {
  font-size: 0.68rem;
  color: var(--wdc-text-3);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.bin-card-metrics {
  display: flex;
  gap: 24px;
}

.metric {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.metric-num {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
  line-height: 1.1;
}

.metric-label {
  font-size: 0.62rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
}

.bin-card-actions {
  padding-top: 4px;
  border-top: 1px solid var(--wdc-border);
  margin-top: auto;
}

.bin-open-btn {
  width: 100%;
  font-weight: 600;
}

/* Detail table */
.bin-detail-table {
  background: transparent;
}

.col-version {
  font-weight: 600;
  color: var(--wdc-text);
}

.col-tag {
  margin-left: 8px;
}

.col-muted {
  color: var(--wdc-text-3);
}

.platform-tag {
  margin-right: 4px;
  margin-bottom: 2px;
  font-size: 0.68rem !important;
  font-weight: 600 !important;
  letter-spacing: 0.02em;
}

.progress-content {
  padding: 8px 0;
}

.progress-msg {
  font-size: 0.86rem;
  margin-bottom: 12px;
  color: var(--wdc-text);
}
</style>
