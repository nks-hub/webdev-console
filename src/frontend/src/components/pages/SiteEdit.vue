<template>
  <div class="site-edit-page">
    <!-- Page header -->
    <div class="page-header">
      <div class="header-left">
        <el-button size="small" text @click="goBack">
          <el-icon><ArrowLeft /></el-icon>
          <span>Back to Sites</span>
        </el-button>
        <div class="title-block">
          <div class="title-label">Edit Site</div>
          <div class="title-name">{{ domain }}</div>
        </div>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openInBrowser" :disabled="!site">
          Open in Browser
        </el-button>
        <el-button
          type="primary"
          size="small"
          :loading="saving"
          :disabled="!dirty"
          @click="save"
        >
          Save &amp; Apply
        </el-button>
      </div>
    </div>

    <!-- Loading -->
    <div v-if="loading" class="state-box">
      <el-skeleton :rows="10" animated />
    </div>

    <!-- Not found -->
    <div v-else-if="!site" class="state-box">
      <el-empty :description="`Site '${domain}' not found.`" />
    </div>

    <!-- Content with tabs -->
    <div v-else class="edit-body">
      <el-tabs v-model="activeTab" class="site-tabs">
        <!-- ── General ──────────────────────────── -->
        <el-tab-pane name="general">
          <template #label>
            <span class="tab-label"><el-icon><Setting /></el-icon> General</span>
          </template>
          <div class="tab-content">
            <!-- Identity card -->
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Identity</span>
                <span class="edit-card-hint">Domain and document root</span>
              </header>
              <div class="edit-card-body">
                <el-form :model="site" label-position="top" size="default">
                  <el-form-item label="Domain">
                    <el-input :model-value="site.domain" disabled>
                      <template #prepend>🌐</template>
                    </el-input>
                  </el-form-item>
                  <el-form-item label="Document Root" required>
                    <el-input
                      v-model="site.documentRoot"
                      placeholder="C:\work\htdocs\myapp"
                      class="path-input"
                      @input="markDirty"
                    >
                      <template #append>
                        <el-button
                          class="browse-append-btn"
                          @click="showFolderBrowser = true"
                        >
                          <el-icon><FolderOpened /></el-icon>
                          <span>Browse…</span>
                        </el-button>
                      </template>
                    </el-input>
                    <div class="hint" v-if="site.documentRoot">
                      <el-icon><Check /></el-icon>
                      Apache will serve files from this folder
                    </div>
                  </el-form-item>
                </el-form>
              </div>
            </section>

            <!-- Alias picker card -->
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Aliases</span>
                <span class="edit-card-hint">{{ aliases.length }} additional domain{{ aliases.length === 1 ? '' : 's' }}</span>
              </header>
              <div class="edit-card-body">
                <div class="alias-chips">
                  <el-tag
                    v-for="alias in aliases"
                    :key="alias"
                    class="alias-chip"
                    closable
                    effect="dark"
                    @close="removeAlias(alias)"
                  >
                    {{ alias }}
                  </el-tag>
                  <el-input
                    v-if="aliasInputVisible"
                    ref="aliasInputRef"
                    v-model="aliasInput"
                    size="small"
                    class="alias-input"
                    placeholder="www.myapp.loc"
                    @keydown.enter.prevent="addAlias"
                    @blur="addAlias"
                  />
                  <el-button
                    v-else
                    size="small"
                    class="alias-add-btn"
                    @click="showAliasInput"
                  >
                    + Add alias
                  </el-button>
                </div>
                <div class="hint">
                  Wildcards supported: <code>*.myapp.loc</code> catches every subdomain.
                </div>
              </div>
            </section>

            <!-- Framework detection card -->
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Framework</span>
                <span class="edit-card-hint">Detected automatically or set manually</span>
              </header>
              <div class="edit-card-body">
                <div class="framework-row">
                  <el-input
                    v-model="site.framework"
                    placeholder="wordpress, laravel, nextjs, …"
                    class="framework-input"
                    @input="markDirty"
                  />
                  <el-button size="default" @click="detectFramework" :loading="detecting">
                    <el-icon><Search /></el-icon>
                    <span>Auto-detect</span>
                  </el-button>
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- ── Runtime ──────────────────────────── -->
        <el-tab-pane name="runtime">
          <template #label>
            <span class="tab-label"><el-icon><Cpu /></el-icon> Runtime</span>
          </template>
          <div class="tab-content">
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Runtime</span>
                <span class="edit-card-hint">Choose how this site is served</span>
              </header>
              <div class="edit-card-body">
                <div class="runtime-picker">
                  <button
                    class="runtime-card"
                    :class="{ active: selectedRuntime === 'static' }"
                    @click="selectRuntime('static')"
                    type="button"
                  >
                    <div class="runtime-card-icon runtime-static">⚡</div>
                    <div class="runtime-card-title">Static</div>
                    <div class="runtime-card-desc">Plain HTML / assets. No language runtime.</div>
                  </button>
                  <button
                    class="runtime-card"
                    :class="{ active: selectedRuntime === 'php' }"
                    @click="selectRuntime('php')"
                    type="button"
                  >
                    <div class="runtime-card-icon runtime-php">PHP</div>
                    <div class="runtime-card-title">PHP</div>
                    <div class="runtime-card-desc">Pick an installed PHP version below.</div>
                  </button>
                  <button
                    class="runtime-card"
                    :class="{ active: selectedRuntime === 'node' }"
                    @click="selectRuntime('node')"
                    type="button"
                    disabled
                    title="Coming soon"
                  >
                    <div class="runtime-card-icon runtime-node">N</div>
                    <div class="runtime-card-title">Node.js</div>
                    <div class="runtime-card-desc">Proxy to a Node upstream (coming soon).</div>
                  </button>
                </div>

                <!-- PHP version sub-picker (only when PHP is selected) -->
                <div v-if="selectedRuntime === 'php'" class="php-version-picker">
                  <label class="sub-label">PHP Version</label>
                  <div class="php-version-grid">
                    <button
                      v-for="v in phpVersions"
                      :key="v"
                      type="button"
                      class="php-version-btn"
                      :class="{ active: site.phpVersion === v }"
                      @click="setPhpVersion(v)"
                    >
                      {{ v }}
                    </button>
                    <div v-if="phpVersions.length === 0" class="hint">
                      No PHP versions installed. Open the Binaries page to install one.
                    </div>
                  </div>
                </div>
              </div>
            </section>

            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">Ports</span>
                <span class="edit-card-hint">Default: 80 (HTTP) and 443 (HTTPS)</span>
              </header>
              <div class="edit-card-body">
                <div class="port-grid">
                  <div class="port-item">
                    <label class="sub-label">HTTP Port</label>
                    <el-input-number
                      v-model="site.httpPort"
                      :min="1"
                      :max="65535"
                      controls-position="right"
                      style="width: 100%"
                      @change="markDirty"
                    />
                  </div>
                  <div class="port-item" v-if="site.sslEnabled">
                    <label class="sub-label">HTTPS Port</label>
                    <el-input-number
                      v-model="site.httpsPort"
                      :min="1"
                      :max="65535"
                      controls-position="right"
                      style="width: 100%"
                      @change="markDirty"
                    />
                  </div>
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- ── SSL ──────────────────────────────── -->
        <el-tab-pane name="ssl">
          <template #label>
            <span class="tab-label"><el-icon><Lock /></el-icon> SSL</span>
          </template>
          <div class="tab-content">
            <section class="edit-card">
              <header class="edit-card-header">
                <span class="edit-card-title">HTTPS</span>
                <span class="edit-card-hint">Local certificates via mkcert</span>
              </header>
              <div class="edit-card-body">
                <div class="ssl-toggle-row">
                  <div class="ssl-toggle-meta">
                    <div class="ssl-toggle-title">Enable HTTPS</div>
                    <div class="ssl-toggle-desc">
                      Generates a locally-trusted certificate and binds an HTTPS vhost on port {{ site.httpsPort || 443 }}.
                    </div>
                  </div>
                  <el-switch v-model="site.sslEnabled" size="large" @change="markDirty" />
                </div>

                <div v-if="site.sslEnabled" class="ssl-toggle-row">
                  <div class="ssl-toggle-meta">
                    <div class="ssl-toggle-title">HTTP → HTTPS redirect</div>
                    <div class="ssl-toggle-desc">
                      Automatically redirect plain HTTP requests to the HTTPS version.
                    </div>
                  </div>
                  <el-switch v-model="redirectHttps" size="large" @change="markDirty" />
                </div>
              </div>
            </section>
          </div>
        </el-tab-pane>

        <!-- ── History ──────────────────────────── -->
        <el-tab-pane name="history">
          <template #label>
            <span class="tab-label"><el-icon><Clock /></el-icon> History ({{ history.length }})</span>
          </template>
          <div class="tab-content">
            <el-empty v-if="history.length === 0" description="No config history yet." :image-size="64" />
            <div v-else class="history-list">
              <div v-for="(h, i) in history" :key="h.timestamp" class="history-row">
                <div class="history-when">
                  <el-icon><Clock /></el-icon>
                  <span>{{ formatDate(h.timestamp) }}</span>
                </div>
                <div class="history-label">{{ h.label ?? `Version ${history.length - i}` }}</div>
                <el-button size="small" text type="primary" @click="rollback(h.timestamp)">
                  Restore
                </el-button>
              </div>
            </div>
          </div>
        </el-tab-pane>

        <!-- ── Danger ───────────────────────────── -->
        <el-tab-pane name="danger">
          <template #label>
            <span class="tab-label danger-label"><el-icon><WarningFilled /></el-icon> Danger</span>
          </template>
          <div class="tab-content">
            <div class="danger-box">
              <div class="danger-title">
                <el-icon><WarningFilled /></el-icon>
                Delete this site
              </div>
              <div class="danger-desc">
                Removes the vhost config and hosts file entry. Document root and databases are not touched.
              </div>
              <el-button type="danger" size="default" @click="confirmDelete">
                Delete {{ site.domain }}
              </el-button>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>

    <!-- Folder browser dialog -->
    <FolderBrowser
      v-model="showFolderBrowser"
      :initial-path="site?.documentRoot"
      @select="onFolderPick"
    />
  </div>
