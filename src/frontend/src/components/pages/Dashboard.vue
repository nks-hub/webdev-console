<template>
  <div class="dashboard-page">
    <!-- Page header -->
    <div class="flex items-center justify-between mb-6 px-6 pt-6">
      <div>
        <h1 class="text-xl font-bold text-white">Dashboard</h1>
        <p class="text-sm text-slate-400 mt-0.5">Local development environment status</p>
      </div>
      <div class="flex items-center gap-2">
        <el-button
          type="success"
          size="small"
          :loading="startingAll"
          :disabled="allRunning || !daemonStore.connected"
          @click="startAll"
        >
          Start All
        </el-button>
        <el-button
          type="danger"
          size="small"
          :loading="stoppingAll"
          :disabled="noneRunning || !daemonStore.connected"
          @click="stopAll"
        >
          Stop All
        </el-button>
        <el-button size="small" @click="router.push('/sites')">+ New Site</el-button>
      </div>
    </div>

    <!-- Offline state -->
    <div v-if="!daemonStore.connected" class="px-6">
      <el-alert
        type="warning"
        title="Daemon offline"
        description="Waiting for connection to NKS WDC daemon on port 5199..."
        :closable="false"
        show-icon
      />
    </div>

    <template v-else>
      <!-- Summary stats row -->
      <div class="grid grid-cols-4 gap-4 px-6 mb-6">
        <div class="stat-card">
          <div class="stat-value text-green-400">{{ runningCount }}</div>
          <div class="stat-label">Running</div>
        </div>
        <div class="stat-card">
          <div class="stat-value text-slate-400">{{ stoppedCount }}</div>
          <div class="stat-label">Stopped</div>
        </div>
        <div class="stat-card">
          <div class="stat-value text-blue-400">{{ sitesStore.sites.length }}</div>
          <div class="stat-label">Sites</div>
        </div>
        <div class="stat-card">
          <div class="stat-value text-purple-400">{{ totalCount }}</div>
          <div class="stat-label">Services</div>
        </div>
      </div>

      <!-- Service cards grid -->
      <div class="px-6 pb-6">
        <div class="services-grid">
          <ServiceCard
            v-for="service in services"
            :key="service.id"
            :service="service"
          />
        </div>

        <!-- Empty state -->
        <el-empty
          v-if="services.length === 0"
          description="No services registered. Check daemon configuration."
          :image-size="80"
        />
      </div>

      <!-- Quick links -->
      <div class="px-6 pb-6" v-if="sitesStore.sites.length > 0">
        <div class="section-title mb-3">Quick Links</div>
        <div class="flex flex-wrap gap-2">
          <a
            v-for="site in sitesStore.sites.slice(0, 8)"
            :key="site.domain"
            :href="`http${site.sslEnabled ? 's' : ''}://${site.domain}`"
            target="_blank"
            class="quick-link"
          >
            <span class="ssl-dot" :class="site.sslEnabled ? 'ssl-on' : 'ssl-off'" />
            {{ site.domain }}
          </a>
          <el-button
            v-if="sitesStore.sites.length > 8"
            size="small"
            text
            @click="router.push('/sites')"
          >
            +{{ sitesStore.sites.length - 8 }} more
          </el-button>
        </div>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { useSitesStore } from '../../stores/sites'
import ServiceCard from '../shared/ServiceCard.vue'

const router = useRouter()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()
const sitesStore = useSitesStore()

const startingAll = ref(false)
const stoppingAll = ref(false)

const services = computed(() => daemonStore.services)
const totalCount = computed(() => services.value.length)
const runningCount = computed(() => services.value.filter((s: any) => s.state === 2).length)
const stoppedCount = computed(() => services.value.filter((s: any) => s.state === 0).length)
const allRunning = computed(() => totalCount.value > 0 && runningCount.value === totalCount.value)
const noneRunning = computed(() => runningCount.value === 0)

onMounted(() => { void sitesStore.load() })

async function startAll() {
  startingAll.value = true
  try {
    await Promise.allSettled(
      services.value
        .filter((s: any) => s.state === 0)
        .map((s: any) => servicesStore.start(s.id))
    )
  } finally {
    startingAll.value = false
  }
}

async function stopAll() {
  stoppingAll.value = true
  try {
    await Promise.allSettled(
      services.value
        .filter((s: any) => s.state === 2)
        .map((s: any) => servicesStore.stop(s.id))
    )
  } finally {
    stoppingAll.value = false
  }
}
</script>

<style scoped>
.dashboard-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.stat-card {
  background: var(--wdc-surface);
  border: 1px solid var(--el-border-color);
  border-radius: 10px;
  padding: 16px 20px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.stat-value {
  font-size: 1.75rem;
  font-weight: 700;
  line-height: 1;
  font-variant-numeric: tabular-nums;
}

.stat-label {
  font-size: 0.78rem;
  color: var(--el-text-color-secondary);
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.services-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 16px;
}

.section-title {
  font-size: 0.78rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--el-text-color-secondary);
}

.quick-link {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  background: var(--wdc-surface);
  border: 1px solid var(--el-border-color);
  border-radius: 6px;
  font-size: 0.82rem;
  color: var(--el-text-color-regular);
  text-decoration: none;
  transition: border-color 0.15s, color 0.15s;
}

.quick-link:hover {
  border-color: var(--el-color-primary);
  color: var(--el-color-primary);
}

.ssl-dot {
  display: inline-block;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
}
.ssl-on  { background: var(--wdc-status-running); }
.ssl-off { background: var(--wdc-status-stopped); }
</style>
