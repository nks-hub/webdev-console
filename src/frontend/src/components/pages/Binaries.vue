<template>
  <div class="binaries-page">
    <div class="flex items-center justify-between mb-5 px-6 pt-6">
      <div>
        <h1 class="text-xl font-bold text-white">Binaries</h1>
        <p class="text-sm text-slate-400 mt-0.5">Manage installed runtimes and server binaries</p>
      </div>
      <el-button size="small" @click="refresh" :loading="loading" title="Refresh catalog and installed list">
        Refresh
      </el-button>
    </div>

    <!-- Installed binaries section -->
    <div class="px-6 mb-6">
      <div class="section-header">
        <span class="section-title">Installed</span>
        <el-tag size="small" type="success" effect="plain">{{ installed.length }} installed</el-tag>
      </div>

      <div v-if="installed.length === 0 && !loading" class="empty-box">
        <span class="text-slate-400 text-sm">No binaries installed yet. Install from catalog below.</span>
      </div>

      <div v-else class="installed-grid">
        <div
          v-for="bin in installed"
          :key="`${bin.app}-${bin.version}`"
          class="installed-card"
        >
          <div class="installed-header">
            <div>
              <span class="installed-app">{{ bin.app }}</span>
              <span class="installed-version">{{ bin.version }}</span>
              <el-tag v-if="bin.isDefault" size="small" type="success" effect="plain" class="ml-2">default</el-tag>
            </div>
            <el-button
              size="small"
              type="danger"
              plain
              :loading="uninstalling.has(`${bin.app}-${bin.version}`)"
              @click="uninstall(bin.app, bin.version)"
            >
              Remove
            </el-button>
          </div>
          <div class="installed-path">{{ bin.path }}</div>
        </div>
      </div>
    </div>

    <el-divider />

    <!-- Catalog section -->
    <div class="px-6 pb-6">
      <div class="section-header mb-4">
        <span class="section-title">Available Catalog</span>
        <el-input
          v-model="catalogSearch"
          placeholder="Filter apps..."
          clearable
          size="small"
          style="width: 200px"
          prefix-icon="Search"
        />
      </div>

      <div v-if="loading" class="px-0">
        <el-skeleton :rows="6" animated />
      </div>

      <el-collapse v-else v-model="openApps" class="catalog-collapse">
        <el-collapse-item
          v-for="(releases, app) in filteredCatalog"
          :key="app"
          :name="app"
        >
          <template #title>
            <div class="catalog-app-header">
              <span class="catalog-app-name">{{ app }}</span>
              <el-tag size="small" type="info" effect="plain" class="ml-2">{{ releases.length }} releases</el-tag>
              <el-tag
                v-if="installedApps.has(app)"
                size="small"
                type="success"
                effect="plain"
                class="ml-1"
              >
                installed
              </el-tag>
            </div>
          </template>

          <div class="release-list">
            <div
              v-for="release in releases"
              :key="release.version"
              class="release-row"
            >
              <div class="release-info">
                <span class="release-version">{{ release.version }}</span>
                <span v-if="release.platform" class="release-meta">{{ release.platform }}</span>
                <span v-if="release.size" class="release-meta">{{ formatSize(release.size) }}</span>
                <el-tag
                  v-if="isInstalled(app, release.version)"
                  size="small"
                  type="success"
                  effect="plain"
                >
                  installed
                </el-tag>
              </div>
              <div class="release-actions">
                <el-button
                  v-if="!isInstalled(app, release.version)"
                  size="small"
                  type="primary"
                  plain
                  :loading="installing.has(`${app}-${release.version}`)"
                  @click="install(app, release.version)"
                >
                  Install
                </el-button>
                <el-button
                  v-else
                  size="small"
                  type="danger"
                  plain
                  :loading="uninstalling.has(`${app}-${release.version}`)"
                  @click="uninstall(app, release.version)"
                >
                  Remove
                </el-button>
              </div>
            </div>
          </div>
        </el-collapse-item>
      </el-collapse>

      <el-empty
        v-if="!loading && Object.keys(filteredCatalog).length === 0"
        description="No catalog entries. Check daemon connection."
        :image-size="80"
      />
    </div>

    <!-- Install progress dialog -->
    <el-dialog v-model="progressVisible" title="Installing..." width="400px" :close-on-click-modal="false">
      <div class="progress-content">
        <p class="text-sm text-slate-300 mb-3">{{ progressMessage }}</p>
        <el-progress :percentage="progressPercent" :status="progressError ? 'exception' : (progressDone ? 'success' : undefined)" />
      </div>
      <template #footer>
        <el-button @click="progressVisible = false" :disabled="!progressDone && !progressError">
          {{ progressDone ? 'Done' : progressError ? 'Close' : 'Installing...' }}
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

