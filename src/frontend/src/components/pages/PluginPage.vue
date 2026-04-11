<!--
  Dynamic plugin page: rendered entirely from the plugin's UI definition.
  Supports both schema-driven (Approach A) and bundle-based (Approach B) plugins.
-->
<template>
  <div class="plugin-page">
    <div v-if="loading" class="page-loading">
      <el-skeleton :rows="4" animated />
    </div>

    <div v-else-if="!manifest" class="page-empty">
      <el-empty :description="`Plugin '${id}' not found.`" />
    </div>

    <template v-else>
      <!-- Header: title + category + version + meta -->
      <div class="plugin-page-header">
        <div class="plugin-page-identity">
          <h2 class="plugin-page-title">{{ manifest.name }}</h2>
          <span class="plugin-page-id mono">{{ manifest.id }}</span>
        </div>
        <div class="plugin-page-badges">
          <el-tag size="small" type="info" effect="dark">v{{ manifest.version }}</el-tag>
          <el-tag
            size="small"
            :type="manifest.enabled ? 'success' : 'info'"
            effect="dark"
          >{{ manifest.enabled ? 'Enabled' : 'Disabled' }}</el-tag>
          <el-tag
            v-if="uiDef?.category"
            size="small"
            effect="plain"
          >{{ uiDef.category }}</el-tag>
        </div>
      </div>

      <!-- Description card — always visible even when the plugin has no
           schema-driven UI. This is the primary UX for "what does this
           plugin do" without forcing users to hunt through docs. -->
      <div class="plugin-desc-card">
        <div class="plugin-desc-title">About</div>
        <p class="plugin-desc-body">
          {{ manifest.description || 'No description provided.' }}
        </p>
        <div class="plugin-desc-meta">
          <span v-if="(manifest as any).author">
            <span class="meta-label">Author</span>
            <span class="meta-val">{{ (manifest as any).author }}</span>
          </span>
          <span v-if="(manifest as any).license">
            <span class="meta-label">License</span>
            <span class="meta-val">{{ (manifest as any).license }}</span>
          </span>
          <span v-if="(manifest as any).capabilities?.length">
            <span class="meta-label">Capabilities</span>
            <span class="meta-val">{{ (manifest as any).capabilities.join(', ') }}</span>
          </span>
          <span v-if="(manifest as any).supportedPlatforms?.length">
            <span class="meta-label">Platforms</span>
            <span class="meta-val">{{ (manifest as any).supportedPlatforms.join(', ') }}</span>
          </span>
        </div>
      </div>

      <!-- Schema-driven UI panels (service card, config editor, log viewer, etc.) -->
      <div v-if="uiDef" class="plugin-page-body">
        <SchemaRenderer :plugin-id="id" :definition="uiDef" />
      </div>
      <div v-else class="plugin-page-body-empty">
        <el-empty
          description="This plugin does not expose a custom UI panel."
          :image-size="60"
        />
      </div>
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

const manifest = computed(() =>
  pluginsStore.manifests.find(p => p.id === props.id)
)
const uiDef = computed(() => pluginsStore.getUi(props.id))

onMounted(async () => {
  if (!manifest.value || !uiDef.value) {
    loading.value = true
    try {
      await pluginsStore.loadAll()
    } finally {
      loading.value = false
    }
  }
})
</script>

<style scoped>
.plugin-page {
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.plugin-page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}
.plugin-page-identity {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.plugin-page-title {
  margin: 0;
  font-size: 1.45rem;
  font-weight: 800;
  color: var(--wdc-text);
  letter-spacing: -0.01em;
}
.plugin-page-id {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  font-family: 'JetBrains Mono', monospace;
}
.plugin-page-badges {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.plugin-desc-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-left: 3px solid var(--wdc-accent);
  border-radius: var(--wdc-radius);
  padding: 18px 22px;
}
.plugin-desc-title {
  font-size: 0.72rem;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
  margin-bottom: 10px;
}
.plugin-desc-body {
  margin: 0 0 14px 0;
  font-size: 0.95rem;
  line-height: 1.55;
  color: var(--wdc-text);
}
.plugin-desc-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 14px 28px;
  padding-top: 12px;
  border-top: 1px solid var(--wdc-border);
}
.plugin-desc-meta > span {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 0.8rem;
}
.meta-label {
  text-transform: uppercase;
  font-size: 0.68rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  color: var(--wdc-text-3);
}
.meta-val {
  color: var(--wdc-text-2);
  font-weight: 500;
}

.plugin-page-body {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.plugin-page-body-empty {
  padding: 20px 0;
}
.page-loading { padding: 32px; }
.page-empty { padding: 32px; }
</style>
