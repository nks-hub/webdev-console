<template>
  <div class="table-structure">
    <div class="structure-toolbar">
      <span class="toolbar-title">
        <el-icon><Grid /></el-icon>
        <span>{{ database }}.{{ table }}</span>
      </span>
      <el-button size="small" :loading="loading" @click="$emit('reload')">
        <el-icon><Refresh /></el-icon>
        <span style="margin-left: 4px">{{ t('common.refresh') }}</span>
      </el-button>
    </div>

    <el-tabs v-model="activeTab" class="structure-tabs">
      <el-tab-pane :label="t('databases.structure.columns')" name="columns">
        <el-table :data="columns" size="small" stripe v-loading="loading" border>
          <el-table-column prop="name" :label="t('databases.structure.colName')" min-width="160">
            <template #default="{ row }">
              <span :class="{ 'pk-col': row.isPrimaryKey }" class="mono">{{ row.name }}</span>
              <el-tag v-if="row.isPrimaryKey" size="small" type="warning" class="ml-1">PK</el-tag>
              <el-tag v-if="row.isAutoIncrement" size="small" type="info" class="ml-1">AI</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="type" :label="t('databases.structure.colType')" min-width="140">
            <template #default="{ row }"><span class="mono type-cell">{{ row.type }}</span></template>
          </el-table-column>
          <el-table-column prop="nullable" :label="t('databases.structure.colNullable')" width="90">
            <template #default="{ row }">
              <el-tag size="small" :type="row.nullable ? 'info' : 'warning'" effect="plain">
                {{ row.nullable ? 'NULL' : 'NOT NULL' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="default" :label="t('databases.structure.colDefault')" min-width="120">
            <template #default="{ row }">
              <span v-if="row.default == null" class="muted">—</span>
              <span v-else class="mono default-cell">{{ row.default }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="comment" :label="t('databases.structure.colComment')" min-width="160" show-overflow-tooltip>
            <template #default="{ row }">
              <span v-if="!row.comment" class="muted">—</span>
              <span v-else>{{ row.comment }}</span>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane :label="t('databases.structure.indexes')" name="indexes">
        <el-table :data="indexes" size="small" stripe v-loading="loading" border>
          <el-table-column prop="name" :label="t('databases.structure.idxName')" min-width="160">
            <template #default="{ row }">
              <span class="mono">{{ row.name }}</span>
              <el-tag v-if="row.primary" size="small" type="warning" class="ml-1">PRIMARY</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="columns" :label="t('databases.structure.idxColumns')" min-width="220">
            <template #default="{ row }"><span class="mono">{{ row.columns.join(', ') }}</span></template>
          </el-table-column>
          <el-table-column prop="unique" :label="t('databases.structure.idxUnique')" width="100">
            <template #default="{ row }">
              <el-tag size="small" :type="row.unique ? 'success' : 'info'" effect="plain">
                {{ row.unique ? 'UNIQUE' : '—' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="type" :label="t('databases.structure.idxType')" width="100">
            <template #default="{ row }"><span class="mono">{{ row.type }}</span></template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Refresh, Grid } from '@element-plus/icons-vue'
import type { ColumnInfo, IndexInfo } from './types'

defineProps<{
  database: string
  table: string
  columns: ColumnInfo[]
  indexes: IndexInfo[]
  loading: boolean
}>()

defineEmits<{ (e: 'reload'): void }>()

const { t } = useI18n()
const activeTab = ref<'columns' | 'indexes'>('columns')
</script>

<style scoped>
.table-structure {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--wdc-bg);
}

.structure-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 14px;
  border-bottom: 1px solid var(--wdc-border);
  flex-shrink: 0;
}

.toolbar-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-family: 'JetBrains Mono', monospace;
  font-weight: 600;
  font-size: 0.86rem;
  color: var(--wdc-text);
}

.structure-tabs {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  padding: 0 14px;
}

.structure-tabs :deep(.el-tabs__content) { flex: 1; overflow: auto; padding-bottom: 14px; }

.mono { font-family: 'JetBrains Mono', monospace; font-size: 0.8rem; }
.type-cell { color: var(--el-color-info); }
.default-cell { color: var(--wdc-text-2); }
.pk-col { color: var(--wdc-accent); font-weight: 600; }
.muted { color: var(--wdc-text-3); font-size: 0.75rem; }
.ml-1 { margin-left: 4px; }
</style>
