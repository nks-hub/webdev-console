<template>
  <div class="sites-page">
    <SitesListSimple v-if="!uiModeStore.isAdvanced" @create="showCreate = true" />

    <template v-if="uiModeStore.isAdvanced">
    <header class="page-head">
      <div>
        <h1 class="page-title">{{ $t('sites.title') }}</h1>
        <p class="page-sub">{{ sitesSummary }}</p>
      </div>
      <div class="header-actions">
        <el-button @click="openHostsFile" :title="$t('sites.openHostsTitle')">
          {{ $t('sites.openHosts') }}
        </el-button>
        <el-button @click="reapplyAll" :loading="reapplying" :title="$t('sites.reapplyAllHint')">
          {{ $t('sites.reapplyAll') }}
        </el-button>
        <el-button type="primary" @click="showCreate = true">{{ $t('sites.create') }}</el-button>
      </div>
    </header>

    <!-- Filter by text and by state; counts sit on the tray keys. -->
    <div class="toolbar" role="search">
      <el-input
        v-model="search"
        :placeholder="$t('sites.filterPlaceholder')"
        clearable
        class="toolbar-search"
        prefix-icon="Search"
        :aria-label="$t('sites.filterPlaceholder')"
      />
      <div class="tray" role="group" :aria-label="$t('sites.list.stateFilter')">
        <button
          v-for="f in stateFilters"
          :key="f.id"
          type="button"
          class="tray-key"
          :aria-pressed="stateFilter === f.id"
          @click="stateFilter = f.id"
        >{{ f.label }} <span class="mono tray-count">{{ f.count }}</span></button>
      </div>
    </div>

    <div class="page-body">
      <div class="sites-mobile-list">
        <article
          v-for="site in filteredSites"
          :key="site.domain"
          class="site-mobile-card"
        >
          <div v-activate:link class="site-mobile-main" @click="editSite(site)">
            <div class="site-mobile-title-row">
              <span class="site-mobile-title">{{ site.domain }}</span>
              <span class="site-status" :class="`ss-${siteState(site)}`">
                <el-icon :size="14" aria-hidden="true">
                  <Close v-if="siteState(site) === 'failed'" />
                  <Remove v-else-if="siteState(site) === 'disabled' || siteState(site) === 'stopped'" />
                  <Check v-else />
                </el-icon>
                {{ $t(`sites.list.state.${siteState(site)}`) }}
              </span>
            </div>
            <div class="site-mobile-meta mono" :title="site.documentRoot">
              {{ rowRuntimeLabel(site) || $t('sites.runtimeStatic') }} · {{ site.sslEnabled ? 'HTTPS' : 'HTTP' }}
            </div>
          </div>
          <div class="site-mobile-actions">
            <el-button size="small" @click.stop="editSite(site)">{{ $t('common.edit') }}</el-button>
            <el-dropdown
              trigger="click"
              :teleported="true"
              :popper-options="{ modifiers: [{ name: 'preventOverflow', options: { boundary: 'viewport', padding: 8 } }] }"
              @command="(cmd: string) => handleRowAction(cmd, site)"
            >
              <el-button size="small" text :aria-label="$t('sites.card.moreActions', { domain: site.domain })"><el-icon><MoreFilled /></el-icon></el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="open">{{ $t('sites.open') }}</el-dropdown-item>
                  <el-dropdown-item command="toggle">{{ site.enabled !== false ? $t('sites.list.disable') : $t('sites.list.enable') }}</el-dropdown-item>
                  <el-dropdown-item command="detect">{{ $t('sites.detect') }}</el-dropdown-item>
                  <el-dropdown-item command="delete" divided class="danger-item">{{ $t('sites.delete') }}</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>
        </article>
      </div>

      <el-table
        :data="filteredSites"
        v-loading="sitesStore.loading"
        @row-click="selectSite"
        class="sites-table"
        row-class-name="cursor-pointer"
        table-layout="auto"
      >
        <el-table-column prop="domain" :label="$t('sites.domain')" min-width="200">
          <template #default="{ row }">
            <div class="cell-domain">
              <span class="col-domain">{{ row.domain }}</span>
              <span
                v-if="row.aliases?.length"
                class="alias-dot mono"
                :title="row.aliases.join(', ')"
              >+{{ row.aliases.length }}</span>
              <!-- Plugin-contributed per-row badges (e.g. Cloudflare tunnel). -->
              <PluginSlot name="sites-row-badges" :context="{ site: row }" />
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.list.status')" min-width="130">
          <template #default="{ row }">
            <span class="site-status" :class="`ss-${siteState(row)}`" :title="failedStepsTitle(row)">
              <el-icon :size="14" aria-hidden="true">
                <Close v-if="siteState(row) === 'failed'" />
                <Remove v-else-if="siteState(row) === 'disabled' || siteState(row) === 'stopped'" />
                <Check v-else />
              </el-icon>
              {{ $t(`sites.list.state.${siteState(row)}`) }}
            </span>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.list.runtime')" min-width="120">
          <template #default="{ row }">
            <span class="mono cell-runtime">{{ rowRuntimeLabel(row) || $t('sites.runtimeStatic') }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.list.address')" min-width="170">
          <template #default="{ row }">
            <span class="cell-address">
              <el-icon v-if="row.sslEnabled" :size="13" class="https-ok" :title="$t('sites.httpsEnabled')"><Lock /></el-icon>
              <span class="mono">{{ row.sslEnabled ? 'https' : 'http' }}{{ portSuffix(row) }}</span>
              <span v-if="bindAddressLabel(row) && bindAddressLabel(row) !== '*'" class="mono muted" :title="$t('sites.bindIp')">{{ bindAddressLabel(row) }}</span>
            </span>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.documentRoot')" min-width="180" class-name="col-docroot-cell">
          <template #default="{ row }">
            <span class="col-mono" :title="row.documentRoot">{{ row.documentRoot }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.framework')" min-width="110" class-name="col-framework-cell">
          <template #default="{ row }">
            <span class="framework-cell">
              <span v-if="row.framework">{{ row.framework }}</span>
              <span
                v-if="composeStatus[row.domain]?.hasCompose"
                class="mono muted"
                :title="composeStatus[row.domain]?.composeFile || ''"
              >{{ $t('sites.composeBadge') }}</span>
              <span v-if="!row.framework && !composeStatus[row.domain]?.hasCompose" class="col-empty">—</span>
            </span>
          </template>
        </el-table-column>

        <el-table-column width="112" align="right" fixed="right">
          <template #header><span class="visually-hidden">{{ $t('common.actions') }}</span></template>
          <template #default="{ row }">
            <div class="site-actions">
              <el-button size="small" @click.stop="editSite(row)">{{ $t('common.edit') }}</el-button>
              <el-dropdown
                trigger="click"
                :teleported="true"
                :popper-options="{ modifiers: [{ name: 'preventOverflow', options: { boundary: 'viewport', padding: 8 } }] }"
                @command="(cmd: string) => handleRowAction(cmd, row)"
                @click.stop
              >
                <el-button size="small" text @click.stop :aria-label="$t('sites.card.moreActions', { domain: row.domain })">
                  <el-icon><MoreFilled /></el-icon>
                </el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item command="open">{{ $t('sites.open') }}</el-dropdown-item>
                    <el-dropdown-item command="toggle">{{ row.enabled !== false ? $t('sites.list.disable') : $t('sites.list.enable') }}</el-dropdown-item>
                    <el-dropdown-item command="detect">{{ $t('sites.detect') }}</el-dropdown-item>
                    <el-dropdown-item command="delete" divided class="danger-item">{{ $t('sites.delete') }}</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <div v-if="filteredSites.length === 0 && !sitesStore.loading" class="list-empty">
        <template v-if="search || stateFilter !== 'all'">
          <p>{{ search ? $t('sites.noMatching', { q: search }) : $t('sites.list.noneInFilter') }}</p>
          <el-button @click="search = ''; stateFilter = 'all'">{{ $t('sites.list.clearFilter') }}</el-button>
        </template>
        <template v-else>
          <p class="list-empty-title">{{ $t('sites.noSitesYet') }}</p>
          <el-button type="primary" @click="showCreate = true">{{ $t('sites.create') }}</el-button>
        </template>
      </div>
    </div>
    </template>

    <!-- Site edit is a full-view route at /sites/:domain/edit (no drawer). -->

    <!-- Create dialog -->
    <el-dialog v-model="showCreate" :title="$t('sites.create')" :width="uiModeStore.isAdvanced ? '600px' : '520px'">

      <!-- Simple mode form -->
      <div v-if="uiModeStore.isSimple">
        <p class="simple-hint">{{ $t('sites.simple.hint') }}</p>
        <el-form :model="newSite" label-position="top" size="default">
          <el-form-item :label="$t('sites.domain')" required>
            <el-input
              v-model="newSite.domain"
              placeholder="myapp.loc"
              autofocus
            />
          </el-form-item>
          <el-form-item :label="$t('sites.documentRoot')" required>
            <el-input v-model="newSite.documentRoot" :placeholder="docRootPlaceholder">
              <template #append>
                <el-button :title="$t('sites.pickFolder')" @click="pickDocumentRoot">
                  <el-icon><FolderOpened /></el-icon>
                </el-button>
              </template>
            </el-input>
          </el-form-item>
          <el-form-item :label="$t('sites.phpVersion')">
            <!-- F92: when no PHP versions are installed the user is dropped
                 into a dead-end select with only "none" — frustrating for
                 a "create my first site" beginner. Surface a one-tap link
                 to the PHP Manager (which auto-flips them into Advanced)
                 so the path forward is obvious. -->
            <el-alert
              v-if="phpVersions.length === 0"
              type="info"
              :closable="false"
              show-icon
              style="margin-bottom: var(--wdc-space-2)"
            >
              <template #title>{{ $t('sites.simple.noPhpInstalledTitle') }}</template>
              <div>
                {{ $t('sites.simple.noPhpInstalledHint') }}
                <a class="simple-inline-link" @click="openPhpManagerFromSimple">
                  {{ $t('sites.simple.noPhpInstalledCta') }}
                </a>
              </div>
            </el-alert>
            <el-select v-model="newSite.phpVersion" style="width: 100%" :placeholder="$t('sites.phpVersionPlaceholder')">
              <el-option v-for="v in phpVersions" :key="v.value" :label="v.label" :value="v.value" />
              <el-option :label="$t('sites.phpNone')" value="none" />
            </el-select>
          </el-form-item>
          <el-form-item :label="$t('sites.bindIp')">
            <el-select
              v-model="newSite.bindAddresses"
              style="width: 100%"
              multiple
              clearable
              collapse-tags
              collapse-tags-tooltip
              filterable
              :loading="bindAddressOptionsLoading"
              :disabled="bindAddressOptionsLoading || bindAddressOptions.length === 0"
              @change="onNewSiteBindAddressesChange"
            >
              <el-option
                v-for="opt in bindAddressOptions"
                :key="opt.value"
                :label="opt.label"
                :value="opt.value"
              >
                <span class="bind-opt-label">{{ opt.label }}</span>
                <span
                  v-if="opt.wildcard || opt.loopback"
                  class="bind-opt-badge"
                >{{ $t('sites.bindIpRecommended') }}</span>
              </el-option>
            </el-select>
            <div class="form-hint">{{ $t('sites.bindIpHint') }}</div>
          </el-form-item>
          <!-- F91.2: new-site toggles hidden when their plugin is disabled. -->
          <el-form-item v-if="pluginsStore.isUiVisible('sites-badge:cloudflare-tunnel')" :label="$t('sites.simple.cloudflareTunnel')">
            <el-switch v-model="newSite.cloudflareTunnel" />
          </el-form-item>
          <el-form-item v-if="pluginsStore.isUiVisible('site-tab:ssl')" :label="$t('sites.ssl')">
            <el-switch v-model="newSite.sslEnabled" />
          </el-form-item>
          <el-form-item>
            <el-button
              type="primary"
              size="large"
              style="width: 100%"
              :loading="creating"
              @click="createSite"
            >{{ $t('sites.simple.submit') }}</el-button>
          </el-form-item>
        </el-form>
        <div class="simple-advanced-link">
          <span v-activate @click="uiModeStore.setUiMode('advanced')">{{ $t('uiMode.switchToAdvanced') }}</span>
        </div>
      </div>

      <!-- Advanced mode form -->
      <div v-else>
        <el-form :model="newSite" label-position="top" size="small">
          <el-form-item :label="$t('sites.template')">
            <el-select v-model="newSite.template" style="width: 100%" :placeholder="$t('sites.templatePlaceholder')" @change="applyTemplate">
              <el-option :label="$t('sites.templateNone')" value="" />
              <el-option
                v-for="tpl in siteTemplates"
                :key="tpl.id"
                :label="tpl.label"
                :value="tpl.id"
              />
            </el-select>
          </el-form-item>
          <el-form-item :label="$t('sites.domain')" required>
            <el-input v-model="newSite.domain" placeholder="myapp.loc" />
          </el-form-item>
          <el-form-item :label="$t('sites.documentRoot')" required>
            <el-input v-model="newSite.documentRoot" :placeholder="docRootPlaceholder">
              <template #append>
                <el-button :title="$t('sites.pickFolder')" @click="pickDocumentRoot">
                  <el-icon><FolderOpened /></el-icon>
                </el-button>
              </template>
            </el-input>
          </el-form-item>
          <el-form-item :label="$t('sites.phpVersion')">
            <el-select v-model="newSite.phpVersion" style="width: 100%" :placeholder="$t('sites.phpVersionPlaceholder')">
              <el-option v-for="v in phpVersions" :key="v.value" :label="v.label" :value="v.value" />
              <el-option :label="$t('sites.phpNone')" value="none" />
            </el-select>
          </el-form-item>
          <el-form-item :label="$t('sites.aliases')">
            <el-input v-model="newSite.aliases" placeholder="www.myapp.loc" />
          </el-form-item>
          <el-form-item :label="$t('sites.bindIp')">
            <el-select
              v-model="newSite.bindAddresses"
              style="width: 100%"
              multiple
              clearable
              collapse-tags
              collapse-tags-tooltip
              filterable
              :loading="bindAddressOptionsLoading"
              :disabled="bindAddressOptionsLoading || bindAddressOptions.length === 0"
              @change="onNewSiteBindAddressesChange"
            >
              <el-option
                v-for="opt in bindAddressOptions"
                :key="opt.value"
                :label="opt.label"
                :value="opt.value"
              >
                <span class="bind-opt-label">{{ opt.label }}</span>
                <span
                  v-if="opt.wildcard || opt.loopback"
                  class="bind-opt-badge"
                >{{ $t('sites.bindIpRecommended') }}</span>
              </el-option>
            </el-select>
            <div class="form-hint">{{ $t('sites.bindIpHint') }}</div>
          </el-form-item>
          <el-form-item :label="$t('sites.ssl')">
            <el-switch v-model="newSite.sslEnabled" />
          </el-form-item>
          <el-form-item :label="$t('sites.createDatabase')">
            <el-switch v-model="newSite.createDb" />
            <el-input
              v-if="newSite.createDb"
              v-model="newSite.dbName"
              :placeholder="$t('sites.createDatabasePlaceholder')"
              style="margin-top: var(--wdc-space-2)"
            />
          </el-form-item>
        </el-form>
      </div>

      <template v-if="uiModeStore.isAdvanced" #footer>
        <el-button @click="showCreate = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="creating" @click="createSite">{{ $t('sites.simple.submit') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { openHostsFile } from '../../utils/hostsFile'
import { ref, reactive, computed, defineAsyncComponent, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox, ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { useUiModeStore } from '../../stores/uiMode'
import { usePluginsStore } from '../../stores/plugins'
import type { SiteInfo } from '../../api/types'
import { fetchBindAddressOptions, fetchDockerComposeStatus, fetchPhpVersions, fetchSystem, daemonBaseUrl, daemonAuthHeaders as authHeaders, type BindAddressOption, type DockerComposeStatus } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { MoreFilled, FolderOpened, Check, Close, Remove, Lock } from '@element-plus/icons-vue'
import { useSiteApplyStore } from '../../stores/siteApply'
// Lazy-load the simple-mode sites list (and its 4 sub-components +
// MiniSparkline). Advanced users land on the table view, never need
// the card grid — keep it out of the Sites chunk's cold-start cost.
const SitesListSimple = defineAsyncComponent(() => import('./SitesListSimple.vue'))
import PluginSlot from '../shared/PluginSlot.vue'
import siteTemplatesConfig from '../../config/site-templates.json'

// Template list for the New Site dialog (advanced mode). Sourced from
// src/config/site-templates.json so product can add/tweak presets without
// touching this component. `fields` is spread into `newSite` on pick, so
// any field on the reactive object (phpVersion, sslEnabled, and future
// additions like `extensions`) can be templated without code changes here.
interface SiteTemplate {
  id: string
  label: string
  fields: Partial<{
    phpVersion: string
    sslEnabled: boolean
    aliases: string
    createDb: boolean
    cloudflareTunnel: boolean
  }> & Record<string, unknown>
  notes?: string
}
const siteTemplates: SiteTemplate[] = (siteTemplatesConfig.templates || []) as SiteTemplate[]

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
const siteApplyStore = useSiteApplyStore()
const daemonStore = useDaemonStore()
const uiModeStore = useUiModeStore()
const pluginsStore = usePluginsStore()

// Task 01: per-row enable/disable toggle state.
const togglingEnabled = ref<string | null>(null)
async function toggleSiteEnabled(site: { domain: string; enabled?: boolean }, value: boolean | string | number) {
  const enabled = value === true
  togglingEnabled.value = site.domain
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/sites/${encodeURIComponent(site.domain)}/enabled`, {
      method: 'PATCH',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    })
    if (!r.ok) throw new Error((await r.text().catch(() => '')) || `HTTP ${r.status}`)
    ElMessage.success(t(enabled ? 'sitesToast.enabled' : 'sitesToast.disabled', { domain: site.domain }))
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(t('sitesToast.toggleFailed', { err: e instanceof Error ? e.message : String(e) }))
  } finally {
    togglingEnabled.value = null
  }
}

// Sites list shows per-row tunnel links when cloudflare.enabled → but the
// link only works if the shared cloudflared process is actually running.
// When it's stopped, render the row in a dimmed style with an "offline"
// badge so users don't click through to a dead URL and see a Cloudflare
// 521 error page. Computed here so it's reactive to SSE service updates.
const cloudflaredRunning = computed(() =>
  daemonStore.services.some(s =>
    s.id === 'cloudflare' && (s.state === 2 || s.status === 'running')
  )
)
// Per-version dropdown entry. `value` is the majorMinor (what the backend
// writes to sites.toml + uses for Apache vhost per-version paths); `label`
// shows the full X.Y.Z version + an "active" hint so the user knows which
// one is the current runtime, not just which dot-directories exist under
// ~/.wdc/binaries/php/.
interface PhpVersionOption { value: string; label: string; isActive: boolean }
const phpVersions = ref<PhpVersionOption[]>([])
const bindAddressOptions = ref<BindAddressOption[]>([])
const bindAddressOptionsLoading = ref(false)

// Map the stored majorMinor (e.g. "8.5") back to the full version label
// ("PHP 8.5.5") for table display so users see the exact installed patch
// and not just the major.minor bucket. Falls back to "PHP X.Y" when we
// have no match (e.g. site points at a version that's no longer
// installed, like the user's bad-php.loc with "7.0.0" — no 7.0 installed
// so we just render what the row already holds).
function phpFullLabel(stored: string): string {
  const match = phpVersions.value.find(p => p.value === stored)
  return match ? match.label : `PHP ${stored}`
}

function rowRuntimeLabel(site: SiteInfo): string {
  if (site.nodeUpstreamPort && site.nodeUpstreamPort > 0) return `Node:${site.nodeUpstreamPort}`
  if (site.phpVersion && site.phpVersion !== 'none') return phpFullLabel(site.phpVersion)
  return t('sites.runtimeStatic')
}
// Native-looking Document Root placeholder. Windows keeps the legacy
// C:\work\htdocs\<app> hint; macOS/Linux get a path rooted at the user's
// home dir so the placeholder itself isn't misleading on those OSes.
const docRootPlaceholder = ref<string>('C:\\work\\htdocs\\myapp')
async function resolveDocRootPlaceholder() {
  try {
    const sys = await fetchSystem()
    if (sys.os.tag === 'macos' || sys.os.tag === 'linux') {
      const home = sys.os.platform?.match(/Users\/([^/]+)/)?.[1]
        ?? sys.os.platform?.match(/home\/([^/]+)/)?.[1]
      docRootPlaceholder.value = home ? `/Users/${home}/Sites/myapp` : '/Sites/myapp'
      if (sys.os.tag === 'linux') {
        docRootPlaceholder.value = home ? `/home/${home}/Sites/myapp` : '/srv/www/myapp'
      }
    }
  } catch { /* keep Windows default — harmless fallback */ }
}

// Native folder picker — renderer calls into Electron main (allowlisted
// via preload `showOpenDialog`). When Electron isn't present (browser
// preview during dev) the button is a no-op and the user types the path.
async function pickDocumentRoot(): Promise<void> {
  const api = (window as unknown as { electronAPI?: { showOpenDialog: (o: unknown) => Promise<{ canceled: boolean; filePaths: string[] }> } }).electronAPI
  if (!api?.showOpenDialog) {
    ElMessage.info(t('sitesToast.folderPickerDesktopOnly'))
    return
  }
  const result = await api.showOpenDialog({
    properties: ['openDirectory', 'createDirectory'],
    title: 'Select document root',
  })
  if (!result.canceled && result.filePaths[0]) {
    newSite.documentRoot = result.filePaths[0]
  }
}

// F92: simple-mode escape hatch when no PHP version is installed yet.
// Flips the user into Advanced so the PHP route un-gates, then navigates
// to the PHP Manager. The create-site dialog stays open in advanced view
// so they can return to it after installing a version.
function openPhpManagerFromSimple(): void {
  uiModeStore.setUiMode('advanced')
  showCreate.value = false
  void router.push('/php')
}
// Docker Compose detection map: domain -> status. Lazy-populated after
// sites load so the Compose badge in the Framework column reflects what
// the daemon sees on disk without blocking the site list itself.
const composeStatus = reactive<Record<string, DockerComposeStatus>>({})

const showCreate = ref(false)
const creating = ref(false)
const reapplying = ref(false)
// F73: discoverMamp() + mampDiscovering moved to Settings General tab.
const search = ref('')

const newSite = reactive({
  template: '',
  domain: '',
  documentRoot: '',
  // Left empty intentionally — `loadPhpVersions()` in onMounted picks the
  // first installed version (preferring the active one) as the default.
  // Previously hardcoded to '8.4' which produced an empty-looking dropdown
  // on fresh installs whose only version was e.g. 8.5 or 8.3 — el-select
  // binds v-model to a non-existent option and renders blank.
  phpVersion: '',
  aliases: '',
  bindAddress: '*',
  bindAddresses: ['*'],
  localhostLoopbackEnabled: false,
  createDb: false,
  dbName: '',
  // SSL on by default so users opting out is the conscious choice — fresh
  // installs with no cert will have Apache skip the SSL vhost block until
  // mkcert actually issues one (handled by SiteOrchestrator on create).
  sslEnabled: true,
  cloudflareTunnel: false,
})

// Auto-suggest document root when domain is typed in simple mode. The
// suggestion sits next to the OS-specific placeholder (it used to be a
// hardcoded C:\work\htdocs path on every OS) and stops as soon as the user
// types or picks a folder of their own.
let lastSuggestedDocRoot = ''
watch(() => newSite.domain, (domain) => {
  if (!uiModeStore.isSimple || !domain) return
  if (newSite.documentRoot && newSite.documentRoot !== lastSuggestedDocRoot) return
  const placeholder = docRootPlaceholder.value
  const sep = placeholder.includes('\\') ? '\\' : '/'
  const base = placeholder.slice(0, Math.max(placeholder.lastIndexOf(sep), 0))
  lastSuggestedDocRoot = base ? `${base}${sep}${domain}` : ''
  newSite.documentRoot = lastSuggestedDocRoot
})

function applyTemplate(tplName: string) {
  if (!tplName) return
  const tpl = siteTemplates.find(t => t.id === tplName)
  if (!tpl || !tpl.fields) return
  // Spread template field overrides into the reactive form state. Only
  // keys present in `fields` are touched — fields the user already filled
  // in (domain, documentRoot) are preserved.
  Object.assign(newSite, tpl.fields)
}

// Per-site state for the list: disabled, last apply failed, not served
// because Apache is down (same rule as the Simple list), or fine.
type SiteListState = 'ok' | 'failed' | 'stopped' | 'disabled'
const apacheRunning = computed(() =>
  daemonStore.services.some(s => s.id === 'apache' && (s.state === 2 || s.status === 'running')))
function siteState(site: SiteInfo): SiteListState {
  if (site.enabled === false) return 'disabled'
  const rec = siteApplyStore.results[site.domain]
  if (rec && rec.steps.some(st => st.status === 'failed')) return 'failed'
  return apacheRunning.value ? 'ok' : 'stopped'
}
function failedStepsTitle(site: SiteInfo): string {
  const rec = siteApplyStore.results[site.domain]
  const failed = rec?.steps.filter(st => st.status === 'failed') ?? []
  return failed.map(st => st.detail ? `${st.name}: ${st.detail}` : st.name).join('\n')
}
function portSuffix(site: SiteInfo): string {
  const port = site.sslEnabled ? (site.httpsPort || 443) : (site.httpPort || 80)
  return (site.sslEnabled && port === 443) || (!site.sslEnabled && port === 80) ? '' : `:${port}`
}

const stateFilter = ref<'all' | SiteListState>('all')
const stateCounts = computed(() => {
  const c = { ok: 0, failed: 0, stopped: 0, disabled: 0 }
  for (const site of sitesStore.sites) c[siteState(site)]++
  return c
})
const stateFilters = computed(() => [
  { id: 'all' as const, label: t('sites.list.filter.all'), count: sitesStore.sites.length },
  { id: 'failed' as const, label: t('sites.list.filter.failed'), count: stateCounts.value.failed },
  { id: 'disabled' as const, label: t('sites.list.filter.disabled'), count: stateCounts.value.disabled },
])
const sitesSummary = computed(() => {
  const total = sitesStore.sites.length
  if (total === 0) return t('sites.list.summaryEmpty')
  const parts = [t('sites.list.summaryTotal', { n: total }, total)]
  if (stateCounts.value.failed) parts.push(t('sites.list.summaryFailed', { n: stateCounts.value.failed }, stateCounts.value.failed))
  if (stateCounts.value.disabled) parts.push(t('sites.list.summaryDisabled', { n: stateCounts.value.disabled }, stateCounts.value.disabled))
  return parts.join(' · ')
})

const filteredSites = computed(() => {
  const q = search.value.toLowerCase()
  const byState = stateFilter.value === 'all'
    ? sitesStore.sites
    : sitesStore.sites.filter(s => siteState(s) === stateFilter.value)
  if (!q) return byState
  return byState.filter(s =>
    s.domain.toLowerCase().includes(q) ||
    s.documentRoot.toLowerCase().includes(q) ||
    (s.framework ?? '').toLowerCase().includes(q)
  )
})

// Open create dialog if navigated with ?create=1
watch(() => route.query.create, (val) => {
  if (val === '1') showCreate.value = true
}, { immediate: true })

async function refreshComposeStatuses() {
  // Fire-and-forget compose detection per site. Individual failures
  // (network, permission, plugin disabled) are silently skipped so one
  // bad row never blocks the badge column as a whole.
  const liveDomains = new Set(sitesStore.sites.map(s => s.domain))

  // Drop entries for sites that no longer exist (deleted in another tab,
  // renamed, etc.) so composeStatus doesn't accumulate garbage across
  // long sessions.
  for (const domain of Object.keys(composeStatus)) {
    if (!liveDomains.has(domain)) delete composeStatus[domain]
  }

  const tasks = sitesStore.sites.map(async (s) => {
    try {
      const status = await fetchDockerComposeStatus(s.domain)
      composeStatus[s.domain] = status
    } catch { /* leave entry absent — no badge rendered */ }
  })
  await Promise.all(tasks)
}

async function loadPhpVersions() {
  try {
    const versions = await fetchPhpVersions()
    phpVersions.value = versions.map(v => {
      const mm = v.majorMinor || v.version.split('.').slice(0, 2).join('.') || v.version
      return {
        value: mm,
        // "PHP 8.5.5 (active)" vs "PHP 8.3.25" — shows the exact patch
        // version so users know which one they'd actually run.
        label: `PHP ${v.version}${v.isActive ? ' (active)' : ''}`,
        isActive: !!v.isActive,
      }
    })
    // Pick the active version if the wizard's phpVersion field is empty
    // or currently references a version that isn't installed (e.g. stale
    // '8.4' default on a fresh install with only 8.5).
    if (phpVersions.value.length > 0) {
      const active = phpVersions.value.find(p => p.isActive)
      const preferred = (active ?? phpVersions.value[0]).value
      const present = phpVersions.value.some(p => p.value === newSite.phpVersion)
      if (!newSite.phpVersion || !present) {
        newSite.phpVersion = preferred
      }
    }
  } catch { phpVersions.value = [] }
}

async function loadBindAddressOptions() {
  bindAddressOptionsLoading.value = true
  try {
    const options = await fetchBindAddressOptions()
    bindAddressOptions.value = options
    newSite.bindAddresses = normalizeBindAddresses(newSite.bindAddresses)
    if (!newSite.bindAddresses.every(v => bindAddressOptions.value.some(o => o.value === v))) {
      newSite.bindAddresses = ['*']
    }
  } catch {
    bindAddressOptions.value = []
  } finally {
    bindAddressOptionsLoading.value = false
  }
}

onMounted(async () => {
  await sitesStore.load()
  void refreshComposeStatuses()
  void resolveDocRootPlaceholder()
  await loadPhpVersions()
  await loadBindAddressOptions()
})

const isNewSiteLocalhost = computed(() => newSite.domain.trim().toLowerCase() === 'localhost')

function normalizeBindAddresses(values: string[] | undefined): string[] {
  const selected = [...new Set((values ?? []).map(v => String(v).trim()).filter(Boolean))]
  if (selected.length === 0) return []
  if (selected.includes('*')) {
    return selected[selected.length - 1] === '*'
      ? ['*']
      : selected.filter(v => v !== '*')
  }
  return selected
}

function onNewSiteBindAddressesChange(values: string[]) {
  newSite.bindAddresses = normalizeBindAddresses(values)
  newSite.bindAddress = newSite.bindAddresses[0] ?? '*'
}

function bindAddressLabel(site: SiteInfo): string {
  const values = normalizeBindAddresses(site.bindAddresses?.length ? site.bindAddresses : [site.bindAddress || '*'])
  return values.length > 2 ? `${values[0]}, +${values.length - 1}` : values.join(', ')
}

// Refresh PHP versions every time the create dialog opens — the user may
// have installed a new binary via the Binaries page between app launches,
// and forcing them to reload the Sites page to see it would surprise them.
watch(showCreate, (open) => {
  if (open) void loadPhpVersions()
  if (open) void loadBindAddressOptions()
})

// Re-scan compose status whenever the set of sites OR any site's
// document root changes. Watching .length alone would miss an edit
// that swaps documentRoot without changing the count, leaving a stale
// badge until the user refreshes the page.
watch(
  () => sitesStore.sites.map(s => `${s.domain}::${s.documentRoot}`).join('|'),
  () => { void refreshComposeStatuses() },
)

function selectSite(row: SiteInfo) {
  // Navigate to full-view edit page instead of opening a drawer
  void router.push(`/sites/${encodeURIComponent(row.domain)}/edit`)
}

async function createSite() {
  if (!newSite.domain || !newSite.documentRoot) {
    ElMessage.warning(t('sitesToast.domainRequired'))
    return
  }
  newSite.bindAddresses = normalizeBindAddresses(newSite.bindAddresses)
  if (newSite.bindAddresses.length === 0) {
    ElMessage.warning(t('sitesToast.bindRequired'))
    return
  }
  creating.value = true
  try {
    const payload: Partial<SiteInfo> & { cloudflareTunnel?: boolean } = {
      domain: newSite.domain,
      documentRoot: newSite.documentRoot,
      phpVersion: newSite.phpVersion,
      sslEnabled: newSite.sslEnabled,
      aliases: newSite.aliases ? newSite.aliases.split(',').map(s => s.trim()).filter(Boolean) : [],
      bindAddress: newSite.bindAddresses[0] ?? '*',
      bindAddresses: normalizeBindAddresses(newSite.bindAddresses),
      localhostLoopbackEnabled: isNewSiteLocalhost.value,
      ...(newSite.cloudflareTunnel ? { cloudflareTunnel: true } : {}),
    }
    const created = await sitesStore.create(payload)

    // Surface daemon's soft warnings (e.g. bind IP not assigned to any
    // active NIC — Apache will fail to bind at start). Previously the
    // create() return type was just SiteInfo so these warnings dropped
    // silently. The sites store now exposes lastCreateWarnings + Hints
    // as a side-channel; toast each one as its own warning so the
    // operator sees what to fix.
    for (const w of sitesStore.lastCreateWarnings) {
      ElMessage.warning({ message: w, duration: 8000, showClose: true })
    }
    for (const h of sitesStore.lastCreateHints) {
      ElMessage.info({ message: h, duration: 6000, showClose: true })
    }

    // Surface silent SSL failure — if the user asked for HTTPS but the
    // daemon's mkcert step didn't produce a cert (binary missing, CA
    // not installed, permission denied), SiteOrchestrator just logs a
    // warning and returns the site with sslEnabled=false. Previously
    // users saw "Site created" and had no clue why https://… wasn't
    // working. We compare the request vs response and toast a warning
    // with guidance on how to recover.
    if (newSite.sslEnabled && created && created.sslEnabled === false) {
      ElMessage.warning({
        message: t('sitesToast.createdSslMissing'),
        duration: 8000,
        showClose: true,
      })
    }

    // Auto-create database if requested
    if (newSite.createDb) {
      const dbName = newSite.dbName || newSite.domain.replace(/\./g, '_').replace(/-/g, '_') + '_db'
      try {
        const dbRes = await fetch(`${daemonBaseUrl()}/api/databases`, {
          method: 'POST',
          headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: dbName }),
        })
        if (!dbRes.ok) throw new Error(`DB create HTTP ${dbRes.status}`)
        ElMessage.success(t('sitesToast.createdWithDb', { domain: newSite.domain, db: dbName }))
      } catch {
        ElMessage.warning(t('sitesToast.createdDbFailed', { domain: newSite.domain }))
      }
    } else {
      ElMessage.success(t('sitesToast.created', { domain: newSite.domain }))
    }

    showCreate.value = false
    // Reset form. phpVersion picks the active/first installed version
    // again (set once by loadPhpVersions on dialog open) instead of a
    // hardcoded literal that may not exist on this machine.
    const defaultPhp = phpVersions.value.find(p => p.isActive)?.value ?? phpVersions.value[0]?.value ?? ''
    Object.assign(newSite, { domain: '', documentRoot: '', phpVersion: defaultPhp, aliases: '', bindAddress: '*', bindAddresses: ['*'], localhostLoopbackEnabled: false, sslEnabled: true, createDb: false, dbName: '', cloudflareTunnel: false, template: '' })
  } catch (e) {
    ElMessage.error(t('sitesToast.createFailed', { err: errorMessage(e) }))
  } finally {
    creating.value = false
  }
}

async function detectFramework(domain: string) {
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/${domain}/detect-framework`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...sitesStore.authHeaders() },
    })
    if (!res.ok) {
      const text = await res.text().catch(() => res.statusText)
      throw new Error(text || `HTTP ${res.status}`)
    }
    const data = await res.json() as { framework?: string }
    if (data.framework) {
      ElMessage.success(t('sitesToast.detected', { framework: data.framework }))
    } else {
      ElMessage.info(t('sitesToast.noFramework'))
    }
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(t('sitesToast.detectFailed', { err: errorMessage(e) }))
  }
}

