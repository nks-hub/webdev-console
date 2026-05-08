<template>
  <div class="db-plugin-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('postgresPlugin.title') }}</h1>
        <span class="page-subtitle">{{ $t('postgresPlugin.subtitle') }}</span>
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
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} PostgreSQL
        </el-button>
      </div>
    </div>

    <div class="page-autostart-row">
      <PluginAutostartSwitch plugin-id="nks.wdc.postgresql" />
    </div>

    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'">
          <CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else />
        </el-icon>
        <div class="status-body">
          <div class="status-title">{{ serviceRunning ? $t('common.running') : $t('common.stopped') }}</div>
          <div class="status-meta">PostgreSQL</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Connection /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('postgresPlugin.port') }}: {{ postgresPort }}</div>
          <div class="status-meta">{{ serviceInfo?.version || $t('postgresPlugin.versionUnknown') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><DataLine /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('postgresPlugin.databaseUser') }}: postgres</div>
          <div class="status-meta">{{ $t('postgresPlugin.localTrust') }}</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="db-tabs">
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('postgresPlugin.tabOverview') }}</span>
        </template>
        <section class="edit-card">
          <header class="edit-card-header">
            <span class="edit-card-title">{{ $t('postgresPlugin.runtime') }}</span>
          </header>
          <div class="edit-card-body">
            <el-descriptions :column="isNarrow ? 1 : 2" border size="small">
              <el-descriptions-item :label="$t('postgresPlugin.status')">
                <el-tag :type="serviceRunning ? 'success' : 'info'" size="small" effect="dark">
                  {{ serviceRunning ? $t('common.running') : $t('common.stopped') }}
                </el-tag>
              </el-descriptions-item>
              <el-descriptions-item :label="$t('postgresPlugin.version')">{{ serviceInfo?.version || '—' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('postgresPlugin.port')">{{ postgresPort }}</el-descriptions-item>
              <el-descriptions-item :label="$t('postgresPlugin.pid')">{{ serviceInfo?.pid ?? '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('postgresPlugin.dataDir')">~/.wdc/data/postgresql</el-descriptions-item>
              <el-descriptions-item :label="$t('postgresPlugin.logFile')">~/.wdc/logs/postgresql/postgresql.log</el-descriptions-item>
            </el-descriptions>
          </div>
        </section>
      </el-tab-pane>

      <el-tab-pane name="databases">
        <template #label>
          <span class="tab-label"><el-icon><Grid /></el-icon> {{ $t('postgresPlugin.tabDatabases') }}</span>
        </template>
        <section class="edit-card">
          <header class="edit-card-header">
            <span class="edit-card-title">{{ $t('postgresPlugin.databaseTools') }}</span>
            <el-button size="small" text :loading="databasesLoading" @click="loadDatabases">
              {{ $t('common.refresh') }}
            </el-button>
          </header>
          <div class="edit-card-body">
            <el-alert
              v-if="databasesError"
              type="warning"
              :closable="false"
              show-icon
              :title="databasesError"
              class="section-alert"
            />
            <div v-if="postgresDatabases.length > 0" class="database-list">
              <div v-for="db in postgresDatabases" :key="db" class="database-row">
                <span class="database-name">{{ db }}</span>
                <el-tag size="small" effect="plain">PostgreSQL</el-tag>
              </div>
            </div>
            <el-empty
              v-else
              :description="databasesLoading ? $t('common.loading') : $t('postgresPlugin.noDatabases')"
              :image-size="48"
            />
            <div class="hint">{{ $t('postgresPlugin.databaseToolsHint') }}</div>
          </div>
        </section>

        <section class="edit-card danger-card">
          <header class="edit-card-header danger-header">
            <span class="edit-card-title">{{ $t('postgresPlugin.resetPassword') }}</span>
            <el-tag type="warning" size="small" effect="dark">{{ $t('postgresPlugin.localOnly') }}</el-tag>
          </header>
          <div class="edit-card-body">
            <el-alert
              type="warning"
              :closable="false"
              show-icon
              :title="$t('postgresPlugin.resetWarning')"
              class="section-alert"
            />
            <el-form label-width="180px" size="default">
              <el-form-item :label="$t('postgresPlugin.newPassword')">
                <el-input v-model="resetPasswordForm.newPassword" type="password" show-password style="max-width: 340px" />
              </el-form-item>
              <el-form-item :label="$t('postgresPlugin.confirmPassword')">
                <el-input v-model="resetPasswordForm.confirm" type="password" show-password style="max-width: 340px" />
              </el-form-item>
            </el-form>
            <div class="card-actions">
              <el-button
                type="warning"
                :loading="resettingPassword"
                :disabled="!resetPasswordForm.newPassword || resetPasswordForm.newPassword !== resetPasswordForm.confirm"
                @click="resetPostgresPassword"
              >
                {{ $t('postgresPlugin.resetPasswordBtn') }}
              </el-button>
              <span v-if="resetPasswordStatus" class="save-status" :class="resetPasswordStatus.kind">
                {{ resetPasswordStatus.message }}
              </span>
            </div>
          </div>
        </section>
      </el-tab-pane>

      <el-tab-pane name="tuning">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('postgresPlugin.tabTuning') }}</span>
        </template>
        <section class="edit-card">
          <header class="edit-card-header">
            <span class="edit-card-title">{{ $t('postgresPlugin.tuningParams') }}</span>
          </header>
          <div class="edit-card-body">
            <el-form label-width="220px" size="default">
              <el-form-item label="listen_addresses">
                <el-input model-value="127.0.0.1" disabled />
              </el-form-item>
              <el-form-item label="port">
                <el-input-number :model-value="postgresPort" disabled :min="1" :max="65535" />
              </el-form-item>
              <el-form-item label="max_connections">
                <el-input-number :model-value="100" disabled :min="1" />
              </el-form-item>
            </el-form>
            <div class="hint">{{ $t('postgresPlugin.tuningHint') }}</div>
          </div>
        </section>
      </el-tab-pane>

      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('postgresPlugin.tabLogs') }}</span>
        </template>
        <section class="edit-card">
          <header class="edit-card-header">
            <span class="edit-card-title">{{ $t('postgresPlugin.tabLogs') }}</span>
          </header>
          <div class="edit-card-body log-body">
            <LogViewer :service-id="'postgresql'" />
          </div>
        </section>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { CircleCheckFilled, CircleClose, Connection, Monitor, Grid, Setting, Document, DataLine } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { useDaemonStore } from '../../stores/daemon'
