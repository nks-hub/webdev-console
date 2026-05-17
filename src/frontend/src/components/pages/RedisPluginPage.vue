<template>
  <div class="cf-page">
    <div class="page-header">
      <div class="header-left">
        <h1 class="page-title">{{ $t('redisPlugin.title') }}</h1>
        <span class="page-subtitle">{{ $t('redisPlugin.subtitle') }}</span>
      </div>
      <div class="header-actions">
        <el-button
          size="small"
          :type="serviceRunning ? 'danger' : 'success'"
          :loading="toggling"
          :disabled="!daemonStore.connected"
          @click="toggleService"
        >
          {{ serviceRunning ? $t('common.stop') : $t('common.run') }} Redis
        </el-button>
      </div>
    </div>
    <div class="page-autostart-row">
      <PluginAutostartSwitch plugin-id="nks.wdc.redis" />
    </div>

    <div class="status-strip">
      <div class="status-card" :class="{ 'status-active': serviceRunning }">
        <el-icon class="status-icon" :class="serviceRunning ? 'icon-running' : 'icon-stopped'">
          <CircleCheckFilled v-if="serviceRunning" /><CircleClose v-else />
        </el-icon>
        <div class="status-body">
          <div class="status-title">{{ serviceRunning ? $t('common.running') : $t('common.stopped') }}</div>
          <div class="status-meta">Redis</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><Connection /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('redisPlugin.port') }}: {{ redisPort }}</div>
          <div class="status-meta">{{ serviceInfo?.version || $t('redisPlugin.versionUnknown') }}</div>
        </div>
      </div>
      <div class="status-card">
        <el-icon class="status-icon"><DataLine /></el-icon>
        <div class="status-body">
          <div class="status-title">{{ $t('redisPlugin.usedMemory') }}: —</div>
          <div class="status-meta">{{ $t('redisPlugin.clients') }}: —</div>
        </div>
      </div>
    </div>

    <el-tabs v-model="activeTab" class="cf-tabs">
      <!-- Overview -->
      <el-tab-pane name="overview">
        <template #label>
          <span class="tab-label"><el-icon><Monitor /></el-icon> {{ $t('redisPlugin.tabOverview') }}</span>
        </template>
        <div class="tab-content">
          <EditCard :title="$t('redisPlugin.tabOverview')">
            <el-descriptions :column="2" border size="small">
                <el-descriptions-item :label="$t('redisPlugin.status')">
                  <el-tag :type="serviceRunning ? 'success' : 'info'" size="small" effect="dark">
                    {{ serviceRunning ? $t('common.running') : $t('common.stopped') }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item :label="$t('redisPlugin.version')">{{ serviceInfo?.version || '—' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('redisPlugin.port')">{{ redisPort }}</el-descriptions-item>
                <el-descriptions-item :label="$t('redisPlugin.pid')">{{ serviceInfo?.pid ?? '—' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('redisPlugin.usedMemory')">—</el-descriptions-item>
                <el-descriptions-item :label="$t('redisPlugin.clients')">—</el-descriptions-item>
            </el-descriptions>
          </EditCard>
        </div>
      </el-tab-pane>

      <!-- Config -->
      <el-tab-pane name="config">
        <template #label>
          <span class="tab-label"><el-icon><Setting /></el-icon> {{ $t('redisPlugin.tabConfig') }}</span>
        </template>
        <div class="tab-content">
          <el-alert
            type="info"
            :closable="false"
            show-icon
            :title="$t('redisPlugin.configPending')"
            style="margin-bottom: 16px"
          />
          <EditCard :title="$t('redisPlugin.configParams')">
            <el-form label-width="200px" size="default">
                <el-form-item label="port">
                  <el-input-number :model-value="redisPort" disabled style="width: 140px" />
                </el-form-item>
                <el-form-item label="maxmemory">
                  <el-input model-value="0" disabled style="width: 180px" />
                  <span class="hint-inline">0 = no limit</span>
                </el-form-item>
                <el-form-item label="maxmemory-policy">
                  <el-select model-value="noeviction" disabled style="width: 180px">
                    <el-option label="noeviction" value="noeviction" />
                    <el-option label="allkeys-lru" value="allkeys-lru" />
                    <el-option label="volatile-lru" value="volatile-lru" />
                  </el-select>
                </el-form-item>
              <el-form-item label="appendonly">
                <el-switch :model-value="false" disabled />
              </el-form-item>
            </el-form>
            <div class="hint">{{ $t('redisPlugin.configPendingHint') }}</div>
          </EditCard>
        </div>
      </el-tab-pane>

      <!-- Logs -->
      <el-tab-pane name="logs">
        <template #label>
          <span class="tab-label"><el-icon><Document /></el-icon> {{ $t('redisPlugin.tabLogs') }}</span>
        </template>
        <div class="tab-content">
          <EditCard :title="$t('redisPlugin.tabLogs')" flush-body>
            <LogViewer :service-id="'redis'" />
          </EditCard>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { CircleCheckFilled, CircleClose, Connection, Monitor, Setting, Document, DataLine } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useDaemonStore } from '../../stores/daemon'
import { startService, stopService } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'
import LogViewer from '../shared/LogViewer.vue'
import PluginAutostartSwitch from '../shared/PluginAutostartSwitch.vue'
import EditCard from '../shared/EditCard.vue'

defineOptions({ name: 'RedisPluginPage' })

const daemonStore = useDaemonStore()
const activeTab = ref<'overview' | 'config' | 'logs'>('overview')
const toggling = ref(false)

const serviceInfo = computed(() => daemonStore.services.find(s => s.id === 'redis'))
const serviceRunning = computed(() => serviceInfo.value?.state === 2 || serviceInfo.value?.status === 'running')
const redisPort = computed(() => (serviceInfo.value as { port?: number } | undefined)?.port ?? 6379)

async function toggleService() {
  toggling.value = true
  try {
    if (serviceRunning.value) await stopService('redis')
    else await startService('redis')
  } catch (e) {
    ElMessage.error(`${serviceRunning.value ? 'Stop' : 'Start'} failed: ${errorMessage(e)}`)
  } finally {
    toggling.value = false
  }
}
</script>

<style scoped>
.cf-page { min-height: 100%; background: transparent; padding: 0; }
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px;
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}
.page-autostart-row { padding: 10px 24px 0; width: 100%; }
.header-left { display: flex; flex-direction: column; gap: 2px; }
.page-title { font-size: 1.6rem; font-weight: 800; color: var(--wdc-text); margin: 0; letter-spacing: -0.02em; }
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
/* .edit-card CSS lives in EditCard shared primitive (plan §6). */
.hint { margin-top: 6px; font-size: 0.78rem; color: var(--wdc-text-3); }
.hint-inline { margin-left: 8px; font-size: 0.82rem; color: var(--wdc-text-3); }
</style>
