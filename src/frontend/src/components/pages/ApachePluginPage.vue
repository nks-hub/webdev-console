<template>
  <div class="wdc-page">
    <ServicePageHead
      service-id="apache"
      plugin-id="nks.wdc.apache"
      :title="t('apache.title')"
      :subtitle="t('apache.subtitle')"
    >
      <template #actions="{ state }">
        <el-button v-if="state === 'running' && localUrl" @click="openLocalUrl">{{ t('apache.openLocal', { url: localUrl }) }}</el-button>
      </template>
      <template #facts>
        <div><dt>{{ t('apache.sites') }}</dt><dd>{{ t('apache.sitesCount', { n: sitesStore.sites.length }, sitesStore.sites.length) }}</dd></div>
      </template>
    </ServicePageHead>

    <el-tabs v-if="installed" v-model="activeTab">
      <el-tab-pane name="vhosts" :label="t('apache.tabVhosts')" lazy>
        <el-empty v-if="sitesStore.sites.length === 0" :description="t('apache.noVhosts')" :image-size="48" />
        <el-table v-else :data="sitesStore.sites" size="small">
          <el-table-column prop="domain" :label="t('sites.domain')" min-width="200">
            <template #default="{ row }">
              <span class="mono">{{ row.domain }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="phpVersion" :label="t('sites.phpVersion')" width="140" />
          <el-table-column label="HTTPS" width="90" align="center">
            <template #default="{ row }">{{ row.ssl ? t('common.yes') : t('common.no') }}</template>
          </el-table-column>
          <el-table-column :label="t('common.actions')" width="110" align="center">
            <template #default="{ row }">
              <el-button size="small" text @click="router.push(`/sites/${row.domain}/edit`)">
                {{ t('common.edit') }}
              </el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane name="logs" :label="t('apache.tabLogs')" lazy>
        <LogViewer service-id="apache" class="tab-log" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { computed, onActivated, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useDaemonStore } from '../../stores/daemon'
import { useSitesStore } from '../../stores/sites'
import { stateOf } from '../../utils/services'
import LogViewer from '../shared/LogViewer.vue'
import ServicePageHead from '../shared/ServicePageHead.vue'

defineOptions({ name: 'ApachePluginPage' })

const { t } = useI18n()
const router = useRouter()
const daemonStore = useDaemonStore()
const sitesStore = useSitesStore()
const activeTab = ref<'vhosts' | 'logs'>('vhosts')

const service = computed(() => daemonStore.services.find(s => s.id === 'apache'))
const installed = computed(() => stateOf(service.value) !== 'notInstalled')

// HTTP port as the Apache module is configured (Settings → Ports); on
// macOS/Linux that is usually 8080, so the link must not assume :80.
const localUrl = computed(() => {
  const port = service.value?.ports?.http ?? service.value?.port
  if (!port) return ''
  return port === 80 ? 'http://localhost/' : `http://localhost:${port}/`
})

function openLocalUrl() {
  if (window.electronAPI?.openExternal) void window.electronAPI.openExternal(localUrl.value)
  else window.open(localUrl.value, '_blank')
}

// Also runs on first mount: every page lives inside <keep-alive>.
onActivated(() => { void sitesStore.load() })
</script>

<style scoped>
.tab-log { height: 460px; }
</style>
