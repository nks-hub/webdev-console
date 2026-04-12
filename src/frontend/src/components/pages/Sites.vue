<template>
  <div class="sites-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">Sites</h1>
        <span class="site-count">{{ sitesStore.sites.length }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openHostsFile" title="Open hosts file">
          Open Hosts
        </el-button>
        <el-button size="small" @click="reapplyAll" :loading="reapplying" title="Regenerate all vhosts">
          Reapply All
        </el-button>
        <el-button type="primary" size="small" @click="showCreate = true">+ New Site</el-button>
      </div>
    </div>

    <!-- Search bar -->
    <div class="search-bar">
      <el-input
        v-model="search"
        placeholder="Filter by domain or docroot..."
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
      >
        <el-table-column prop="domain" label="Domain" min-width="220">
          <template #default="{ row }">
            <div class="cell-domain">
              <div class="cell-domain-row">
                <span class="col-domain">{{ row.domain }}</span>
                <span class="cell-port">:{{ row.httpPort || 80 }}</span>
                <el-tag
                  v-if="row.sslEnabled"
                  size="small"
                  type="success"
                  effect="dark"
                  class="cell-tag"
                  title="HTTPS enabled"
                >SSL</el-tag>
              </div>
              <div v-if="row.aliases?.length" class="col-aliases">
                <span class="alias-dot">↳</span>
                {{ row.aliases.join(', ') }}
              </div>
              <div
                v-if="row.cloudflare?.enabled && row.cloudflare?.subdomain"
                class="col-tunnel"
                :class="{ 'col-tunnel-offline': !cloudflaredRunning }"
              >
                <span class="tunnel-icon">☁</span>
                <a
                  :href="`https://${row.cloudflare.subdomain}.${row.cloudflare.zoneName}`"
                  target="_blank"
                  @click.stop
                  :title="cloudflaredRunning
                    ? 'Open public URL in browser'
                    : 'Tunnel service is stopped — this URL will not respond until you start cloudflared'"
                >{{ row.cloudflare.subdomain }}.{{ row.cloudflare.zoneName }}</a>
                <span v-if="!cloudflaredRunning" class="tunnel-badge-offline">offline</span>
              </div>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="Document Root" min-width="260">
          <template #default="{ row }">
            <span class="col-mono">{{ row.documentRoot }}</span>
          </template>
        </el-table-column>

        <el-table-column label="Runtime" width="130">
          <template #default="{ row }">
            <el-tag
              v-if="row.phpVersion && row.phpVersion !== 'none'"
              size="small"
              effect="dark"
              class="runtime-tag runtime-php"
            >PHP {{ row.phpVersion }}</el-tag>
            <el-tag
              v-else-if="row.framework === 'node' || row.framework === 'nextjs'"
              size="small"
              effect="dark"
              class="runtime-tag runtime-node"
            >Node</el-tag>
            <el-tag
              v-else
              size="small"
              effect="plain"
              class="runtime-tag runtime-static"
            >Static</el-tag>
          </template>
        </el-table-column>

        <el-table-column label="Framework" width="180">
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
              >🐳 Compose</el-tag>
              <span
                v-if="!row.framework && !composeStatus[row.domain]?.hasCompose"
                class="col-empty"
              >—</span>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="Actions" width="280" fixed="right">
          <template #default="{ row }">
            <div class="site-actions">
              <el-button size="small" type="primary" @click.stop="openInBrowser(row)">Open</el-button>
              <el-button size="small" @click.stop="detectFramework(row.domain)" title="Auto-detect framework">Detect</el-button>
              <el-button size="small" type="danger" plain @click.stop="confirmDelete(row.domain)">Delete</el-button>
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

    <!-- Site edit is a full-view route at /sites/:domain/edit (no drawer). -->

    <!-- Create dialog -->
    <el-dialog v-model="showCreate" title="New Site" width="520px">
      <el-form :model="newSite" label-position="top" size="small">
        <el-form-item label="Template">
          <el-select v-model="newSite.template" style="width: 100%" placeholder="Choose a template…" @change="applyTemplate">
            <el-option label="— No template —" value="" />
            <el-option label="WordPress (PHP 8.4, SSL)" value="wordpress" />
            <el-option label="Laravel (PHP 8.4, SSL)" value="laravel" />
            <el-option label="Nette (PHP 8.4, SSL)" value="nette" />
            <el-option label="Symfony (PHP 8.4, SSL)" value="symfony" />
            <el-option label="Next.js (Node proxy, SSL)" value="nextjs" />
            <el-option label="Node.js (Node proxy)" value="node" />
            <el-option label="Static HTML" value="static" />
          </el-select>
        </el-form-item>
        <el-form-item label="Domain" required>
          <el-input v-model="newSite.domain" placeholder="myapp.loc" />
        </el-form-item>
        <el-form-item label="Document Root" required>
          <el-input v-model="newSite.documentRoot" placeholder="C:\work\htdocs\myapp" />
        </el-form-item>
        <el-form-item label="PHP Version">
          <el-select v-model="newSite.phpVersion" style="width: 100%" placeholder="Select PHP version">
            <el-option v-for="v in phpVersions" :key="v" :label="v" :value="v" />
            <el-option label="None" value="none" />
          </el-select>
        </el-form-item>
        <el-form-item label="Aliases (comma-separated)">
          <el-input v-model="newSite.aliases" placeholder="www.myapp.loc" />
        </el-form-item>
        <el-form-item label="SSL">
          <el-switch v-model="newSite.sslEnabled" />
        </el-form-item>
        <el-form-item label="Create Database">
          <el-switch v-model="newSite.createDb" />
          <el-input
            v-if="newSite.createDb"
            v-model="newSite.dbName"
            placeholder="auto: myapp_db"
            style="margin-top: 8px"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreate = false">Cancel</el-button>
        <el-button type="primary" :loading="creating" @click="createSite">Create Site</el-button>
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
import type { SiteInfo } from '../../api/types'
import { fetchDockerComposeStatus, type DockerComposeStatus } from '../../api/daemon'

const route = useRoute()
const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()

// Sites list shows per-row tunnel links when cloudflare.enabled → but the
// link only works if the shared cloudflared process is actually running.
// When it's stopped, render the row in a dimmed style with an "offline"
// badge so users don't click through to a dead URL and see a Cloudflare
// 521 error page. Computed here so it's reactive to SSE service updates.
const cloudflaredRunning = computed(() =>
  (daemonStore.services as any[]).some(s =>
    s.id === 'cloudflare' && (s.state === 2 || s.status === 'running')
  )
)
const phpVersions = ref<string[]>([])
// Docker Compose detection map: domain -> status. Lazy-populated after
// sites load so the Compose badge in the Framework column reflects what
// the daemon sees on disk without blocking the site list itself.
const composeStatus = reactive<Record<string, DockerComposeStatus>>({})

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = (window as any).daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}
const showCreate = ref(false)
const creating = ref(false)
const reapplying = ref(false)
const search = ref('')

