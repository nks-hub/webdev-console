<template>
  <div class="hosts-manager">
    <div class="page-header">
      <div class="header-title-block">
        <h1 class="page-title">{{ $t('hosts.title') }}</h1>
        <p class="page-subtitle">{{ $t('hosts.subtitle') }}</p>
      </div>
      <div class="header-actions">
        <el-button size="small" @click="openHostsFile">
          {{ $t('hosts.openFile') }}
        </el-button>
        <el-button size="small" type="primary" :loading="reapplying" @click="reapply">
          {{ $t('hosts.reapply') }}
        </el-button>
      </div>
    </div>

    <div class="page-body">
      <el-alert
        type="info"
        :title="$t('hosts.autoManagedInfo')"
        :description="`C:\\Windows\\System32\\drivers\\etc\\hosts`"
        show-icon
        :closable="false"
        style="margin-bottom: 16px"
      />

      <el-card shadow="never">
        <el-table
          v-if="entries.length > 0"
          :data="entries"
          size="small"
          style="width: 100%"
        >
          <el-table-column prop="ip" :label="$t('hosts.ipColumn')" width="130" class-name="mono" />
          <el-table-column prop="hostname" :label="$t('hosts.hostnameColumn')" min-width="180" />
          <el-table-column prop="source" :label="$t('hosts.sourceColumn')" min-width="160">
            <template #default="{ row }">
              <el-tag size="small" type="info" effect="plain">{{ row.source }}</el-tag>
            </template>
          </el-table-column>
        </el-table>

        <el-empty
          v-else
          :image-size="64"
          :description="$t('hosts.emptyTitle')"
        />
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { useSitesStore } from '../../stores/sites'

const { t } = useI18n()
const sitesStore = useSitesStore()
const reapplying = ref(false)

interface HostEntry {
  ip: string
  hostname: string
  source: string
}

function daemonBase(): string {
  const urlPort = new URLSearchParams(window.location.search).get('port')
  const port = (window as any).daemonApi?.getPort() ?? (urlPort ? parseInt(urlPort) : 5199)
  return `http://localhost:${port}`
}

/** Derive WDC-managed host entries from the sites store (domain + aliases). */
const entries = computed<HostEntry[]>(() => {
  const result: HostEntry[] = []
  for (const site of sitesStore.sites) {
    result.push({ ip: '127.0.0.1', hostname: site.domain, source: site.domain })
    for (const alias of (site.aliases ?? [])) {
      result.push({ ip: '127.0.0.1', hostname: alias, source: site.domain })
    }
  }
  return result
})

async function reapply() {
  reapplying.value = true
  try {
    const res = await fetch(`${daemonBase()}/api/sites/reapply-all`, {
      method: 'POST',
      headers: sitesStore.authHeaders(),
    })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    ElMessage.success(t('hosts.reapply'))
  } catch (e: any) {
    ElMessage.error(e?.message ?? String(e))
  } finally {
    reapplying.value = false
  }
}

function openHostsFile() {
  const hostsPath = 'C:\\Windows\\System32\\drivers\\etc\\hosts'
  window.open(`vscode://file/${hostsPath}`, '_self')
}

onMounted(() => {
  if (!sitesStore.sites.length) {
    void sitesStore.load()
  }
})
</script>

<style scoped>
.hosts-manager {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.page-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  padding: 20px 24px 16px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.header-title-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.page-title {
  font-size: 1.35rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0;
}

.page-subtitle {
  font-size: 0.85rem;
  color: var(--wdc-text-3);
  margin: 0;
}

.header-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.page-body {
  flex: 1;
  overflow-y: auto;
  padding: 20px 24px;
}
</style>
