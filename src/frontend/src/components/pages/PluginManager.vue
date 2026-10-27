<template>
  <div class="pm-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">{{ $t('plugins.title') }}</h1>
        <p class="page-subtitle">
          <!-- F91.3: show enabled/total split so "13 installed" doesn't
               hide the fact that only N are actually active. -->
          {{ $t('plugins.enabledRatio', { enabled: pluginsStore.enabledPlugins.length, total: pluginsStore.manifests.length }) }}
          <span v-if="marketplace.reachable">· {{ $t('plugins.marketplaceCount', { count: marketplace.plugins.length }) }}</span>
        </p>
      </div>
      <div class="header-actions">
        <el-input
          v-model="search"
          :placeholder="$t('plugins.searchPlaceholder')"
          clearable
          size="small"
          style="width: 220px"
          prefix-icon="Search"
        />
        <el-button
          v-if="activeTab === 'marketplace'"
          size="small"
          :loading="loadingMarketplace"
          @click="reloadMarketplace"
        >
          {{ $t('plugins.reload') }}
        </el-button>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="pm-tabs">
      <el-tab-pane :label="$t('plugins.installed')" name="installed" />
      <el-tab-pane name="marketplace">
        <template #label>
          <span>{{ $t('plugins.marketplace') }}</span>
          <el-tag v-if="marketplace.reachable" size="small" type="success" effect="plain" style="margin-left:var(--wdc-space-1-5)">
            {{ marketplace.plugins.length }}
          </el-tag>
        </template>
      </el-tab-pane>
    </el-tabs>

    <!-- Loading skeleton -->
    <div v-if="activeTab === 'installed' && pluginsStore.loading" class="page-body-pad">
      <el-skeleton :rows="5" animated />
    </div>

    <!-- Marketplace tab -->
    <div v-else-if="activeTab === 'marketplace'" class="page-body-pad">
      <el-alert
        v-if="marketplace.source === 'built-in'"
        type="info"
        :closable="false"
        show-icon
        :title="$t('plugins.marketplaceBuiltIn')"
        style="margin-bottom: var(--wdc-space-4)"
      />
      <el-alert
        v-else-if="marketplace.error"
        type="warning"
        :closable="false"
        show-icon
        :title="$t('plugins.marketplaceUnreachable', { error: marketplace.error })"
        :description="$t('plugins.marketplaceSource', { source: marketplace.source })"
        style="margin-bottom: var(--wdc-space-4)"
      />
      <div v-if="loadingMarketplace" class="page-body-pad">
        <el-skeleton :rows="3" animated />
      </div>
      <div v-else-if="filteredMarketplace.length === 0" class="pm-empty">
        <el-empty :description="search ? $t('plugins.noMatching', { q: search }) : $t('plugins.noMarketplaceItems')" :image-size="60" />
      </div>
      <div v-else class="pm-grid">
        <div
          v-for="mp in filteredMarketplace"
          :key="mp.id"
          class="pm-card"
          :class="{ 'pm-card--enabled': mp.installed }"
        >
          <div class="pm-card-header">
            <div class="pm-card-title">
              <ServiceIcon :service="mp.id" :active="true" />
              <span class="pm-name">{{ mp.name }}</span>
              <el-tag v-if="mp.installed" size="small" type="success" effect="plain">{{ $t('plugins.installedTag') }}</el-tag>
            </div>
          </div>
          <div class="pm-card-desc" :title="pluginDescription(mp)">{{ pluginDescription(mp) }}</div>
          <div class="pm-card-footer">
            <div class="pm-meta">
              <span class="pm-version">v{{ installedVersion(mp.id) ?? mp.version }}</span>
              <span v-if="mp.author" class="pm-author">{{ $t('plugins.byAuthor', { author: mp.author }) }}</span>
              <span v-if="mp.license" class="pm-author">· {{ mp.license }}</span>
            </div>
            <!-- F91.7: surface why there's no install action.
                 - installed + builtIn → "Built-in, cannot uninstall"
                 - installed + downloadUrl → allow reinstall/update
                 - !installed + downloadUrl → Install
                 - !installed + !downloadUrl → "Source unavailable"
                 Before the fix, builtIn plugins with null downloadUrl
                 showed no button at all and users thought marketplace was
                 broken. -->
            <!-- F91.12: built-in + installed → Vestavěný; built-in + uninstalled
                 → Vrátit plugin (reset blacklist + restart daemon, or prompt
                 rebuild when DLL is already gone). -->
            <div v-if="mp.builtIn && mp.installed" class="pm-mp-actions">
              <el-tag size="small" type="info" effect="plain">{{ $t('plugins.builtIn') }}</el-tag>
            </div>
            <div v-else-if="mp.builtIn && !mp.installed" class="pm-mp-actions">
              <el-button
                size="small"
                type="primary"
                :loading="restoring.has(mp.id)"
                @click="doRestore(mp.id)"
              >{{ $t('plugins.restore') }}</el-button>
              <el-tag size="small" type="info" effect="plain">{{ $t('plugins.builtIn') }}</el-tag>
            </div>
            <div v-else-if="!mp.installed" class="pm-mp-actions">
              <el-button
                size="small"
                type="primary"
                :loading="installing.has(mp.id)"
                :disabled="!mp.downloadUrl"
                @click="installFromMarketplace(mp)"
              >
                {{ $t('plugins.install') }}
              </el-button>
              <el-button
                v-if="mp.downloadUrl"
                size="small"
                text
                @click="copyDownloadUrl(mp.downloadUrl)"
              >
                {{ $t('plugins.copyUrl') }}
              </el-button>
              <el-tag v-else size="small" type="warning" effect="plain">{{ $t('plugins.sourceUnavailable') }}</el-tag>
            </div>
            <div v-else class="pm-mp-actions">
              <el-button
                v-if="mp.downloadUrl"
                size="small"
                :loading="installing.has(mp.id)"
                @click="installFromMarketplace(mp)"
              >
                {{ $t('plugins.reinstall') }}
              </el-button>
              <el-tag v-else size="small" type="success" effect="plain">{{ $t('plugins.installedTag') }}</el-tag>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Plugin cards grid -->
    <div v-else class="pm-grid page-body-pad">
      <div
        v-for="plugin in filteredPlugins"
        :key="plugin.id"
        class="pm-card"
        :class="{ 'pm-card--enabled': plugin.enabled, 'pm-card--disabled': !plugin.enabled }"
      >
        <div class="pm-card-header">
          <div class="pm-card-title">
            <ServiceIcon :service="plugin.id" :active="plugin.enabled" />
            <span class="pm-name">{{ plugin.name }}</span>
            <el-tag size="small" type="info" effect="plain" class="pm-type-tag">
              {{ plugin.type === 'service' ? $t('plugins.type.service') : $t('plugins.type.tool') }}
            </el-tag>
          </div>
          <el-switch
            :model-value="plugin.enabled"
            :loading="toggling.has(plugin.id)"
            :aria-label="$t('plugins.enableAria', { name: plugin.name })"
            @change="toggle(plugin.id)"
            size="small"
          />
        </div>

        <div class="pm-card-desc" :title="pluginDescription(plugin)">
          {{ pluginDescription(plugin) }}
        </div>

        <div class="pm-card-footer">
          <div class="pm-meta">
            <span class="pm-version">v{{ plugin.version }}</span>
            <span v-if="plugin.author" class="pm-author">{{ $t('plugins.byAuthor', { author: plugin.author }) }}</span>
          </div>
        </div>

        <!-- A service plugin cannot start without its binary; say so and
             link to the module instead of offering auto-start. -->
        <div v-if="plugin.enabled && missingBinary(plugin)" class="pm-binary-missing">
          <span>{{ $t('plugins.binaryMissing') }}</span>
          <el-button size="small" link type="primary" @click="openBinary(plugin)">
            {{ $t('plugins.installBinary') }}
          </el-button>
        </div>
        <!-- Per-plugin autostart toggle. The <PluginAutostartSwitch> guard
             (type==='service' && serviceId) handles the non-service cases
             so we don't need to double-gate here. Compact variant keeps
             the card's vertical rhythm consistent with the other rows. -->
        <PluginAutostartSwitch v-else :plugin="plugin" compact />

        <!-- Open plugin page button if has UI. Must be a full-width solid
             button for readable contrast — the old text-primary variant
             rendered as low-contrast blue-on-blue on the flat surface. -->
        <div class="pm-card-actions" v-if="plugin.enabled && plugin.ui">
          <el-button
            size="small"
            type="primary"
            plain
            class="pm-open-btn"
            @click="router.push(`/plugin/${plugin.id}`)"
          >
            {{ $t('plugins.openPanel') }} <el-icon><ArrowRight /></el-icon>
          </el-button>
        </div>
        <!-- F91.4: uninstall option — only exposed when the plugin is
             disabled (backend enforces the same rule). A quiet text button:
             for a switched-off built-in plugin the usual next step is the
             switch above, not removal. -->
        <div class="pm-card-actions pm-card-actions--disabled" v-if="!plugin.enabled">
          <span class="pm-disabled-hint">{{ $t('plugins.disabledHint') }}</span>
          <el-button
            size="small"
            type="danger"
            text
            :loading="uninstalling.has(plugin.id)"
            @click="confirmUninstall(plugin.id, plugin.name)"
          >
            {{ $t('plugins.uninstall') }}
          </el-button>
        </div>
      </div>

      <!-- Empty state -->
      <div v-if="filteredPlugins.length === 0 && !pluginsStore.loading" class="pm-empty">
        <el-empty :description="search ? $t('plugins.noMatching', { q: search }) : $t('plugins.noneLoaded')" :image-size="60" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, ElNotification } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { ArrowRight } from '@element-plus/icons-vue'
