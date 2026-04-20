<template>
  <div class="site-composer">
    <!-- F75: loading skeleton now announces the current operation so users
         during multi-second composer calls (install / outdated fetch) know
         what they're waiting on instead of a silent skeleton. -->
    <div v-if="loading" class="composer-loading">
      <div class="composer-loading-label">
        <el-icon class="is-loading"><Loading /></el-icon>
        <span>{{ currentOperation }}</span>
      </div>
    </div>
    <div v-else-if="running && currentOperation" class="composer-running-banner">
      <el-icon class="is-loading"><Loading /></el-icon>
      <span>{{ currentOperation }}</span>
    </div>

    <!-- Error loading status -->
    <el-alert
      v-else-if="loadError"
      :title="loadError"
      type="error"
      show-icon
      :closable="false"
      class="composer-alert"
    />

    <!-- No composer.json detected -->
    <div v-else-if="status && !status.hasComposerJson" class="composer-empty">
      <el-empty :description="$t('sites.composer.noProject')">
        <template #extra>
          <p class="composer-empty-hint">{{ $t('sites.composer.initHint') }}</p>
          <el-button type="primary" @click="openInitDialog">
            {{ $t('sites.composer.initTitle') }}
          </el-button>
        </template>
      </el-empty>
    </div>

    <!-- Main composer panel -->
    <div v-else-if="status" class="composer-panel">
      <!-- Framework auto-install suggestion banner -->
      <el-alert
        v-if="status.installSuggestion && !isBannerDismissed"
        :title="$t('sites.composer.frameworkDetected', { framework: status.installSuggestion.framework })"
        type="warning"
        show-icon
        :closable="false"
        class="composer-framework-banner"
      >
        <template #default>
          <p class="banner-description">{{ $t('sites.composer.installPrompt') }}</p>
          <div class="banner-actions">
            <el-button type="primary" size="small" :loading="running" @click="runInstall">
              {{ $t('sites.composer.installNow') }}
            </el-button>
            <el-button size="small" @click="dismissBanner">
              {{ $t('sites.composer.dismiss') }}
            </el-button>
          </div>
        </template>
      </el-alert>

      <!-- Status summary -->
      <el-card class="composer-card" shadow="never">
        <template #header>
          <span class="card-title">{{ $t('sites.composer.tabLabel') }}</span>
        </template>

        <div class="status-row">
          <div class="status-item">
            <span class="status-label">composer.json</span>
            <el-tag type="success" size="small">
              <el-icon><Check /></el-icon> present
            </el-tag>
          </div>

          <div class="status-item">
            <span class="status-label">{{ $t('sites.composer.lockStatus') }}</span>
            <el-tag :type="status.hasLock ? 'success' : 'warning'" size="small">
              <el-icon v-if="status.hasLock"><Check /></el-icon>
              <el-icon v-else><Warning /></el-icon>
              {{ status.hasLock ? 'present' : 'missing' }}
            </el-tag>
          </div>

          <div class="status-item">
            <span class="status-label">PHP</span>
            <el-tag v-if="status.phpVersion" type="info" size="small">
              {{ status.phpVersion }}
            </el-tag>
            <span v-else class="status-na">—</span>
          </div>

          <div class="status-item">
            <span class="status-label">{{ $t('sites.composer.packageCount', { count: status.packages.length }) }}</span>
          </div>
        </div>

        <!-- Actions bar — directly below status row -->
        <div class="actions-row">
          <el-button
            type="primary"
            :loading="running"
            @click="runInstall"
          >
            {{ $t('sites.composer.install') }}
          </el-button>

          <el-button
            :loading="running"
            @click="showRequireDialog = true"
          >
            {{ $t('sites.composer.require') }}
          </el-button>

          <el-button
            :loading="diagnosing"
            @click="runDiagnose"
          >
            {{ $t('sites.composer.checkConflicts') }}
          </el-button>

          <el-button
            circle
            :icon="Refresh"
            :loading="loading"
            :title="$t('common.refresh')"
            @click="loadStatus"
          />
        </div>

        <!-- Packages table -->
        <el-table
          v-if="status.packages.length > 0"
          :data="tableRows"
          class="pkg-table"
          size="small"
          style="margin-top: 16px"
        >
          <!-- Name -->
          <el-table-column :label="$t('sites.composer.colName')" min-width="260" prop="name" sortable>
            <template #default="{ row }">
              <div class="pkg-name-cell">
                <span
                  v-if="outdatedMap[row.name]"
                  class="pkg-outdated-dot"
                  :title="t('sites.composer.outdatedBadge')"
                />
                <el-popover
                  placement="right"
                  trigger="hover"
                  :show-after="400"
                  :hide-after="200"
                  width="360"
                  @show="loadPackageInfo(row.name)"
                >
                  <template #reference>
                    <span
                      class="pkg-name-link mono"
                      :title="t('sites.composer.openOnPackagist', { name: row.name })"
                      @click.stop="openPackagist(row.raw)"
                    >{{ row.name }}</span>
                  </template>
                  <div v-if="pkgInfoLoading[row.name]" class="pkg-info-loading">Načítám…</div>
                  <div v-else-if="pkgInfoCache[row.name]" class="pkg-info">
                    <div class="pkg-info-header mono">{{ row.name }}</div>
                    <div v-if="pkgInfoCache[row.name].abandoned" class="pkg-abandoned">
                      <el-icon><WarningFilled /></el-icon>
                      {{ abandonedLabel(pkgInfoCache[row.name].abandoned) }}
                    </div>
                    <p class="pkg-desc">{{ pkgInfoCache[row.name].description }}</p>
                    <div class="pkg-stats mono">
                      <span>⭐ {{ pkgInfoCache[row.name].favers }}</span>
                      <span>↓ {{ pkgInfoCache[row.name].downloads }}</span>
                    </div>
                    <div v-if="outdatedMap[row.name]" class="pkg-update-hint">
                      {{ t('sites.composer.updateAvailable', { latest: outdatedMap[row.name].latest }) }}
                    </div>
                    <a
                      v-if="pkgInfoCache[row.name].repository"
                      class="pkg-repo-link"
                      href="#"
                      @click.prevent="openExternal(pkgInfoCache[row.name].repository)"
                    >{{ t('sites.composer.openRepository') }}</a>
                  </div>
                  <div v-else class="pkg-info-loading">—</div>
                </el-popover>
              </div>
            </template>
          </el-table-column>

          <!-- Required constraint -->
          <el-table-column :label="$t('sites.composer.colRequired')" width="120" prop="required" sortable>
            <template #default="{ row }">
              <span class="mono">{{ row.constraint }}</span>
            </template>
          </el-table-column>

          <!-- Installed version -->
          <el-table-column :label="$t('sites.composer.colInstalled')" width="130" prop="installed" sortable>
            <template #default="{ row }">
              <span
                class="mono"
                :class="outdatedMap[row.name] ? 'version-outdated' : ''"
              >{{ outdatedMap[row.name]?.version ?? '—' }}</span>
            </template>
          </el-table-column>

          <!-- Latest version -->
          <el-table-column :label="$t('sites.composer.colLatest')" width="150" prop="latest" sortable>
            <template #default="{ row }">
              <span class="mono">{{ outdatedMap[row.name]?.latest ?? '—' }}</span>
              <el-tooltip
                v-if="outdatedMap[row.name] && isMajorJump(row.constraint, outdatedMap[row.name].latest)"
                :content="$t('sites.composer.majorJump')"
                placement="top"
              >
                <el-icon class="major-jump-icon"><Warning /></el-icon>
              </el-tooltip>
            </template>
          </el-table-column>

          <!-- Status -->
          <el-table-column :label="$t('sites.composer.colStatus')" width="120" prop="status" sortable>
            <template #default="{ row }">
              <el-tag
                v-if="pkgInfoCache[row.name]?.abandoned"
                type="danger"
                size="small"
              >{{ $t('sites.composer.statusAbandoned') }}</el-tag>
              <el-tag
                v-else-if="outdatedMap[row.name]"
                type="warning"
                size="small"
              >{{ $t('sites.composer.statusOutdated') }}</el-tag>
              <el-tag
                v-else
                type="success"
                size="small"
              >{{ $t('sites.composer.statusOk') }}</el-tag>
            </template>
          </el-table-column>

          <!-- Actions -->
          <el-table-column :label="$t('common.actions')" width="100" align="right">
            <template #default="{ row }">
              <el-button
                size="small"
                :icon="Delete"
                circle
                type="danger"
                plain
                :title="t('sites.composer.removeTitle')"
                @click="confirmRemove(row.raw)"
              />
            </template>
          </el-table-column>
        </el-table>
      </el-card>

      <!-- Last command output -->
      <el-card v-if="lastResult" class="composer-card composer-output" shadow="never">
        <template #header>
          <div class="output-header">
            <span class="card-title">{{ $t('sites.composer.lastOutput') }}</span>
            <el-tag
              :type="lastResult.exitCode === 0 ? 'success' : 'danger'"
              size="small"
            >
              {{ $t('sites.composer.exitCode') }}: {{ lastResult.exitCode }}
            </el-tag>
          </div>
        </template>

        <el-collapse v-model="outputOpen">
          <el-collapse-item v-if="lastResult.stdout" name="stdout" title="stdout">
            <pre class="output-pre">{{ lastResult.stdout }}</pre>
          </el-collapse-item>
          <el-collapse-item v-if="lastResult.stderr" name="stderr" title="stderr">
            <pre class="output-pre output-pre--err">{{ lastResult.stderr }}</pre>
          </el-collapse-item>
        </el-collapse>
      </el-card>
    </div>

    <!-- Init composer dialog -->
    <el-dialog
      v-model="showInitDialog"
      :title="$t('sites.composer.initTitle')"
      width="440px"
      :close-on-click-modal="!running"
    >
      <el-form label-position="top">
        <el-form-item :label="$t('sites.composer.initName')">
          <el-input v-model="initForm.name" :disabled="running" />
        </el-form-item>
        <el-form-item :label="$t('sites.composer.initDescription')">
          <el-input v-model="initForm.description" :disabled="running" />
        </el-form-item>
        <el-form-item :label="$t('sites.composer.initType')">
          <el-select v-model="initForm.type" :disabled="running" style="width: 100%">
            <el-option label="project" value="project" />
            <el-option label="library" value="library" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('sites.composer.initStability')">
          <el-select v-model="initForm.stability" :disabled="running" style="width: 100%">
            <el-option label="stable" value="stable" />
            <el-option label="dev" value="dev" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showInitDialog = false" :disabled="running">
          {{ $t('common.cancel') }}
        </el-button>
        <el-button type="primary" :loading="running" @click="runInit">
          {{ $t('sites.composer.initTitle') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- Diagnose / conflicts dialog -->
    <el-dialog
      v-model="showDiagnoseDialog"
      :title="$t('sites.composer.conflictsTitle')"
      width="520px"
    >
      <div v-if="diagnoseResult">
        <div v-if="diagnoseResult.errors.length === 0 && diagnoseResult.warnings.length === 0" class="diagnose-ok">
          <el-icon color="var(--el-color-success)"><Check /></el-icon>
          {{ $t('sites.composer.noConflicts') }}
        </div>
        <template v-else>
          <div v-for="e in diagnoseResult.errors" :key="e" class="diagnose-line diagnose-error">
            <el-icon><Warning /></el-icon> {{ e }}
          </div>
          <div v-for="w in diagnoseResult.warnings" :key="w" class="diagnose-line diagnose-warning">
            <el-icon><Warning /></el-icon> {{ w }}
          </div>
        </template>
      </div>
    </el-dialog>

    <!-- Require package dialog -->
    <el-dialog
      v-model="showRequireDialog"
      :title="$t('sites.composer.require')"
      width="480px"
      :close-on-click-modal="!running"
    >
      <el-form @submit.prevent="runRequire">
        <el-form-item :label="$t('sites.composer.packageName')">
          <el-autocomplete
            v-model="requirePackage"
            :fetch-suggestions="searchPackagist"
            :placeholder="$t('sites.composer.packageNameHint')"
            class="pkg-search"
            :disabled="running"
            :debounce="300"
            autofocus
            @select="onSelectPackage"
            @keyup.enter="runRequire"
          >
            <template #default="{ item }">
              <div class="suggestion-row">
                <span class="suggestion-name mono">{{ item.value }}</span>
                <span class="suggestion-desc">{{ item.description?.slice(0, 60) }}</span>
              </div>
            </template>
          </el-autocomplete>
        </el-form-item>
        <el-form-item v-if="availableVersions.length" :label="$t('sites.composer.versionSelectPlaceholder')">
          <el-select
            v-model="selectedVersion"
            size="small"
            :placeholder="$t('sites.composer.versionSelectPlaceholder')"
            style="width: 200px"
          >
            <el-option label="(auto)" value="" />
            <el-option
              v-for="v in availableVersions"
              :key="v"
              :label="v"
              :value="v"
            />
          </el-select>
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="showRequireDialog = false" :disabled="running">
          {{ $t('common.cancel') }}
        </el-button>
        <el-button
          type="primary"
          :loading="running"
          :disabled="!requirePackage.trim()"
          @click="runRequire"
        >
          {{ $t('sites.composer.require') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Check, Warning, Refresh, WarningFilled, Delete, Loading } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { composerStatus, composerInstall, composerRequire, composerRemove, composerOutdated, composerInit, composerDiagnose } from '../../api/daemon'
import type { ComposerStatus, ComposerCommandResult } from '../../api/types'

const props = defineProps<{ domain: string }>()

const { t } = useI18n()

const loading = ref(false)
const loadError = ref<string | null>(null)
const status = ref<ComposerStatus | null>(null)

// F75: label describing the operation currently in flight — displayed
// next to the skeleton while loading and in a slim banner while running.
const currentOperation = ref<string>('')
const running = ref(false)
const lastResult = ref<ComposerCommandResult | null>(null)
const outputOpen = ref<string[]>(['stdout', 'stderr'])

const showRequireDialog = ref(false)
const requirePackage = ref('')
const removing = ref<string | null>(null)

const showInitDialog = ref(false)
const initForm = ref({ name: '', description: '', type: 'project', stability: 'stable' })

const diagnosing = ref(false)
const showDiagnoseDialog = ref(false)
const diagnoseResult = ref<{ warnings: string[]; errors: string[] } | null>(null)

const DISMISS_KEY = computed(() => `wdc-composer-dismiss-${props.domain}`)
const isBannerDismissed = ref(false)

// Outdated map keyed by package name
const outdatedMap = ref<Record<string, { version?: string; latest: string; status: string }>>({})

// Package info cache from packagist.org
const pkgInfoCache = ref<Record<string, any>>({})
const pkgInfoLoading = ref<Record<string, boolean>>({})

// Version picker state
const availableVersions = ref<string[]>([])
const selectedVersion = ref<string>('')

interface TableRow {
  raw: string
  name: string
  constraint: string
}

const tableRows = computed<TableRow[]>(() => {
  if (!status.value) return []
  return status.value.packages.map(pkg => {
    const parts = pkg.split(':')
    return {
      raw: pkg,
      name: parts[0],
      constraint: parts[1] ?? '',
    }
  })
})

function loadDismissState(): void {
  isBannerDismissed.value = localStorage.getItem(DISMISS_KEY.value) === '1'
}

function dismissBanner(): void {
  localStorage.setItem(DISMISS_KEY.value, '1')
  isBannerDismissed.value = true
}

async function loadOutdated(): Promise<void> {
  try {
    const result = await composerOutdated(props.domain)
    const map: Record<string, { version?: string; latest: string; status: string }> = {}
    for (const entry of result.installed) {
      if (entry.name && entry.latest) {
        map[entry.name] = { version: entry.version ?? undefined, latest: entry.latest, status: entry.latestStatus ?? '' }
      }
    }
    outdatedMap.value = map
  } catch {
    // Non-critical — silently ignore
  }
}

async function loadStatus(): Promise<void> {
  loading.value = true
  loadError.value = null
  currentOperation.value = 'Loading composer status from composer.json…'
  try {
    status.value = await composerStatus(props.domain)
    currentOperation.value = 'Checking for outdated packages…'
    await loadOutdated()
  } catch (err: unknown) {
    loadError.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
    currentOperation.value = ''
  }
}

async function loadPackageInfo(name: string): Promise<void> {
  if (pkgInfoCache.value[name] !== undefined || pkgInfoLoading.value[name]) return
  pkgInfoLoading.value[name] = true
  try {
    const r = await fetch(`https://packagist.org/packages/${name}.json`)
    if (r.ok) {
      const d = await r.json()
      pkgInfoCache.value[name] = {
        description: d.package?.description ?? null,
        abandoned: d.package?.abandoned ?? null,
        favers: d.package?.favers ?? 0,
        downloads: d.package?.downloads?.total ?? 0,
        repository: d.package?.repository ?? null,
      }
    } else {
      pkgInfoCache.value[name] = null
    }
  } catch {
    pkgInfoCache.value[name] = null
  } finally {
    pkgInfoLoading.value[name] = false
  }
}

function abandonedLabel(v: boolean | string): string {
  if (v === true) return t('sites.composer.abandoned')
  if (typeof v === 'string') return t('sites.composer.abandonedReplacedBy', { name: v })
  return t('sites.composer.abandoned')
}

async function searchPackagist(query: string, cb: (items: any[]) => void): Promise<void> {
  if (!query || query.length < 2) { cb([]); return }
  try {
    const r = await fetch(`https://packagist.org/search.json?q=${encodeURIComponent(query)}&per_page=10`)
    if (r.ok) {
      const d = await r.json()
      cb((d.results ?? []).map((p: any) => ({
        value: p.name,
        description: p.description,
        downloads: p.downloads,
        favers: p.favers,
      })))
      return
    }
  } catch { /* silent */ }
  cb([])
}

async function onSelectPackage(item: any): Promise<void> {
  requirePackage.value = item.value
  availableVersions.value = []
  selectedVersion.value = ''
  try {
    const r = await fetch(`https://packagist.org/packages/${item.value}.json`)
    if (r.ok) {
      const d = await r.json()
      const versions = Object.keys(d.package?.versions || {})
        .filter(v => !v.startsWith('dev-') && !/-(alpha|beta|rc)\d*$/i.test(v))
        .sort((a, b) => b.localeCompare(a, undefined, { numeric: true }))
        .slice(0, 8)
      availableVersions.value = versions
      selectedVersion.value = versions[0] ?? ''
    }
  } catch { /* silent */ }
}

function openExternal(url: string): void {
  if (window.electronAPI?.openExternal) {
    ;window.electronAPI.openExternal(url)
  } else {
    window.open(url, '_blank')
  }
}

async function runInstall(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('sites.composer.install'),
      { type: 'info', confirmButtonText: t('common.apply'), cancelButtonText: t('common.cancel') }
    )
  } catch {
    return
  }

  running.value = true
  currentOperation.value = 'Running composer install — downloading dependencies…'
  try {
    const result = await composerInstall(props.domain)
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.success'))
      dismissBanner()
      await loadStatus()
    } else {
      ElMessage.error(t('sites.composer.failed', { code: result.exitCode }))
    }
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    running.value = false
    currentOperation.value = ''
  }
}

