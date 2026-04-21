<template>
  <div class="cf-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('apache.title') }}</h1>
        <span class="page-subtitle">{{ $t('apache.subtitle') }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="refresh" :loading="refreshing">{{ $t('common.refresh') }}</el-button>
        <el-button
          size="small"
          :type="serviceRunning ? 'danger' : 'success'"
          :loading="toggling"
          :disabled="!daemonStore.connected"
          @click="toggleService"
        >
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} Apache
        </el-button>
      </div>
    </div>

    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'">
          <CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else />
        </el-icon>
        <div class="status-body">
          <div class="status-title">{{ serviceRunning ? $t('common.running') : $t('common.stopped') }}</div>
          <div class="status-meta" v-if="serviceInfo?.uptime">{{ $t('apache.uptime') }}: {{ formatUptime(serviceInfo.uptime) }}</div>
          <div class="status-meta" v-else>Apache</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Connection /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('apache.ports') }}: 80 / 443</div>
          <div class="status-meta">{{ serviceInfo?.version || $t('apache.versionUnknown') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><List /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ sitesStore.sites.length }} {{ $t('apache.vhostsActive') }}</div>
          <div class="status-meta">{{ $t('apache.vhostsMeta') }}</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- Overview -->
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('apache.tabOverview') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('apache.statusCard') }}</span>
            </header>
            <div class="edit-card-body">
              <el-descriptions :column="2" border size="small">
                <el-descriptions-item :label="$t('apache.status')">
                  <el-tag :type="serviceRunning ? 'success' : 'info'" size="small" effect="dark">
                    {{ serviceRunning ? $t('common.running') : $t('common.stopped') }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('apache.version')">{{ serviceInfo?.version || '—' }}</el-descriptions-item>
                <el-descriptions-item label="HTTP">:80</el-descriptions-item>
                <el-descriptions-item label="HTTPS">:443</el-descriptions-item>
                <el-descriptions-item :label="$t('apache.vhostsActive')">{{ sitesStore.sites.length }}</el-descriptions-item>
                <el-descriptions-item :label="$t('apache.pid')">{{ serviceInfo?.pid ?? '—' }}</el-descriptions-item>
              </el-descriptions>
            </div>
          </section>
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('apache.recentErrors') }}</span>
              <span class="edit-card-hint">{{ $t('apache.recentErrorsHint') }}</span>
            </header>
            <div class="edit-card-body">
              <LogViewer :service-id="'apache'" style="height: 200px" />
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Vhosts -->
      <el-tab-pane name="vhosts">
        <template #label>
          <span class="tab-label"><el-icon><Share /></el-icon> {{ $t('apache.tabVhosts') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('apache.enabledSites') }}</span>
              <span class="edit-card-hint">{{ sitesStore.sites.length }} {{ $t('apache.vhostsActive') }}</span>
            </header>
            <div class="edit-card-body">
              <el-empty v-if="sitesStore.sites.length === 0" :description="$t('apache.noVhosts')" :image-size="48" />
              <el-table v-else :data="sitesStore.sites" size="small">
                <el-table-column prop="domain" :label="$t('sites.domain')" min-width="200">
                  <template #default="{ row }">
                    <span class="mono">{{ row.domain }}</span>
                  </template>
                </el-table-column>
                <el-table-column prop="phpVersion" :label="$t('sites.phpVersion')" width="110" />
                <el-table-column label="HTTPS" width="80" align="center">
                  <template #default="{ row }">
                    <el-tag v-if="row.ssl" size="small" type="success" effect="dark">SSL</el-tag>
                    <el-tag v-else size="small" effect="plain">HTTP</el-tag>
                  </template>
                </el-table-column>
                <el-table-column :label="$t('common.actions')" width="100" align="center">
                  <template #default="{ row }">
                    <el-button size="small" text @click="$router.push(`/sites/${row.domain}/edit`)">
                      {{ $t('common.edit') }}
                    </el-button>
                  </template>
                </el-table-column>
              </el-table>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Tuning -->
      <el-tab-pane name="tuning">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('apache.tabTuning') }}</span>
        </template>
        <div class="tab-content">
          <el-alert
            type="info"
            :closable="false"
            show-icon
            :title="$t('apache.tuningPending')"
            style="margin-bottom: 16px"
          />
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('apache.mpm') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="200px" size="default">
                <el-form-item :label="$t('apache.mpmModule')">
                  <el-select v-model="tuning.mpm" disabled style="width: 200px">
                    <el-option label="prefork" value="prefork" />
                    <el-option label="worker" value="worker" />
                    <el-option label="event" value="event" />
                  </el-select>
                </el-form-item>
                <el-form-item label="MaxRequestWorkers">
                  <el-input-number v-model="tuning.maxRequestWorkers" disabled :min="1" />
                </el-form-item>
                <el-form-item label="KeepAlive">
                  <el-switch v-model="tuning.keepAlive" disabled />
                </el-form-item>
                <el-form-item label="KeepAliveTimeout">
                  <el-input-number v-model="tuning.keepAliveTimeout" disabled :min="1" style="width: 120px" />
                  <span class="hint-inline">s</span>
                </el-form-item>
                <el-form-item label="Timeout">
                  <el-input-number v-model="tuning.timeout" disabled :min="1" style="width: 120px" />
                  <span class="hint-inline">s</span>
                </el-form-item>
              </el-form>
              <div class="hint">{{ $t('apache.tuningPendingHint') }}</div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Logs -->
      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('apache.tabLogs') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('apache.tabLogs') }}</span>
            </header>
            <div class="edit-card-body" style="padding: 0">
              <LogViewer :service-id="'apache'" />
            </div>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { CircleCheckFilled, CircleClose, Connection, Monitor, Share, Setting, Document, List } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import { startService, stopService } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'