import { daemonAuthHeaders as authHeaders, daemonBaseUrl, startService, stopService } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'

defineOptions({ name: 'PostgreSqlPluginPage' })

const daemonStore = useDaemonStore()
const { t } = useI18n()
const activeTab = ref<'overview' | 'databases' | 'tuning' | 'logs'>('overview')
const refreshing = ref(false)
const toggling = ref(false)
const isNarrow = ref(false)
const postgresDatabases = ref<string[]>([])
const databasesLoading = ref(false)
const databasesError = ref('')
const resetPasswordForm = reactive({ newPassword: '', confirm: '' })
const resettingPassword = ref(false)
const resetPasswordStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

const serviceInfo = computed(() => daemonStore.services.find(s => s.id === 'postgresql'))
const serviceRunning = computed(() => serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running')
const postgresPort = computed(() => (serviceInfo.value as { port?: number } | undefined)?.port ?? 5432)

function updateNarrow() {
  isNarrow.value = window.innerWidth <= 720
}

async function refresh() {
  refreshing.value = true
  try {
    await daemonStore.poll()
    await loadDatabases()
  } finally {
    refreshing.value = false
  }
}

async function loadDatabases() {
  databasesLoading.value = true
  databasesError.value = ''
  try {
    const response = await fetch(`${daemonBaseUrl()}/api/plugins/postgresql/databases`, {
      headers: authHeaders(),
    })
    const data: { databases?: string[]; error?: string } = await response.json().catch(() => ({}))
    postgresDatabases.value = data.databases ?? []
    if (data.error) databasesError.value = data.error
  } catch (e) {
    postgresDatabases.value = []
    databasesError.value = errorMessage(e)
  } finally {
    databasesLoading.value = false
  }
}

async function resetPostgresPassword() {
  if (resetPasswordForm.newPassword !== resetPasswordForm.confirm) {
    ElMessage.warning('Passwords do not match')
    return
  }

  try {
    await ElMessageBox.confirm(
      t('postgresPlugin.resetConfirm'),
      t('postgresPlugin.resetPassword'),
      { type: 'warning', confirmButtonText: t('postgresPlugin.resetPasswordBtn') }
    )
  } catch { return }

  resettingPassword.value = true
  resetPasswordStatus.value = null
  try {
    const response = await fetch(`${daemonBaseUrl()}/api/plugins/postgresql/reset-password`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ newPassword: resetPasswordForm.newPassword }),
    })
    if (!response.ok) {
      const data: { error?: string; detail?: string } = await response.json().catch(() => ({}))
      throw new Error(data.error || data.detail || `HTTP ${response.status}`)
    }
    resetPasswordStatus.value = { kind: 'ok', message: t('postgresPlugin.resetSuccess') }
    ElMessage.success(t('postgresPlugin.resetSuccess'))
    resetPasswordForm.newPassword = ''
    resetPasswordForm.confirm = ''
  } catch (e) {
    resetPasswordStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(`PostgreSQL password reset failed: ${errorMessage(e)}`)
  } finally {
    resettingPassword.value = false
  }
}