import { usePluginsStore } from '../../stores/plugins'
import ServiceIcon from '../shared/ServiceIcon.vue'
import { errorMessage } from '../../utils/errors'
import {
  fetchMarketplace,
  installPluginFromMarketplace,
  uninstallPlugin,
  restartDaemon,
  restorePlugin,
  waitForDaemon,
  daemonToken,
  fetchBinaryCatalog,
  fetchInstalledBinaries,
  type MarketplaceResponse,
  type MarketplacePlugin,
} from '../../api/daemon'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const pluginsStore = usePluginsStore()
const search = ref('')
const toggling = ref<Set<string>>(new Set())
// F91.4: tracks in-flight uninstall calls so the button spins while the
// backend removes files + we reload the manifest list.
const uninstalling = ref<Set<string>>(new Set())
// F91.12: tracks in-flight restore calls for the marketplace "Obnovit" button.
const restoring = ref<Set<string>>(new Set())
// ?tab=marketplace&q=<id> lets a page whose plugin is missing link here.
const activeTab = ref<'installed' | 'marketplace'>(route.query.tab === 'marketplace' ? 'marketplace' : 'installed')
if (typeof route.query.q === 'string') search.value = route.query.q
const loadingMarketplace = ref(false)
const marketplace = reactive<MarketplaceResponse>({
  source: '',
  reachable: false,
  plugins: [],
})

