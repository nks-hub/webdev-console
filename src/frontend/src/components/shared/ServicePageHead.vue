<!--
  Head of a service's own page (Apache, PHP, MySQL, PostgreSQL, Mailpit,
  Redis): title, the state in one sentence, the facts the daemon reports
  (uptime, version, ports, PID), the lifecycle actions and auto-start. A
  service that is not installed gets "Install" instead of Start and nothing
  that needs a running binary.
-->
<template>
  <header class="page-head">
    <div>
      <h1 class="page-title">{{ title }}</h1>
      <p v-if="subtitle" class="page-sub">{{ subtitle }}</p>
    </div>
    <div class="head-actions">
      <slot name="actions" :state="state" />
      <el-button v-if="state === 'notInstalled'" type="primary" @click="install">{{ t('servicesPage.install') }}</el-button>
      <template v-else-if="state === 'running'">
        <el-button :loading="busy" @click="act('restart')">{{ t('servicesPage.restart') }}</el-button>
        <el-button type="danger" :loading="busy" @click="act('stop')">{{ t('servicesPage.stop') }}</el-button>
      </template>
      <el-button
        v-else-if="state !== 'starting' && state !== 'stopping'"
        type="primary"
        :loading="busy"
        :disabled="!daemonStore.connected"
        @click="act('start')"
      >{{ isProblemState(state) ? t('servicesPage.fixRestart') : t('servicesPage.start') }}</el-button>
    </div>
  </header>

  <p v-if="!daemonStore.connected" class="svc-sentence" role="status">{{ t('dashboard.connectingTitle') }}</p>
  <p v-else-if="!service" class="svc-sentence" role="status">{{ t('servicesPage.unknown', { name: title }) }}</p>
  <template v-else>
    <p class="svc-sentence" :class="`st-${state}`" role="status" aria-live="polite">
      {{ t(`servicesPage.explain.${stateKeyOf(service)}`, { name: shortName(service) }) }}
    </p>

    <ServiceErrorNote :service-id="serviceId" />

    <dl v-if="state !== 'notInstalled'" class="dl">
      <div v-if="state === 'running' && service.uptimeSeconds != null">
        <dt>{{ t('servicesPage.facts.uptime') }}</dt><dd class="mono">{{ formatDuration(service.uptimeSeconds, locale) }}</dd>
      </div>
      <div v-if="service.version"><dt>{{ t('servicesPage.facts.version') }}</dt><dd class="mono">{{ service.version }}</dd></div>
      <div v-if="portsLabel(service)"><dt>{{ t('servicesPage.facts.ports') }}</dt><dd class="mono">{{ portsLabel(service) }}</dd></div>
      <div v-if="state === 'running' && service.pid"><dt>PID</dt><dd class="mono">{{ service.pid }}</dd></div>
      <slot name="facts" />
    </dl>

    <PluginAutostartSwitch v-if="state !== 'notInstalled'" :plugin-id="pluginId" />
  </template>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore, type ServiceOp } from '../../stores/services'
import { formatDuration } from '../../utils/format'
import { shortServiceName as shortName } from '../../utils/serviceState'
import { installRouteOf, isProblemState, portsLabel, stateKeyOf, stateOf } from '../../utils/services'
import PluginAutostartSwitch from './PluginAutostartSwitch.vue'
import ServiceErrorNote from './ServiceErrorNote.vue'

const props = defineProps<{
  serviceId: string
  pluginId: string
  title: string
  subtitle?: string
}>()

const { t, locale } = useI18n()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()

const service = computed(() => daemonStore.services.find(s => s.id === props.serviceId))
const state = computed(() => stateOf(service.value))
const busy = computed(() => servicesStore.isBusy(props.serviceId))

function act(op: ServiceOp) {
  void servicesStore.act(props.serviceId, op, service.value ? shortName(service.value) : props.title)
}

function install() {
  if (service.value) void router.push(installRouteOf(service.value))
}
</script>

<style scoped>
.page-head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-4);
}
.page-title { font-size: 1.728rem; line-height: 1.1; letter-spacing: -0.028em; }
.page-sub { margin-top: var(--wdc-space-2); color: var(--wdc-text-2); }
.head-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.head-actions :deep(.el-button + .el-button) { margin-left: 0; }

.svc-sentence { color: var(--wdc-text); line-height: 1.45; }
.svc-sentence.st-crashed, .svc-sentence.st-disabled { color: var(--wdc-status-error); }

.dl { margin: 0; display: grid; max-width: 40rem; }
.dl > div, .dl :slotted(div) {
  display: grid;
  grid-template-columns: 9rem minmax(0, 1fr);
  gap: var(--wdc-space-3);
  padding: var(--wdc-space-2) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.dl dt, .dl :slotted(dt) { font-family: var(--wdc-font-display); font-weight: 500; color: var(--wdc-text-2); }
.dl dd, .dl :slotted(dd) { margin: 0; }
</style>