async function toggleService() {
  toggling.value = true
  try {
    if (serviceRunning.value) await stopService('postgresql')
    else await startService('postgresql')
    await daemonStore.poll()
  } catch (e) {
    ElMessage.error(`PostgreSQL ${serviceRunning.value ? 'stop' : 'start'} failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

onMounted(() => {
  updateNarrow()
  window.addEventListener('resize', updateNarrow)
  void loadDatabases()
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', updateNarrow)
})
</script>

<style scoped>
.db-plugin-page { min-height: 100%; background: transparent; padding: 0; }
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  padding: 20px 24px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}
.page-autostart-row { padding: 10px 24px 0; max-width: 720px; }
.header-left { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.page-title { font-size: 1.6rem; font-weight: 800; color: var(--wdc-text); margin: 0; letter-spacing: -0.02em; }
.page-subtitle { font-size: 0.78rem; color: var(--wdc-text-3); }
.header-actions { display: flex; gap: 8px; flex-wrap: wrap; justify-content: flex-end; }
.status-strip { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; padding: 18px 24px 4px; }
.status-card { display: flex; align-items: center; gap: 12px; min-width: 0; padding: 14px 16px; background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); }
.status-card.status-active { border-color: var(--wdc-status-running); }
.status-icon { font-size: 1.4rem; width: 30px; text-align: center; color: var(--wdc-text-3); flex-shrink: 0; }
.status-active .status-icon { color: var(--wdc-status-running); }
.status-body { display: flex; flex-direction: column; min-width: 0; }
.status-title { font-size: 0.92rem; font-weight: 700; color: var(--wdc-text); overflow-wrap: anywhere; }
.status-meta { font-size: 0.72rem; color: var(--wdc-text-3); overflow-wrap: anywhere; }
.db-tabs { padding: 16px 24px; }
.edit-card { background: var(--wdc-surface); border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius); overflow: hidden; }
.edit-card-header { padding: 14px 20px; background: var(--wdc-surface-2); border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: center; gap: 12px; }
.edit-card-title { font-size: 0.78rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: var(--wdc-text); }
.edit-card-body { padding: 18px 20px; }
.edit-card-body :deep(.el-descriptions__cell) { word-break: break-word; overflow-wrap: anywhere; }
.log-body { padding: 0; }
.hint { margin-top: 6px; font-size: 0.82rem; line-height: 1.55; color: var(--wdc-text-3); }
.section-alert { margin-bottom: 14px; }
.database-list { display: flex; flex-direction: column; border: 1px solid var(--wdc-border); border-radius: var(--wdc-radius-sm); overflow: hidden; }
.database-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 12px; background: var(--wdc-surface); border-bottom: 1px solid var(--wdc-border); }
.database-row:last-child { border-bottom: 0; }
.database-name { font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.86rem; color: var(--wdc-text); overflow-wrap: anywhere; }
.card-actions { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 12px; }
.save-status { font-size: 0.82rem; font-weight: 600; overflow-wrap: anywhere; }
.save-status.ok { color: var(--wdc-status-running); }
.save-status.err { color: var(--wdc-status-error); }
.danger-card { border-color: var(--wdc-warning); }
.danger-header { background: color-mix(in srgb, var(--wdc-warning) 10%, var(--wdc-surface-2)); border-bottom-color: var(--wdc-warning); }

@media (max-width: 860px) {
  .page-header { align-items: flex-start; flex-direction: column; }
  .header-actions { width: 100%; justify-content: stretch; }
  .header-actions :deep(.el-button) { flex: 1 1 160px; }
  .status-strip { grid-template-columns: 1fr; }
}

@media (max-width: 560px) {
  .page-header,
  .page-autostart-row,
  .status-strip,
  .db-tabs { padding-left: 14px; padding-right: 14px; }
  .edit-card-header { align-items: flex-start; flex-direction: column; }
}
</style>