</template>

<script setup lang="ts">
// Named export so <keep-alive exclude="SiteEdit"> in App.vue can skip caching
// this page — SiteEdit is parametric by :domain and MUST refresh state on
// every navigation, unlike Dashboard/Sites/Binaries which benefit from cache.
defineOptions({ name: 'SiteEdit' })
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  ArrowLeft, Setting, Cpu, Lock, Clock, WarningFilled,
  FolderOpened, Check, Search,
} from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import type { SiteInfo } from '../../api/types'
import FolderBrowser from '../shared/FolderBrowser.vue'

const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()

const domain = computed(() => String(route.params.domain || ''))

const site = ref<SiteInfo | null>(null)
const loading = ref(false)
const saving = ref(false)
const dirty = ref(false)
const activeTab = ref('general')
const phpVersions = ref<string[]>([])
const history = ref<Array<{ timestamp: string; label?: string }>>([])
const redirectHttps = ref(true)

// ── Alias chip picker ──────────────────────────────────────────────────
const aliases = ref<string[]>([])
const aliasInput = ref('')
const aliasInputVisible = ref(false)
const aliasInputRef = ref<any>(null)
watch(() => site.value?.aliases, (al) => { aliases.value = [...(al ?? [])] }, { immediate: true })

function showAliasInput() {
  aliasInputVisible.value = true
  void nextTick(() => aliasInputRef.value?.focus?.())
}

