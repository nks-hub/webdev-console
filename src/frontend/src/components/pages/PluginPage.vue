<!--
  Dynamic plugin page: rendered entirely from the plugin's UI definition.
  Supports both schema-driven (Approach A) and bundle-based (Approach B) plugins.
-->
<template>
  <div class="plugin-page">
    <div v-if="loading" class="page-loading">
      <el-skeleton :rows="4" animated />
    </div>

    <div v-else-if="loadError" class="page-empty">
      <el-empty description="Failed to load plugin">
        <template #default>
          <div style="color: var(--el-color-danger); margin-bottom: 12px">{{ loadError }}</div>
          <el-button type="primary" @click="$router.push('/dashboard')">Back to Dashboard</el-button>
        </template>
      </el-empty>
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
          <span v-if="manifest.author">
            <span class="meta-label">Author</span>
            <span class="meta-val">{{ manifest.author }}</span>
          </span>
          <span v-if="manifest.license">
            <span class="meta-label">License</span>
            <span class="meta-val">{{ manifest.license }}</span>
          </span>
          <span v-if="manifest.capabilities?.length">
            <span class="meta-label">Capabilities</span>
            <span class="meta-val">{{ manifest.capabilities.join(', ') }}</span>
          </span>
          <span v-if="manifest.supportedPlatforms?.length">
            <span class="meta-label">Platforms</span>
            <span class="meta-val">{{ manifest.supportedPlatforms.join(', ') }}</span>
          </span>
        </div>
      </div>

      <!-- Task 26: plugin-delivered custom UI bundle. When the plugin's
           manifest declares { pageBundleUrl }, lazy-load it (UMD/ESM)
           and mount as the primary page body. Schema-driven panels then
           serve as a fallback when no bundle is declared. -->
      <div v-if="pluginPageComponent" class="plugin-page-body">
        <component
          :is="pluginPageComponent"
          :plugin-id="id"
          :manifest="manifest"
        />
      </div>
      <div v-else-if="pluginPageError" class="plugin-page-body">
        <el-alert
          type="warning"
          :title="`Plugin UI bundle failed to load: ${pluginPageError}`"
          :description="'Falling back to schema-driven panels.'"
          :closable="false"
          show-icon
        />
        <SchemaRenderer v-if="uiDef" :plugin-id="id" :definition="uiDef" />
      </div>
      <!-- Schema-driven UI panels (service card, config editor, log viewer, etc.) -->
      <div v-else-if="uiDef" class="plugin-page-body">
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
import { computed, onMounted, ref, shallowRef, watch, markRaw, type Component } from 'vue'
import { useRouter } from 'vue-router'
import { usePluginsStore } from '../../stores/plugins'
import SchemaRenderer from '../../plugins/SchemaRenderer.vue'

const props = defineProps<{ id: string }>()

const router = useRouter()
const pluginsStore = usePluginsStore()
const loading = ref(false)
const loadError = ref('')

// Task 26: lazy-loaded plugin page bundle (UMD/ESM). Non-null when the
// plugin's manifest declares pageBundleUrl AND the bundle resolved
// successfully. shallowRef to avoid Vue reactively walking the entire
// component tree (components are immutable).
const pluginPageComponent = shallowRef<Component | null>(null)
const pluginPageError = ref<string | null>(null)

async function loadPluginBundle(url: string) {
  pluginPageError.value = null
  pluginPageComponent.value = null
  try {
    // Resolve relative paths against the plugin's own directory. A
    // plugin.json with "./ui/foo.umd.js" will be served by the daemon
    // under /plugins/<id>/ui/foo.umd.js — we normalize here so future
    // plugins don't have to hard-code absolute URLs.
    const resolved = url.startsWith('./') || url.startsWith('../')
      ? `/plugins/${props.id}/${url.replace(/^\.?\/+/, '')}`
      : url
    // @vite-ignore — dynamic import by runtime URL is intentional here.
    const mod = await import(/* @vite-ignore */ resolved)
    const exported = (mod.default || mod[props.id] || mod) as Component
    pluginPageComponent.value = markRaw(exported)
  } catch (e) {
    pluginPageError.value = e instanceof Error ? e.message : String(e)
    pluginPageComponent.value = null
  }
}

// Plugins with dedicated full-page routes (not schema-driven) redirect
// away from the generic PluginPage to their custom page so users don't
// see a half-rendered SchemaRenderer fallback. Add new entries as
// dedicated pages are built for other plugins.
const PLUGIN_CUSTOM_ROUTES: Record<string, string> = {
  'nks.wdc.cloudflare': '/cloudflare',
  'nks.wdc.apache': '/plugins/apache',
  'nks.wdc.php': '/plugins/php-custom',
  'nks.wdc.mysql': '/plugins/mysql',
  'nks.wdc.mailpit': '/plugins/mailpit',
  'nks.wdc.redis': '/plugins/redis',
}

const manifest = computed(() =>
  pluginsStore.manifests.find(p => p.id === props.id)
)
const uiDef = computed(() => pluginsStore.getUi(props.id))

// Task 26: watch manifest for pageBundleUrl and lazy-load when it shows up.
// Using watch rather than onMounted-only so plugin reloads (hot-swap
// during dev, or catalog refresh) can swap in the bundle without a full
// page refresh.
watch(
  manifest,
  (m) => {
    const url = (m as { pageBundleUrl?: string } | undefined)?.pageBundleUrl
    if (url) void loadPluginBundle(url)
    else { pluginPageComponent.value = null; pluginPageError.value = null }
  },
  { immediate: true },
)

onMounted(async () => {
  // Redirect to custom page if this plugin has one
  const customRoute = PLUGIN_CUSTOM_ROUTES[props.id]
  if (customRoute) {
    void router.replace(customRoute)
    return
  }

  if (!manifest.value || !uiDef.value) {
    loading.value = true
    loadError.value = ''
    try {
      await pluginsStore.loadAll()
    } catch (e) {
      loadError.value = e instanceof Error ? e.message : String(e || 'Failed to load plugins')
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
