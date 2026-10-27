<!--
  The last failed start/stop/restart of a service, kept until the next
  successful action (servicesStore.lastError). Names the cause when the
  message is one the OS or the server binary prints, and links to the fix.
-->
<template>
  <div v-if="message" class="svc-error" role="alert">
    <p class="svc-error-title">{{ t('servicesPage.lastError.title') }}</p>
    <pre class="svc-error-text mono">{{ message }}</pre>
    <p v-if="cause" class="svc-error-hint">{{ t(`servicesPage.cause.${cause}`) }}</p>
    <div class="svc-error-actions">
      <el-button size="small" @click="copy">{{ t('servicesPage.lastError.copy') }}</el-button>
      <el-button v-if="cause === 'port'" size="small" @click="router.push({ path: '/settings', query: { tab: 'ports' } })">
        {{ t('servicesPage.lastError.openPorts') }}
      </el-button>
      <el-button v-else-if="cause === 'config'" size="small" @click="router.push(`/service/${encodeURIComponent(serviceId)}/config`)">
        {{ t('servicesPage.fixConfig') }}
      </el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useServicesStore } from '../../stores/services'
import { cleanError, errorCause } from '../../utils/services'

const props = defineProps<{ serviceId: string }>()

const { t } = useI18n()
const router = useRouter()
const servicesStore = useServicesStore()

const message = computed(() => servicesStore.lastError[props.serviceId] ?? '')
const cause = computed(() => message.value ? errorCause(message.value) : null)

async function copy() {
  try {
    await navigator.clipboard.writeText(message.value)
    ElMessage.success(t('servicesPage.lastError.copied'))
  } catch (e) {
    ElMessage.error(cleanError(e))
  }
}
</script>

<style scoped>
.svc-error {
  display: grid;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-3);
  border: 1px solid var(--wdc-status-error);
  border-radius: var(--wdc-radius-md);
}
.svc-error-title { font-family: var(--wdc-font-display); font-weight: 500; color: var(--wdc-status-error); }
.svc-error-text {
  margin: 0;
  max-height: 160px;
  overflow: auto;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-size: 0.78rem;
  color: var(--wdc-text);
}
.svc-error-hint { color: var(--wdc-text-2); font-size: 0.87rem; line-height: 1.45; }
.svc-error-actions { display: flex; flex-wrap: wrap; gap: var(--wdc-space-2); }
.svc-error-actions :deep(.el-button + .el-button) { margin-left: 0; }
</style>