const newSite = reactive({
  template: '',
  domain: '',
  documentRoot: '',
  phpVersion: '8.4',
  aliases: '',
  createDb: false,
  dbName: '',
  sslEnabled: false,
})

const TEMPLATES: Record<string, { php: string; ssl: boolean }> = {
  wordpress: { php: '8.4', ssl: true },
  laravel:   { php: '8.4', ssl: true },
  nette:     { php: '8.4', ssl: true },
  symfony:   { php: '8.4', ssl: true },
  nextjs:    { php: 'none', ssl: true },
  node:      { php: 'none', ssl: false },
  static:    { php: 'none', ssl: false },
}

function applyTemplate(tplName: string) {
  const tpl = TEMPLATES[tplName]
  if (!tpl) return
  newSite.phpVersion = tpl.php
  newSite.sslEnabled = tpl.ssl
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
  const tasks = sitesStore.sites.map(async (s) => {
    try {
      const status = await fetchDockerComposeStatus(s.domain)
      composeStatus[s.domain] = status
    } catch { /* leave entry absent — no badge rendered */ }
  })
  await Promise.all(tasks)
}

onMounted(async () => {
  await sitesStore.load()
  void refreshComposeStatuses()
  try {
    const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: sitesStore.authHeaders() })
    if (r.ok) {
      const versions = await r.json()
      phpVersions.value = versions.map((v: any) => v.majorMinor || v.version?.split('.').slice(0, 2).join('.') || v.version)
    }
  } catch { phpVersions.value = ['8.4', '8.3', '8.2'] }
})

// Re-scan compose status after any mutation that could change the set
// of sites or their document roots.
watch(() => sitesStore.sites.length, () => { void refreshComposeStatuses() })

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
    const payload = {
      domain: newSite.domain,
      documentRoot: newSite.documentRoot,
      phpVersion: newSite.phpVersion,
      sslEnabled: newSite.sslEnabled,
      aliases: newSite.aliases ? newSite.aliases.split(',').map(s => s.trim()).filter(Boolean) : [],
    }
    await sitesStore.create(payload)

    // Auto-create database if requested
    if (newSite.createDb) {
      const dbName = newSite.dbName || newSite.domain.replace(/\./g, '_').replace(/-/g, '_') + '_db'
      try {
        const dbRes = await fetch(`${daemonBase()}/api/databases`, {
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
    Object.assign(newSite, { domain: '', documentRoot: '', phpVersion: '8.4', aliases: '', sslEnabled: false, createDb: false, dbName: '' })
  } catch (e: any) {
    ElMessage.error(`Create failed: ${e.message}`)
  } finally {
    creating.value = false
  }
}

async function detectFramework(domain: string) {
  try {
    const res = await fetch(`${daemonBase()}/api/sites/${domain}/detect-framework`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...sitesStore.authHeaders() },
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const data = await res.json() as { framework?: string }
    if (data.framework) {
      ElMessage.success(`Detected: ${data.framework}`)
    } else {
      ElMessage.info('No framework detected')
    }
    await sitesStore.load()
  } catch {
    ElMessage.error('Detection failed')
  }
}

async function confirmDelete(domain: string) {
  try {
    await ElMessageBox.confirm(`Delete site "${domain}"? This cannot be undone.`, 'Warning', {
      type: 'warning',
      confirmButtonText: 'Delete',
      confirmButtonClass: 'el-button--danger',
    })
    await sitesStore.remove(domain)
    ElMessage.success('Site deleted')
  } catch { /* user cancelled */ }
}

async function reapplyAll() {
  reapplying.value = true
  try {
    const res = await fetch(`${daemonBase()}/api/sites/reapply-all`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (res.ok) {
      ElMessage.success('All vhosts regenerated')
    } else {
      ElMessage.error(`Failed: HTTP ${res.status}`)
    }
  } catch (e: any) {
    ElMessage.error(`Reapply failed: ${e.message}`)
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
  window.open(`${proto}://${site.domain}${portSuffix}`, '_blank')
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
  display: flex;
  align-items: center;
  gap: 4px;
  font-family: 'JetBrains Mono', monospace;
}
.alias-dot { color: var(--wdc-text-3); }

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
</style>