function addAlias() {
  const v = aliasInput.value.trim()
  if (v && !aliases.value.includes(v)) {
    aliases.value.push(v)
    markDirty()
  }
  aliasInput.value = ''
  aliasInputVisible.value = false
}

function removeAlias(a: string) {
  aliases.value = aliases.value.filter(x => x !== a)
  markDirty()
}

// ── Runtime picker ─────────────────────────────────────────────────────
const selectedRuntime = computed<'static' | 'php' | 'node'>(() => {
  if (site.value?.phpVersion && site.value.phpVersion !== 'none') return 'php'
  if (site.value?.framework === 'node' || site.value?.framework === 'nextjs') return 'node'
  return 'static'
})

function selectRuntime(rt: 'static' | 'php' | 'node') {
  if (!site.value) return
  if (rt === 'static') {
    site.value.phpVersion = 'none'
  } else if (rt === 'php') {
    // Pick the first available version, fallback to 8.4
    site.value.phpVersion = phpVersions.value[0] ?? '8.4'
  } else if (rt === 'node') {
    // Reserved — doesn't change phpVersion yet
    return
  }
  markDirty()
}

function setPhpVersion(v: string) {
  if (!site.value) return
  site.value.phpVersion = v
  markDirty()
}

// ── Folder browser ─────────────────────────────────────────────────────
const showFolderBrowser = ref(false)