async function runRequire(): Promise<void> {
  const basePkg = requirePackage.value.trim()
  if (!basePkg) return

  const pkg = selectedVersion.value
    ? `${basePkg}:^${selectedVersion.value}`
    : basePkg

  running.value = true
  try {
    const result = await composerRequire(props.domain, pkg)
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.success'))
      showRequireDialog.value = false
      requirePackage.value = ''
      availableVersions.value = []
      selectedVersion.value = ''
      await loadStatus()
    } else {
      ElMessage.error(t('sites.composer.failed', { code: result.exitCode }))
    }
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    running.value = false
  }
}

function openPackagist(pkg: string): void {
  const name = pkg.split(':')[0]
  const url = `https://packagist.org/packages/${name}`
  openExternal(url)
}

async function confirmRemove(pkg: string): Promise<void> {
  const name = pkg.split(':')[0]
  try {
    await ElMessageBox.confirm(
      t('sites.composer.removeConfirm', { name }),
      t('sites.composer.removeTitle'),
      { type: 'warning', confirmButtonText: t('common.delete'), confirmButtonClass: 'el-button--danger' }
    )
  } catch {
    return
  }
  removing.value = name
  try {
    const result = await composerRemove(props.domain, name)
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.removed', { name }))
      await loadStatus()
    } else {
      ElMessage.error(t('sites.composer.failed', { code: result.exitCode }))
    }
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    removing.value = null
  }
}