async function confirmDelete(domain: string) {
  try {
    await ElMessageBox.confirm(t('sitesToast.deleteConfirm', { domain }), t('sitesToast.deleteConfirmTitle'), {
      type: 'warning',
      confirmButtonText: 'Delete',
      confirmButtonClass: 'el-button--danger',
    })
  } catch {
    // User cancelled the confirm dialog — don't proceed and don't surface an error.
    return
  }
  try {
    await sitesStore.remove(domain)
    ElMessage.success(t('sitesToast.deleted'))
  } catch (e) {
    ElMessage.error(t('sitesToast.deleteFailed', { err: errorMessage(e) }))
  }
}

async function reapplyAll() {
  reapplying.value = true
  try {
    const res = await fetch(`${daemonBaseUrl()}/api/sites/reapply-all`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (res.ok) {
      const results: Array<{ domain: string; ok: boolean; error?: string }> = await res.json()
      const ok = results.filter(r => r.ok).length
      const failed = results.filter(r => !r.ok)
      if (failed.length === 0) {
        ElMessage.success(t('toast.vhostsRegenerated', { n: ok }))
      } else {
        ElMessage.warning(t('toast.vhostsPartial', { ok, failed: failed.length, list: failed.map(f => f.domain).join(', ') }))
      }
    } else {
      ElMessage.error(t('toast.httpFailed', { status: res.status }))
    }
  } catch (e) {
    ElMessage.error(t('toast.reapplyFailed', { err: errorMessage(e) }))
  } finally {
    reapplying.value = false
  }
}


function openInBrowser(site: SiteInfo) {
  const proto = site.sslEnabled ? 'https' : 'http'
  const port = site.sslEnabled ? (site.httpsPort || 443) : (site.httpPort || 80)
  const portSuffix = (site.sslEnabled && port === 443) || (!site.sslEnabled && port === 80) ? '' : `:${port}`
  const url = `${proto}://${site.domain}${portSuffix}`
  // Prefer shell.openExternal via preload so the URL opens in the user's
  // default system browser instead of a new Electron BrowserWindow — which
  // is what `window.open(url, '_blank')` would do under Electron.
  const api = window.electronAPI
  if (api?.openExternal) api.openExternal(url)
  else window.open(url, '_blank')
}

function editSite(site: SiteInfo) {
  void router.push(`/sites/${encodeURIComponent(site.domain)}/edit`)
}

function handleRowAction(cmd: string, row: SiteInfo) {
  if (cmd === 'open') openInBrowser(row)
  else if (cmd === 'detect') void detectFramework(row.domain)
  else if (cmd === 'toggle') void toggleSiteEnabled(row, row.enabled === false)
  else if (cmd === 'delete') void confirmDelete(row.domain)
}

// rollbackConfig + formatDate removed — rollback is handled in SiteEdit History tab
</script>

<style scoped>
/* Sites list — hairline table on the page ground (redesign phase 3). */
.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
}
.page-title {
  font-size: 1.728rem;
  line-height: 1.1;
  letter-spacing: -0.028em;
}
.page-sub {
  margin-top: var(--wdc-space-2);
  color: var(--wdc-text-2);
}
.header-actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--wdc-space-2);
}
.header-actions :deep(.el-button + .el-button) { margin-left: 0; }

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-3);
}
.toolbar-search { max-width: 320px; }