async function reloadMarketplace() {
  loadingMarketplace.value = true
  try {
    const data = await fetchMarketplace()
    marketplace.source = data.source
    marketplace.reachable = data.reachable
    marketplace.plugins = data.plugins
    marketplace.count = data.count
    marketplace.error = data.error
  } catch (e) {
    marketplace.reachable = false
    marketplace.error = errorMessage(e)
    marketplace.plugins = []
  } finally {
    loadingMarketplace.value = false
  }
}

async function copyDownloadUrl(url: string) {
  try {
    await navigator.clipboard.writeText(url)
    ElMessage.success(t('plugins.toast.urlCopied'))
  } catch {
    ElMessage.warning(t('plugins.toast.clipboardFailed'))
  }
}

const installing = ref<Set<string>>(new Set())

async function installFromMarketplace(mp: MarketplacePlugin) {
  if (!mp.downloadUrl) return
  try {
    await ElMessageBox.confirm(
      t('plugins.toast.installConfirmMessage', { name: mp.name, version: mp.version, url: mp.downloadUrl }),
      t('plugins.toast.installConfirmTitle'),
      { confirmButtonText: t('plugins.toast.installBtn'), cancelButtonText: t('common.cancel'), type: 'info' }
    )
  } catch { return }

  installing.value.add(mp.id)
  try {
    const result = await installPluginFromMarketplace(mp.id, mp.downloadUrl, mp.sha256)
    ElNotification({
      title: t('plugins.toast.installedNotification', { name: mp.name }),
      message: result.restartRequired
        ? t('plugins.restartRequired')
        : t('plugins.toast.loadedAfterInstall'),
      type: 'success',
      duration: 6000,
    })
    // Mark as installed locally so the button hides on next render
    mp.installed = true
    void reloadMarketplace()
  } catch (e) {
    ElNotification({
      title: t('plugins.installFailed'),
      message: errorMessage(e) || t('common.unknownError'),
      type: 'error',
      duration: 0,
    })
  } finally {
    installing.value.delete(mp.id)
  }
}

