<template>
  <div class="site-composer">
    <!-- Loading skeleton -->
    <div v-if="loading" class="composer-loading">
      <el-skeleton :rows="4" animated />
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
          <el-tag type="info" class="composer-init-cmd">
            composer init
          </el-tag>
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

        <!-- Package chips -->
        <div v-if="status.packages.length > 0" class="package-list">
          <el-popover
            v-for="pkg in status.packages"
            :key="pkg"
            placement="top"
            trigger="hover"
            :show-after="500"
            :hide-after="200"
            width="360"
            @show="loadPackageInfo(pkg.split(':')[0])"
          >
            <template #reference>
              <el-tag
                size="small"
                effect="plain"
                :class="['pkg-tag', 'pkg-tag-interactive', outdatedMap[pkg.split(':')[0]] ? 'pkg-outdated' : '']"
                role="button"
                tabindex="0"
                :aria-label="t('sites.composer.openOnPackagist', { name: pkg.split(':')[0] })"
                :title="outdatedMap[pkg.split(':')[0]] ? t('sites.composer.updateAvailable', { latest: outdatedMap[pkg.split(':')[0]].latest }) : undefined"
                @click="openPackagist(pkg)"
                @keydown.enter="openPackagist(pkg)"
              >
                {{ pkg }}
                <span v-if="outdatedMap[pkg.split(':')[0]]" class="pkg-outdated-dot" :title="t('sites.composer.outdatedBadge')" />
                <el-icon
                  class="pkg-remove"
                  :title="t('sites.composer.removeTitle')"
                  @click.stop="confirmRemove(pkg)"
                ><Close /></el-icon>
              </el-tag>
            </template>

            <div v-if="pkgInfoLoading[pkg.split(':')[0]]" class="pkg-info-loading">Načítám…</div>
            <div v-else-if="pkgInfoCache[pkg.split(':')[0]]" class="pkg-info">
              <div class="pkg-info-header mono">{{ pkg.split(':')[0] }}</div>
              <div v-if="pkgInfoCache[pkg.split(':')[0]].abandoned" class="pkg-abandoned">
                <el-icon><WarningFilled /></el-icon>
                {{ abandonedLabel(pkgInfoCache[pkg.split(':')[0]].abandoned) }}
              </div>
              <p class="pkg-desc">{{ pkgInfoCache[pkg.split(':')[0]].description }}</p>
              <div class="pkg-stats mono">
                <span>⭐ {{ pkgInfoCache[pkg.split(':')[0]].favers }}</span>
                <span>↓ {{ pkgInfoCache[pkg.split(':')[0]].downloads }}</span>
              </div>
              <div v-if="outdatedMap[pkg.split(':')[0]]" class="pkg-update-hint">
                {{ t('sites.composer.updateAvailable', { latest: outdatedMap[pkg.split(':')[0]].latest }) }}
              </div>
              <a
                v-if="pkgInfoCache[pkg.split(':')[0]].repository"
                class="pkg-repo-link"
                href="#"
                @click.prevent="openExternal(pkgInfoCache[pkg.split(':')[0]].repository)"
              >{{ t('sites.composer.openRepository') }}</a>
            </div>
            <div v-else class="pkg-info-loading">—</div>
          </el-popover>
        </div>
      </el-card>

      <!-- Actions -->
      <el-card class="composer-card" shadow="never">
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
            circle
            :icon="Refresh"
            :loading="loading"
            :title="$t('common.refresh')"
            @click="loadStatus"
          />
        </div>
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

    <!-- Require package dialog -->
    <el-dialog
      v-model="showRequireDialog"
      :title="$t('sites.composer.require')"
      width="440px"
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
            @select="onSelectSuggestion"
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
import { Check, Warning, Refresh, Close, WarningFilled } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { composerStatus, composerInstall, composerRequire, composerRemove, composerOutdated } from '../../api/daemon'
import type { ComposerStatus, ComposerCommandResult } from '../../api/types'

const props = defineProps<{ domain: string }>()

const { t } = useI18n()

const loading = ref(false)
const loadError = ref<string | null>(null)
const status = ref<ComposerStatus | null>(null)

const running = ref(false)
const lastResult = ref<ComposerCommandResult | null>(null)
const outputOpen = ref<string[]>(['stdout', 'stderr'])

const showRequireDialog = ref(false)
const requirePackage = ref('')
const removing = ref<string | null>(null)

const DISMISS_KEY = computed(() => `wdc-composer-dismiss-${props.domain}`)
const isBannerDismissed = ref(false)

// Outdated map keyed by package name
const outdatedMap = ref<Record<string, { latest: string; status: string }>>({})

// Package info cache from packagist.org
const pkgInfoCache = ref<Record<string, any>>({})
const pkgInfoLoading = ref<Record<string, boolean>>({})

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
    const map: Record<string, { latest: string; status: string }> = {}
    for (const entry of result.installed) {
      if (entry.name && entry.latest) {
        map[entry.name] = { latest: entry.latest, status: entry.latestStatus ?? '' }
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
  try {
    status.value = await composerStatus(props.domain)
    await loadOutdated()
  } catch (err: unknown) {
    loadError.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
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

function onSelectSuggestion(item: any): void {
  requirePackage.value = item.value
}

function openExternal(url: string): void {
  if ((window as any).electronAPI?.openExternal) {
    ;(window as any).electronAPI.openExternal(url)
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
  }
}

async function runRequire(): Promise<void> {
  const pkg = requirePackage.value.trim()
  if (!pkg) return

  running.value = true
  try {
    const result = await composerRequire(props.domain, pkg)
    lastResult.value = result
    outputOpen.value = result.exitCode !== 0 ? ['stdout', 'stderr'] : ['stdout']
    if (result.exitCode === 0) {
      ElMessage.success(t('sites.composer.success'))
      showRequireDialog.value = false
      requirePackage.value = ''
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
  padding: 24px;
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

.package-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.pkg-tag {
  font-family: monospace;
  font-size: 12px;
}

.pkg-tag-interactive {
  cursor: pointer;
  transition: background 0.15s, transform 0.15s;
  position: relative;
  user-select: none;
}

.pkg-tag-interactive:hover {
  background: var(--el-color-primary-light-9);
  transform: translateY(-1px);
}

.pkg-tag-interactive:focus-visible {
  outline: 2px solid var(--el-color-primary);
  outline-offset: 2px;
}

.pkg-tag-interactive .pkg-remove {
  opacity: 0;
  margin-left: 6px;
  font-size: 11px;
  cursor: pointer;
  color: var(--el-color-danger);
  transition: opacity 0.15s;
  vertical-align: middle;
}

.pkg-tag-interactive:hover .pkg-remove {
  opacity: 1;
}

.pkg-outdated {
  border-color: var(--el-color-warning) !important;
}

.pkg-outdated-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--el-color-warning);
  margin-left: 4px;
  vertical-align: middle;
  flex-shrink: 0;
}

.actions-row {
  display: flex;
  gap: 8px;
  align-items: center;
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
</style>
