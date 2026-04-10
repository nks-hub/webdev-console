<template>
  <div class="pm-page">
    <div class="flex items-center justify-between mb-5 px-6 pt-6">
      <div>
        <h1 class="text-xl font-bold text-white">Plugins</h1>
        <p class="text-sm text-slate-400 mt-0.5">{{ pluginsStore.manifests.length }} plugins installed</p>
      </div>
      <el-input
        v-model="search"
        placeholder="Search plugins..."
        clearable
        size="small"
        style="width: 220px"
        prefix-icon="Search"
      />
    </div>

    <!-- Loading skeleton -->
    <div v-if="pluginsStore.loading" class="px-6">
      <el-skeleton :rows="5" animated />
    </div>

    <!-- Plugin cards grid -->
    <div v-else class="pm-grid px-6 pb-6">
      <div
        v-for="plugin in filteredPlugins"
        :key="plugin.id"
        class="pm-card"
        :class="{ 'pm-card--enabled': plugin.enabled, 'pm-card--disabled': !plugin.enabled }"
      >
        <div class="pm-card-header">
          <div class="pm-card-title">
            <span class="pm-name">{{ plugin.name }}</span>
            <el-tag size="small" type="info" effect="plain" class="pm-type-tag">{{ plugin.type }}</el-tag>
          </div>
          <el-switch
            :model-value="plugin.enabled"
            :loading="toggling.has(plugin.id)"
            @change="toggle(plugin.id)"
            size="small"
          />
        </div>

        <div class="pm-card-desc">
          {{ plugin.description || 'No description available.' }}
        </div>

        <div class="pm-card-footer">
          <div class="pm-meta">
            <span class="pm-version">v{{ plugin.version }}</span>
            <span v-if="plugin.author" class="pm-author">by {{ plugin.author }}</span>
          </div>
          <div class="pm-perms">
            <el-tag v-if="plugin.permissions?.network" size="small" type="warning" effect="plain">network</el-tag>
            <el-tag v-if="plugin.permissions?.process" size="small" type="danger" effect="plain">process</el-tag>
            <el-tag v-if="plugin.permissions?.gui" size="small" type="info" effect="plain">gui</el-tag>
          </div>
        </div>

        <!-- Open plugin page button if has UI -->
        <div class="pm-card-actions" v-if="plugin.enabled && plugin.ui">
          <el-button
            size="small"
            text
            type="primary"
            @click="router.push(`/plugin/${plugin.id}`)"
          >
            Open Panel &rarr;
          </el-button>
        </div>
      </div>

      <!-- Empty state -->
      <div v-if="filteredPlugins.length === 0 && !pluginsStore.loading" class="pm-empty">
        <el-empty :description="search ? `No plugins matching '${search}'` : 'No plugins loaded'" :image-size="60" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { usePluginsStore } from '../../stores/plugins'

const router = useRouter()
const pluginsStore = usePluginsStore()
const search = ref('')
const toggling = ref<Set<string>>(new Set())

const filteredPlugins = computed(() =>
  pluginsStore.manifests.filter(p =>
    search.value === '' ||
    p.name.toLowerCase().includes(search.value.toLowerCase()) ||
    p.id.toLowerCase().includes(search.value.toLowerCase()) ||
    (p.description ?? '').toLowerCase().includes(search.value.toLowerCase())
  )
)

async function toggle(id: string) {
  toggling.value.add(id)
  try { await pluginsStore.toggleEnable(id) }
  finally { toggling.value.delete(id) }
}

onMounted(() => { void pluginsStore.loadAll() })
</script>

<style scoped>
.pm-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.pm-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 16px;
}

.pm-card {
  background: var(--wdc-surface);
  border: 1px solid var(--el-border-color);
  border-radius: 10px;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  transition: border-color 0.15s, opacity 0.15s;
  border-left-width: 3px;
}

.pm-card--enabled { border-left-color: var(--el-color-primary); }
.pm-card--disabled { border-left-color: var(--el-border-color); opacity: 0.65; }

.pm-card:hover { border-color: var(--el-color-primary-light-5, #a0a0ff); }

.pm-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}

.pm-card-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.pm-name {
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.pm-type-tag { flex-shrink: 0; }

.pm-card-desc {
  font-size: 0.8rem;
  color: var(--el-text-color-secondary);
  line-height: 1.4;
  flex: 1;
}

.pm-card-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.pm-meta {
  display: flex;
  align-items: center;
  gap: 8px;
}

.pm-version {
  font-size: 0.72rem;
  font-family: monospace;
  color: var(--el-text-color-secondary);
}

.pm-author {
  font-size: 0.72rem;
  color: var(--el-text-color-secondary);
}

.pm-perms {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.pm-card-actions {
  padding-top: 4px;
  border-top: 1px solid var(--el-border-color);
}

.pm-empty {
  grid-column: 1 / -1;
  display: flex;
  justify-content: center;
  padding: 40px 0;
}
</style>
