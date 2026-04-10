<template>
  <nav class="sidebar">
    <div class="sidebar-brand" @click="navigate('/dashboard')">
      <el-icon :size="20" color="var(--wdc-accent)"><Monitor /></el-icon>
      <div class="brand-text" v-if="!collapsed">NKS WDC</div>
    </div>

    <div class="sidebar-section">
      <div class="section-label" v-if="!collapsed">Overview</div>
      <div class="nav-item" :class="{ active: isActive('/dashboard') }" @click="navigate('/dashboard')">
        <el-icon :size="18"><Odometer /></el-icon>
        <span class="nav-label" v-if="!collapsed">Dashboard</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/sites') }" @click="navigate('/sites')">
        <el-icon :size="18"><Link /></el-icon>
        <span class="nav-label" v-if="!collapsed">Sites</span>
      </div>
    </div>

    <div class="sidebar-section">
      <div class="section-label" v-if="!collapsed">Manage</div>
      <div class="nav-item" :class="{ active: isActive('/binaries') }" @click="navigate('/binaries')">
        <el-icon :size="18"><Download /></el-icon>
        <span class="nav-label" v-if="!collapsed">Binaries</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/plugins') }" @click="navigate('/plugins')">
        <el-icon :size="18"><Box /></el-icon>
        <span class="nav-label" v-if="!collapsed">Plugins</span>
      </div>
      <div class="nav-item" :class="{ active: isActive('/settings') }" @click="navigate('/settings')">
        <el-icon :size="18"><Setting /></el-icon>
        <span class="nav-label" v-if="!collapsed">Settings</span>
      </div>
    </div>

    <div class="sidebar-spacer" />

    <div class="sidebar-collapse" @click="collapsed = !collapsed">
      <el-icon :size="16"><Fold v-if="!collapsed" /><Expand v-else /></el-icon>
      <span class="nav-label" v-if="!collapsed">Collapse</span>
    </div>
  </nav>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { Monitor, Link, Download, Box, Setting, Fold, Expand, Odometer } from '@element-plus/icons-vue'

const router = useRouter()
const route = useRoute()
const collapsed = ref(false)

function isActive(path: string) {
  return route.path === path || route.path.startsWith(path + '/')
}

function navigate(path: string) {
  void router.push(path)
}
</script>

<style scoped>
.sidebar {
  width: 200px;
  display: flex;
  flex-direction: column;
  background: var(--wdc-surface);
  border-right: 1px solid var(--wdc-border);
  flex-shrink: 0;
  overflow: hidden;
  transition: width 0.2s ease;
  padding: 8px;
}

.sidebar-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 14px 10px 18px;
  cursor: pointer;
  user-select: none;
}
.brand-text {
  font-size: 1rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: -0.02em;
}

.sidebar-section {
  margin-bottom: 6px;
}

.section-label {
  font-size: 0.72rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
  padding: 10px 10px 6px;
}

.nav-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 10px;
  border-radius: var(--wdc-radius-sm);
  cursor: pointer;
  color: var(--wdc-text-2);
  font-size: 0.9rem;
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

.nav-label {
  flex: 1;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
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