function onFolderPick(path: string) {
  if (!site.value) return
  site.value.documentRoot = path
  markDirty()
}

// ── Auto-detect framework ──────────────────────────────────────────────
const detecting = ref(false)
async function detectFramework() {
  if (!site.value) return
  detecting.value = true
  try {
    const res = await fetch(`${daemonBase()}/api/sites/${site.value.domain}/detect-framework`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const data = await res.json()
    if (data.framework) {
      site.value.framework = data.framework
      ElMessage.success(`Detected: ${data.framework}`)
      markDirty()
    } else {
      ElMessage.info('No framework detected')
    }
  } catch (e: any) {
    ElMessage.error(`Detection failed: ${e.message}`)
  } finally {
    detecting.value = false
  }
}

function markDirty() { dirty.value = true }

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  if (urlPort && /^\d+$/.test(urlPort)) return `http://localhost:${urlPort}`
  const p = (window as any).daemonApi?.getPort?.()
  return `http://localhost:${typeof p === 'number' ? p : 5199}`
}

async function load() {
  loading.value = true
  try {
    await sitesStore.load()
    const found = sitesStore.sites.find(s => s.domain === domain.value)
    site.value = found ? { ...found, aliases: [...(found.aliases ?? [])] } : null
    dirty.value = false

    // php versions
    try {
      const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: sitesStore.authHeaders() })
      if (r.ok) {
        const versions = await r.json()
        phpVersions.value = versions.map((v: any) => v.majorMinor || v.version?.split('.').slice(0, 2).join('.') || v.version)
      }
    } catch { phpVersions.value = ['8.4', '8.3', '8.2'] }

    // history
    try {
      const res = await fetch(`${daemonBase()}/api/sites/${domain.value}/history`, {
        headers: sitesStore.authHeaders(),
      })
      if (res.ok) history.value = await res.json() as Array<{ timestamp: string; label?: string }>
    } catch { /* optional */ }
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!site.value) return
  saving.value = true
  try {
    // Commit aliases from chip picker (no more comma-separated string)
    site.value.aliases = [...aliases.value]
    await sitesStore.update(site.value.domain, site.value)
    ElMessage.success('Site updated')
    dirty.value = false
  } catch (e: any) {
    ElMessage.error(`Update failed: ${e.message}`)
  } finally {
    saving.value = false
  }
}

async function rollback(timestamp: string) {
  if (!site.value) return
  try {
    await ElMessageBox.confirm(`Restore config from ${formatDate(timestamp)}?`, 'Restore', {
      type: 'warning',
      confirmButtonText: 'Restore',
    })
    const res = await fetch(`${daemonBase()}/api/sites/${site.value.domain}/rollback`, {
      method: 'POST',
      headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ timestamp }),
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    ElMessage.success('Config restored')
    await load()
  } catch { /* cancelled or error already shown */ }
}