.tray {
  display: inline-flex;
  gap: var(--wdc-space-0-5);
  padding: var(--wdc-space-0-5);
  border-radius: var(--wdc-radius-md);
  background: var(--wdc-surface-2);
}
.tray-key {
  min-height: 30px;
  padding: 0 var(--wdc-space-3);
  border: 1px solid transparent;
  border-radius: var(--wdc-radius);
  background: transparent;
  color: var(--wdc-text-2);
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.87rem;
  cursor: pointer;
}
@media (hover: hover) { .tray-key:hover { color: var(--wdc-text); } }
.tray-key[aria-pressed="true"] {
  background: var(--wdc-key-face);
  border-color: var(--wdc-border-strong);
  color: var(--wdc-text);
  box-shadow: var(--wdc-shadow-key-press);
}
.tray-key:focus-visible { outline: 2px solid var(--wdc-accent); outline-offset: 1px; }
.tray-count { margin-left: var(--wdc-space-1); color: var(--wdc-text-3); font-size: 0.78rem; }

.page-body { min-width: 0; }
.sites-mobile-list { display: none; }

.cell-domain {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--wdc-space-1-5);
  min-width: 0;
}
.col-domain {
  font-family: var(--wdc-font-display);
  font-weight: 500;
  font-size: 0.93rem;
  color: var(--wdc-text);
}
.alias-dot {
  font-size: 0.74rem;
  color: var(--wdc-text-3);
  cursor: help;
}

