<template>
  <nav class="sidebar" :class="{ collapsed }">
    <div class="sidebar-top">
      <div v-if="!collapsed" class="workspace-card">
        <div class="workspace-mark">NW</div>
        <div class="workspace-copy">
          <span class="workspace-title">NKS WebDev Console</span>
          <span class="workspace-subtitle">{{ runningCount }}/{{ services.length }} services online</span>
        </div>
      </div>

      <div class="collapse-toggle" @click="toggleCollapse" :title="collapsed ? 'Expand sidebar' : 'Collapse sidebar'">
        <el-icon :size="16"><Fold v-if="!collapsed" /><Expand v-else /></el-icon>
      </div>
    </div>

    <div class="nav-cluster">
      <div class="nav-item" :class="{ active: isActive('/dashboard') }" @click="navigate('/dashboard')">
        <span class="nav-icon-shell"><el-icon :size="18"><House /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">Overview</span>
      </div>
      <div class="nav-item sites-btn" :class="{ active: isActive('/sites') }" @click="navigate('/sites')">
        <span class="nav-icon-shell"><el-icon :size="18"><Link /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">Sites</span>
      </div>
    </div>

    <div class="sidebar-section">
      <div class="section-label" v-if="!collapsed">
        <span>Web Server</span>
        <span class="section-count">{{ webServices.length }}</span>
      </div>
      <template v-for="svc in webServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Running' : 'Stopped' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="langServices.length">
      <div class="section-label" v-if="!collapsed">
        <span>Languages</span>
        <span class="section-count">{{ langServices.length }}</span>
      </div>
      <template v-for="svc in langServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Ready' : 'Idle' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="dbServices.length">
      <div class="section-label" v-if="!collapsed">
        <span>Database</span>
        <span class="section-count">{{ dbServices.length }}</span>
      </div>
      <template v-for="svc in dbServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Running' : 'Offline' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="cacheServices.length">
      <div class="section-label" v-if="!collapsed">
        <span>Cache &amp; Mail</span>
        <span class="section-count">{{ cacheServices.length }}</span>
      </div>
      <template v-for="svc in cacheServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`), running: svc.state === 2 }" @click="openService(svc.id)">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <ServiceIcon :service="svc.id" :active="svc.state === 2" />
          </el-tooltip>
          <div class="svc-copy">
            <span class="svc-name">{{ shortName(svc) }}</span>
            <span class="svc-meta">{{ svc.state === 2 ? 'Running' : 'Standby' }}</span>
          </div>
          <span class="svc-led" :class="{ on: svc.state === 2 }" />
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @click.stop
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-spacer" />

    <div class="sidebar-bottom">
      <div class="nav-item" :class="{ active: isActive('/databases') }" @click="navigate('/databases')">
        <span class="nav-icon-shell"><el-icon :size="18"><Coin /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">Databases</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/ssl') }" @click="navigate('/ssl')">
        <span class="nav-icon-shell"><el-icon :size="18"><Lock /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">SSL</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/php') }" @click="navigate('/php')">
        <span class="nav-icon-shell"><el-icon :size="18"><Cpu /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">PHP</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/binaries') }" @click="navigate('/binaries')">
        <span class="nav-icon-shell"><el-icon :size="18"><Download /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">Binaries</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/plugins') }" @click="navigate('/plugins')">
        <span class="nav-icon-shell"><el-icon :size="18"><Box /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">Plugins</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/settings') }" @click="navigate('/settings')">
        <span class="nav-icon-shell"><el-icon :size="18"><Setting /></el-icon></span>
        <span class="nav-label" v-if="!collapsed">Settings</span>
      </div>
    </div>
  </nav>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { Link, Download, Box, Setting, Coin, Lock, Cpu, Fold, Expand, House } from '@element-plus/icons-vue'
import ServiceIcon from '../shared/ServiceIcon.vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { ElMessage } from 'element-plus'

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()

const collapsed = ref(localStorage.getItem('wdc-sidebar-collapsed') === 'true')

function toggleCollapse() {
  collapsed.value = !collapsed.value
  localStorage.setItem('wdc-sidebar-collapsed', String(collapsed.value))
}

const services = computed(() => daemonStore.services as any[])
const runningCount = computed(() => services.value.filter(s => s.state === 2).length)

const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
  'Mailpit': 'Mailpit',
}
function shortName(svc: any): string {
  return SHORT_NAMES[svc.displayName] || svc.displayName || svc.id
}

const SERVICE_CATEGORIES: Record<string, string> = {
  apache: 'web', nginx: 'web', caddy: 'web',
  php: 'lang',
  mysql: 'db', mariadb: 'db', postgresql: 'db', mongodb: 'db',
  redis: 'cache', memcached: 'cache', mailpit: 'cache',
}

const webServices = computed(() => services.value.filter(s => SERVICE_CATEGORIES[s.id] === 'web'))
const langServices = computed(() => services.value.filter(s => SERVICE_CATEGORIES[s.id] === 'lang'))
const dbServices = computed(() => services.value.filter(s => SERVICE_CATEGORIES[s.id] === 'db'))
const cacheServices = computed(() => services.value.filter(s =>
  SERVICE_CATEGORIES[s.id] === 'cache' || !SERVICE_CATEGORIES[s.id]))

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}

