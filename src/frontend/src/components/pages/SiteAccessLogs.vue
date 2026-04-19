<template>
  <div class="access-logs-wrap">
    <!-- Toolbar -->
    <div class="logs-toolbar">
      <el-button
        size="small"
        :loading="loading"
        :icon="Refresh"
        @click="fetchLogs"
      >
        {{ $t('sites.access.refresh') }}
      </el-button>

      <div class="toolbar-group">
        <span class="toolbar-label">{{ $t('sites.access.lines') }}</span>
        <el-select
          v-model="selectedLines"
          size="small"
          style="width: 90px"
          @change="fetchLogs"
        >
          <el-option v-for="n in LINE_OPTIONS" :key="n" :label="String(n)" :value="n" />
        </el-select>
      </div>

      <div class="toolbar-group">
        <span class="toolbar-label">{{ $t('sites.access.since') }}</span>
        <el-date-picker
          v-model="sinceDate"
          type="datetime"
          size="small"
          style="width: 200px"
          clearable
          :placeholder="$t('sites.access.since')"
          @change="fetchLogs"
        />
      </div>

      <div class="toolbar-group">
        <span class="toolbar-label">{{ $t('sites.access.autoRefresh') }}</span>
        <el-switch v-model="autoRefresh" size="small" @change="onAutoRefreshToggle" />
      </div>
    </div>

    <!-- Loading skeleton -->
    <el-skeleton v-if="loading && entries.length === 0" :rows="8" animated style="padding: 16px 0" />

    <!-- Empty state -->
    <el-empty
      v-else-if="!loading && entries.length === 0"
      :description="$t('sites.access.empty')"
      :image-size="64"
    />

    <!-- Log table -->
    <div v-else class="table-responsive">
      <el-table
        :data="entries"
        size="small"
        stripe
        class="logs-table"
      >
        <el-table-column
          :label="$t('sites.access.timestamp')"
          prop="timestamp"
          width="165"
          fixed
        >
          <template #default="{ row }">
            <span class="mono">{{ formatTimestamp(row.timestamp) }}</span>
          </template>
        </el-table-column>

        <el-table-column
          prop="realIp"
          width="140"
        >
          <template #header>
            <el-tooltip
              content="Real client IP (prefers CF-Connecting-IP / XFF, falls back to socket)"
              placement="top"
              :show-after="300"
            >
              <span>{{ $t('sites.access.ip') }}</span>
            </el-tooltip>
          </template>
          <template #default="{ row }">
            <span class="mono dim">{{ row.realIp }}</span>
          </template>
        </el-table-column>

        <el-table-column
          :label="$t('sites.access.method')"
          prop="method"
          width="80"
          align="center"
        >
          <template #default="{ row }">
            <el-tag
              v-if="row.method"
              :type="methodType(row.method)"
              size="small"
              effect="dark"
              class="method-tag"
            >
              {{ row.method }}
            </el-tag>
            <span v-else class="dim">—</span>
          </template>
        </el-table-column>

        <el-table-column
          :label="$t('sites.access.path')"
          prop="path"
          min-width="220"
        >
          <template #default="{ row }">
            <el-tooltip
              v-if="row.path && row.path.length > 60"
              :content="row.path"
              placement="top"
              :show-after="300"
            >
              <span class="mono path-truncated">{{ row.path }}</span>
            </el-tooltip>
            <span v-else class="mono">{{ row.path ?? '—' }}</span>
          </template>
        </el-table-column>

        <el-table-column
          :label="$t('sites.access.status')"
          prop="status"
          width="80"
          align="center"
        >
          <template #default="{ row }">
            <el-tag
              :type="statusType(row.status)"
              size="small"
              effect="plain"
              class="status-tag"
            >
              {{ row.status }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column
          :label="$t('sites.access.bytes')"
          prop="bytes"
          width="90"
          align="right"
        >
          <template #default="{ row }">
            <span class="mono dim">{{ formatBytes(row.bytes) }}</span>
          </template>
        </el-table-column>

        <el-table-column
          :label="$t('sites.access.userAgent')"
          prop="userAgent"
          width="150"
        >
          <template #default="{ row }">
            <el-tooltip
              v-if="row.userAgent"
              :content="row.userAgent"
              placement="top-start"
              :show-after="300"
            >
              <span class="ua-truncated dim">{{ row.userAgent }}</span>
            </el-tooltip>
            <span v-else class="dim">—</span>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { getAccessLogs } from '../../api/daemon'
import type { AccessLogEntry } from '../../api/types'

const props = defineProps<{ domain: string }>()

const { t } = useI18n()

const LINE_OPTIONS = [50, 100, 200, 500, 1000] as const

const entries = ref<AccessLogEntry[]>([])
const loading = ref(false)
const selectedLines = ref<number>(100)

function defaultSinceDate(): Date {
  const d = new Date()
  d.setHours(d.getHours() - 24)
  return d
}

const sinceDate = ref<Date | null>(defaultSinceDate())
const autoRefresh = ref(false)
let refreshTimer: ReturnType<typeof setInterval> | null = null

const sinceIso = computed<string | undefined>(() => {
  if (!sinceDate.value) return undefined
  return sinceDate.value instanceof Date
    ? sinceDate.value.toISOString()
    : new Date(sinceDate.value).toISOString()
})

async function fetchLogs(): Promise<void> {
  loading.value = true
  try {
    entries.value = await getAccessLogs(props.domain, {
      lines: selectedLines.value,
      since: sinceIso.value,
    })
  } catch (e: any) {
    ElMessage.error(`${t('sites.access.refresh')}: ${e?.message ?? String(e)}`)
  } finally {
    loading.value = false
  }
}

function onAutoRefreshToggle(on: boolean | string | number): void {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer)
    refreshTimer = null
  }
  if (on) {
    refreshTimer = setInterval(() => void fetchLogs(), 5000)
  }
}