async function confirmDelete() {
  if (!site.value) return
  try {
    await ElMessageBox.confirm(
      `Delete site "${site.value.domain}"? This cannot be undone.`,
      'Confirm deletion',
      { type: 'warning', confirmButtonText: 'Delete', confirmButtonClass: 'el-button--danger' }
    )
    await sitesStore.remove(site.value.domain)
    ElMessage.success('Site deleted')
    router.push('/sites')
  } catch { /* user cancelled */ }
}

function openInBrowser() {
  if (!site.value) return
  const s = site.value
  const proto = s.sslEnabled ? 'https' : 'http'
  const port = s.sslEnabled ? (s.httpsPort || 443) : (s.httpPort || 80)
  const portSuffix = (s.sslEnabled && port === 443) || (!s.sslEnabled && port === 80) ? '' : `:${port}`
  window.open(`${proto}://${s.domain}${portSuffix}`, '_blank')
}

function goBack() {
  router.push('/sites')
}

function formatDate(s: string): string {
  try { return new Date(s).toLocaleString() } catch { return s }
}

watch(domain, () => { void load() })
onMounted(() => { void load() })
</script>

<style scoped>
.site-edit-page {
  display: flex;
  flex-direction: column;
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 20px;
  border-bottom: 1px solid var(--wdc-border);
  background: var(--wdc-surface);
  flex-shrink: 0;
}
.header-left {
  display: flex;
  align-items: center;
  gap: 14px;
}
.title-block {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.title-label {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--wdc-text-3);
}
.title-name {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.header-actions {
  display: flex;
  gap: 8px;
}

.state-box {
  padding: 32px 24px;
}

.edit-body {
  flex: 1;
  padding: 0 20px;
}

.site-tabs {
  margin-top: 8px;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 0.88rem;
}
.tab-label.danger-label {
  color: var(--wdc-status-error);
}

.tab-content {
  padding: 24px 4px 28px;
  display: flex;
  flex-direction: column;
  gap: 18px;
  max-width: 960px;
}

.two-col-form {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px 24px;
  max-width: 880px;
}
.two-col-form .el-form-item {
  margin-bottom: 0;
}

.hint {
  margin-top: 6px;
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  display: flex;
  align-items: center;
  gap: 6px;
}
.hint code {
  font-family: 'JetBrains Mono', monospace;
  background: var(--wdc-surface-2);
  padding: 1px 6px;
  border-radius: 3px;
  color: var(--wdc-accent);
  font-size: 0.76rem;
}

/* ─── Card sections ──────────────────────────────────────────────────── */
.edit-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  overflow: hidden;
}
.edit-card-header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  padding: 14px 20px;
  background: var(--wdc-surface-2);
  border-bottom: 1px solid var(--wdc-border);
}
.edit-card-title {
  font-size: 0.78rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text);
}
.edit-card-hint {
  font-size: 0.75rem;
  color: var(--wdc-text-3);
  font-weight: 500;
}
.edit-card-body {
  padding: 20px;
}

/* ─── Path input + Browse ────────────────────────────────────────────── */
.path-input {
  width: 100%;
}
.path-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.85rem;
}
.path-input :deep(.el-input-group__append) {
  padding: 0 !important;
  background: var(--wdc-surface-2) !important;
  border-color: var(--wdc-border) !important;
}
.browse-append-btn {
  border: none !important;
  background: transparent !important;
  color: var(--wdc-accent) !important;
  font-weight: 700 !important;
  height: 100% !important;
  padding: 0 16px !important;
  box-shadow: none !important;
}
.browse-append-btn:hover {
  background: var(--wdc-accent-dim) !important;
}

/* ─── Alias chip picker ──────────────────────────────────────────────── */
.alias-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  min-height: 32px;
}
.alias-chip {
  font-family: 'JetBrains Mono', monospace !important;
  font-size: 0.78rem !important;
  font-weight: 600 !important;
  padding: 4px 10px !important;
  height: 28px !important;
  background: var(--wdc-accent) !important;
  border-color: var(--wdc-accent) !important;
  color: var(--wdc-bg) !important;
}
.alias-input {
  width: 220px;
}
.alias-input :deep(.el-input__inner) {
  font-family: 'JetBrains Mono', monospace;
}
.alias-add-btn {
  font-weight: 600 !important;
  border-style: dashed !important;
}

