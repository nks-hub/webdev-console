<template>
  <div class="sidebar" :class="{ collapsed }">
    <div class="sidebar-toggle" @click="collapsed = !collapsed">
      <el-icon><Fold v-if="!collapsed" /><Expand v-else /></el-icon>
    </div>

    <el-menu
      :default-active="currentRoute"
      :collapse="collapsed"
      :collapse-transition="false"
      class="sidebar-menu"
      @select="navigate"
    >
      <!-- Fixed core items -->
      <el-menu-item index="/dashboard">
        <el-icon><Monitor /></el-icon>
        <template #title>Dashboard</template>
      </el-menu-item>

      <el-menu-item index="/sites">
        <el-icon><Link /></el-icon>
        <template #title>Sites</template>
      </el-menu-item>

      <!-- Dynamic plugin categories -->
      <el-sub-menu
        v-for="category in pluginsStore.sidebarCategories"
        :key="category.id"
        :index="`cat-${category.id}`"
      >
        <template #title>
          <el-icon><Grid /></el-icon>
          <span>{{ category.label }}</span>
        </template>

        <el-menu-item
          v-for="plugin in category.plugins"
          :key="plugin.id"
          :index="`/plugin/${plugin.id}`"
        >
          <span class="status-dot" :class="[`dot-${plugin.serviceStatus}`]" />
          <template #title>{{ plugin.name }}</template>
        </el-menu-item>
      </el-sub-menu>

      <!-- Fixed bottom items -->
      <el-divider style="margin: 8px 0;" />

      <el-menu-item index="/plugins">
        <el-icon><Box /></el-icon>
        <template #title>Plugins</template>
      </el-menu-item>

      <el-menu-item index="/settings">
        <el-icon><Setting /></el-icon>
        <template #title>Settings</template>
      </el-menu-item>
    </el-menu>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { Monitor, Link, Grid, Box, Setting, Fold, Expand } from '@element-plus/icons-vue'
import { usePluginsStore } from '../../stores/plugins'

const router = useRouter()
const route = useRoute()
const pluginsStore = usePluginsStore()
const collapsed = ref(false)

const currentRoute = computed(() => route.path)

function navigate(path: string) {
  void router.push(path)
}
</script>

<style scoped>
.sidebar {
  width: 200px;
  display: flex;
  flex-direction: column;
  background: var(--df-surface);
  border-right: 1px solid var(--el-border-color);
  transition: width 0.2s;
  flex-shrink: 0;
}
.sidebar.collapsed { width: 56px; }
.sidebar-toggle {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 8px 12px;
  cursor: pointer;
  color: var(--el-text-color-secondary);
}
.sidebar-toggle:hover { color: var(--el-text-color-primary); }
.sidebar-menu { flex: 1; border-right: none; background: transparent; }
.status-dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  margin-right: 6px;
  flex-shrink: 0;
}
.dot-running { background: var(--el-color-success); }
.dot-stopped { background: var(--el-color-info); }
.dot-error   { background: var(--el-color-danger); }
.dot-starting { background: var(--el-color-warning); }
</style>
