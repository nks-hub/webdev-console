<template>
  <section class="apply-pipeline" :aria-label="t('siteApply.title')">
    <header class="ap-header">
      <div class="ap-title">
        <span class="ap-heading">{{ t('siteApply.title') }}</span>
        <span class="ap-meta">
          <template v-if="record">
            {{ record.success ? t('siteApply.allOk') : t('siteApply.someFailed', { n: failedCount }) }}
            · {{ t('siteApply.when', { when: relative(record.at) }) }}
          </template>
          <template v-else>{{ t('siteApply.notYet') }}</template>
        </span>
      </div>
      <el-button
        size="small"
        :type="record && !record.success ? 'primary' : 'default'"
        :loading="busy"
        @click="reapply"
      >
        {{ t('siteApply.applyAgain') }}
      </el-button>
    </header>

    <ol v-if="record && record.steps.length" class="ap-steps">
      <li
        v-for="step in record.steps"
        :key="step.name"
        class="ap-step"
        :class="`ap-step--${step.status}`"
      >
        <el-icon class="ap-icon" aria-hidden="true">
          <CircleCheck v-if="step.status === 'ok'" />
          <Remove v-else-if="step.status === 'skipped'" />
          <CircleClose v-else />
        </el-icon>
        <div class="ap-body">
          <div class="ap-line">
            <span class="ap-name">{{ stepLabel(step.name) }}</span>
            <span class="ap-status">{{ t(`siteApply.status.${step.status}`) }}</span>
          </div>
          <div v-if="step.detail" class="ap-detail">{{ step.detail }}</div>
          <div v-if="step.status === 'failed' && hintFor(step.name)" class="ap-hint">
            {{ hintFor(step.name) }}
          </div>
        </div>
      </li>
    </ol>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { CircleCheck, CircleClose, Remove } from '@element-plus/icons-vue'
import { useSiteApplyStore } from '../../stores/siteApply'
import { errorMessage } from '../../utils/errors'

/**
 * Shows what the last apply of a site did, step by step (certificate,
 * php.ini, vhost, PHP, web-server reload, hosts file, tunnel), and lets the
 * user re-run it after fixing a failed step. Fed by the daemon's
 * `site:applied` event, so it reflects saves, reapply-all and startup
 * re-apply as well as the button here.
 */
const props = defineProps<{ domain: string }>()
const { t, te } = useI18n()
const store = useSiteApplyStore()

const record = computed(() => store.results[props.domain])
const busy = computed(() => store.applying.has(props.domain))
const failedCount = computed(() => record.value?.steps.filter(s => s.status === 'failed').length ?? 0)

function stepLabel(name: string): string {
  const key = `siteApply.steps.${name}`
  return te(key) ? t(key) : name
}

function hintFor(name: string): string | null {
  const key = `siteApply.hints.${name}`
  return te(key) ? t(key) : null
}

// Reactive clock for relative(): Date.now() alone is not reactive, so
// "applied just now" never aged while the page stayed open.
const now = ref(Date.now())
const nowTimer = setInterval(() => { now.value = Date.now() }, 30_000)
onBeforeUnmount(() => clearInterval(nowTimer))

function relative(at: number): string {
  const s = Math.max(0, Math.round((now.value - at) / 1000))
  if (s < 60) return t('common.justNow')
  const m = Math.round(s / 60)
  return m < 60 ? t('siteApply.minutesAgo', { n: m }) : new Date(at).toLocaleTimeString()
}

async function reapply() {
  try {
    const result = await store.apply(props.domain)
    if (result.success) ElMessage.success(t('siteApply.appliedOk'))
    else ElMessage.warning(t('siteApply.someFailed', { n: result.steps.filter(s => s.status === 'failed').length }))
  } catch (e) {
    ElMessage.error(t('toast.applyFailed', { err: errorMessage(e) }))
  }
}
</script>

<style scoped>
.apply-pipeline {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  padding: var(--wdc-space-3) var(--wdc-space-4);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3);
}
.ap-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
}
.ap-title { display: flex; flex-direction: column; gap: var(--wdc-space-1); min-width: 0; }
.ap-heading { font-weight: 600; color: var(--wdc-text); }
.ap-meta { font-size: 0.85rem; color: var(--wdc-text-3); }
.ap-steps {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: var(--wdc-space-2);
}
.ap-step {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: flex-start;
  padding: var(--wdc-space-2) var(--wdc-space-3);
  border-radius: var(--wdc-radius-sm);
  background: var(--wdc-surface-2);
  border-left: 3px solid var(--wdc-border);
}
.ap-step--ok      { border-left-color: var(--wdc-status-running); }
.ap-step--skipped { border-left-color: var(--wdc-status-stopped); }
.ap-step--failed  { border-left-color: var(--wdc-status-error); }
.ap-icon { margin-top: 2px; flex-shrink: 0; }
.ap-step--ok .ap-icon      { color: var(--wdc-status-running); }
.ap-step--skipped .ap-icon { color: var(--wdc-status-stopped); }
.ap-step--failed .ap-icon  { color: var(--wdc-status-error); }
.ap-body { min-width: 0; display: flex; flex-direction: column; gap: var(--wdc-space-1); }
.ap-line { display: flex; gap: var(--wdc-space-2); align-items: baseline; flex-wrap: wrap; }
.ap-name { font-weight: 500; color: var(--wdc-text); }
.ap-status { font-size: 0.8rem; color: var(--wdc-text-2); }
.ap-detail {
  font-size: 0.8rem;
  color: var(--wdc-text-2);
  overflow-wrap: anywhere;
}
.ap-hint { font-size: 0.8rem; color: var(--wdc-text-3); }
</style>