function formatTimestamp(iso: string): string {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

function methodType(method: string): '' | 'success' | 'warning' | 'danger' | 'info' {
  switch (method.toUpperCase()) {
    case 'GET':    return ''
    case 'POST':   return 'success'
    case 'PUT':    return 'warning'
    case 'PATCH':  return 'warning'
    case 'DELETE': return 'danger'
    default:       return 'info'
  }
}

function statusType(status: number): '' | 'success' | 'warning' | 'danger' | 'info' {
  if (status >= 500) return 'danger'
  if (status >= 400) return 'warning'
  if (status >= 300) return 'info'
  if (status >= 200) return 'success'
  return ''
}

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B'
  if (bytes < 1024) return `${bytes} B`
  const kb = bytes / 1024
  if (kb < 1024) return `${kb.toFixed(1)} KB`
  return `${(kb / 1024).toFixed(1)} MB`
}

onMounted(() => void fetchLogs())
onBeforeUnmount(() => {
  if (refreshTimer !== null) clearInterval(refreshTimer)
})
</script>

<style scoped>
.access-logs-wrap {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.logs-toolbar {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
}

.toolbar-group {
  display: flex;
  align-items: center;
  gap: 6px;
}

.toolbar-label {
  font-size: 0.78rem;
  color: var(--wdc-text-3);
  white-space: nowrap;
}

.table-responsive {
  overflow-x: auto;
  -webkit-overflow-scrolling: touch;
  width: 100%;
}

.logs-table {
  width: 100%;
  font-size: 0.82rem;
}

.mono {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
}

.dim {
  color: var(--wdc-text-3);
}

.method-tag {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
  font-weight: 700;
  letter-spacing: 0.03em;
}

.status-tag {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
  font-weight: 600;
}

.path-truncated {
  display: block;
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: default;
}

.ua-truncated {
  display: block;
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 0.78rem;
  cursor: default;
}
</style>
