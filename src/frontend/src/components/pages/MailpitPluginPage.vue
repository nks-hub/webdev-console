<template>
  <div class="wdc-page">
    <ServicePageHead
      service-id="mailpit"
      plugin-id="nks.wdc.mailpit"
      :title="t('mailpitPlugin.title')"
      :subtitle="t('mailpitPlugin.subtitle')"
    >
      <template #actions="{ state }">
        <el-tooltip v-if="uiUrl" :content="t('mailpitPlugin.notRunning')" :disabled="state === 'running'" placement="bottom">
          <!-- span keeps the tooltip working while the button is disabled -->
          <span>
            <el-button :disabled="state !== 'running'" @click="openExternal(uiUrl)">{{ t('mailpitPlugin.openUi') }}</el-button>
          </span>
        </el-tooltip>
      </template>
    </ServicePageHead>

    <section v-if="installed" class="log-section">
      <h2 class="section-title">{{ t('mailpitPlugin.tabLogs') }}</h2>
      <LogViewer service-id="mailpit" />
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useDaemonStore } from '../../stores/daemon'
import { stateOf } from '../../utils/services'
import LogViewer from '../shared/LogViewer.vue'
import ServicePageHead from '../shared/ServicePageHead.vue'

defineOptions({ name: 'MailpitPluginPage' })

const { t } = useI18n()
const daemonStore = useDaemonStore()
const service = computed(() => daemonStore.services.find(s => s.id === 'mailpit'))
const installed = computed(() => stateOf(service.value) !== 'notInstalled')
// Web UI port as the daemon reads it from the Mailpit config; no link when unknown.
const uiUrl = computed(() => {
  const port = service.value?.ports?.http ?? service.value?.port
  return port ? `http://localhost:${port}` : ''
})

function openExternal(url: string) {
  if (window.electronAPI?.openExternal) void window.electronAPI.openExternal(url)
  else window.open(url, '_blank')
}
</script>

<style scoped>
.log-section { display: grid; gap: var(--wdc-space-3); }
.section-title { font-size: 1.1rem; }
.log-section :deep(.log-viewer) { height: 420px; }
</style>