watch(activeTab, (tab) => {
  if (tab === 'marketplace' && marketplace.plugins.length === 0 && !loadingMarketplace.value) {
    void reloadMarketplace()
  }
})

// First-party plugins ship English plugin.json descriptions; show the
// translated one for known ids and the manifest text for anything else.
function pluginDescription(p: { id: string; description?: string | null }): string {
  const key = `plugins.descriptions.${p.id.replace(/^nks\.wdc\./, '').replace(/\./g, '_')}`
  return te(key) ? t(key) : (p.description || t('plugins.page.noDescription'))
}

/** The built-in catalogue lists 1.0.0 for everything; show what is really installed. */
function installedVersion(id: string): string | null {
  return pluginsStore.manifests.find(m => m.id === id)?.version ?? null
}

function matchesSearch(p: { id: string; name: string; description?: string | null }): boolean {
  const q = search.value.trim().toLowerCase()
  return q === ''
    || p.name.toLowerCase().includes(q)
    || p.id.toLowerCase().includes(q)
    || pluginDescription(p).toLowerCase().includes(q)
}

const filteredPlugins = computed(() => pluginsStore.manifests.filter(matchesSearch))
const filteredMarketplace = computed(() => marketplace.plugins.filter(matchesSearch))

// Catalog apps and installed binaries, to tell when a service plugin has
// nothing to run. Only apps the catalog knows are checked.
const catalogApps = ref<Set<string>>(new Set())
const installedApps = ref<Set<string>>(new Set())
const BINARY_FOR_SERVICE: Record<string, string> = { cloudflare: 'cloudflared' }

function missingBinary(plugin: { type?: string; serviceId?: string | null }): string | null {
  if (plugin.type !== 'service' || !plugin.serviceId) return null
  const app = BINARY_FOR_SERVICE[plugin.serviceId] ?? plugin.serviceId
  return catalogApps.value.has(app) && !installedApps.value.has(app) ? app : null
}

function openBinary(plugin: { type?: string; serviceId?: string | null }) {
  const app = missingBinary(plugin)
  if (app) void router.push({ path: '/binaries', query: { app } })
}

async function loadBinaryState() {
  try {
    const [catalog, installed] = await Promise.all([fetchBinaryCatalog(), fetchInstalledBinaries()])
    catalogApps.value = new Set(Object.keys(catalog))
    installedApps.value = new Set(installed.map(b => b.app))
  } catch { /* no binary hints then */ }
}

