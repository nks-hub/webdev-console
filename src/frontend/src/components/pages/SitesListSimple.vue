<template>
  <div class="sites-simple">
    <div class="simple-header">
      <h1 class="page-title">{{ $t('sites.title') }}</h1>
      <el-button type="primary" size="small" @click="emit('create')">{{ $t('sites.card.newSite') }}</el-button>
    </div>

    <div v-if="sitesStore.loading" v-loading="true" class="loading-wrap" />

    <el-empty
      v-else-if="sitesStore.sites.length === 0"
      :description="$t('sites.card.welcomeSubtext')"
      :image-size="80"
    >
      <template #description>
        <p class="empty-title">{{ $t('sites.card.welcomeTitle') }}</p>
        <p class="empty-sub">{{ $t('sites.card.welcomeSubtext') }}</p>
      </template>
      <el-button type="primary" size="large" @click="emit('create')">{{ $t('sites.create') }}</el-button>
    </el-empty>

    <div v-else class="card-grid">
      <el-card
        v-for="site in sitesStore.sites"
        :key="site.domain"
        class="site-card"
        shadow="hover"
      >
        <div class="card-body" @click="navigateToSite(site.domain)">
          <div class="card-title">{{ site.domain }}</div>

          <div class="card-status">
            <span class="status-dot" :class="apacheRunning ? 'dot-green' : 'dot-red'" />
            <span class="status-text">{{
              apacheRunning ? $t('sites.card.running') : $t('sites.card.stopped')
            }}</span>
          </div>

          <div class="card-badges">
            <el-tag
              v-if="site.phpVersion && site.phpVersion !== 'none'"
              size="small"
              effect="dark"
              class="badge-php"
            >PHP {{ site.phpVersion }}</el-tag>
            <el-tag
              v-if="site.sslEnabled"
              size="small"
              type="success"
              effect="dark"
            >HTTPS</el-tag>
            <el-tag
              v-if="site.cloudflare?.enabled"
              size="small"
              type="warning"
              effect="dark"
            >{{ $t('sites.simple.cloudflareTunnel') }}</el-tag>
          </div>
        </div>

        <div class="card-actions" @click.stop>
          <el-button size="small" type="primary" :icon="ExternalLinkIcon" @click="openSite(site)">{{ $t('sites.card.open') }}</el-button>

          <el-button
            v-if="apacheRunning"
            size="small"
            circle
            :icon="StopIcon"
            :loading="toggling"
            :title="$t('sites.card.stop')"
            @click="stopApache"
          />
          <el-button
            v-else
            size="small"
            circle
            type="success"
            :icon="PlayIcon"
            :loading="toggling"
            :title="$t('sites.card.start')"
            @click="startApache"
          />

          <el-dropdown trigger="click" @command="(cmd: string) => handleCommand(cmd, site.domain)">
            <el-button size="small" circle><el-icon><MoreFilled /></el-icon></el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="delete" class="danger-item">{{ $t('sites.card.delete') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, h } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessageBox, ElMessage } from 'element-plus'
import { MoreFilled } from '@element-plus/icons-vue'
import { useSitesStore } from '../../stores/sites'
import { useDaemonStore } from '../../stores/daemon'
import { startService, stopService } from '../../api/daemon'
import type { SiteInfo } from '../../api/types'

const ExternalLinkIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, [h('path', { d: 'M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6' }), h('polyline', { points: '15 3 21 3 21 9' }), h('line', { x1: '10', y1: '14', x2: '21', y2: '3' })]) }
const PlayIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'currentColor' }, [h('polygon', { points: '5 3 19 12 5 21 5 3' })]) }
const StopIcon = { render: () => h('svg', { xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: '1em', height: '1em', fill: 'currentColor' }, [h('rect', { x: '3', y: '3', width: '18', height: '18', rx: '2' })]) }

const { t: $t } = useI18n()

const emit = defineEmits<{ (e: 'create'): void }>()