defineOptions({ name: 'ApachePluginPage' })

const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()
const activeTab = ref<'overview' | 'vhosts' | 'tuning' | 'logs'>('overview')
const refreshing = ref(false)
const toggling = ref(false)

const tuning = reactive({ mpm: 'event', maxRequestWorkers: 150, keepAlive: true, keepAliveTimeout: 5, timeout: 300 })

const serviceInfo = computed(() => daemonStore.services.find(s => s.id === 'apache'))
const serviceRunning = computed(() => serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running')

async function refresh() {
  refreshing.value = true
  try {
    if (sitesStore.sites.length === 0) await sitesStore.load()
  } finally {
    refreshing.value = false
  }
}

async function toggleService() {
  toggling.value = true
  try {
    if (serviceRunning.value) await stopService('apache')
    else await startService('apache')
  } catch (e) {
    ElMessage.error(`${serviceRunning.value ? 'Stop' : 'Start'} failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

function formatUptime(secs: number | string | undefined): string {
  if (typeof secs !== 'number') secs = Number(secs) || 0
  if (!secs || secs < 1) return '—'
  const h = Math.floor(secs / 3600)
  const m = Math.floor((secs % 3600) / 60)
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

onMounted(async () => {
  if (sitesStore.sites.length === 0) void sitesStore.load()
})
</script>

<style scoped>
.cf-page { min-height: 100%; background: var(--wdc-bg); padding: 0; }
.page-header { display: flex; align-items: center; justify-content: space-between; padding: 20px 24px 14px; border-bottom: 1px solid var(--wdc-border); }
.header-left { display: flex; flex-direction: column; gap: 2px; }
.page-title { font-size: 1.25rem; font-weight: 800; color: var(--wdc-text); margin: 0; }
.page-subtitle { font-size: 0.78rem; color: var(--wdc-text-3); }
.header-actions { display: flex; gap: 8px; }
.status-strip { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; padding: 18px 24px 4px; }
.status-card { display: flex; align-items: center; gap: 12px; padding: 14px 16px; background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); }
.status-card.status-active { border-color: var(--wdc-status-running); }
.status-icon { font-size: 1.4rem; width: 30px; text-align: center; color: var(--wdc-text-3); }
.status-active .status-icon { color: var(--wdc-status-running); }
.status-body { display: flex; flex-direction: column; min-width: 0; }
.status-title { font-size: 0.92rem; font-weight: 700; color: var(--wdc-text); }
.status-meta { font-size: 0.72rem; color: var(--wdc-text-3); }
.cf-tabs { padding: 16px 24px; }
.tab-content { display: flex; flex-direction: column; gap: 16px; }
.edit-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); overflow: hidden; }
.edit-card-header { padding: 14px 20px; background: var(--wdc-surface-2); border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: baseline; }
.edit-card-title { font-size: 0.78rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: var(--wdc-text); }
.edit-card-hint { font-size: 0.75rem; color: var(--wdc-text-3); }
.edit-card-body { padding: 18px 20px; }
.hint { margin-top: 6px; font-size: 0.78rem; color: var(--wdc-text-3); }
.hint-inline { margin-left: 8px; font-size: 0.82rem; color: var(--wdc-text-3); }
.mono { font-family: 'JetBrains Mono', monospace; }
</style>
