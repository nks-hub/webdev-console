<template>
  <div class="dashboard">
    <div class="dashboard-header">
      <h2>Dashboard</h2>
      <div class="quick-actions">
        <el-button type="success" size="small" @click="startAll">Start All</el-button>
        <el-button type="danger" size="small" @click="stopAll">Stop All</el-button>
        <el-button size="small" @click="router.push('/sites')">New Site</el-button>
      </div>
    </div>

    <el-empty v-if="!daemonStore.connected" description="Daemon offline — waiting for connection..." />

    <template v-else>
      <div class="service-grid">
        <ServiceCard
          v-for="service in services"
          :key="service.id"
          :service="service"
        />
      </div>

      <el-divider content-position="left">Summary</el-divider>

      <div class="summary-row">
        <el-statistic title="Total Sites" :value="sitesStore.sites.length" />
        <el-statistic
          title="Active Services"
          :value="runningCount"
          :suffix="`/ ${totalCount}`"
        />
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import ServiceCard from '../shared/ServiceCard.vue'

const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()

const services = computed(() => daemonStore.status?.services ?? [])
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter(s => s.status === 'running').length)

onMounted(() => { void sitesStore.load() })

async function startAll() {
  await Promise.allSettled(
    services.value
      .filter(s => s.status === 'stopped')
      .map(s => servicesStore.start(s.id))
  )
}

async function stopAll() {
  await Promise.allSettled(
    services.value
      .filter(s => s.status === 'running')
      .map(s => servicesStore.stop(s.id))
  )
}
</script>

<style scoped>
.dashboard { padding: 24px; display: flex; flex-direction: column; gap: 20px; }
.dashboard-header { display: flex; align-items: center; justify-content: space-between; }
.dashboard-header h2 { margin: 0; font-size: 1.2rem; }
.quick-actions { display: flex; gap: 8px; }
.service-grid { display: flex; flex-wrap: wrap; gap: 16px; }
.summary-row { display: flex; gap: 32px; }
</style>