async function toggle(id: string) {
  toggling.value.add(id)
  try {
    await pluginsStore.toggleEnable(id)
  } catch (e) {
    ElMessage.error(t('plugins.toast.toggleFailed', { err: errorMessage(e) }))
  } finally {
    toggling.value.delete(id)
  }
}

// F91.4: destructive confirm → DELETE /api/plugins/{id}. Daemon always
// flags restartRequired because the DLL cannot unload from the running
// AssemblyLoadContext — we pass that note to the user via notification
// instead of silently succeeding.
// F91.12: put a previously-uninstalled built-in plugin back into rotation.
// Backend clears blacklist; if DLL is still on disk → restart daemon; if
// DLL was purged → prompt user to rebuild. We wait for respawn with
// waitForDaemon, then refresh both lists.
async function doRestore(id: string) {
  restoring.value.add(id)
  try {
    const tokenBefore = daemonToken()
    const res = await restorePlugin(id)
    if (res.rebuildRequired) {
      ElNotification.warning({
        title: t('plugins.toast.rebuildTitle'),
        message: res.message,
        duration: 8000,
      })
      return
    }
    if (res.restartRequired) {
      ElNotification.info({
        title: t('plugins.toast.restoringTitle'),
        message: res.message,
        duration: 3000,
      })
      const ready = await waitForDaemon(15000, tokenBefore)
      if (!ready) ElMessage.warning(t('plugins.toast.restartTimeout'))
      await pluginsStore.loadAll()
      await reloadMarketplace()
      ElMessage.success(t('plugins.toast.restored', { id }))
    }
  } catch (e) {
    ElMessage.error(t('plugins.toast.restoreFailed', { err: errorMessage(e) }))
  } finally {
    restoring.value.delete(id)
  }
}

async function confirmUninstall(id: string, name: string) {
  try {
    await ElMessageBox.confirm(
      t('plugins.toast.uninstallConfirmMessage', { name }),
      t('plugins.toast.uninstallConfirmTitle'),
      { type: 'warning', confirmButtonText: t('plugins.toast.uninstallBtn'), cancelButtonText: t('common.cancel') }
    )
  } catch { return /* user cancelled */ }

  uninstalling.value.add(id)
  try {
    const res = await uninstallPlugin(id)
    // F91.7: auto-restart when locked files linger — Electron respawns
    // daemon on exit code 99 so the user doesn't have to do anything.
    if (res.restartRequired && (res.lockedFiles ?? 0) > 0) {
      ElNotification.info({
        title: t('plugins.toast.uninstalledNotificationTitle'),
        message: t('plugins.toast.uninstalledRestarting', { msg: res.message ?? t('plugins.toast.fileDeleted') }),
        duration: 4000,
      })
      const tokenBefore = daemonToken()
      try { await restartDaemon() } catch { /* daemon already closing the socket */ }
      // F91.12: poll /healthz until the respawned daemon answers.
      // Replaces a flat 3.5s sleep that sometimes reloaded stale data
      // when the new daemon took longer to boot.
      const ready = await waitForDaemon(15000, tokenBefore)
      if (!ready) {
        ElMessage.warning(t('plugins.toast.restartTimeoutLong'))
      }
      await pluginsStore.loadAll()
      ElMessage.success(t('plugins.toast.uninstalledRestart'))
    } else {
      ElMessage.success(t('plugins.toast.uninstalled'))
      await pluginsStore.loadAll()
    }
  } catch (e) {
    ElMessage.error(t('plugins.toast.uninstallFailed', { err: errorMessage(e) }))
  } finally {
    uninstalling.value.delete(id)
  }
}

onMounted(() => {
  void pluginsStore.loadAll()
  void loadBinaryState()
  if (activeTab.value === 'marketplace') void reloadMarketplace()
})
</script>

