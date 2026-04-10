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
            <el-form :model="site" label-position="top" size="default" class="two-col-form">
              <el-form-item label="Domain">
                <el-input :model-value="site.domain" disabled />
              </el-form-item>
              <el-form-item label="Document Root" required>
                <el-input v-model="site.documentRoot" @input="markDirty" />
              </el-form-item>
              <el-form-item label="Framework (auto-detected)">
                <el-input v-model="site.framework" placeholder="e.g. wordpress, laravel" @input="markDirty" />
              </el-form-item>
              <el-form-item label="Aliases (comma-separated)">
                <el-input
                  :model-value="aliasesStr"
                  placeholder="www.myapp.loc, dev.myapp.loc"
                  @update:model-value="(v: string) => { aliasesStr = v; markDirty() }"
                />
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- ── Runtime ──────────────────────────── -->
        <el-tab-pane name="runtime">
          <template #label>
            <span class="tab-label"><el-icon><Cpu /></el-icon> Runtime</span>
          </template>
          <div class="tab-content">
            <el-form :model="site" label-position="top" size="default" class="two-col-form">
              <el-form-item label="PHP Version">
                <el-select v-model="site.phpVersion" style="width: 100%" @change="markDirty">
                  <el-option v-for="v in phpVersions" :key="v" :label="v" :value="v" />
                  <el-option label="None" value="none" />
                </el-select>
              </el-form-item>
              <el-form-item label="HTTP Port">
                <el-input-number v-model="site.httpPort" :min="1" :max="65535" style="width: 100%" @change="markDirty" />
              </el-form-item>
              <el-form-item label="HTTPS Port" v-if="site.sslEnabled">
                <el-input-number v-model="site.httpsPort" :min="1" :max="65535" style="width: 100%" @change="markDirty" />
              </el-form-item>
            </el-form>
          </div>
        </el-tab-pane>

        <!-- ── SSL ──────────────────────────────── -->
        <el-tab-pane name="ssl">
          <template #label>
            <span class="tab-label"><el-icon><Lock /></el-icon> SSL</span>
          </template>
          <div class="tab-content">
            <el-form :model="site" label-position="top" size="default">
              <el-form-item label="Enable HTTPS (mkcert)">
                <el-switch v-model="site.sslEnabled" @change="markDirty" />
                <div class="hint">Generates a trusted local certificate via mkcert and enables HTTPS vhost on port 443.</div>
              </el-form-item>
              <el-form-item label="HTTP → HTTPS redirect" v-if="site.sslEnabled">
                <el-switch v-model="redirectHttps" @change="markDirty" />
              </el-form-item>
            </el-form>
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
              <div class="danger-title">Delete this site</div>
              <div class="danger-desc">
                Removes the vhost config and hosts file entry. Document root and databases are not touched.
              </div>
              <el-button type="danger" @click="confirmDelete">Delete {{ site.domain }}</el-button>
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Setting, Cpu, Lock, Clock, WarningFilled } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useSitesStore } from '../../stores/sites'
import type { SiteInfo } from '../../api/types'

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

const aliasesStr = ref('')
watch(() => site.value?.aliases, (al) => { aliasesStr.value = (al ?? []).join(', ') }, { immediate: true })

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
    // commit aliases from string
    site.value.aliases = aliasesStr.value.split(',').map(s => s.trim()).filter(Boolean)
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
  padding: 20px 4px;
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
  padding: 20px 24px;
  background: rgba(239, 68, 68, 0.06);
  border: 1px solid rgba(239, 68, 68, 0.24);
  border-radius: var(--wdc-radius);
  max-width: 640px;
}
.danger-title {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-status-error);
  margin-bottom: 6px;
}
.danger-desc {
  font-size: 0.85rem;
  color: var(--wdc-text-2);
  margin-bottom: 14px;
}
</style>