const loading = ref(false)
const catalog = ref<Record<string, BinaryRelease[]>>({})
const installed = ref<InstalledBinary[]>([])
const installing = ref<Set<string>>(new Set())
const uninstalling = ref<Set<string>>(new Set())
const catalogSearch = ref('')
const openApps = ref<string[]>([])

// Progress dialog
const progressVisible = ref(false)
const progressMessage = ref('')
const progressPercent = ref(0)
const progressDone = ref(false)
const progressError = ref(false)

const installedApps = computed(() => new Set(installed.value.map(b => b.app)))

const filteredCatalog = computed(() => {
  const q = catalogSearch.value.toLowerCase()
  if (!q) return catalog.value
  return Object.fromEntries(
    Object.entries(catalog.value).filter(([app]) => app.toLowerCase().includes(q))
  )
})

function isInstalled(app: string, version: string): boolean {
  return installed.value.some(b => b.app === app && b.version === version)
}

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
  progressMessage.value = `Installing ${app} ${version}...`
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

function formatSize(bytes: number): string {
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

onMounted(() => { void refresh() })
</script>

<style scoped>
.binaries-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.section-header {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 14px;
}

.section-title {
  font-size: 0.78rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--el-text-color-secondary);
}

.empty-box {
  padding: 24px;
  background: var(--wdc-surface);
  border: 1px dashed var(--el-border-color);
  border-radius: 8px;
  text-align: center;
}

.installed-grid {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.installed-card {
  background: var(--wdc-surface);
  border: 1px solid var(--el-border-color);
  border-radius: 8px;
  padding: 12px 16px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.installed-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.installed-app {
  font-size: 0.88rem;
  font-weight: 600;
  color: var(--el-text-color-primary);
  margin-right: 8px;
}

.installed-version {
  font-size: 0.78rem;
  font-family: monospace;
  color: var(--el-color-primary);
}

.installed-path {
  font-size: 0.72rem;
  font-family: monospace;
  color: var(--el-text-color-secondary);
  word-break: break-all;
}

.catalog-collapse {
  background: transparent;
  border: 1px solid var(--el-border-color);
  border-radius: 8px;
  overflow: hidden;
}

.catalog-app-header {
  display: flex;
  align-items: center;
}

.catalog-app-name {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--el-text-color-primary);
  text-transform: capitalize;
}

.release-list {
  display: flex;
  flex-direction: column;
  gap: 0;
  padding: 4px 0;
}

.release-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-bottom: 1px solid var(--el-border-color-lighter, #2a2d3a);
  transition: background 0.1s;
}

.release-row:last-child { border-bottom: none; }
.release-row:hover { background: var(--wdc-elevated, #242736); }

.release-info {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.release-version {
  font-size: 0.85rem;
  font-family: monospace;
  color: var(--el-text-color-primary);
  font-weight: 500;
}

.release-meta {
  font-size: 0.75rem;
  color: var(--el-text-color-secondary);
}

.release-actions { flex-shrink: 0; }

.progress-content {
  padding: 8px 0;
}
</style>