<style scoped>
/* Flat redesign — mirrors .svc-card from Dashboard.vue. No gradients, solid
   surface, hairline WDC border, solid accent left-edge for enabled state,
   readable secondary text via --wdc-text-2/3 (not --el-text-color-*). */
.pm-page {
  min-height: 100%;
  background: var(--wdc-bg);
  max-width: none;
  margin: 0 auto;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 0 var(--wdc-space-4);
  margin-bottom: 0;
  border-bottom: 1px solid var(--wdc-border);
  background: transparent;
}

.page-title {
  font-size: 1.6rem;
  font-weight: 800;
  color: var(--wdc-text);
  letter-spacing: -0.02em;
  margin: 0;
}

.page-subtitle {
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  margin-top: var(--wdc-space-0-5);
}

.page-body-pad {
  padding: var(--wdc-space-4) 0 var(--wdc-space-5);
}

.pm-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: var(--wdc-space-3);
  justify-content: start;
}

.pm-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: var(--wdc-space-3) var(--wdc-space-3-5);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3);
  transition: border-color 0.15s, background 0.15s;
  border-left-width: 3px;
  border-left-style: solid;
  border-left-color: var(--wdc-border);
}

.pm-card--enabled {
  border-left-color: var(--wdc-accent);
  background: var(--wdc-surface);
}
.pm-card--disabled { opacity: 0.55; }
.pm-card--disabled .pm-name { color: var(--wdc-text-muted); }
.pm-card--disabled:hover { opacity: 0.75; }

.pm-card:hover {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-surface-2);
}
.pm-card--enabled:hover { border-left-color: var(--wdc-accent); }

.pm-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--wdc-space-2-5);
}

.pm-card-title {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2-5);
  flex-wrap: wrap;
  min-width: 0;
}

.pm-name {
  font-size: 1.05rem;
  font-weight: 600;
  color: var(--wdc-text);
  letter-spacing: 0;
}

.pm-type-tag {
  flex-shrink: 0;
  font-size: 0.7rem;
  padding: 0 var(--wdc-space-1-5);
  height: 18px;
  line-height: 18px;
  margin-left: var(--wdc-space-1);
}

.pm-card-desc {
  font-size: 0.85rem;
  color: var(--wdc-text-dim);
  line-height: 1.5;
  max-height: 3em;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.pm-card-footer {
  /* pins version, binary hint and actions to the bottom of equal-height cards */
  margin-top: auto;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-2-5);
  flex-wrap: wrap;
}

.pm-meta {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
  font-size: 0.82rem;
  color: var(--wdc-text-muted);
  font-family: var(--wdc-font-display);
}

.pm-version {
  font-family: 'JetBrains Mono', ui-monospace, monospace;
  font-size: 0.72rem;
  color: var(--wdc-text-muted);
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  padding: 1px var(--wdc-space-2);
  border-radius: var(--wdc-radius-full);
  text-transform: none;
  letter-spacing: 0;
  font-weight: 500;
}

.pm-author {
  font-size: 0.75rem;
  color: var(--wdc-text-muted);
}

.header-actions {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
}

.pm-binary-missing {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-2);
  font-size: 0.8rem;
  color: var(--wdc-warning);
}

.pm-card-actions--disabled {
  justify-content: space-between;
  align-items: center;
}

.pm-disabled-hint {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
}

.pm-card-actions {
  display: flex;
  gap: var(--wdc-space-2);
  justify-content: flex-end;
  padding-top: var(--wdc-space-3);
  border-top: 1px solid var(--wdc-border);
}

.pm-open-btn {
  width: 100%;
  font-weight: 600;
  letter-spacing: 0.01em;
}

.pm-empty {
  grid-column: 1 / -1;
  display: flex;
  justify-content: center;
  padding: var(--wdc-space-6-5) 0;
}

.pm-mp-actions {
  display: flex;
  gap: var(--wdc-space-1-5);
  align-items: center;
}
</style>
