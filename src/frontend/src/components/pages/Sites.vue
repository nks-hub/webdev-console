<template>
  <div class="sites-page">
    <SitesListSimple v-if="!uiModeStore.isAdvanced" @create="showCreate = true" />

    <template v-if="uiModeStore.isAdvanced">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('sites.title') }}</h1>
        <span class="site-count">{{ sitesStore.sites.length }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openHostsFile" title="Open hosts file">
          {{ $t('sites.openHosts') }}
        </el-button>
        <el-button size="small" @click="reapplyAll" :loading="reapplying" title="Regenerate all vhosts">
          Reapply All
        </el-button>
        <el-button type="primary" size="small" @click="showCreate = true">{{ $t('sites.create') }}</el-button>
      </div>
    </div>

    <!-- Search bar -->
    <div class="search-bar">
      <el-input
        v-model="search"
        :placeholder="$t('sites.filterPlaceholder')"
        clearable
        size="small"
        style="max-width: 320px"
        prefix-icon="Search"
      />
    </div>

    <div class="page-body">
      <el-table
        :data="filteredSites"
        v-loading="sitesStore.loading"
        stripe
        @row-click="selectSite"
        class="sites-table"
        row-class-name="cursor-pointer"
        table-layout="auto"
      >
        <el-table-column prop="domain" :label="$t('sites.domain')" min-width="200">
          <template #default="{ row }">
            <div class="cell-domain">
              <div class="cell-domain-row">
                <span class="col-domain">{{ row.domain }}</span>
                <!-- Port badge. SSL-enabled sites advertise both :80→:443
                     (HTTP redirects, HTTPS serves), so show both ports so
                     the user isn't misled into thinking SSL didn't get
                     wired up. SSL-off sites stay on :80 alone. -->
                <span class="cell-port">{{
                  row.sslEnabled
                    ? `:${row.httpPort || 80}/:${row.httpsPort || 443}`
                    : `:${row.httpPort || 80}`
                }}</span>
                <el-tag
                  v-if="row.sslEnabled"
                  size="small"
                  type="success"
                  effect="dark"
                  class="cell-tag"
                  title="HTTPS enabled"
                >SSL</el-tag>
              </div>
              <div v-if="row.aliases?.length" class="col-aliases" :title="row.aliases.join(', ')">
                <span class="alias-dot">+{{ row.aliases.length }}</span>
                <span class="alias-preview">{{ row.aliases[0] }}<template v-if="row.aliases.length > 1">, …</template></span>
              </div>
              <!-- F91.6: plugin-contributed per-row badges.
                   Cloudflare plugin registers CloudflareTunnelBadge here via
                   ContributeSitesBadge(). Disabling the plugin removes its
                   contribution → badge disappears without a template change. -->
              <PluginSlot name="sites-row-badges" :context="{ site: row }" />
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.documentRoot')" min-width="220" class-name="col-docroot-cell">
          <template #default="{ row }">
            <span class="col-mono" :title="row.documentRoot">{{ row.documentRoot }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.phpVersion')" width="110">
          <template #default="{ row }">
            <el-tag
              v-if="row.nodeUpstreamPort && row.nodeUpstreamPort > 0"
              size="small"
              effect="dark"
              class="runtime-tag runtime-node"
            >Node:{{ row.nodeUpstreamPort }}</el-tag>
            <el-tag
              v-else-if="row.phpVersion && row.phpVersion !== 'none'"
              size="small"
              effect="dark"
              class="runtime-tag runtime-php"
              :title="phpFullLabel(row.phpVersion)"
            >{{ phpFullLabel(row.phpVersion) }}</el-tag>
            <el-tag
              v-else
              size="small"
              effect="plain"
              class="runtime-tag runtime-static"
            >Static</el-tag>
          </template>
        </el-table-column>

        <el-table-column :label="$t('sites.framework')" width="130" class-name="col-framework-cell">
          <template #default="{ row }">
            <div class="framework-cell">
              <el-tag
                v-if="row.framework"
                size="small"
                type="warning"
                effect="dark"
                class="cell-tag"
              >{{ row.framework }}</el-tag>
              <el-tag
                v-if="composeStatus[row.domain]?.hasCompose"
                size="small"
                type="info"
                effect="plain"
                class="cell-tag compose-tag"
                :title="composeStatus[row.domain]?.composeFile || ''"
              >Compose</el-tag>
              <span
                v-if="!row.framework && !composeStatus[row.domain]?.hasCompose"
                class="col-empty"
              >—</span>
            </div>
          </template>
        </el-table-column>

        <!-- Akce column: enable toggle + Edit + overflow menu merged into
             one column so the fixed-right sticky doesn't overlap
             neighbouring cells when the viewport narrows. Previously the
             'Povoleno' switch lived in its own 100px column which pushed
             the total fixed width past 1060px and broke layout below
             ~1100px viewport. -->
        <el-table-column :label="$t('common.actions')" width="180" fixed="right">
          <template #default="{ row }">
            <div class="site-actions">
              <el-switch
                :model-value="row.enabled !== false"
                :loading="togglingEnabled === row.domain"
                size="small"
                inline-prompt
                :title="row.enabled !== false ? 'Povoleno' : 'Zakázáno'"
                @change="(v: boolean | string | number) => toggleSiteEnabled(row, v)"
                @click.stop
              />
              <el-button size="small" type="primary" @click.stop="editSite(row)">{{ $t('common.edit') }}</el-button>
              <!-- Task 01: teleported + preventOverflow popper so the
                   dropdown never clips at the right edge of the table
                   (fixed="right" column was pushing it off-screen). -->
              <el-dropdown
                trigger="click"
                :teleported="true"
                :popper-options="{ modifiers: [{ name: 'preventOverflow', options: { boundary: 'viewport', padding: 8 } }] }"
                @command="(cmd: string) => handleRowAction(cmd, row)"
                @click.stop
              >
                <el-button size="small" @click.stop :aria-label="$t('sites.card.moreActions', { domain: row.domain })">
                  <el-icon><MoreFilled /></el-icon>
                </el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item command="open">{{ $t('sites.open') }}</el-dropdown-item>
                    <el-dropdown-item command="detect">{{ $t('sites.detect') }}</el-dropdown-item>
                    <el-dropdown-item command="delete" class="danger-item">{{ $t('sites.delete') }}</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <el-empty
        v-if="filteredSites.length === 0 && !sitesStore.loading"
        :description="search ? `No sites matching '${search}'` : 'No sites configured yet'"
        :image-size="80"
      />
    </div>
    </template>

    <!-- Site edit is a full-view route at /sites/:domain/edit (no drawer). -->

    <!-- Create dialog -->
    <el-dialog v-model="showCreate" :title="$t('sites.create')" width="520px">

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
                <el-button @click="pickDocumentRoot">…</el-button>
              </template>
            </el-input>
          </el-form-item>
          <el-form-item :label="$t('sites.phpVersion')">
            <el-select v-model="newSite.phpVersion" style="width: 100%" :placeholder="$t('sites.phpVersionPlaceholder')">
              <el-option v-for="v in phpVersions" :key="v.value" :label="v.label" :value="v.value" />
              <el-option :label="$t('sites.phpNone')" value="none" />
            </el-select>
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
          <span @click="uiModeStore.setUiMode('advanced')">{{ $t('uiMode.switchToAdvanced') }}</span>
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
                <el-button @click="pickDocumentRoot">…</el-button>
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
          <el-form-item :label="$t('sites.ssl')">
            <el-switch v-model="newSite.sslEnabled" />
          </el-form-item>
          <el-form-item :label="$t('sites.createDatabase')">
            <el-switch v-model="newSite.createDb" />
            <el-input
              v-if="newSite.createDb"
              v-model="newSite.dbName"
              :placeholder="$t('sites.createDatabasePlaceholder')"
              style="margin-top: 8px"
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
import { ref, reactive, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox, ElMessage } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { useUiModeStore } from '../../stores/uiMode'
import { usePluginsStore } from '../../stores/plugins'
import type { SiteInfo } from '../../api/types'
import { fetchDockerComposeStatus, fetchPhpVersions, fetchSystem, daemonBaseUrl, daemonAuthHeaders as authHeaders, type DockerComposeStatus } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { MoreFilled } from '@element-plus/icons-vue'
import SitesListSimple from './SitesListSimple.vue'
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

const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
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
    ElMessage.success(enabled ? `Site ${site.domain} enabled` : `Site ${site.domain} disabled`)
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(`Toggle failed: ${e instanceof Error ? e.message : e}`)
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
    ElMessage.info('Folder picker only available in the desktop app')
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
  createDb: false,
  dbName: '',
  // SSL on by default so users opting out is the conscious choice — fresh
  // installs with no cert will have Apache skip the SSL vhost block until
  // mkcert actually issues one (handled by SiteOrchestrator on create).
  sslEnabled: true,
  cloudflareTunnel: false,
})