function openInitDialog(): void {
  initForm.value = {
    name: `local/${props.domain.replace(/\./g, '-')}`,
    description: '',
    type: 'project',
    stability: 'stable',
  }
  showInitDialog.value = true
}

async function runInit(): Promise<void> {
  running.value = true
  try {
    const result = await composerInit(props.domain, { ...initForm.value })
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.success'))
      showInitDialog.value = false
      await loadStatus()
    } else {
      ElMessage.error(t('sites.composer.failed', { code: result.exitCode }))
    }
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    running.value = false
  }
}

async function runDiagnose(): Promise<void> {
  diagnosing.value = true
  try {
    diagnoseResult.value = await composerDiagnose(props.domain)
    showDiagnoseDialog.value = true
  } catch (err: unknown) {
    ElMessage.error(err instanceof Error ? err.message : String(err))
  } finally {
    diagnosing.value = false
  }
}

function isMajorJump(requiredConstraint: string, latest: string | null): boolean {
  if (!latest) return false
  const reqMajor = parseInt(requiredConstraint.replace(/[\^~><=]/g, '').split('.')[0], 10)
  const latestMajor = parseInt(latest.split('.')[0], 10)
  return Number.isFinite(reqMajor) && Number.isFinite(latestMajor) && latestMajor > reqMajor
}

