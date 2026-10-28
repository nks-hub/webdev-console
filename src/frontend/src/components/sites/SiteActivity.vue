<template>
  <div class="sd-section">
    <h2 class="sd-section-title">{{ t('sites.detail.simple.activity.title') }}</h2>

    <!-- Errors block -->
    <div class="sd-activity-block">
      <div class="sd-activity-label">
        {{ t('sites.detail.simple.activity.errors.countLabel', { count: errorLines.length }) }}
        <span v-if="errorLines.length > 0" class="sd-err-badge">{{ errorLines.length }}</span>
      </div>
      <ul v-if="errorLines.length > 0" class="sd-err-list">
        <li
          v-for="(line, idx) in errorLines.slice(0, showAllErrors ? errorLines.length : 3)"
          :key="idx"
          class="sd-err-line"
        >
          <span class="sd-err-ts mono">{{ formatErrorTime(line.timestamp) }}</span>
          <span class="sd-err-msg">{{ line.message }}</span>
        </li>
      </ul>
      <div v-else class="sd-activity-empty">
        {{ t('sites.detail.simple.activity.errors.none') }}
      </div>
      <div class="sd-activity-actions">
        <el-button
          v-if="errorLines.length > 3 && !showAllErrors"
          size="small"
          link
          @click="showAllErrors = true"
        >
          {{ t('sites.detail.simple.activity.errors.showAll', { n: errorLines.length - 3 }) }}
        </el-button>
        <el-button size="small" link class="sd-full-logs" @click="openFullLogs">
          {{ t('sites.detail.simple.activity.fullLogs') }}
          <el-icon style="margin-left:var(--wdc-space-1)"><ArrowRight /></el-icon>
        </el-button>
      </div>
    </div>

    <!-- Traffic sparkline -->
    <div class="sd-activity-block">
      <div class="sd-activity-label">
        {{ t('sites.detail.simple.activity.traffic.last24h') }}
      </div>
      <div class="sd-traffic-row">
        <MiniSparkline :values="hourlyHits" :width="200" :height="32" />
        <span class="sd-traffic-count mono">
          {{ t('sites.detail.simple.activity.traffic.hits', { count: totalHits }, totalHits) }}
        </span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { ArrowRight } from '@element-plus/icons-vue'
import MiniSparkline from '../common/MiniSparkline.vue'
import type { SiteErrorLogEntry } from '../../api/types'
import { fetchHourlyRequests, fetchRecentErrors } from '../../utils/siteActivity'

defineOptions({ name: 'SiteActivity' })

// The "Recent activity" pane is a
// self-contained read-only widget (errors of the last 24 h + hourly hits sparkline)
// that doesn't share state with the editable site-config rows. Pulling
// it out keeps the parent component focused on edit flows and lets future
// dashboards reuse the same activity card without copying fetch logic.

const props = defineProps<{
  /** Domain whose error log + metrics history should be displayed. */
  domain: string
  /** Refresh trigger — bump this number to force a reload (e.g. when the
   *  parent receives an SSE-style event indicating new logs). Optional. */
  refreshKey?: number
}>()

const { t } = useI18n()
const router = useRouter()

const errorLines = ref<SiteErrorLogEntry[]>([])
const hourlyHits = ref<number[]>([])
const showAllErrors = ref(false)

const totalHits = computed(() => hourlyHits.value.reduce((a, b) => a + b, 0))

function formatErrorTime(iso: string): string {
  try {
    const d = new Date(iso)
    const hh = String(d.getHours()).padStart(2, '0')
    const mm = String(d.getMinutes()).padStart(2, '0')
    return `${hh}:${mm}`
  } catch { return iso.slice(0, 5) }
}

function openFullLogs() {
  router.push(`/sites/${encodeURIComponent(props.domain)}/edit?tab=logs`)
}

// Both reads are silent on failure: a rotated or locked log file is no reason for a toast on a
// summary card; the Logs and Metrics tabs report their own errors.
async function load() {
  const domain = props.domain
  await Promise.all([
    fetchRecentErrors(domain).then((e) => { errorLines.value = e }).catch(() => {}),
    fetchHourlyRequests(domain).then((h) => { hourlyHits.value = h }).catch(() => {}),
  ])
}

watch(() => [props.domain, props.refreshKey], () => { void load() }, { immediate: true })

defineExpose({ reload: load })
</script>

<style scoped>
.sd-section-title {
  margin: 0 0 var(--wdc-space-3);
  font-size: 0.93rem;
  color: var(--wdc-text-2);
}
.sd-activity-block {
  padding: var(--wdc-space-3) 0;
  border-bottom: 1px solid var(--wdc-border);
}
.sd-activity-block:last-child { border-bottom: 0; }
.sd-activity-label {
  font-size: 13px;
  color: var(--el-text-color-regular);
  margin-bottom: var(--wdc-space-1-5);
  display: flex;
  align-items: center;
  gap: var(--wdc-space-2);
}
.sd-err-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 20px;
  padding: 0 var(--wdc-space-1-5);
  height: 18px;
  background: var(--el-color-danger);
  color: white;
  border-radius: var(--wdc-radius);
  font-size: 11px;
  font-weight: 600;
}
.sd-err-list {
  list-style: none;
  padding: 0;
  margin: 0;
  max-height: 160px;
  overflow-y: auto;
}
.sd-err-line {
  display: flex;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-1) 0;
  font-size: 12px;
  line-height: 1.4;
}
.sd-err-ts { color: var(--el-text-color-secondary); flex-shrink: 0; }
.sd-err-msg {
  color: var(--el-text-color-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.sd-activity-empty {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  padding: var(--wdc-space-1) 0;
}
.sd-activity-actions { display: flex; gap: 12px; margin-top: 6px; }
.sd-full-logs { margin-left: auto; min-height: 32px; }
.sd-traffic-row { display: flex; align-items: center; gap: 12px; }
.sd-traffic-count { color: var(--el-text-color-secondary); font-size: 12px; }

@media (max-width: 760px) {
  .sd-traffic-row {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