/* ─── Framework detect row ───────────────────────────────────────────── */
.framework-row {
  display: flex;
  gap: 8px;
  align-items: stretch;
}
.framework-input {
  flex: 1;
}

/* ─── Runtime picker cards ───────────────────────────────────────────── */
.runtime-picker {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 12px;
}
.runtime-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  padding: 18px 20px;
  background: var(--wdc-surface-2);
  border: 2px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  cursor: pointer;
  transition: border-color 0.12s, background 0.12s, transform 0.08s;
  text-align: left;
  font-family: inherit;
}
.runtime-card:hover:not(:disabled) {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-elevated);
}
.runtime-card.active {
  border-color: var(--wdc-accent);
  background: var(--wdc-accent-dim);
  box-shadow: 0 0 0 3px var(--wdc-accent-glow);
}
.runtime-card:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.runtime-card-icon {
  width: 44px;
  height: 44px;
  border-radius: var(--wdc-radius-sm);
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 800;
  font-size: 1.1rem;
  font-family: 'JetBrains Mono', monospace;
  margin-bottom: 4px;
}
.runtime-card-icon.runtime-static {
  background: var(--wdc-elevated);
  color: var(--wdc-text);
}
.runtime-card-icon.runtime-php {
  background: #4f5b93;
  color: #ffffff;
}
.runtime-card-icon.runtime-node {
  background: #3c873a;
  color: #ffffff;
}
.runtime-card-title {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
}
.runtime-card-desc {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  line-height: 1.4;
}

/* ─── PHP version sub-picker ─────────────────────────────────────────── */
.php-version-picker {
  margin-top: 20px;
  padding-top: 20px;
  border-top: 1px solid var(--wdc-border);
}
.sub-label {
  display: block;
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-2);
  margin-bottom: 10px;
}
.php-version-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.php-version-btn {
  min-width: 76px;
  padding: 10px 18px;
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  color: var(--wdc-text-2);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.92rem;
  font-weight: 700;
  cursor: pointer;
  transition: all 0.12s;
}
.php-version-btn:hover {
  border-color: var(--wdc-border-strong);
  color: var(--wdc-text);
}
.php-version-btn.active {
  background: #4f5b93;
  border-color: #4f5b93;
  color: #ffffff;
}

/* ─── Port grid ──────────────────────────────────────────────────────── */
.port-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  max-width: 520px;
}
.port-item {
  display: flex;
  flex-direction: column;
}

/* ─── SSL toggle rows ────────────────────────────────────────────────── */
.ssl-toggle-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 24px;
  padding: 14px 0;
}
.ssl-toggle-row + .ssl-toggle-row {
  border-top: 1px solid var(--wdc-border);
}
.ssl-toggle-meta {
  flex: 1;
}
.ssl-toggle-title {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--wdc-text);
  margin-bottom: 4px;
}
.ssl-toggle-desc {
  font-size: 0.8rem;
  color: var(--wdc-text-3);
  line-height: 1.4;
}

.history-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 720px;
}
.history-row {
  display: grid;
  grid-template-columns: 220px 1fr auto;
  align-items: center;
  gap: 16px;
  padding: 12px 16px;
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
}
.history-when {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 0.82rem;
  color: var(--wdc-text-2);
}
.history-label {
  font-size: 0.85rem;
  color: var(--wdc-text);
}

.danger-box {
  padding: 24px 28px;
  background: rgba(239, 68, 68, 0.06);
  border: 2px solid rgba(239, 68, 68, 0.32);
  border-radius: var(--wdc-radius);
  max-width: 640px;
}
.danger-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-status-error);
  margin-bottom: 8px;
}
.danger-desc {
  font-size: 0.85rem;
  color: var(--wdc-text-2);
  margin-bottom: 14px;
}
</style>
