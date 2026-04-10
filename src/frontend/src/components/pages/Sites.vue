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
        <el-table-column prop="domain" label="Domain" min-width="160">
          <template #default="{ row }">
            <div>
              <span class="col-domain">{{ row.domain }}</span>
              <div v-if="row.aliases?.length" class="col-aliases">
                {{ row.aliases.join(', ') }}
              </div>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="Document Root" min-width="200">
          <template #default="{ row }">
            <span class="col-mono">{{ row.documentRoot }}</span>
          </template>
        </el-table-column>

        <el-table-column label="PHP" width="75">
          <template #default="{ row }">
            <el-tag v-if="row.phpVersion && row.phpVersion !== 'none'" size="small" effect="plain">
              {{ row.phpVersion }}
            </el-tag>
            <span v-else class="col-empty">—</span>
          </template>
        </el-table-column>

        <el-table-column label="Framework" width="110">
          <template #default="{ row }">
            <el-tag v-if="row.framework" size="small" type="warning" effect="plain">{{ row.framework }}</el-tag>
            <span v-else class="col-empty">—</span>
          </template>
        </el-table-column>

        <el-table-column label="SSL" width="52" align="center">
          <template #default="{ row }">
            <el-icon :color="row.sslEnabled ? '#22c55e' : '#64748b'" :title="row.sslEnabled ? 'SSL enabled' : 'No SSL'">
              <Lock />
            </el-icon>
          </template>
        </el-table-column>

        <el-table-column label="Port" width="70" align="center">
          <template #default="{ row }">
            <span class="col-mono col-empty">{{ row.httpPort || 80 }}</span>
          </template>
        </el-table-column>

        <el-table-column label="Actions" width="150" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text @click.stop="openInBrowser(row)">Open</el-button>
            <el-button size="small" text @click.stop="detectFramework(row.domain)" title="Auto-detect framework">Detect</el-button>
            <el-button size="small" type="danger" text @click.stop="confirmDelete(row.domain)">Del</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-empty
        v-if="filteredSites.length === 0 && !sitesStore.loading"
        :description="search ? `No sites matching '${search}'` : 'No sites configured yet'"
        :image-size="80"
      />
    </div>

    <!-- Site detail drawer -->
    <el-drawer
      v-model="drawerOpen"
      :title="selectedSite?.domain ?? 'Edit Site'"
      direction="rtl"
      size="420px"
    >
      <div v-if="selectedSite" class="site-detail">
        <el-form :model="selectedSite" label-position="top" size="small">
          <el-form-item label="Document Root">
            <el-input v-model="selectedSite.documentRoot" />
          </el-form-item>
          <el-form-item label="PHP Version">
            <el-select v-model="selectedSite.phpVersion" style="width: 100%">
              <el-option v-for="v in phpVersions" :key="v" :label="v" :value="v" />
              <el-option label="None" value="none" />
            </el-select>
          </el-form-item>
          <el-form-item label="Aliases (comma-separated)">
            <el-input v-model="aliasesStr" placeholder="www.myapp.loc, dev.myapp.loc" />
          </el-form-item>
          <el-form-item label="Framework">
            <el-input v-model="selectedSite.framework" placeholder="auto-detect" />
          </el-form-item>
          <el-form-item label="HTTP Port">
            <el-input-number v-model="selectedSite.httpPort" :min="1" :max="65535" style="width: 100%" />
          </el-form-item>
          <el-form-item label="SSL">
            <el-switch v-model="selectedSite.sslEnabled" />
          </el-form-item>
        </el-form>

        <div class="drawer-actions">
          <el-button type="primary" size="small" @click="saveSelected">Save Changes</el-button>
          <el-button size="small" @click="drawerOpen = false">Cancel</el-button>
        </div>

        <!-- Site history -->
        <div class="history-section" v-if="siteHistory.length > 0">
          <div class="history-title">Config History</div>
          <div class="history-list">
            <div v-for="(h, i) in siteHistory" :key="i" class="history-item">
              <span class="history-date">{{ formatDate(h.timestamp) }}</span>
              <span class="history-label">{{ h.label ?? `Version ${i + 1}` }}</span>
            </div>
          </div>
        </div>
      </div>
    </el-drawer>

    <!-- Create dialog -->
    <el-dialog v-model="showCreate" title="New Site" width="480px">
      <el-form :model="newSite" label-position="top" size="small">
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
import { ref, reactive, computed, onMounted } from 'vue'
import { Lock } from '@element-plus/icons-vue'
import { ElMessageBox, ElMessage } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import type { SiteInfo } from '../../api/types'

