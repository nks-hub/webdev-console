<template>
  <div class="cf-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('mysqlPlugin.title') }}</h1>
        <span class="page-subtitle">{{ $t('mysqlPlugin.subtitle') }}</span>
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
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} MySQL
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
          <div class="status-meta">MySQL</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Connection /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('mysqlPlugin.port') }}: {{ mysqlPort }}</div>
          <div class="status-meta">{{ serviceInfo?.version || $t('mysqlPlugin.versionUnknown') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><DataLine /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('mysqlPlugin.connections') }}: —</div>
          <div class="status-meta">{{ $t('mysqlPlugin.connectionsMeta') }}</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- Overview -->
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('mysqlPlugin.tabOverview') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tabOverview') }}</span>
            </header>
            <div class="edit-card-body">
              <el-descriptions :column="2" border size="small">
                <el-descriptions-item :label="$t('mysqlPlugin.status')">
                  <el-tag :type="serviceRunning ? 'success' : 'info'" size="small" effect="dark">
                    {{ serviceRunning ? $t('common.running') : $t('common.stopped') }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.version')">{{ serviceInfo?.version || '—' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.port')">{{ mysqlPort }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.pid')">{{ serviceInfo?.pid ?? '—' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.dataDir')">{{ $t('mysqlPlugin.dataDirUnknown') }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mysqlPlugin.connections')">—</el-descriptions-item>
              </el-descriptions>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Databases -->
      <el-tab-pane name="databases">
        <template #label>
          <span class="tab-label"><el-icon><Grid /></el-icon> {{ $t('mysqlPlugin.tabDatabases') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tabDatabases') }}</span>
              <span class="edit-card-hint">
                <el-button size="small" text @click="$router.push('/databases')">
                  {{ $t('mysqlPlugin.openDatabasesPage') }}
                </el-button>
              </span>
            </header>
            <div class="edit-card-body">
              <div class="hint">{{ $t('mysqlPlugin.databasesEmbedHint') }}</div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Root Password -->
      <el-tab-pane name="password">
        <template #label>
          <span class="tab-label"><el-icon><Key /></el-icon> {{ $t('mysqlPlugin.tabPassword') }}</span>
        </template>
        <div class="tab-content">
          <!-- Change password -->
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.changePassword') }}</span>
              <span class="edit-card-hint">{{ $t('mysqlPlugin.changePasswordHint') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="180px" size="default">
                <el-form-item :label="$t('mysqlPlugin.currentPassword')">
                  <el-input v-model="changePwd.current" type="password" show-password style="max-width: 340px" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.newPassword')">
                  <el-input v-model="changePwd.newPwd" type="password" show-password style="max-width: 340px" />
                </el-form-item>
                <el-form-item :label="$t('mysqlPlugin.confirmPassword')">
                  <el-input v-model="changePwd.confirm" type="password" show-password style="max-width: 340px" />
                </el-form-item>
              </el-form>
              <div class="card-actions">
                <el-button
                  type="primary"
                  :loading="changingPwd"
                  :disabled="!changePwd.current || !changePwd.newPwd || changePwd.newPwd !== changePwd.confirm"
                  @click="changePassword"
                >
                  {{ $t('mysqlPlugin.changePasswordBtn') }}
                </el-button>
                <span v-if="changePwdStatus" class="save-status" :class="changePwdStatus.kind">
                  {{ changePwdStatus.message }}
                </span>
              </div>
            </div>
          </section>

          <!-- Reset password (danger) -->
          <section class="edit-card danger-card">
            <header class="edit-card-header danger-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.resetPassword') }}</span>
              <el-tag type="danger" size="small" effect="dark">DANGER</el-tag>
            </header>
            <div class="edit-card-body">
              <el-alert
                type="warning"
                :closable="false"
                show-icon
                style="margin-bottom: 16px"
              >
                <template #title>{{ $t('mysqlPlugin.resetWarning') }}</template>
                <template #default>
                  <p style="margin: 6px 0 0">{{ $t('mysqlPlugin.resetDescription') }}</p>
                </template>
              </el-alert>
              <el-form label-width="180px" size="default">
                <el-form-item :label="$t('mysqlPlugin.newRootPassword')">
                  <el-input v-model="resetPwd.newPwd" type="password" show-password style="max-width: 340px" />
                </el-form-item>
              </el-form>
              <div class="card-actions">
                <el-button
                  type="danger"
                  :loading="resettingPwd"
                  :disabled="!resetPwd.newPwd"
                  @click="resetPassword"
                >
                  {{ $t('mysqlPlugin.resetPasswordBtn') }}
                </el-button>
                <span v-if="resetPwdStatus" class="save-status" :class="resetPwdStatus.kind">
                  {{ resetPwdStatus.message }}
                </span>
              </div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Tuning -->
      <el-tab-pane name="tuning">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('mysqlPlugin.tabTuning') }}</span>
        </template>
        <div class="tab-content">
          <el-alert
            type="info"
            :closable="false"
            show-icon
            :title="$t('mysqlPlugin.tuningPending')"
            style="margin-bottom: 16px"
          />
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tuningParams') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="240px" size="default">
                <el-form-item label="max_connections">
                  <el-input-number v-model="tuning.maxConnections" disabled :min="1" />
                </el-form-item>
                <el-form-item label="innodb_buffer_pool_size">
                  <el-input v-model="tuning.innodbBufferPoolSize" disabled style="width: 180px" />
                </el-form-item>
                <el-form-item label="query_cache_size">
                  <el-input v-model="tuning.queryCacheSize" disabled style="width: 180px" />
                </el-form-item>
              </el-form>
              <div class="hint">{{ $t('mysqlPlugin.tuningPendingHint') }}</div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Logs -->
      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('mysqlPlugin.tabLogs') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mysqlPlugin.tabLogs') }}</span>
            </header>
            <div class="edit-card-body" style="padding: 0">
              <LogViewer :service-id="'mysql'" />
            </div>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { CircleCheckFilled, CircleClose, Connection, Monitor, Grid, Key, Setting, Document, DataLine } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { daemonBaseUrl, daemonAuthHeaders as authHeaders, startService, stopService } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'

defineOptions({ name: 'MySqlPluginPage' })

const daemonStore = useDaemonStore()
const activeTab = ref<'overview' | 'databases' | 'password' | 'tuning' | 'logs'>('overview')
const refreshing = ref(false)
const toggling = ref(false)

const changePwd = reactive({ current: '', newPwd: '', confirm: '' })
const changingPwd = ref(false)
const changePwdStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

const resetPwd = reactive({ newPwd: '' })
const resettingPwd = ref(false)
const resetPwdStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

const tuning = reactive({ maxConnections: 151, innodbBufferPoolSize: '128M', queryCacheSize: '0' })

const serviceInfo = computed(() => daemonStore.services.find(s => s.id === 'mysql'))
const serviceRunning = computed(() => serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running')
const mysqlPort = computed(() => (serviceInfo.value as { port?: number } | undefined)?.port ?? 3306)

async function refresh() {
  refreshing.value = true
  try {
    await new Promise(r => setTimeout(r, 200))
  } finally {
    refreshing.value = false
  }
}

async function toggleService() {
  toggling.value = true
  try {
    if (serviceRunning.value) await stopService('mysql')
    else await startService('mysql')
  } catch (e) {
    ElMessage.error(`${serviceRunning.value ? 'Stop' : 'Start'} failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

async function changePassword() {
  if (changePwd.newPwd !== changePwd.confirm) {
    ElMessage.warning('Passwords do not match')
    return
  }
  changingPwd.value = true
  changePwdStatus.value = null
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/change-password`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword: changePwd.current, newPassword: changePwd.newPwd }),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    changePwdStatus.value = { kind: 'ok', message: 'Password changed successfully' }
    ElMessage.success('MySQL root password changed')
    changePwd.current = ''
    changePwd.newPwd = ''
    changePwd.confirm = ''
  } catch (e) {
    changePwdStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(`Change password failed: ${errorMessage(e)}`)
  } finally {
    changingPwd.value = false
  }
}

async function resetPassword() {
  try {
    await ElMessageBox.confirm(
      'This will stop MySQL, start it in safe mode, reset ALL root@* accounts, then restart. Continue?',
      'Reset root password',
      { type: 'warning', confirmButtonText: 'Reset', confirmButtonClass: 'el-button--danger' }
    )
  } catch { return }

  resettingPwd.value = true
  resetPwdStatus.value = null
  try {
    const r = await fetch(`${daemonBaseUrl()}/api/plugins/mysql/reset-password`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: JSON.stringify({ newPassword: resetPwd.newPwd }),
    })
    if (!r.ok) {
      const err: { error?: string } = await r.json().catch(() => ({}))
      throw new Error(err.error || `HTTP ${r.status}`)
    }
    resetPwdStatus.value = { kind: 'ok', message: 'Root password reset successfully' }
    ElMessage.success('MySQL root password reset')
    resetPwd.newPwd = ''
  } catch (e) {
    resetPwdStatus.value = { kind: 'err', message: errorMessage(e) }
    ElMessage.error(`Reset failed: ${errorMessage(e)}`)
  } finally {
    resettingPwd.value = false
  }
}

onMounted(() => { /* services already loaded by daemon store poll */ })
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
.edit-card-header { padding: 14px 20px; background: var(--wdc-surface-2); border-bottom: 1px solid var(--wdc-border); display: flex; justify-content: space-between; align-items: center; }
.edit-card-title { font-size: 0.78rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: var(--wdc-text); }
.edit-card-hint { font-size: 0.75rem; color: var(--wdc-text-3); }
.edit-card-body { padding: 18px 20px; }
.hint { margin-top: 6px; font-size: 0.78rem; color: var(--wdc-text-3); }
.card-actions { display: flex; gap: 8px; align-items: center; margin-top: 12px; }
.save-status { font-size: 0.82rem; font-weight: 600; }
.save-status.ok { color: var(--wdc-status-running); }
.save-status.err { color: var(--wdc-status-error); }
.danger-card { border-color: var(--el-color-danger-light-5); }
.danger-header { background: color-mix(in srgb, var(--el-color-danger) 8%, var(--wdc-surface-2)); border-bottom-color: var(--el-color-danger-light-5); }
</style>
