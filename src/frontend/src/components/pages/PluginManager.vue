<template>
  <div class="pm-page">
    <div class="page-header">
      <div>
        <h1 class="page-title">{{ $t('plugins.title') }}</h1>
        <p class="page-subtitle">
          {{ $t('plugins.installedCount', { count: pluginsStore.manifests.length }) }}
          <span v-if="marketplace.reachable">· {{ $t('plugins.marketplaceCount', { count: marketplace.plugins.length }) }}</span>
        </p>
      </div>
      <el-input
        v-if="activeTab === 'installed'"
        v-model="search"
        placeholder="Search plugins..."
        clearable
        size="small"
        style="width: 220px"
        prefix-icon="Search"
      />
      <el-button
        v-else
        size="small"
        :loading="loadingMarketplace"
        @click="reloadMarketplace"
      >
        {{ $t('common.refresh') }}
      </el-button>
    </div>

    <el-tabs v-model="activeTab" class="pm-tabs">
      <el-tab-pane :label="$t('plugins.installed')" name="installed" />
      <el-tab-pane name="marketplace">
        <template #label>
          <span>{{ $t('plugins.marketplace') }}</span>
          <el-tag v-if="marketplace.reachable" size="small" type="success" effect="plain" style="margin-left:6px">
            {{ marketplace.plugins.length }}
          </el-tag>
        </template>
      </el-tab-pane>
    </el-tabs>

    <!-- Loading skeleton -->
    <div v-if="activeTab === 'installed' && pluginsStore.loading" class="page-body-pad">
      <el-skeleton :rows="5" animated />
    </div>

    <!-- Marketplace tab -->
    <div v-else-if="activeTab === 'marketplace'" class="page-body-pad">
      <el-alert
        v-if="!marketplace.reachable && marketplace.error"
        type="warning"
        :closable="false"
        show-icon
        :title="`Marketplace unreachable: ${marketplace.error}`"
        :description="`Source: ${marketplace.source}`"
        style="margin-bottom: 16px"
      />
      <div v-if="loadingMarketplace" class="page-body-pad">
        <el-skeleton :rows="3" animated />
      </div>
      <div v-else-if="marketplace.plugins.length === 0" class="pm-empty">
        <el-empty description="No plugins available in marketplace" :image-size="60" />
      </div>
      <div v-else class="pm-grid">
        <div
          v-for="mp in marketplace.plugins"
          :key="mp.id"
          class="pm-card"
          :class="{ 'pm-card--enabled': mp.installed }"
        >
          <div class="pm-card-header">
            <div class="pm-card-title">
              <ServiceIcon :service="mp.id" :active="true" />
              <span class="pm-name">{{ mp.name }}</span>
              <el-tag v-if="mp.installed" size="small" type="success" effect="plain">installed</el-tag>
            </div>
          </div>
          <div class="pm-card-desc">{{ mp.description || 'No description.' }}</div>
          <div class="pm-card-footer">
            <div class="pm-meta">
              <span class="pm-version">v{{ mp.version }}</span>
              <span v-if="mp.author" class="pm-author">by {{ mp.author }}</span>
              <span v-if="mp.license" class="pm-author">· {{ mp.license }}</span>
            </div>
            <div v-if="!mp.installed" class="pm-mp-actions">
              <el-button
                size="small"
                type="primary"
                :loading="installing.has(mp.id)"
                :disabled="!mp.downloadUrl"
                @click="installFromMarketplace(mp)"
              >
                Install
              </el-button>
              <el-button
                size="small"
                text
                :disabled="!mp.downloadUrl"
                @click="copyDownloadUrl(mp.downloadUrl)"
              >
                Copy URL
              </el-button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Plugin cards grid -->
    <div v-else class="pm-grid page-body-pad">
      <div
        v-for="plugin in filteredPlugins"
        :key="plugin.id"
        class="pm-card"
        :class="{ 'pm-card--enabled': plugin.enabled, 'pm-card--disabled': !plugin.enabled }"
      >
        <div class="pm-card-header">
          <div class="pm-card-title">
            <ServiceIcon :service="plugin.id" :active="plugin.enabled" />
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

        <!-- Open plugin page button if has UI. Must be a full-width solid
             button for readable contrast — the old text-primary variant
             rendered as low-contrast blue-on-blue on the flat surface. -->
        <div class="pm-card-actions" v-if="plugin.enabled && plugin.ui">
          <el-button
            size="small"
            type="primary"
            plain
            class="pm-open-btn"
            @click="router.push(`/plugin/${plugin.id}`)"
          >
            {{ $t('common.open') }} panel <el-icon><ArrowRight /></el-icon>
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
import { ref, reactive, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, ElNotification } from 'element-plus'
import { ArrowRight } from '@element-plus/icons-vue'
import { usePluginsStore } from '../../stores/plugins'
import ServiceIcon from '../shared/ServiceIcon.vue'
import {
  fetchMarketplace,
  installPluginFromMarketplace,
  type MarketplaceResponse,
  type MarketplacePlugin,
} from '../../api/daemon'