.site-status {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  white-space: nowrap;
}
.ss-ok .el-icon { color: var(--wdc-status-running); }
.ss-failed { color: var(--wdc-status-error); }
.ss-disabled,
.ss-stopped { color: var(--wdc-text-3); }

.cell-runtime { color: var(--wdc-text-2); font-size: 0.82rem; }
.cell-address {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1-5);
  font-size: 0.82rem;
  color: var(--wdc-text-2);
}
.https-ok { color: var(--wdc-status-running); }
.muted { color: var(--wdc-text-3); }

.col-mono {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--wdc-font-mono);
  font-size: 0.78rem;
  color: var(--wdc-text-2);
  direction: rtl;
  text-align: left;
}
.sites-table :deep(.col-docroot-cell .cell) { max-width: 260px; }
.sites-table :deep(.el-table__row) { cursor: pointer; }

.col-empty { color: var(--wdc-text-3); }
.framework-cell {
  display: inline-flex;
  gap: var(--wdc-space-1-5);
  font-size: 0.82rem;
  color: var(--wdc-text-2);
}

.list-empty {
  display: grid;
  justify-items: start;
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-5) 0;
  border-bottom: 1px solid var(--wdc-border);
  color: var(--wdc-text-2);
}
.list-empty-title { font-family: var(--wdc-font-display); font-weight: 500; color: var(--wdc-text); }

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}