onMounted(() => {
  loadDismissState()
  loadStatus()
})
</script>

<style scoped>
.site-composer {
  padding: 16px;
}

.composer-loading {
  padding: 48px 24px;
  text-align: center;
  color: var(--el-text-color-secondary);
}
.composer-loading-label {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  font-size: 14px;
}
.composer-loading-label .el-icon {
  font-size: 16px;
}

.composer-alert {
  margin-bottom: 16px;
}

.composer-empty {
  padding: 40px 0;
  text-align: center;
}

.composer-empty-hint {
  color: var(--el-text-color-secondary);
  margin: 8px 0;
  font-size: 13px;
}

.composer-init-cmd {
  font-family: monospace;
  font-size: 13px;
}

.composer-panel {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.composer-card {
  border-radius: 8px;
}

.card-title {
  font-weight: 600;
  font-size: 14px;
}

.status-row {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  align-items: center;
  margin-bottom: 12px;
}

.status-item {
  display: flex;
  align-items: center;
  gap: 8px;
}

.status-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.status-na {
  color: var(--el-text-color-placeholder);
}

.actions-row {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-bottom: 4px;
}

/* Package table */
.pkg-table :deep(.el-table__row) {
  cursor: default;
}

.pkg-name-cell {
  display: flex;
  align-items: center;
  gap: 6px;
}

.pkg-outdated-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--el-color-warning);
  flex-shrink: 0;
}