const sitesStore = useSitesStore()
const phpVersions = ref<string[]>([])

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = (window as any).daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}
const drawerOpen = ref(false)
const selectedSite = ref<SiteInfo | null>(null)
const showCreate = ref(false)
const creating = ref(false)
const reapplying = ref(false)
const search = ref('')
const siteHistory = ref<Array<{ timestamp: string; label?: string }>>([])

const newSite = reactive({
  domain: '',
  documentRoot: '',
  phpVersion: '8.4',
  aliases: '',
  createDb: false,
  dbName: '',
  sslEnabled: false,
})

const filteredSites = computed(() => {
  const q = search.value.toLowerCase()
  if (!q) return sitesStore.sites
  return sitesStore.sites.filter(s =>
    s.domain.toLowerCase().includes(q) ||
    s.documentRoot.toLowerCase().includes(q) ||
    (s.framework ?? '').toLowerCase().includes(q)
  )
})

const aliasesStr = computed({
  get: () => selectedSite.value?.aliases?.join(', ') ?? '',
  set: (v: string) => {
    if (selectedSite.value) {
      selectedSite.value.aliases = v.split(',').map(s => s.trim()).filter(Boolean)
    }
  },
})

onMounted(async () => {
  void sitesStore.load()
  try {
    const r = await fetch(`${daemonBase()}/api/php/versions`, { headers: sitesStore.authHeaders() })
    if (r.ok) {
      const versions = await r.json()
      phpVersions.value = versions.map((v: any) => v.majorMinor || v.version?.split('.').slice(0, 2).join('.') || v.version)
    }
  } catch { phpVersions.value = ['8.4', '8.3', '8.2'] }
})

function selectSite(row: SiteInfo) {
  selectedSite.value = { ...row, aliases: [...(row.aliases || [])] }
  drawerOpen.value = true
  void loadHistory(row.domain)
}

async function loadHistory(domain: string) {
  siteHistory.value = []
  try {
    const res = await fetch(`${daemonBase()}/api/sites/${domain}/history`, {
      headers: sitesStore.authHeaders(),
    })
    if (res.ok) {
      siteHistory.value = await res.json() as Array<{ timestamp: string; label?: string }>
    }
  } catch { /* history endpoint optional */ }
}

async function saveSelected() {
  if (!selectedSite.value) return
  try {
    await sitesStore.update(selectedSite.value.domain, selectedSite.value)
    ElMessage.success('Site updated')
    drawerOpen.value = false
  } catch (e: any) {
    ElMessage.error(`Update failed: ${e.message}`)
  }
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
        await fetch(`${daemonBase()}/api/databases`, {
          method: 'POST',
          headers: { ...sitesStore.authHeaders(), 'Content-Type': 'application/json' },
          body: JSON.stringify({ name: dbName }),
        })
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

function formatDate(iso: string): string {
  try { return new Date(iso).toLocaleString() } catch { return iso }
}
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

.col-domain {
  font-size: 0.88rem;
  font-weight: 600;
  color: var(--wdc-text);
}

.col-aliases {
  font-size: 0.75rem;
  color: var(--wdc-text-2);
  margin-top: 2px;
}

.col-mono {
  font-size: 0.78rem;
  font-family: monospace;
  color: var(--el-text-color-regular);
}

.col-empty {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
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