// Auto-suggest document root when domain is typed in simple mode
watch(() => newSite.domain, (domain) => {
  if (uiModeStore.isSimple && domain) {
    newSite.documentRoot = `C:\\work\\htdocs\\${domain}`
  }
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

const filteredSites = computed(() => {
  const q = search.value.toLowerCase()
  if (!q) return sitesStore.sites
  return sitesStore.sites.filter(s =>
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

onMounted(async () => {
  await sitesStore.load()
  void refreshComposeStatuses()
  void resolveDocRootPlaceholder()
  await loadPhpVersions()
})

// Refresh PHP versions every time the create dialog opens — the user may
// have installed a new binary via the Binaries page between app launches,
// and forcing them to reload the Sites page to see it would surprise them.
watch(showCreate, (open) => {
  if (open) void loadPhpVersions()
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
    ElMessage.warning('Domain and document root are required')
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
      ...(newSite.cloudflareTunnel ? { cloudflareTunnel: true } : {}),
    }
    const created = await sitesStore.create(payload)

    // Surface silent SSL failure — if the user asked for HTTPS but the
    // daemon's mkcert step didn't produce a cert (binary missing, CA
    // not installed, permission denied), SiteOrchestrator just logs a
    // warning and returns the site with sslEnabled=false. Previously
    // users saw "Site created" and had no clue why https://… wasn't
    // working. We compare the request vs response and toast a warning
    // with guidance on how to recover.
    if (newSite.sslEnabled && created && created.sslEnabled === false) {
      ElMessage.warning({
        message: 'Site vytvořen, ale SSL certifikát nebyl vystaven. Nainstaluj mkcert + CA v Binaries → SSL.',
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
        ElMessage.success(`Site ${newSite.domain} + database ${dbName} created`)
      } catch {
        ElMessage.success(`Site ${newSite.domain} created (database creation failed)`)
      }
    } else {
      ElMessage.success(`Site ${newSite.domain} created`)
    }

    showCreate.value = false
    // Reset form. phpVersion picks the active/first installed version
    // again (set once by loadPhpVersions on dialog open) instead of a
    // hardcoded literal that may not exist on this machine.
    const defaultPhp = phpVersions.value.find(p => p.isActive)?.value ?? phpVersions.value[0]?.value ?? ''
    Object.assign(newSite, { domain: '', documentRoot: '', phpVersion: defaultPhp, aliases: '', sslEnabled: true, createDb: false, dbName: '', cloudflareTunnel: false, template: '' })
  } catch (e) {
    ElMessage.error(`Create failed: ${errorMessage(e)}`)
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
      ElMessage.success(`Detected: ${data.framework}`)
    } else {
      ElMessage.info('No framework detected')
    }
    await sitesStore.load()
  } catch (e) {
    ElMessage.error(`Detection failed: ${errorMessage(e)}`)
  }
}

async function confirmDelete(domain: string) {
  try {
    await ElMessageBox.confirm(`Delete site "${domain}"? This cannot be undone.`, 'Warning', {
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
    ElMessage.success('Site deleted')
  } catch (e) {
    ElMessage.error(`Delete failed: ${errorMessage(e)}`)
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
        ElMessage.success(`All ${ok} vhosts regenerated`)
      } else {
        ElMessage.warning(`${ok} OK, ${failed.length} failed: ${failed.map(f => f.domain).join(', ')}`)
      }
    } else {
      ElMessage.error(`Failed: HTTP ${res.status}`)
    }
  } catch (e) {
    ElMessage.error(`Reapply failed: ${errorMessage(e)}`)
  } finally {
    reapplying.value = false
  }
}

function openHostsFile() {
  // Open hosts file in system editor
  const hostsPath = 'C:\\Windows\\System32\\drivers\\etc\\hosts'
  window.open(`vscode://file/${hostsPath}`, '_self')
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
  else if (cmd === 'delete') void confirmDelete(row.domain)
}

// rollbackConfig + formatDate removed — rollback is handled in SiteEdit History tab
</script>

<style scoped>
.sites-page {
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

.site-count {
  font-size: 0.72rem;
  font-weight: 600;
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
  padding: 2px 8px;
  border-radius: 10px;
  font-family: 'JetBrains Mono', monospace;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.search-bar {
  padding: 0 24px;
  margin-bottom: 16px;
}

.page-body {
  padding: 0 24px 24px;
}

.sites-table :deep(.el-table__header) {
  background: var(--wdc-surface-2);
}
.sites-table :deep(.el-table__header th) {
  background: var(--wdc-surface-2) !important;
  color: var(--wdc-text-2) !important;
  font-weight: 700;
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  border-bottom: 2px solid var(--wdc-border-strong) !important;
}
.sites-table :deep(.el-table__row) {
  transition: background 0.12s;
}
.sites-table :deep(.el-table__row:hover > td) {
  background: var(--wdc-hover) !important;
}
.sites-table :deep(td) {
  padding: 14px 12px !important;
  border-bottom: 1px solid var(--wdc-border) !important;
}

.cell-domain {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.cell-domain-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.col-domain {
  font-size: 0.95rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: 0.01em;
}
.cell-port {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.75rem;
  color: var(--wdc-text-3);
  font-weight: 500;
}
.cell-tag {
  font-weight: 700 !important;
  letter-spacing: 0.04em;
  font-size: 0.68rem !important;
}

.col-aliases {
  font-size: 0.76rem;
  color: var(--wdc-text-2);
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-family: 'JetBrains Mono', monospace;
  /* F71: don't let the alias list grow the row vertically when there are
     many aliases — truncate to a single line and show the full list via
     the title tooltip on hover. The +N counter next to the first alias
     tells the user how many there are. */
  max-width: 320px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.alias-dot {
  color: var(--wdc-accent);
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: 999px;
  padding: 1px 6px;
  font-size: 0.66rem;
  font-weight: 600;
  flex-shrink: 0;
}
.alias-preview {
  overflow: hidden;
  text-overflow: ellipsis;
}

.col-tunnel {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  font-size: 0.76rem;
  font-family: 'JetBrains Mono', monospace;
}
.col-tunnel a {
  color: #f38020; /* Cloudflare orange */
  font-weight: 600;
  text-decoration: none;
}
.col-tunnel a:hover { text-decoration: underline; }
.tunnel-icon {
  color: #f38020;
  font-size: 0.95rem;
}

/* Dimmed offline state — tunnel service is stopped so the public URL
   won't actually respond. Same hue but desaturated + muted badge so
   the row is still clearly a "tunnel exists" marker, just parked. */
.col-tunnel-offline a {
  color: var(--wdc-text-3);
  text-decoration: line-through;
}
.col-tunnel-offline .tunnel-icon {
  color: var(--wdc-text-3);
}
.tunnel-badge-offline {
  font-size: 0.62rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  padding: 1px 6px;
  background: var(--wdc-surface-2);
  color: var(--wdc-text-3);
  border: 1px solid var(--wdc-border);
  border-radius: 6px;
  margin-left: 4px;
}

.col-mono {
  font-size: 0.82rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-2);
  /* Single-line truncate for long docroots (no more 2-line wrap that
     breaks row height on narrow viewports). Full path stays in the
     native tooltip via :title on the span. */
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: middle;
}

/* Force docroot cell to respect overflow so flex parent doesn't stretch */
.sites-table :deep(.col-docroot-cell) {
  overflow: hidden;
}
.sites-table :deep(.col-docroot-cell .cell) {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Framework column is the easiest to drop when the viewport gets
   tight — the tag is nice-to-have, everything critical is in
   Domain/PHP/Actions. Hide under 1100 px so the remaining four
   columns breathe. */
@media (max-width: 1100px) {
  .sites-table :deep(.col-framework-cell) {
    display: none;
  }
}
/* Below 900 px, also drop the PHP version column — most users on
   laptop screens have a single PHP installed so the tag is redundant
   with docroot + framework. */
@media (max-width: 900px) {
  .sites-table :deep(.col-framework-cell),
  .sites-table :deep(th.el-table__cell):nth-child(3),
  .sites-table :deep(td.el-table__cell):nth-child(3) {
    display: none;
  }
}

/* Row actions: keep switch + edit + overflow on one line, prevent
   wrapping into 2 rows which collides with the fixed-right column
   border. */
.site-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: nowrap;
  white-space: nowrap;
}

.col-empty {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}

/* Framework column can now hold a framework tag + a Compose badge side-by-side */
.framework-cell {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
}
.compose-tag {
  font-size: 0.7rem !important;
  letter-spacing: 0.02em;
}

.runtime-tag {
  font-weight: 700 !important;
  font-size: 0.7rem !important;
  letter-spacing: 0.04em;
}
.runtime-tag.runtime-php {
  /* PHP brand indigo, strong contrast white text — 7.2:1 AAA */
  background: #4f5b93 !important;
  border-color: #4f5b93 !important;
  color: #ffffff !important;
}
.runtime-tag.runtime-node {
  background: #3c873a !important;
  border-color: #3c873a !important;
  color: #fff !important;
}
.runtime-tag.runtime-static {
  background: transparent !important;
  border-color: var(--wdc-border-strong) !important;
  color: var(--wdc-text-3) !important;
}

.site-actions {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: nowrap;
  overflow: hidden;
}

:global(.danger-item) {
  color: var(--el-color-danger) !important;
}

.site-detail {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 4px 0;
}

.drawer-actions {
  display: flex;
  gap: 8px;
}

.history-section {
  border-top: 1px solid var(--el-border-color);
  padding-top: 12px;
}

.history-title {
  font-size: 0.78rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--el-text-color-secondary);
  margin-bottom: 8px;
}

.history-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.history-item {
  display: flex;
  justify-content: space-between;
  font-size: 0.78rem;
  color: var(--el-text-color-regular);
  padding: 4px 0;
  border-bottom: 1px dashed var(--el-border-color-lighter, #333);
}

.history-date { color: var(--el-text-color-secondary); font-family: monospace; }
.history-label { color: var(--el-text-color-regular); }

:global(.cursor-pointer) { cursor: pointer; }

.simple-hint {
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  margin: 0 0 16px;
  line-height: 1.5;
}

.simple-advanced-link {
  margin-top: 12px;
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
</style>
