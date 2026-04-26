<template>
  <div class="page mcp-kinds-page">
    <div class="page-header">
      <h2>{{ t('mcpKinds.title') }}</h2>
      <el-button :loading="loading" @click="refresh">
        <el-icon><Refresh /></el-icon> {{ t('mcpKinds.refresh') }}
      </el-button>
    </div>

    <p class="muted">{{ t('mcpKinds.description') }}</p>

    <el-alert
      v-if="!loading && kinds.length === 0"
      :title="t('mcpKinds.empty')"
      type="info"
      show-icon
      :closable="false"
    />

    <el-table v-else :data="kinds" stripe size="small" class="kinds-table">
      <el-table-column prop="id" :label="t('mcpKinds.col.id')" min-width="180">
        <template #default="{ row }">
          <code class="mono">{{ row.id }}</code>
        </template>
      </el-table-column>
      <el-table-column prop="label" :label="t('mcpKinds.col.label')" min-width="240" />
      <el-table-column prop="pluginId" :label="t('mcpKinds.col.plugin')" width="160">
        <template #default="{ row }">
          <el-tag :type="row.pluginId === 'core' ? 'info' : 'success'" size="small" effect="plain">
            {{ row.pluginId }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="danger" :label="t('mcpKinds.col.danger')" width="140">
        <template #default="{ row }">
          <el-tag
            :type="row.danger === 'destructive' ? 'danger' : 'warning'"
            size="small"
            effect="plain"
          >
            {{ t('mcpKinds.danger.' + row.danger) }}
          </el-tag>
        </template>
      </el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import { listMcpKinds, type McpKindRow } from '../../api/daemon'

const { t } = useI18n()
const loading = ref(false)
const kinds = ref<McpKindRow[]>([])

onMounted(refresh)

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const r = await listMcpKinds()
    kinds.value = r.entries
  } catch (e) {
    ElMessage.error(t('mcpKinds.toastLoadFailed', { error: (e as Error).message }))
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.page { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.page-header { display: flex; align-items: center; justify-content: space-between; }
.page-header h2 { margin: 0; }
.muted { color: var(--el-text-color-secondary); }
.kinds-table { margin-top: 8px; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; font-size: 12px; }
</style>