const router = useRouter()
const pluginsStore = usePluginsStore()
const search = ref('')
const toggling = ref<Set<string>>(new Set())
const activeTab = ref<'installed' | 'marketplace'>('installed')
const loadingMarketplace = ref(false)
const marketplace = reactive<MarketplaceResponse>({
  source: '',
  reachable: false,
  plugins: [],
})

async function reloadMarketplace() {
  loadingMarketplace.value = true
  try {
    const data = await fetchMarketplace()
    marketplace.source = data.source
    marketplace.reachable = data.reachable
    marketplace.plugins = data.plugins
    marketplace.count = data.count
    marketplace.error = data.error
  } catch (e: any) {
    marketplace.reachable = false
    marketplace.error = e?.message || String(e)
    marketplace.plugins = []
  } finally {
    loadingMarketplace.value = false
  }
}

async function copyDownloadUrl(url: string) {
  try {
    await navigator.clipboard.writeText(url)
    ElMessage.success('Download URL copied to clipboard')
  } catch {
    ElMessage.warning('Could not access clipboard')
  }
}

const installing = ref<Set<string>>(new Set())

async function installFromMarketplace(mp: MarketplacePlugin) {
  if (!mp.downloadUrl) return
  try {
    await ElMessageBox.confirm(
      `Install ${mp.name} v${mp.version} from ${mp.downloadUrl}?\n\nA daemon restart will be required after installation.`,
      'Confirm install',
      { confirmButtonText: 'Install', cancelButtonText: 'Cancel', type: 'info' }
    )
  } catch { return }

  installing.value.add(mp.id)
  try {
    const result = await installPluginFromMarketplace(mp.id, mp.downloadUrl)
    ElNotification({
      title: `${mp.name} installed`,
      message: result.restartRequired
        ? 'Restart the daemon to load the new plugin.'
        : 'Plugin loaded.',
      type: 'success',
      duration: 6000,
    })
    // Mark as installed locally so the button hides on next render
    mp.installed = true
    void reloadMarketplace()
  } catch (e: any) {
    ElNotification({
      title: 'Install failed',
      message: e?.message || String(e) || 'Unknown error',
      type: 'error',
      duration: 0,
    })
  } finally {
    installing.value.delete(mp.id)
  }
}

watch(activeTab, (tab) => {
  if (tab === 'marketplace' && marketplace.plugins.length === 0 && !loadingMarketplace.value) {
    void reloadMarketplace()
  }
})

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
  try {
    await pluginsStore.toggleEnable(id)
  } catch (e: any) {
    ElMessage.error(`Plugin toggle failed: ${e?.message || e}`)
  } finally {
    toggling.value.delete(id)
  }
}

onMounted(() => { void pluginsStore.loadAll() })
</script>

<style scoped>
/* Flat redesign — mirrors .svc-card from Dashboard.vue. No gradients, solid
   surface, hairline WDC border, solid accent left-edge for enabled state,
   readable secondary text via --wdc-text-2/3 (not --el-text-color-*). */
.pm-page {
  min-height: 100%;
  background: var(--wdc-bg);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px 0;
  margin-bottom: 16px;
}

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
  letter-spacing: 0.01em;
}

.page-subtitle {
  font-size: 0.76rem;
  color: var(--wdc-text-3);
  margin-top: 2px;
}

.page-body-pad {
  padding: 0 24px 24px;
}

.pm-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

.pm-card {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius);
  padding: 16px 18px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  transition: border-color 0.12s, background 0.12s;
  border-left-width: 3px;
  border-left-style: solid;
  border-left-color: var(--wdc-border);
}

.pm-card--enabled { border-left-color: var(--wdc-accent); }
.pm-card--disabled { opacity: 0.55; }

.pm-card:hover {
  border-color: var(--wdc-border-strong);
  background: var(--wdc-surface-2);
}
.pm-card--enabled:hover { border-left-color: var(--wdc-accent); }

.pm-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.pm-card-title {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  min-width: 0;
}

.pm-name {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--wdc-text);
  letter-spacing: 0.005em;
}

.pm-type-tag {
  flex-shrink: 0;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.pm-card-desc {
  font-size: 0.78rem;
  color: var(--wdc-text-2);
  line-height: 1.5;
  flex: 1;
}

.pm-card-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.pm-meta {
  display: flex;
  align-items: center;
  gap: 8px;
}

.pm-version {
  font-size: 0.7rem;
  font-family: 'JetBrains Mono', monospace;
  color: var(--wdc-text-3);
}

.pm-author {
  font-size: 0.7rem;
  color: var(--wdc-text-3);
}

.pm-perms {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.pm-card-actions {
  padding-top: 8px;
  margin-top: 4px;
  border-top: 1px solid var(--wdc-border);
}

.pm-open-btn {
  width: 100%;
  font-weight: 600;
  letter-spacing: 0.01em;
}

.pm-empty {
  grid-column: 1 / -1;
  display: flex;
  justify-content: center;
  padding: 40px 0;
}

.pm-mp-actions {
  display: flex;
  gap: 6px;
  align-items: center;
}
</style>
