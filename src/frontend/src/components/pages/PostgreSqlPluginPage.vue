<template>
  <div class="wdc-page">
    <ServicePageHead
      service-id="postgresql"
      plugin-id="nks.wdc.postgresql"
      :title="t('postgresPlugin.title')"
      :subtitle="t('postgresPlugin.subtitle')"
    />

    <el-tabs v-if="installed" v-model="activeTab">
      <el-tab-pane name="databases" :label="t('postgresPlugin.tabDatabases')">
        <section class="block">
          <div class="block-head">
            <h2 class="section-title">{{ t('postgresPlugin.databaseTools') }}</h2>
            <el-button v-if="running" size="small" text :loading="databasesLoading" @click="loadDatabases">
              {{ t('common.refresh') }}
            </el-button>
          </div>
          <p v-if="!running" class="hint">{{ t('postgresPlugin.needsRunning') }}</p>
          <template v-else>
            <el-alert v-if="databasesError" type="warning" :closable="false" show-icon :title="databasesError" />
            <ul v-if="postgresDatabases.length > 0" class="database-list">
              <li v-for="db in postgresDatabases" :key="db" class="database-row mono">{{ db }}</li>
            </ul>
            <p v-else-if="!databasesLoading && !databasesError" class="hint">{{ t('postgresPlugin.noDatabases') }}</p>
          </template>
        </section>

        <section class="block">
          <h2 class="section-title">{{ t('postgresPlugin.resetPassword') }}</h2>
          <p class="hint">{{ t('postgresPlugin.resetWarning') }}</p>
          <el-form label-width="180px" class="reset-form" @submit.prevent>
            <el-form-item :label="t('postgresPlugin.newPassword')">
              <el-input v-model="resetPasswordForm.newPassword" type="password" show-password />
            </el-form-item>
            <el-form-item :label="t('postgresPlugin.confirmPassword')">
              <el-input v-model="resetPasswordForm.confirm" type="password" show-password />
            </el-form-item>
          </el-form>
          <div class="card-actions">
            <el-button
              type="warning"
              :loading="resettingPassword"
              :disabled="!resetPasswordForm.newPassword || resetPasswordForm.newPassword !== resetPasswordForm.confirm"
              @click="resetPostgresPassword"
            >
              {{ t('postgresPlugin.resetPasswordBtn') }}
            </el-button>
            <span v-if="resetPasswordStatus" class="save-status" :class="resetPasswordStatus.kind">
              {{ resetPasswordStatus.message }}
            </span>
          </div>
        </section>
      </el-tab-pane>

      <el-tab-pane name="logs" :label="t('postgresPlugin.tabLogs')" lazy>
        <LogViewer service-id="postgresql" class="tab-log" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onActivated, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { useDaemonStore } from '../../stores/daemon'
import { daemonAuthHeaders as authHeaders, daemonBaseUrl } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import { stateOf } from '../../utils/services'
import LogViewer from '../shared/LogViewer.vue'
import ServicePageHead from '../shared/ServicePageHead.vue'

defineOptions({ name: 'PostgreSqlPluginPage' })

const daemonStore = useDaemonStore()
const { t } = useI18n()
const activeTab = ref<'databases' | 'logs'>('databases')
const postgresDatabases = ref<string[]>([])
const databasesLoading = ref(false)
const databasesError = ref('')
const resetPasswordForm = reactive({ newPassword: '', confirm: '' })
const resettingPassword = ref(false)
const resetPasswordStatus = ref<{ kind: 'ok' | 'err'; message: string } | null>(null)

const state = computed(() => stateOf(daemonStore.services.find(s => s.id === 'postgresql')))
const installed = computed(() => state.value !== 'notInstalled')
const running = computed(() => state.value === 'running')

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
    else if (!response.ok) databasesError.value = `HTTP ${response.status}`
  } catch (e) {
    postgresDatabases.value = []
    databasesError.value = errorMessage(e)
  } finally {
    databasesLoading.value = false
  }
}

async function resetPostgresPassword() {
  if (resetPasswordForm.newPassword !== resetPasswordForm.confirm) {
    ElMessage.warning(t('postgresPlugin.toast.passwordsDontMatch'))
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
    ElMessage.error(t('postgresPlugin.toast.resetFailed', { err: errorMessage(e) }))
  } finally {
    resettingPassword.value = false
  }
}

// The list needs a running server: load it when the page shows (also on
// first mount, inside <keep-alive>) and whenever PostgreSQL comes up.
onActivated(() => { if (running.value) void loadDatabases() })
watch(running, (isRunning) => { if (isRunning) void loadDatabases() })
</script>

<style scoped>
.block { display: grid; gap: var(--wdc-space-3); padding-bottom: var(--wdc-space-5); }
.block + .block { padding-top: var(--wdc-space-5); border-top: 1px solid var(--wdc-border); }
.block-head { display: flex; align-items: baseline; justify-content: space-between; gap: var(--wdc-space-3); }
.section-title { font-size: 1.1rem; }
.hint { margin: 0; font-size: 0.87rem; line-height: 1.5; color: var(--wdc-text-3); }
.tab-log { height: 460px; }
.database-list { margin: 0; padding: 0; list-style: none; border-top: 1px solid var(--wdc-border); max-width: 40rem; }
.database-row { padding: var(--wdc-space-2) 0; border-bottom: 1px solid var(--wdc-border); font-size: 0.86rem; overflow-wrap: anywhere; }
.reset-form { max-width: 34rem; }
.card-actions { display: flex; gap: var(--wdc-space-2); align-items: center; flex-wrap: wrap; }
.save-status { font-size: 0.82rem; font-weight: 600; overflow-wrap: anywhere; }
.save-status.ok { color: var(--wdc-status-running); }
.save-status.err { color: var(--wdc-status-error); }
</style>
