<template>
  <div class="cf-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('mailpitPlugin.title') }}</h1>
        <span class="page-subtitle">{{ $t('mailpitPlugin.subtitle') }}</span>
      </div>
      <div class="header-actions">
        <el-button size="small" type="primary" @click="openUi">
          <el-icon><Link /></el-icon>
          {{ $t('mailpitPlugin.openUi') }}
        </el-button>
        <el-button
          size="small"
          :type="serviceRunning ? 'danger' : 'success'"
          :loading="toggling"
          :disabled="!daemonStore.connected"
          @click="toggleService"
        >
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} Mailpit
        </el-button>
      </div>
    </div>
    <div class="page-autostart-row">
      <PluginAutostartSwitch plugin-id="nks.wdc.mailpit" />
    </div>

    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'">
          <CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else />
        </el-icon>
        <div class="status-body">
          <div class="status-title">{{ serviceRunning ? $t('common.running') : $t('common.stopped') }}</div>
          <div class="status-meta">Mailpit</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Message /></el-icon>
        <div class="status-body">
          <div class="status-title">SMTP: {{ smtpPort }}</div>
          <div class="status-meta">HTTP: {{ httpPort }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><ChatDotRound /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('mailpitPlugin.messages') }}: —</div>
          <div class="status-meta">{{ $t('mailpitPlugin.messagesMeta') }}</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- Overview -->
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('mailpitPlugin.tabOverview') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mailpitPlugin.tabOverview') }}</span>
            </header>
            <div class="edit-card-body">
              <el-descriptions :column="2" border size="small">
                <el-descriptions-item :label="$t('mailpitPlugin.status')">
                  <el-tag :type="serviceRunning ? 'success' : 'info'" size="small" effect="dark">
                    {{ serviceRunning ? $t('common.running') : $t('common.stopped') }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('mailpitPlugin.smtpPort')">{{ smtpPort }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mailpitPlugin.httpPort')">{{ httpPort }}</el-descriptions-item>
                <el-descriptions-item :label="$t('mailpitPlugin.messages')">—</el-descriptions-item>
              </el-descriptions>
              <div class="card-actions" style="margin-top: 16px">
                <el-button type="primary" @click="openUi">
                  <el-icon><Link /></el-icon>
                  {{ $t('mailpitPlugin.openUi') }} (http://localhost:{{ httpPort }})
                </el-button>
              </div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Config -->
      <el-tab-pane name="config">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('mailpitPlugin.tabConfig') }}</span>
        </template>
        <div class="tab-content">
          <el-alert
            type="info"
            :closable="false"
            show-icon
            :title="$t('mailpitPlugin.configPending')"
            style="margin-bottom: 16px"
          />
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mailpitPlugin.configParams') }}</span>
            </header>
            <div class="edit-card-body">
              <el-form label-width="200px" size="default">
                <el-form-item :label="$t('mailpitPlugin.smtpPort')">
                  <el-input-number :model-value="smtpPort" disabled style="width: 140px" />
                </el-form-item>
                <el-form-item :label="$t('mailpitPlugin.httpPort')">
                  <el-input-number :model-value="httpPort" disabled style="width: 140px" />
                </el-form-item>
                <el-form-item :label="$t('mailpitPlugin.maxMessages')">
                  <el-input-number :model-value="500" disabled style="width: 140px" />
                </el-form-item>
              </el-form>
              <div class="hint">{{ $t('mailpitPlugin.configPendingHint') }}</div>
            </div>
          </section>
        </div>
      </el-tab-pane>

      <!-- Logs -->
      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('mailpitPlugin.tabLogs') }}</span>
        </template>
        <div class="tab-content">
          <section class="edit-card">
            <header class="edit-card-header">
              <span class="edit-card-title">{{ $t('mailpitPlugin.tabLogs') }}</span>
            </header>
            <div class="edit-card-body" style="padding: 0">
              <LogViewer :service-id="'mailpit'" />
            </div>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { CircleCheckFilled, CircleClose, Message, ChatDotRound, Monitor, Setting, Document, Link } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { startService, stopService } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'

defineOptions({ name: 'MailpitPluginPage' })

const daemonStore = useDaemonStore()
const activeTab = ref<'overview' | 'config' | 'logs'>('overview')
const toggling = ref(false)

const serviceInfo = computed(() => daemonStore.services.find(s => s.id === 'mailpit'))
const serviceRunning = computed(() => serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running')
const smtpPort = computed(() => (serviceInfo.value as { smtpPort?: number; port?: number } | undefined)?.smtpPort ?? 1025)
const httpPort = computed(() => (serviceInfo.value as { httpPort?: number } | undefined)?.httpPort ?? 8025)

async function toggleService() {
  toggling.value = true
  try {
    if (serviceRunning.value) await stopService('mailpit')
    else await startService('mailpit')
  } catch (e) {
    ElMessage.error(`${serviceRunning.value ? 'Stop' : 'Start'} failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}

function openUi() {
  window.open(`http://localhost:${httpPort.value}`, '_blank')
}
</script>

<style scoped>
.cf-page { min-height: 100%; background: var(--wdc-bg); padding: 0; }
.page-header { display: flex; align-items: center; justify-content: space-between; padding: 20px 24px 14px; border-bottom: 1px solid var(--wdc-border); }
.page-autostart-row { padding: 10px 24px 0; max-width: 720px; }
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
.card-actions { display: flex; gap: 8px; align-items: center; }
</style>
