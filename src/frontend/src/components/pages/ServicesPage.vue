<template>
  <div class="services-page">
    <div class="page-header">
      <div class="header-title-block">
        <h1 class="page-title">{{ t('servicesPage.title') }}</h1>
        <span class="page-subtitle">
          {{ t('servicesPage.summary', { running: runningCount, total: services.length }) }}
        </span>
      </div>
      <el-radio-group v-model="filter" size="small" :aria-label="t('servicesPage.filterLabel')">
        <el-radio-button value="all">{{ t('servicesPage.filter.all') }}</el-radio-button>
        <el-radio-button value="running">{{ t('servicesPage.filter.running') }}</el-radio-button>
        <el-radio-button value="problems">
          {{ t('servicesPage.filter.problems') }}<template v-if="problemCount"> ({{ problemCount }})</template>
        </el-radio-button>
      </el-radio-group>
    </div>

    <el-alert
      v-if="!daemonStore.connected"
      type="warning"
      :title="t('dashboard.connectingTitle')"
      :closable="false"
      show-icon
    />

    <el-empty
      v-else-if="visible.length === 0"
      :description="filter === 'problems' ? t('servicesPage.noProblems') : t('servicesPage.empty')"
      :image-size="64"
    />

    <div v-else class="svc-list">
      <div v-for="svc in visible" :key="svc.id" class="svc-row">
        <ServiceCard :service="svc" />

        <!-- A stopped or crashed service gets the tail of its own log and
             the two usual ways out, instead of a bare red badge. -->
        <div v-if="isProblem(svc)" class="svc-diagnose">
          <div class="diag-head">
            <span class="diag-title">{{ t('servicesPage.whyTitle') }}</span>
            <el-button size="small" text :loading="loadingLogs.has(svc.id)" @click="loadLogs(svc.id)">
              {{ logs[svc.id] ? t('common.refresh') : t('servicesPage.showLog') }}
            </el-button>
          </div>
          <pre v-if="logs[svc.id]?.length" class="diag-log mono">{{ logs[svc.id].join('\n') }}</pre>
          <p v-else-if="logs[svc.id]" class="diag-empty">{{ t('servicesPage.logEmpty') }}</p>
          <div class="diag-actions">
            <el-button size="small" type="primary" :loading="servicesStore.isBusy(svc.id)" @click="restart(svc)">
              {{ t('servicesPage.fixRestart') }}
            </el-button>
            <el-button size="small" @click="router.push(`/service/${encodeURIComponent(svc.id)}/config`)">
              {{ t('servicesPage.fixConfig') }}
            </el-button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import ServiceCard from '../shared/ServiceCard.vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { fetchServiceLogs } from '../../api/daemon'
import type { ServiceInfo } from '../../api/types'
import { errorMessage } from '../../utils/errors'

/**
 * All services in one place, in both UI modes. Previously the only list
 * was the Advanced dashboard grid; Simple mode had no way to see why a
 * service was down. Problem rows show the service's recent log and offer
 * restart or config editing.
 */
const { t } = useI18n()
const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()

const filter = ref<'all' | 'running' | 'problems'>('all')
const logs = ref<Record<string, string[]>>({})
const loadingLogs = ref<Set<string>>(new Set())

// state: 0=Stopped, 1=Starting, 2=Running, 3=Stopping, 4=Crashed, 5=Disabled
const services = computed(() => daemonStore.services as ServiceInfo[])
const runningCount = computed(() => services.value.filter(s => s.state === 2).length)
const isProblem = (s: ServiceInfo) => s.state === 4 || s.state === 0
const problemCount = computed(() => services.value.filter(isProblem).length)

const visible = computed(() => {
  const list = [...services.value].sort((a, b) =>
    Number(isProblem(b)) - Number(isProblem(a)) || (a.displayName || a.id).localeCompare(b.displayName || b.id))
  if (filter.value === 'running') return list.filter(s => s.state === 2)
  if (filter.value === 'problems') return list.filter(isProblem)
  return list
})

async function loadLogs(id: string) {
  loadingLogs.value = new Set(loadingLogs.value).add(id)
  try {
    const lines = await fetchServiceLogs(id, 30)
    logs.value = { ...logs.value, [id]: Array.isArray(lines) ? lines : [] }
  } catch (e) {
    ElMessage.error(t('toast.loadFailed', { err: errorMessage(e) }))
  } finally {
    const next = new Set(loadingLogs.value)
    next.delete(id)
    loadingLogs.value = next
  }
}

async function restart(svc: ServiceInfo) {
  try {
    await servicesStore.start(svc.id)
    ElMessage.success(t('toast.serviceStarted', { name: svc.displayName || svc.id }))
  } catch (e) {
    ElMessage.error(t('toast.startFailed', { err: errorMessage(e) }))
    void loadLogs(svc.id)
  }
}
</script>

<style scoped>
.services-page {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4);
  min-height: 100%;
  background: var(--wdc-bg);
}
/* Same header strip as Dashboard/Backups. */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
  padding: var(--wdc-space-4) var(--wdc-space-5);
  border-bottom: 1px solid var(--wdc-accent-glow);
  background: linear-gradient(180deg, var(--wdc-accent-dim), transparent);
}
.services-page > :not(.page-header) {
  margin-left: var(--wdc-space-5);
  margin-right: var(--wdc-space-5);
}
.svc-list { margin-bottom: var(--wdc-space-5); }
.header-title-block { display: flex; flex-direction: column; gap: var(--wdc-space-1); }
.page-title { margin: 0; font-size: 1.4rem; }
.page-subtitle { color: var(--wdc-text-3); }
.svc-list {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: var(--wdc-space-4);
}
.svc-row { display: flex; flex-direction: column; gap: var(--wdc-space-2); min-width: 0; }
.svc-diagnose {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  padding: var(--wdc-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-2);
}
.diag-head { display: flex; align-items: center; justify-content: space-between; gap: var(--wdc-space-2); }
.diag-title { font-weight: 600; color: var(--wdc-text); }
.diag-log {
  margin: 0;
  max-height: 200px;
  overflow: auto;
  font-size: 0.78rem;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  background: var(--wdc-surface-2);
  border-radius: var(--wdc-radius-sm);
  padding: var(--wdc-space-2);
  color: var(--wdc-text-2);
}
.diag-empty { margin: 0; color: var(--wdc-text-3); font-size: 0.85rem; }
.diag-actions { display: flex; gap: var(--wdc-space-2); flex-wrap: wrap; }
</style>
