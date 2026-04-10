<!--
  Dynamic plugin page: rendered entirely from the plugin's UI definition.
  Supports both schema-driven (Approach A) and bundle-based (Approach B) plugins.
-->
<template>
  <div class="plugin-page">
    <div v-if="loading" class="page-loading">
      <el-skeleton :rows="4" animated />
    </div>

    <div v-else-if="!uiDef" class="page-empty">
      <el-empty :description="`Plugin '${id}' has no UI defined.`" />
    </div>

    <template v-else>
      <div class="plugin-page-header">
        <h2>{{ uiDef.title }}</h2>
        <el-tag size="small" type="info">{{ uiDef.category }}</el-tag>
      </div>

      <SchemaRenderer :plugin-id="id" :definition="uiDef" />
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { usePluginsStore } from '../../stores/plugins'
import SchemaRenderer from '../../plugins/SchemaRenderer.vue'

const props = defineProps<{ id: string }>()

const pluginsStore = usePluginsStore()
const loading = ref(false)

const uiDef = computed(() => pluginsStore.getUi(props.id))

onMounted(async () => {
  if (!uiDef.value) {
    loading.value = true
    try {
      // Trigger load of this plugin's UI if not already cached
      await pluginsStore.loadAll()
    } finally {
      loading.value = false
    }
  }
})
</script>

<style scoped>
.plugin-page { padding: 24px; }
.plugin-page-header { display: flex; align-items: center; gap: 12px; margin-bottom: 20px; }
.plugin-page-header h2 { margin: 0; font-size: 1.2rem; }
.page-loading { padding: 32px; }
</style>
