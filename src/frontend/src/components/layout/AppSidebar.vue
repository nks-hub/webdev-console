<template>
  <nav class="sidebar">
    <!-- Sites button -->
    <div class="nav-item sites-btn" :class="{ active: isActive('/sites') }" @click="navigate('/sites')">
      <el-icon :size="18"><Link /></el-icon>
      <span class="nav-label">Sites</span>
    </div>

    <!-- Service categories -->
    <div class="sidebar-section">
      <div class="section-label">Web Server</div>
      <template v-for="svc in webServices" :key="svc.id">
        <div class="service-item" :class="{ active: isActive(`/service/${svc.id}`) }">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <span class="svc-dot" :class="svc.state === 2 ? 'dot-on' : 'dot-off'" />
          </el-tooltip>
          <span class="svc-name" @click="navigate(`/service/${svc.id}`)">{{ shortName(svc) }}</span>
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="langServices.length">
      <div class="section-label">Languages</div>
      <template v-for="svc in langServices" :key="svc.id">
        <div class="service-item">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <span class="svc-dot" :class="svc.state === 2 ? 'dot-on' : 'dot-off'" />
          </el-tooltip>
          <span class="svc-name" @click="navigate(`/service/${svc.id}`)">{{ shortName(svc) }}</span>
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="dbServices.length">
      <div class="section-label">Database</div>
      <template v-for="svc in dbServices" :key="svc.id">
        <div class="service-item">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <span class="svc-dot" :class="svc.state === 2 ? 'dot-on' : 'dot-off'" />
          </el-tooltip>
          <span class="svc-name" @click="navigate(`/service/${svc.id}`)">{{ shortName(svc) }}</span>
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-section" v-if="cacheServices.length">
      <div class="section-label">Cache &amp; Mail</div>
      <template v-for="svc in cacheServices" :key="svc.id">
        <div class="service-item">
          <el-tooltip :content="svc.state === 2 ? 'Running' : 'Stopped'" placement="right" :show-after="500">
            <span class="svc-dot" :class="svc.state === 2 ? 'dot-on' : 'dot-off'" />
          </el-tooltip>
          <span class="svc-name" @click="navigate(`/service/${svc.id}`)">{{ shortName(svc) }}</span>
          <el-switch
            :model-value="svc.state === 2"
            :loading="servicesStore.isBusy(svc.id)"
            size="small"
            @change="toggleSvc(svc)"
          />
        </div>
      </template>
    </div>

    <div class="sidebar-spacer" />

    <!-- Bottom items -->
    <div class="sidebar-bottom">
      <div class="nav-item" :class="{ active: isActive('/databases') }" @click="navigate('/databases')">
        <el-icon :size="16"><Coin /></el-icon>
        <span class="nav-label">Databases</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/ssl') }" @click="navigate('/ssl')">
        <el-icon :size="16"><Lock /></el-icon>
        <span class="nav-label">SSL</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/php') }" @click="navigate('/php')">
        <el-icon :size="16"><Cpu /></el-icon>
        <span class="nav-label">PHP</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/binaries') }" @click="navigate('/binaries')">
        <el-icon :size="16"><Download /></el-icon>
        <span class="nav-label">Binaries</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/plugins') }" @click="navigate('/plugins')">
        <el-icon :size="16"><Box /></el-icon>
        <span class="nav-label">Plugins</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/settings') }" @click="navigate('/settings')">
        <el-icon :size="16"><Setting /></el-icon>
        <span class="nav-label">Settings</span>
      </div>
    </div>
  </nav>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { Link, Download, Box, Setting, Coin, Lock, Cpu } from '@element-plus/icons-vue'
import { useDaemonStore } from '../../stores/daemon'
import { useServicesStore } from '../../stores/services'
import { ElMessage } from 'element-plus'

const router = useRouter()
const route = useRoute()
const daemonStore = useDaemonStore()
const servicesStore = useServicesStore()

const services = computed(() => daemonStore.services as any[])

// Short names for sidebar display
const SHORT_NAMES: Record<string, string> = {
  'Apache HTTP Server': 'Apache',
  'PHP (Multi-version)': 'PHP',
  'Mailpit': 'Mailpit',
}
function shortName(svc: any): string {
  return SHORT_NAMES[svc.displayName] || svc.displayName || svc.id
}

// Categorize services like FlyEnv
const SERVICE_CATEGORIES: Record<string, string> = {
  apache: 'web', nginx: 'web',
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
  void router.push(path)
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
  width: 210px;
  display: flex;
  flex-direction: column;
  background: var(--wdc-surface);
  border-right: 1px solid var(--wdc-border);
  flex-shrink: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 10px;
}

/* Sites button — prominent */
.sites-btn {
  margin-bottom: 8px;
  font-weight: 600;
}

.sidebar-section {
  margin-bottom: 4px;
}

.section-label {
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
  padding: 12px 8px 5px;
}

/* Service item with toggle */
.service-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border-radius: var(--wdc-radius-sm);
  transition: background 0.1s;
}
.service-item:hover {
  background: var(--wdc-hover);
}

.svc-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}
.dot-on { background: var(--wdc-status-running); box-shadow: 0 0 4px rgba(34, 197, 94, 0.4); }
.dot-off { background: var(--wdc-status-stopped); }

.svc-name {
  flex: 1;
  font-size: 0.85rem;
  color: var(--wdc-text);
  cursor: pointer;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.svc-name:hover {
  color: var(--wdc-accent);
}

/* Navigation items */
.nav-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 10px;
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
  color: var(--wdc-text-2);
  font-size: 0.88rem;
  font-weight: 500;
  transition: all 0.1s;
}
.nav-item:hover {
  background: var(--wdc-hover);
  color: var(--wdc-text);
}
.nav-item.active {
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
}
.nav-label {
  flex: 1;
  white-space: nowrap;
}

.sidebar-spacer { flex: 1; }

.sidebar-bottom {
  border-top: 1px solid var(--wdc-border);
  padding-top: 6px;
  margin-top: 6px;
}
</style>