/* The sidebar takes ~232px, so these breakpoints leave the table room
   for its pinned action column without clipping the last header. */
@media (max-width: 1400px) {
  .sites-table :deep(.col-framework-cell) { display: none; }
}
@media (max-width: 1100px) {
  .sites-table :deep(.col-docroot-cell) { display: none; }
}
@media (max-width: 700px) {
  .toolbar-search { max-width: none; width: 100%; }
  .sites-table { display: none; }
  .sites-mobile-list {
    display: grid;
    border-top: 1px solid var(--wdc-border);
  }
  .site-mobile-card {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: var(--wdc-space-3);
    align-items: center;
    min-width: 0;
    padding: var(--wdc-space-3) 0;
    border-bottom: 1px solid var(--wdc-border);
  }
  .site-mobile-main { min-width: 0; cursor: pointer; }
  .site-mobile-title-row, .site-mobile-actions {
    display: flex;
    align-items: center;
    gap: var(--wdc-space-2);
    min-width: 0;
  }
  .site-mobile-title {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: var(--wdc-font-display);
    font-weight: 500;
    color: var(--wdc-text);
  }
  .site-mobile-meta {
    margin-top: var(--wdc-space-1);
    color: var(--wdc-text-3);
    font-size: 0.78rem;
  }
}