.pkg-name-link {
  cursor: pointer;
  color: var(--el-color-primary);
  font-size: 12px;
}

.pkg-name-link:hover {
  text-decoration: underline;
}

.version-outdated {
  color: var(--el-color-warning);
}

.output-header {
  display: flex;
  align-items: center;
  gap: 12px;
}

.output-pre {
  font-family: monospace;
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
  background: var(--el-fill-color-light);
  border-radius: 4px;
  padding: 10px;
  margin: 0;
  max-height: 300px;
  overflow-y: auto;
}

.output-pre--err {
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
}

.composer-framework-banner {
  align-items: flex-start;
}

.banner-description {
  margin: 0 0 8px;
  font-size: 13px;
  color: var(--el-text-color-regular);
}

.banner-actions {
  display: flex;
  gap: 8px;
}

/* Package info popover */
.pkg-info-loading {
  font-size: 13px;
  color: var(--el-text-color-secondary);
  padding: 4px 0;
}

.pkg-info {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.pkg-info-header {
  font-size: 13px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  word-break: break-all;
}

.pkg-abandoned {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--el-color-danger);
}

.pkg-desc {
  margin: 0;
  font-size: 12px;
  color: var(--el-text-color-regular);
  line-height: 1.5;
}

.pkg-stats {
  display: flex;
  gap: 12px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.pkg-update-hint {
  font-size: 12px;
  color: var(--el-color-warning);
  font-weight: 500;
}

.pkg-repo-link {
  font-size: 12px;
  color: var(--el-color-primary);
  text-decoration: none;
}

.pkg-repo-link:hover {
  text-decoration: underline;
}

/* Autocomplete */
.pkg-search {
  width: 100%;
}

.suggestion-row {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 2px 0;
}

.suggestion-name {
  font-size: 13px;
  font-weight: 500;
  color: var(--el-text-color-primary);
}

.suggestion-desc {
  font-size: 11px;
  color: var(--el-text-color-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mono {
  font-family: monospace;
}

.major-jump-icon {
  color: var(--el-color-warning);
  margin-left: 4px;
  vertical-align: middle;
}

.diagnose-ok {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  color: var(--el-color-success);
  padding: 8px 0;
}

.diagnose-line {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  font-size: 13px;
  padding: 4px 0;
  line-height: 1.4;
}

.diagnose-error {
  color: var(--el-color-danger);
}

.diagnose-warning {
  color: var(--el-color-warning);
}
</style>
