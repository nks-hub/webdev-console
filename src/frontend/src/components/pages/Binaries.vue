<template>
  <div class="binaries-page">
    <!-- Grid view: one card per managed runtime -->
    <template v-if="!selectedApp">
      <div class="page-header">
        <div class="header-title-block">
          <h1 class="page-title">{{ $t('binaries.title') }}</h1>
          <p class="page-subtitle">
            {{ $t('binaries.modulesCount', { n: moduleCards.length }, moduleCards.length) }} ·
            {{ $t('binaries.installedCount', { n: installed.length }, installed.length) }} ·
            {{ $t('binaries.availableCount', { n: totalAvailable }, totalAvailable) }}
            <template v-if="outdatedCount > 0"> ·
              <el-button
                link
                type="warning"
                class="outdated-link"
                :aria-pressed="outdatedOnly"
                @click="outdatedOnly = !outdatedOnly"
              >{{ outdatedOnly ? $t('binaries.showAll') : $t('binaries.outdatedCount', { n: outdatedCount }, outdatedCount) }}</el-button>
            </template>
          </p>
          <!-- Catalog health pill — reactive to /api/system polled on mount.
               When unreachable the background flips red + hint points users
               at Settings > Advanced to change the catalog URL. -->
          <div
            v-if="catalogStatus"
            class="catalog-health"
            :class="catalogStatus.reachable ? 'catalog-health-ok' : 'catalog-health-err'"
            :title="catalogStatusTitle"
          >
            <span class="catalog-health-dot"></span>
            <span class="catalog-health-label">
              {{ $t('binaries.catalog') }}:
              {{ catalogStatus.reachable
                ? $t('binaries.catalogReleases', { n: catalogStatus.cachedCount }, catalogStatus.cachedCount)
                : $t('binaries.catalogUnreachable') }}
            </span>
            <span class="catalog-health-url mono">{{ catalogUrlShort }}</span>
          </div>
        </div>
        <div class="header-actions">
          <el-input
            v-model="gridSearch"
            :placeholder="$t('binaries.filterPlaceholder')"
            clearable
            size="small"
            style="width: 220px"
            prefix-icon="Search"
          />
          <el-button size="small" :loading="loading" @click="refresh">{{ $t('common.refresh') }}</el-button>
        </div>
      </div>

      <div v-if="loading" class="page-body-pad">
        <el-skeleton :rows="5" animated />
      </div>

      <div v-else-if="filteredModules.length === 0" class="page-body-pad">
        <el-empty :description="gridSearch ? $t('binaries.noMatching', { q: gridSearch }) : $t('binaries.noEntries')" :image-size="80" />
      </div>

      <template v-else>
        <div class="bin-grid page-body-pad">
          <!-- Composer built-in card — first item in grid, same structure as catalog cards -->
          <div
            v-if="!gridSearch && !outdatedOnly"
            class="bin-card bin-card--builtin"
            :class="{ 'bin-card--has-installed': composerInstalled }"
          >
            <div class="bin-card-header">
              <ServiceIcon service="composer" :active="composerInstalled" />
              <div class="bin-card-title">
                <span class="bin-card-name">
                  Composer
                  <el-tag size="small" type="info" effect="plain" class="builtin-tag">{{ $t('binaries.builtIn') }}</el-tag>
                </span>
                <span class="bin-card-latest mono" :title="composerPath ?? ''">{{ composerVersionDisplay }}</span>
              </div>
            </div>
            <div class="bin-card-metrics">
              <div class="metric">
                <span class="metric-num mono">{{ composerInstalled ? 1 : 0 }}</span>
                <span class="metric-label">{{ $t('binaries.metricInstalled') }}</span>
              </div>
            </div>
            <div class="bin-card-actions">
              <el-button
                size="small"
                type="primary"
                plain
                :loading="composerInstalling"
                class="bin-open-btn"
                @click.stop="installComposer"
              >
                {{ composerInstalling
                  ? $t('binaries.composer.installing')
                  : composerInstalled ? $t('binaries.composer.update') : $t('binaries.composer.install') }}
              </el-button>
            </div>
          </div>

          <div
            v-for="card in filteredModules"
            :key="card.app"
            v-activate
            class="bin-card"
            :class="{ 'bin-card--has-installed': card.installedCount > 0 }"
            @click="selectedApp = card.app"
          >
            <div class="bin-card-header">
              <ServiceIcon :service="card.app" :active="card.installedCount > 0" />
              <div class="bin-card-title">
                <span class="bin-card-name">{{ appDisplayName(card.app) }}</span>
                <span v-if="card.latest" class="bin-card-latest mono">{{ $t('binaries.latest', { v: card.latest }) }}</span>
              </div>
            </div>
            <div class="bin-card-metrics">
              <div class="metric">
                <span class="metric-num mono">{{ card.installedCount }}</span>
                <span class="metric-label">{{ $t('binaries.metricInstalled') }}</span>
              </div>
              <div class="metric">
                <span class="metric-num mono">{{ card.available }}</span>
                <span class="metric-label">{{ $t('binaries.metricAvailable') }}</span>
              </div>
              <div class="metric" v-if="card.defaultVersion">
                <span class="metric-num mono">{{ card.defaultVersion }}</span>
                <span class="metric-label">{{ $t('binaries.metricDefault') }}</span>
              </div>
            </div>
            <p v-if="card.outdated" class="bin-card-outdated">
              {{ $t('binaries.newerAvailable', { installed: card.newestInstalled, latest: card.latest }) }}
            </p>
            <div class="bin-card-actions">
              <el-button size="small" type="primary" plain class="bin-open-btn" tabindex="-1">
                {{ $t('binaries.manageVersions') }} <el-icon><ArrowRight /></el-icon>
              </el-button>
            </div>
          </div>
        </div>
      </template>
    </template>

    <!-- Detail view: merged installed + catalog table for a single module -->
    <template v-else>
      <div class="page-header">
        <div class="header-title-block">
          <el-button size="small" text class="back-btn" @click="selectedApp = null">
            &larr; {{ $t('binaries.back') }}
          </el-button>
          <h1 class="page-title page-title-detail">
            <ServiceIcon :service="selectedApp" :active="true" />
            <span>{{ appDisplayName(selectedApp) }}</span>
          </h1>
          <p class="page-subtitle">
            {{ $t('binaries.versionsInstalled', { installed: detailRows.filter(r => r.installed).length, total: detailRows.length }) }}
          </p>
        </div>
        <div class="header-actions">
          <el-button size="small" :loading="loading" @click="refresh">{{ $t('common.refresh') }}</el-button>
        </div>
      </div>

      <div class="page-body-pad">
        <el-alert
          v-if="failedInstall && failedInstall.app === selectedApp"
          type="error"
          show-icon
          :closable="true"
          class="install-failed-alert"
          :title="failedInstall.message"
          @close="failedInstall = null"
        >
          <template v-if="suggestedVersion">
            {{ $t('binaries.progress.suggestion', { version: suggestedVersion }) }}
          </template>
        </el-alert>

        <el-table
          v-if="detailRows.length > 0"
          :data="detailRows"
          class="bin-detail-table"
          stripe
          row-key="version"
          :row-class-name="rowClass"
        >
          <el-table-column :label="$t('binaries.col.version')" prop="version" min-width="140">
            <template #default="{ row }">
              <span class="mono col-version">{{ row.version }}</span>
              <el-tag v-if="row.isDefault" size="small" type="success" effect="plain" class="col-tag">{{ selectedApp === 'php' ? $t('php.default') : $t('binaries.default') }}</el-tag>
              <el-tag v-if="failedInstall?.app === selectedApp && failedInstall?.version === row.version" size="small" type="danger" effect="plain" class="col-tag">{{ $t('binaries.failedTag') }}</el-tag>
              <el-tag v-else-if="row.version === suggestedVersion" size="small" type="primary" effect="plain" class="col-tag">{{ $t('binaries.suggestedTag') }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column :label="$t('binaries.col.platforms')" min-width="200">
            <template #default="{ row }">
              <el-tag
                v-for="p in row.platforms"
                :key="p"
                size="small"
                :type="p === nativePlatform ? 'success' : 'info'"
                :effect="p === nativePlatform ? 'dark' : 'plain'"
                class="platform-tag mono"
              >
                <span v-if="p === nativePlatform" class="native-marker">* </span>{{ p }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column :label="$t('binaries.col.source')" width="140">
            <template #default="{ row }">
              <span class="mono col-muted">{{ row.source || '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="$t('binaries.col.status')" width="140">
            <template #default="{ row }">
              <el-tag
                v-if="row.installed"
                size="small"
                type="success"
                effect="plain"
              >{{ $t('binaries.statusInstalled') }}</el-tag>
              <el-tag
                v-else
                size="small"
                type="info"
                effect="plain"
              >{{ $t('binaries.statusAvailable') }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column :label="$t('common.actions')" width="320" align="right">
            <template #default="{ row }">
              <el-tooltip
                v-if="!row.installed && !row.native"
                :content="$t('binaries.noBuildInRelease', { platform: nativePlatform })"
                placement="left"
              >
                <el-button size="small" type="info" plain disabled>
                  {{ $t('binaries.notAvailable') }}
                </el-button>
              </el-tooltip>
              <el-button
                v-else-if="!row.installed"
                size="small"
                type="primary"
                :plain="row.version !== suggestedVersion"
                :loading="installing.has(`${selectedApp}-${row.version}`)"
                :disabled="progress.phase === 'running'"
                @click="install(selectedApp!, row.version)"
              >
                {{ $t('binaries.install') }}
              </el-button>
              <template v-else>
                <el-button
                  v-if="selectedApp === 'php' && !row.isDefault"
                  size="small"
                  text
                  @click="setPhpDefault(row.version)"
                >
                  {{ $t('php.setDefault') }}
                </el-button>
                <el-button
                  size="small"
                  type="danger"
                  plain
                  :loading="uninstalling.has(`${selectedApp}-${row.version}`)"
                  @click="uninstall(selectedApp!, row.version)"
                >
                  {{ $t('binaries.remove') }}
                </el-button>
              </template>
            </template>
          </el-table-column>
        </el-table>

        <el-empty v-else :description="$t('binaries.noVersionsInCatalog')" :image-size="80" />
      </div>
    </template>

    <!-- Install progress dialog -->
    <el-dialog
      v-model="progress.visible"
      :title="$t('binaries.progress.title', { name: appDisplayName(progress.app), version: progress.version })"
      width="460px"
      :close-on-click-modal="false"
      :close-on-press-escape="progress.phase !== 'running'"
      :show-close="progress.phase !== 'running'"
    >
      <div class="progress-content" aria-live="polite">
        <p class="progress-msg">{{ progressMessage }}</p>
        <el-progress
          v-if="progress.phase !== 'error'"
          :percentage="progressPercent ?? 100"
          :indeterminate="progressPercent === null && progress.phase === 'running'"
          :duration="2"
          :show-text="progressPercent !== null"
          :status="progress.phase === 'done' ? 'success' : undefined"
        />
        <details v-if="progress.phase === 'error' && progress.detail" class="progress-detail">
          <summary>{{ $t('binaries.progress.technicalDetail') }}</summary>
          <pre class="mono">{{ progress.detail }}</pre>
        </details>
      </div>
      <template #footer>
        <el-button v-if="progress.phase === 'running'" @click="cancelInstall">{{ $t('common.cancel') }}</el-button>
        <template v-else-if="progress.phase === 'error'">
          <el-button @click="progress.visible = false">{{ $t('common.close') }}</el-button>
          <el-button v-if="suggestedVersion" type="primary" @click="tryAnotherVersion">
            {{ $t('binaries.progress.tryVersion', { version: suggestedVersion }) }}
          </el-button>
        </template>
        <template v-else>
          <el-button @click="progress.visible = false">{{ $t('common.close') }}</el-button>
          <el-button v-if="progress.app === 'php'" type="primary" @click="setPhpDefault(progress.version); progress.visible = false">
            {{ $t('php.setDefault') }}
          </el-button>
          <el-button v-if="progress.serviceId" type="primary" :loading="progress.starting" @click="startInstalledService">
            {{ $t('binaries.progress.startService', { name: appDisplayName(progress.app) }) }}
          </el-button>
        </template>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowRight } from '@element-plus/icons-vue'
import { useI18n } from 'vue-i18n'
import {
  fetchBinaryCatalog,
  fetchInstalledBinaries,
  fetchPhpVersions,
  fetchServices,
  fetchSystem,
  installBinary,
  startService,
  subscribeEventsMap,
  uninstallBinary,
  daemonBaseUrl,
  daemonAuthHeaders as authHeaders,
  type BinaryProgressEvent,
} from '../../api/daemon'
import type { BinaryRelease, InstalledBinary } from '../../api/types'
import { errorMessage } from '../../utils/errors'
import { appDisplayName, installErrorDetail, installErrorMessage } from '../../utils/binaries'
import ServiceIcon from '../shared/ServiceIcon.vue'

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()

const loading = ref(false)
const catalog = ref<Record<string, BinaryRelease[]>>({})
const installed = ref<InstalledBinary[]>([])
const installing = ref<Set<string>>(new Set())
const uninstalling = ref<Set<string>>(new Set())

// Normalised "os/arch" tag of the current machine, e.g. "windows/x64". Used
// by the detail table to highlight the release matching the host so users
// don't misread the 6 cross-platform chips as "I need to pick one".
const nativePlatform = ref<string>('')

// Catalog health pulled from /api/system once on mount. Null until the
// first successful fetch; null hides the pill. Updated on refresh click
// so users see the post-refresh state without reloading the page.
const catalogStatus = ref<{ url: string; cachedCount: number; lastFetch: string | null; reachable: boolean } | null>(null)

// Short form of the catalog URL for inline display — strips protocol
// and trailing slash so "http://127.0.0.1:8765/" renders as "127.0.0.1:8765".
const catalogUrlShort = computed(() => {
  const u = catalogStatus.value?.url ?? ''
  return u.replace(/^https?:\/\//, '').replace(/\/$/, '')
})

const catalogStatusTitle = computed(() => {
  if (!catalogStatus.value) return ''
  const { url, cachedCount, lastFetch, reachable } = catalogStatus.value
  const when = lastFetch ? new Date(lastFetch).toLocaleString(locale.value) : t('binaries.catalogNever')
  return reachable
    ? t('binaries.catalogTooltipOk', { url, n: cachedCount, when })
    : t('binaries.catalogTooltipErr', { url, when })
})

const gridSearch = ref('')
const outdatedOnly = ref(false)

// The open module lives in the URL (?app=mysql) so the wizard, Databases
// and PHP pages can link straight to its versions and a reload keeps it.
const selectedApp = computed<string | null>({
  get: () => (typeof route.query.app === 'string' && route.query.app ? route.query.app : null),
  set: (app) => { void router.push({ path: '/binaries', query: app ? { app } : {} }) },
})

type InstallPhase = 'idle' | 'running' | 'done' | 'error'
const progress = reactive({
  visible: false,
  phase: 'idle' as InstallPhase,
  app: '',
  version: '',
  downloaded: 0,
  total: -1,
  received: false,
  message: '',
  detail: '',
  serviceId: '' as string,
  starting: false,
})
let installAbort: AbortController | null = null

/** Last failed install, kept after the dialog closes so the table can point at it. */
const failedInstall = ref<{ app: string; version: string; message: string } | null>(null)

/** major.minor of the PHP version new sites get (PHP is the only app with a default today). */
const activePhp = ref<string | null>(null)
const majorMinorOf = (v: string) => v.split('.').slice(0, 2).join('.')

async function loadActivePhp() {
  try {
    activePhp.value = (await fetchPhpVersions()).find(v => v.isActive)?.majorMinor ?? null
  } catch { activePhp.value = null /* PHP plugin missing */ }
}

async function setPhpDefault(version: string) {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/php/default`, {
      method: 'PUT',
      headers: authHeaders(),
      body: JSON.stringify({ version }),
    })
    const body = await r.json().catch(() => ({})) as { error?: string }
    if (!r.ok) throw new Error(body.error || `HTTP ${r.status}`)
    ElMessage.success(t('toast.phpDefaultSet', { version: majorMinorOf(version) }))
    await loadActivePhp()
  } catch (e) {
    ElMessage.error(t('toast.setDefaultFailed', { err: errorMessage(e) }))
  }
}

type ModuleCard = {
  app: string
  latest: string | null
  available: number
  installedCount: number
  newestInstalled: string | null
  outdated: boolean
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
  /** A build for this machine exists (always true while the platform is unknown). */
  native: boolean
}

// Semver descending sort so "latest" always wins and the detail table
// shows newest at the top.
//
// Previously we naively split on /[.-]/ which turned `2.11.0-beta.1` into
// `["2", "11", "0", "beta", "1"]` — `parseInt("beta")` returns NaN > the
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

/** Versions installable on this machine, newest first. */
function nativeVersions(app: string): string[] {
  const native = nativePlatform.value
  const versions = new Set((catalog.value[app] ?? [])
    .filter(r => !native || `${r.os}/${r.arch}` === native)
    .map(r => r.version))
  return [...versions].sort(versionGreater)
}

const moduleCards = computed<ModuleCard[]>(() => {
  const apps = new Set<string>([
    ...Object.keys(catalog.value).filter(a => a.toLowerCase() !== 'composer'),
    ...installed.value.map(b => b.app).filter(a => a.toLowerCase() !== 'composer'),
  ])
  return Array.from(apps).map(app => {
    const versions = nativeVersions(app)
    const installedForApp = installed.value.filter(b => b.app === app)
    const newestInstalled = installedForApp.map(b => b.version).sort(versionGreater)[0] ?? null
    const latest = versions[0] ?? newestInstalled
    return {
      app,
      latest,
      available: versions.length,
      installedCount: installedForApp.length,
      newestInstalled,
      outdated: !!newestInstalled && !!latest && versionGreater(latest, newestInstalled) < 0,
      defaultVersion: app === 'php'
        ? installedForApp.find(b => majorMinorOf(b.version) === activePhp.value)?.version ?? null
        : installedForApp.find(b => b.isDefault)?.version ?? null,
    }
  }).sort((a, b) => a.app.localeCompare(b.app))
})

const totalAvailable = computed(() => moduleCards.value.reduce((s, c) => s + c.available, 0))

const outdatedCount = computed(() => moduleCards.value.filter(c => c.outdated).length)

const filteredModules = computed(() => {
  const q = gridSearch.value.toLowerCase().trim()
  return moduleCards.value.filter(c =>
    (!outdatedOnly.value || c.outdated)
    && (!q || c.app.toLowerCase().includes(q) || appDisplayName(c.app).toLowerCase().includes(q)))
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
        native: false,
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
      native: true,
    }
    row.installed = true
    row.isDefault = !!i.isDefault
    byVersion.set(i.version, row)
  }

  // Sort each row's platform list so the native host appears first —
  // makes the detail table scannable by OS at a glance ("where's my
  // platform?" always at the left edge). Cross-platform entries after
  // native are kept in stable alphabetical order for determinism.
  const native = nativePlatform.value
  for (const row of byVersion.values()) {
    if (app === 'php') row.isDefault = row.installed && majorMinorOf(row.version) === activePhp.value
    row.native = !native || row.platforms.length === 0 || row.platforms.includes(native)
    row.platforms.sort((a, b) => {
      if (a === native && b !== native) return -1
      if (b === native && a !== native) return 1
      return a.localeCompare(b)
    })
  }

  return Array.from(byVersion.values()).sort((a, b) => versionGreater(a.version, b.version))
})

/**
 * After a failed install: the next installable version below the failed one
 * (older builds tend to sit on stable mirrors), else the newest other one.
 */
const suggestedVersion = computed<string | null>(() => {
  const failed = failedInstall.value
  if (!failed) return null
  const candidates = nativeVersions(failed.app)
    .filter(v => v !== failed.version && !installed.value.some(b => b.app === failed.app && b.version === v))
  return candidates.find(v => versionGreater(v, failed.version) > 0) ?? candidates[0] ?? null
})

function rowClass({ row }: { row: DetailRow }): string {
  return row.version === suggestedVersion.value && failedInstall.value?.app === selectedApp.value
    ? 'row-suggested'
    : ''
}

const progressPercent = computed<number | null>(() => {
  if (progress.phase === 'done') return 100
  if (!progress.received || progress.total <= 0 || progress.downloaded >= progress.total) return null
  return Math.min(99, Math.floor(progress.downloaded / progress.total * 100))
})

function formatMb(bytes: number): string {
  return (bytes / 1024 / 1024).toLocaleString(locale.value, { maximumFractionDigits: 1, minimumFractionDigits: 1 })
}

const progressMessage = computed(() => {
  const name = appDisplayName(progress.app)
  if (progress.phase === 'done') return t('binaries.progress.done', { name, version: progress.version })
  if (progress.phase === 'error') return progress.message
  if (!progress.received) return t('binaries.progress.connecting')
  if (progress.total > 0 && progress.downloaded >= progress.total) return t('binaries.progress.extracting')
  return progress.total > 0
    ? t('binaries.progress.downloadedOf', { done: formatMb(progress.downloaded), total: formatMb(progress.total) })
    : t('binaries.progress.downloaded', { done: formatMb(progress.downloaded) })
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
    void loadActivePhp()
    // Re-fetch catalog health after the data refresh so the pill shows
    // the latest snapshot. Runs in parallel with the other two calls
    // above via fire-and-forget — no need to block the UI on it.
    void loadSystemInfo()
  } catch (e) {
    ElMessage.error(t('toast.loadFailed', { err: errorMessage(e) }))
  } finally {
    loading.value = false
  }
}

async function install(app: string, version: string) {
  const key = `${app}-${version}`
  installing.value.add(key)
  installAbort = new AbortController()
  Object.assign(progress, {
    visible: true, phase: 'running', app, version,
    downloaded: 0, total: -1, received: false, message: '', detail: '', serviceId: '', starting: false,
  })
  const unsubscribe = subscribeEventsMap({
    'binary:progress': (data) => {
      const e = data as BinaryProgressEvent
      if (e.app !== app || e.version !== version || progress.phase !== 'running') return
      // SSE writes may arrive out of order; never move the bar backwards.
      progress.downloaded = Math.max(progress.downloaded, e.downloaded)
      progress.total = e.total
      progress.received = true
    },
  })

  try {
    await installBinary(app, version, installAbort.signal)
    progress.phase = 'done'
    if (failedInstall.value?.app === app) failedInstall.value = null
    await refresh()
    try {
      const services = await fetchServices()
      progress.serviceId = services.some(s => s.id === app) ? app : ''
    } catch { /* no start shortcut then */ }
  } catch (e) {
    const message = installErrorMessage((k, n) => t(k, n ?? {}), e, app, version)
    progress.phase = 'error'
    progress.message = message
    progress.detail = e instanceof DOMException && e.name === 'AbortError' ? '' : installErrorDetail(e)
    failedInstall.value = { app, version, message }
  } finally {
    unsubscribe()
    installAbort = null
    installing.value.delete(key)
  }
}

function cancelInstall() {
  installAbort?.abort()
}

function tryAnotherVersion() {
  progress.visible = false
  if (failedInstall.value) selectedApp.value = failedInstall.value.app
}

async function startInstalledService() {
  progress.starting = true
  try {
    await startService(progress.serviceId)
    ElMessage.success(t('binaries.progress.serviceStarted', { name: appDisplayName(progress.app) }))
    progress.visible = false
  } catch (e) {
    ElMessage.error(t('binaries.progress.serviceStartFailed', { name: appDisplayName(progress.app), err: errorMessage(e) }))
  } finally {
    progress.starting = false
  }
}

async function uninstall(app: string, version: string) {
  try {
    await ElMessageBox.confirm(
      t('binaries.removeConfirmMessage', { app: appDisplayName(app), version }),
      t('binaries.removeConfirmTitle'),
      { type: 'warning', confirmButtonText: t('binaries.remove'), cancelButtonText: t('common.cancel') },
    )
  } catch {
    return
  }
  const key = `${app}-${version}`
  uninstalling.value.add(key)
  try {
    await uninstallBinary(app, version)
    ElMessage.success(t('toast.removedVersion', { app: appDisplayName(app), version }))
    await refresh()
  } catch (e) {
    ElMessage.error(t('toast.removeFailed', { err: errorMessage(e) }))
  } finally {
    uninstalling.value.delete(key)
  }
}

async function loadSystemInfo() {
  // Fetches OS tag + catalog health in one call. OS never changes during
  // the session but catalog health does (user may click Refresh after
  // fixing their catalog URL), so we re-hit /api/system after any
  // successful refresh() call below.
  try {
    const sys = await fetchSystem()
    if (sys?.os?.tag && sys?.os?.arch) {
      nativePlatform.value = `${sys.os.tag}/${sys.os.arch}`
    }
    if (sys?.catalog) {
      catalogStatus.value = sys.catalog
    }
  } catch { /* daemon not reachable yet — pill stays hidden */ }
}

// Composer built-in
const composerVersionDisplay = computed(() => {
  const v = composerVersion.value
  if (!v) return composerPath.value ? t('binaries.composer.versionUnknown') : t('binaries.composer.source')
  if (v.length > 24) return `${v.slice(0, 10)}…${v.slice(-4)}`
  return v
})

const composerVersion = ref<string | null>(null)
const composerPath = ref<string | null>(null)
const composerInstalling = ref(false)
const composerInstalled = computed(() => !!composerVersion.value || !!composerPath.value)

async function loadComposerVersion() {
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/composer/version`, { headers: authHeaders() })
    if (r.ok) {
      const d = await r.json() as { version: string | null; path: string | null }
      composerVersion.value = d.version
      composerPath.value = d.path
    }
  } catch { /* silent */ }
}

async function installComposer() {
  composerInstalling.value = true
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/composer/self-install`, { method: 'POST', headers: authHeaders() })
    if (!r.ok) {
      const body: { detail?: string; error?: string } = await r.json().catch(() => ({}))
      ElMessage.error(t('binaries.composer.installFailed', { err: body.error || body.detail || `HTTP ${r.status}` }))
      return
    }
    const body = await r.json().catch(() => ({})) as { path?: string }
    await loadComposerVersion()
    // The phar is on disk even when the version probe has no PHP to run it with.
    if (!composerPath.value && body.path) composerPath.value = body.path
    ElMessage.success(t('binaries.composer.installed', { version: composerVersion.value || t('binaries.composer.versionUnknown') }))
  } catch (e) {
    ElMessage.error(t('binaries.composer.installFailed', { err: errorMessage(e) }))
  } finally {
    composerInstalling.value = false
  }
}

let unsubscribeInstalled: (() => void) | null = null

onMounted(() => {
  void refresh()
  void loadSystemInfo()
  void loadComposerVersion()
  // Installs started elsewhere (wizard, MCP, CLI) show up without a reload.
  unsubscribeInstalled = subscribeEventsMap({
    'binary:installed': () => { if (progress.phase !== 'running') void refresh() },
  })
})

onBeforeUnmount(() => {
  unsubscribeInstalled?.()
  installAbort?.abort()
})
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
  padding: 0 0 var(--wdc-space-4);
  margin-bottom: 0;
  gap: var(--wdc-space-3);
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
  letter-spacing: -0.02em;
  margin: 0;
}

.page-title-detail {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2-5);
}

.page-subtitle {
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.outdated-link {
  font-size: inherit;
  padding: 0;
  height: auto;
  vertical-align: baseline;
}

/* Catalog health pill below the page subtitle — green when the daemon
   has a populated cache, red when the fetch returned zero. Title
   attribute carries the full multi-line explanation. */
.catalog-health {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  margin-top: var(--wdc-space-1-5);
  padding: var(--wdc-space-1) var(--wdc-space-2-5);
  border-radius: var(--wdc-radius-full);
  font-size: 0.72rem;
  font-weight: 600;
  cursor: help;
  border: 1px solid currentColor;
}
.catalog-health-ok {
  color: var(--wdc-status-running);
}
.catalog-health-err {
  color: var(--wdc-status-error);
}
.catalog-health-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: currentColor;
}
.catalog-health-label { font-weight: 700; }
.catalog-health-url {
  color: var(--wdc-text-3);
  font-size: 0.68rem;
  font-weight: 500;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
}

.back-btn {
  padding: var(--wdc-space-0-5) var(--wdc-space-1-5) !important;
  height: auto !important;
  font-size: 0.76rem !important;
}

.page-body-pad {
  padding: 0 0 var(--wdc-space-5);
}

.install-failed-alert {
  margin: var(--wdc-space-3) 0;
}

/* Grid of module cards */
.bin-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: var(--wdc-space-3-5);
}

.bin-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-left: 3px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-4) var(--wdc-space-4-5);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3-5);
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
  gap: var(--wdc-space-3);
}

.bin-card-title {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-0-5);
  min-width: 0;
}

.bin-card-name {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: 0.005em;
}

.bin-card-latest {
  font-size: 0.72rem;
  color: var(--wdc-text-3);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 100%;
  display: inline-block;
}

.bin-card-outdated {
  margin: 0;
  font-size: 0.76rem;
  color: var(--wdc-warning);
}

.bin-card-metrics {
  display: flex;
  gap: var(--wdc-space-5);
}

.metric {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-0-5);
}

.metric-num {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
  line-height: 1.1;
}

.metric-label {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  font-family: var(--wdc-font-display);
}

.bin-card-actions {
  padding-top: var(--wdc-space-1);
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

.bin-detail-table :deep(.row-suggested td) {
  background: var(--wdc-accent-dim) !important;
}

.col-version {
  font-weight: 600;
  color: var(--wdc-text);
}

.col-tag {
  margin-left: var(--wdc-space-2);
}

.col-muted {
  color: var(--wdc-text-3);
}

.platform-tag {
  margin-right: var(--wdc-space-1);
  margin-bottom: var(--wdc-space-0-5);
  font-size: 0.68rem !important;
  font-weight: 600 !important;
  letter-spacing: 0.02em;
}

.progress-content {
  padding: var(--wdc-space-2) 0;
}

.progress-msg {
  font-size: 0.86rem;
  margin-bottom: var(--wdc-space-3);
  color: var(--wdc-text);
}

.progress-detail summary {
  cursor: pointer;
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}

.progress-detail pre {
  margin: var(--wdc-space-2) 0 0;
  font-size: 0.72rem;
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--wdc-text-2);
}

.bin-card--builtin {
  border-left-color: var(--el-color-info);
  cursor: default;
}

.bin-card--builtin:hover {
  border-left-color: var(--el-color-info);
}

.builtin-tag {
  margin-left: var(--wdc-space-1-5);
}
</style>
