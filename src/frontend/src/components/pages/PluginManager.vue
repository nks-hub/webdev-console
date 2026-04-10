<template>
  <div class="plugin-manager">
    <div class="pm-header">
      <h2>Plugins</h2>
      <el-input v-model="search" placeholder="Search plugins..." clearable size="small" style="width: 220px" />
    </div>

    <el-table :data="filteredPlugins" v-loading="pluginsStore.loading" stripe>
      <el-table-column prop="name" label="Name" min-width="140">
        <template #default="{ row }">
          <div class="plugin-name">
            <span>{{ row.name }}</span>
            <el-tag size="small" type="info" effect="plain">{{ row.type }}</el-tag>
          </div>
        </template>
      </el-table-column>

      <el-table-column prop="version" label="Version" width="90" />

      <el-table-column prop="description" label="Description" min-width="200">
        <template #default="{ row }">
          <span class="desc">{{ row.description ?? '—' }}</span>
        </template>
      </el-table-column>

      <el-table-column label="Permissions" width="140">
        <template #default="{ row }">
          <div class="perm-chips">
            <el-tag v-if="row.permissions?.network" size="small" type="warning">network</el-tag>
            <el-tag v-if="row.permissions?.process" size="small" type="danger">process</el-tag>
            <el-tag v-if="row.permissions?.gui" size="small" type="info">gui</el-tag>
          </div>
        </template>
      </el-table-column>

      <el-table-column label="Status" width="90">
        <template #default="{ row }">
          <el-tag :type="row.enabled ? 'success' : 'info'" size="small" effect="dark">
            {{ row.enabled ? 'Enabled' : 'Disabled' }}
          </el-tag>
        </template>
      </el-table-column>

      <el-table-column label="Actions" width="120" fixed="right">
        <template #default="{ row }">
          <el-switch
            :model-value="row.enabled"
            :loading="toggling.has(row.id)"
            @change="toggle(row.id)"
          />
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { usePluginsStore } from '../../stores/plugins'

const pluginsStore = usePluginsStore()
const search = ref('')
const toggling = ref<Set<string>>(new Set())

const filteredPlugins = computed(() =>
  pluginsStore.manifests.filter(p =>
    search.value === '' ||
    p.name.toLowerCase().includes(search.value.toLowerCase()) ||
    p.id.toLowerCase().includes(search.value.toLowerCase())
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
.plugin-manager { padding: 24px; }
.pm-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 20px; }
.pm-header h2 { margin: 0; font-size: 1.2rem; }
.plugin-name { display: flex; align-items: center; gap: 6px; }
.perm-chips { display: flex; flex-wrap: wrap; gap: 4px; }
.desc { font-size: 0.82rem; color: var(--el-text-color-secondary); }
</style>