const router = useRouter()
const sitesStore = useSitesStore()
const daemonStore = useDaemonStore()

const toggling = ref(false)

const apacheRunning = computed(() =>
  (daemonStore.services as any[]).some(
    s => s.id === 'apache' && (s.state === 2 || s.status === 'running')
  )
)

onMounted(async () => {
  if (sitesStore.sites.length === 0) {
    await sitesStore.load()
  }
})

function navigateToSite(domain: string) {
  void router.push(`/sites/${encodeURIComponent(domain)}/edit`)
}

function openSite(site: SiteInfo) {
  const proto = site.sslEnabled ? 'https' : 'http'
  const port = site.sslEnabled ? (site.httpsPort || 443) : (site.httpPort || 80)
  const portSuffix = (site.sslEnabled && port === 443) || (!site.sslEnabled && port === 80) ? '' : `:${port}`
  window.open(`${proto}://${site.domain}${portSuffix}`, '_blank')
}

async function startApache() {
  toggling.value = true
  try {
    await startService('apache')
  } catch (e: any) {
    ElMessage.error(`Start failed: ${e?.message || e}`)
  } finally {
    toggling.value = false
  }
}

async function stopApache() {
  toggling.value = true
  try {
    await stopService('apache')
  } catch (e: any) {
    ElMessage.error(`Stop failed: ${e?.message || e}`)
  } finally {
    toggling.value = false
  }
}

async function handleCommand(cmd: string, domain: string) {
  if (cmd === 'delete') {
    try {
      await ElMessageBox.confirm(
        $t('sites.card.deleteConfirm', { domain }),
        $t('sites.card.delete'),
        { type: 'warning', confirmButtonText: $t('sites.card.delete'), confirmButtonClass: 'el-button--danger' }
      )
    } catch {
      return
    }
    try {
      await sitesStore.remove(domain)
      ElMessage.success(`${domain} deleted`)
    } catch (e: any) {
      ElMessage.error(`Delete failed: ${e?.message || e}`)
    }
  }
}
</script>

<style scoped>
.sites-simple {
  padding: 24px;
  min-height: 100%;
  background: var(--wdc-bg);
}

.simple-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 24px;
}

.page-title {
  font-size: 1.15rem;
  font-weight: 700;
  color: var(--wdc-text);
}

.loading-wrap {
  height: 200px;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 16px;
}

.site-card {
  cursor: default;
  border-radius: 12px !important;
  transition: box-shadow 0.2s ease, transform 0.2s ease;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
}

.site-card:hover {
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.28);
  transform: scale(1.02);
}

:deep(.el-card__body) {
  padding: 20px;
}

.card-body {
  cursor: pointer;
  padding-bottom: 12px;
}

.card-title {
  font-size: 1.1rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin-bottom: 8px;
  word-break: break-all;
}

.card-status {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 10px;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.dot-green {
  background: #22c55e;
  animation: pulse-green 2s ease-in-out infinite;
}
.dot-red { background: #ef4444; }

@keyframes pulse-green {
  0%, 100% { box-shadow: 0 0 0 0 rgba(34, 197, 94, 0.6); }
  50%       { box-shadow: 0 0 0 5px rgba(34, 197, 94, 0); }
}

.status-text {
  font-size: 0.8rem;
  color: var(--wdc-text-2);
}

.card-badges {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.badge-php {
  background: #4f5b93 !important;
  border-color: #4f5b93 !important;
  color: #fff !important;
  font-weight: 700 !important;
  font-size: 0.68rem !important;
}

.card-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  padding-top: 12px;
  border-top: 1px solid var(--wdc-border);
  margin-top: 4px;
}

.empty-title {
  font-size: 1rem;
  font-weight: 600;
  color: var(--wdc-text);
  margin-bottom: 4px;
}

.empty-sub {
  font-size: 0.85rem;
  color: var(--wdc-text-2);
  margin-bottom: 12px;
}

.danger-item {
  color: var(--el-color-danger) !important;
}
</style>
