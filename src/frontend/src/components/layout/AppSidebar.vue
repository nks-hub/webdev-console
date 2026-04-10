<template>
  <nav class="sidebar">
    <div class="sidebar-brand" @click="navigate('/dashboard')">
      <div class="brand-icon">⚡</div>
      <div class="brand-text" v-if="!collapsed">NKS WDC</div>
    </div>

    <div class="sidebar-section">
      <div class="section-label" v-if="!collapsed">Overview</div>
      <div
        class="nav-item" :class="{ active: isActive('/dashboard') }"
        @click="navigate('/dashboard')"
      >
        <span class="nav-icon">📊</span>
        <span class="nav-label" v-if="!collapsed">Dashboard</span>
      </div>
      <div
        class="nav-item" :class="{ active: isActive('/sites') }"
        @click="navigate('/sites')"
      >
        <span class="nav-icon">🌐</span>
        <span class="nav-label" v-if="!collapsed">Sites</span>
        <span class="nav-badge" v-if="!collapsed && sitesCount > 0">{{ sitesCount }}</span>
      </div>
    </div>

    <div class="sidebar-section">
      <div class="section-label" v-if="!collapsed">Manage</div>
      <div
        class="nav-item" :class="{ active: isActive('/binaries') }"
        @click="navigate('/binaries')"
      >
        <span class="nav-icon">📦</span>
        <span class="nav-label" v-if="!collapsed">Binaries</span>
      </div>
      <div
        class="nav-item" :class="{ active: isActive('/plugins') }"
        @click="navigate('/plugins')"
      >
        <span class="nav-icon">🔌</span>
        <span class="nav-label" v-if="!collapsed">Plugins</span>
        <span class="nav-badge" v-if="!collapsed">{{ pluginCount }}</span>
      </div>
      <div
        class="nav-item" :class="{ active: isActive('/settings') }"
        @click="navigate('/settings')"
      >
        <span class="nav-icon">⚙️</span>
        <span class="nav-label" v-if="!collapsed">Settings</span>
      </div>
    </div>

    <div class="sidebar-spacer" />

    <div class="sidebar-collapse" @click="collapsed = !collapsed">
      <span class="nav-icon">{{ collapsed ? '▸' : '◂' }}</span>
      <span class="nav-label" v-if="!collapsed">Collapse</span>
    </div>
  </nav>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useSitesStore } from '../../stores/sites'
import { usePluginsStore } from '../../stores/plugins'

const router = useRouter()
const route = useRoute()
const sitesStore = useSitesStore()
const pluginsStore = usePluginsStore()
const collapsed = ref(false)

const sitesCount = computed(() => sitesStore.sites.length)
const pluginCount = computed(() => pluginsStore.manifests.length)

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}

function navigate(path: string) {
  void router.push(path)
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
  overflow: hidden;
  transition: width 0.2s ease;
  padding: 8px;
}

.sidebar:has(.sidebar-collapse:hover) { /* subtle feedback */ }

.sidebar-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 10px 16px;
  cursor: pointer;
  user-select: none;
}
.brand-icon { font-size: 1.4rem; }
.brand-text {
  font-size: 0.95rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: -0.02em;
}

.sidebar-section {
  margin-bottom: 8px;
}

.section-label {
  font-size: 0.65rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
  padding: 8px 10px 4px;
}

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
  transition: all 0.12s ease;
  user-select: none;
}

.nav-item:hover {
  background: var(--wdc-hover);
  color: var(--wdc-text);
}

.nav-item.active {
  background: var(--wdc-accent-dim);
  color: var(--wdc-accent);
}

.nav-icon {
  font-size: 1.1rem;
  width: 22px;
  text-align: center;
  flex-shrink: 0;
}

.nav-label {
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.nav-badge {
  font-size: 0.65rem;
  font-weight: 600;
  background: var(--wdc-surface-2);
  color: var(--wdc-text-2);
  padding: 1px 6px;
  border-radius: 10px;
  min-width: 18px;
  text-align: center;
}

.sidebar-spacer { flex: 1; }

.sidebar-collapse {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
  color: var(--wdc-text-3);
  font-size: 0.82rem;
  transition: all 0.12s ease;
}
.sidebar-collapse:hover {
  background: var(--wdc-hover);
  color: var(--wdc-text-2);
}
</style>