function navigate(path: string) {
  if (route.path === path) {
    void router.replace({ path, query: {} })
  } else {
    void router.push(path)
  }
}

function openService(id: string) {
  void router.push(`/service/${id}/config`)
}

async function toggleSvc(svc: any) {
  const name = svc.displayName || svc.id
  try {
    if (svc.state === 2) {
      await servicesStore.stop(svc.id)
      ElMessage.success(`${name} stopped`)
    } else {
      await servicesStore.start(svc.id)
      ElMessage.success(`${name} started`)
    }
  } catch (err: any) {
    ElMessage.error(`${name}: ${err.message}`)
  }
}
</script>

<style scoped>
.sidebar {
  width: 256px;
  display: flex;
  flex-direction: column;
  background:
    radial-gradient(circle at top left, rgba(86, 194, 255, 0.14), transparent 26%),
    linear-gradient(180deg, rgba(255, 255, 255, 0.04), transparent 26%),
    var(--wdc-surface);
  border-right: 1px solid rgba(255, 255, 255, 0.1);
  flex-shrink: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 12px 10px 10px;
}

.sidebar-top {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-bottom: 12px;
}

.workspace-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 12px;
  border-radius: 16px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background:
    linear-gradient(145deg, rgba(86, 194, 255, 0.14), rgba(124, 255, 165, 0.05)),
    rgba(255, 255, 255, 0.03);
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.05);
}

.workspace-mark {
  width: 38px;
  height: 38px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 12px;
  background: linear-gradient(135deg, var(--wdc-accent), var(--wdc-accent-2));
  color: #081017;
  font-size: 0.82rem;
  font-weight: 800;
  letter-spacing: 0.08em;
  box-shadow: 0 8px 18px rgba(86, 194, 255, 0.24);
}

.workspace-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.workspace-title {
  color: var(--wdc-text);
  font-size: 0.88rem;
  font-weight: 700;
  letter-spacing: 0.01em;
}

.workspace-subtitle {
  color: var(--wdc-text-3);
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.09em;
}

.nav-cluster {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 10px;
}

.sites-btn {
  font-weight: 700;
}

.sidebar-section {
  margin-bottom: 8px;
}

.section-label {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 0.74rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--wdc-text-3);
  padding: 12px 10px 8px;
}

.section-count {
  min-width: 20px;
  height: 20px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  color: var(--wdc-text-2);
  font-size: 0.66rem;
  letter-spacing: 0;
}

.service-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 14px;
  transition: background 0.1s, border-color 0.1s;
  min-height: 48px;
  border: 1px solid transparent;
  cursor: pointer;
}

.service-item:hover {
  background: rgba(255, 255, 255, 0.04);
  border-color: rgba(255, 255, 255, 0.08);
}

.service-item.active {
  background: linear-gradient(180deg, rgba(86, 194, 255, 0.14), rgba(86, 194, 255, 0.06));
  border-color: rgba(86, 194, 255, 0.28);
}

.svc-copy {
  min-width: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.svc-name {
  font-size: 0.92rem;
  font-weight: 600;
  color: var(--wdc-text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.svc-meta {
  color: var(--wdc-text-3);
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}

.svc-led {
  width: 8px;
  height: 8px;
  flex-shrink: 0;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.18);
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.12);
}

.svc-led.on {
  background: var(--wdc-status-running);
  box-shadow: 0 0 0 5px rgba(34, 197, 94, 0.12);
}

.nav-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 11px 12px;
  border-radius: 14px;
  cursor: pointer;
  color: var(--wdc-text-2);
  font-size: 0.92rem;
  font-weight: 600;
  transition: all 0.1s;
  border: 1px solid transparent;
}

.nav-item:hover {
  background: rgba(255, 255, 255, 0.04);
  color: var(--wdc-text);
  border-color: rgba(255, 255, 255, 0.06);
}

.nav-item.active {
  background: linear-gradient(180deg, rgba(86, 194, 255, 0.16), rgba(86, 194, 255, 0.08));
  color: var(--wdc-text);
  border-color: rgba(86, 194, 255, 0.24);
}

.nav-label {
  flex: 1;
  white-space: nowrap;
}

.nav-icon-shell {
  width: 30px;
  height: 30px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.06);
}

.collapse-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 6px;
  margin-bottom: 4px;
  cursor: pointer;
  color: var(--wdc-text-3);
  border-radius: 12px;
  transition: all 0.12s;
  align-self: flex-end;
}

.collapse-toggle:hover {
  color: var(--wdc-text);
  background: rgba(255, 255, 255, 0.05);
}

.sidebar.collapsed {
  width: 64px;
  padding: 10px 6px;
}

.sidebar.collapsed .nav-item {
  justify-content: center;
  padding: 10px 0;
}

.sidebar.collapsed .service-item {
  justify-content: center;
  padding: 9px 0;
  gap: 0;
}

.sidebar.collapsed .service-item .svc-copy,
.sidebar.collapsed .service-item .svc-led,
.sidebar.collapsed .service-item .el-switch,
.sidebar.collapsed .workspace-card,
.sidebar.collapsed .nav-label {
  display: none;
}

.sidebar.collapsed .collapse-toggle {
  align-self: center;
}

.sidebar-spacer {
  flex: 1;
}

.sidebar-bottom {
  border-top: 1px solid rgba(255, 255, 255, 0.08);
  padding-top: 8px;
  margin-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
</style>
