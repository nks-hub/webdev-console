<template>
  <div class="wdc-page">
    <ServicePageHead
      service-id="redis"
      plugin-id="nks.wdc.redis"
      :title="t('redisPlugin.title')"
      :subtitle="t('redisPlugin.subtitle')"
    />

    <section v-if="installed" class="log-section">
      <h2 class="section-title">{{ t('redisPlugin.tabLogs') }}</h2>
      <LogViewer service-id="redis" />
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

defineOptions({ name: 'RedisPluginPage' })

const { t } = useI18n()
const daemonStore = useDaemonStore()
const installed = computed(() => stateOf(daemonStore.services.find(s => s.id === 'redis')) !== 'notInstalled')
</script>

<style scoped>
.log-section { display: grid; gap: var(--wdc-space-3); }
.section-title { font-size: 1.1rem; }
.log-section :deep(.log-viewer) { height: 420px; }
</style>