.site-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--wdc-space-1);
}
.site-actions :deep(.el-button + .el-button) { margin-left: 0; }

:global(.danger-item) {
  color: var(--wdc-status-error) !important;
}

.site-detail {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4);
  padding: var(--wdc-space-1) 0;
}

.drawer-actions {
  display: flex;
  gap: var(--wdc-space-2);
}

.history-section {
  border-top: 1px solid var(--el-border-color);
  padding-top: var(--wdc-space-3);
}

.history-title {
  font-size: 0.82rem;
  font-weight: 500;
  color: var(--el-text-color-secondary);
  margin-bottom: var(--wdc-space-2);
  font-family: var(--wdc-font-display);
}

.history-list {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-1);
}

.history-item {
  display: flex;
  justify-content: space-between;
  font-size: 0.78rem;
  color: var(--el-text-color-regular);
  padding: var(--wdc-space-1) 0;
  border-bottom: 1px dashed var(--el-border-color-lighter, #333);
}

.history-date { color: var(--el-text-color-secondary); font-family: monospace; }
.history-label { color: var(--el-text-color-regular); }

:global(.cursor-pointer) { cursor: pointer; }

.simple-hint {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  margin: 0 0 var(--wdc-space-4);
  line-height: 1.5;
}

.form-hint {
  margin-top: var(--wdc-space-1);
  color: var(--wdc-text-3);
  font-size: 12px;
  line-height: 1.35;
}

.simple-advanced-link {
  margin-top: var(--wdc-space-3);
  text-align: center;
  font-size: 0.8rem;
  color: var(--wdc-text-3);
}

.simple-advanced-link span {
  cursor: pointer;
  color: var(--wdc-accent);
  font-weight: 600;
  text-decoration: underline;
  text-underline-offset: 2px;
}

.simple-advanced-link span:hover {
  opacity: 0.8;
}

.simple-inline-link {
  cursor: pointer;
  color: var(--wdc-accent);
  font-weight: 600;
  text-decoration: underline;
  text-underline-offset: 2px;
  margin-left: var(--wdc-space-1);
}
.simple-inline-link:hover {
  opacity: 0.8;
}
</style>
