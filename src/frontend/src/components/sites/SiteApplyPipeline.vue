<template>
  <section class="apply-pipeline" :class="{ 'is-compact': compact }" :aria-label="t('siteApply.title')">
    <header v-if="!compact" class="ap-header">
      <el-icon :size="16" class="ap-icon" :class="`ap-icon--${tone}`" aria-hidden="true">
        <Close v-if="tone === 'failed'" />
        <Clock v-else-if="tone === 'waiting'" />
        <Check v-else-if="tone === 'ok'" />
        <Remove v-else />
      </el-icon>
      <div class="ap-summary">
        <strong class="ap-headline">{{ headline }}</strong>
        <span v-if="whenText" class="ap-when">{{ whenText }}</span>
      </div>
      <div class="ap-actions">
        <el-button
          v-if="record && !failedCount"
          size="small"
          text
          :aria-expanded="showSteps"
          @click="showSteps = !showSteps"
        >{{ showSteps ? t('siteApply.hideSteps') : t('siteApply.showSteps') }}</el-button>
        <el-button
          size="small"
          :type="tone === 'failed' ? 'primary' : 'default'"
          :loading="busy"
          @click="reapply"
        >{{ record ? t('siteApply.applyAgain') : t('siteApply.applyNow') }}</el-button>
      </div>
    </header>

    <!-- The ruler: one segment per step, coloured by its result, so the
         place where an apply stopped is visible at a glance. -->
    <ol
      v-if="record && record.steps.length && stepsOpen"
      class="ruler"
      :style="{ '--ap-steps': record.steps.length }"
    >
      <li
        v-for="step in record.steps"
        :key="step.name"
        class="step"
        :class="`step--${step.status}`"
      >
        <span class="tick" aria-hidden="true" />
        <span class="step-name">{{ siteState.stepLabel(step.name) }}</span>
        <span class="step-status">
          <el-icon :size="12" aria-hidden="true">
            <Check v-if="step.status === 'ok'" />
            <Remove v-else-if="step.status === 'skipped'" />
            <Close v-else />
          </el-icon>
          {{ t(`siteApply.status.${step.status}`) }}
        </span>
        <span v-if="step.status === 'skipped' && skipReason(step)" class="step-reason">{{ skipReason(step) }}</span>
      </li>
    </ol>

    <!-- What failed and what to do about it, for the first failed step. -->
    <div v-if="firstFailed" class="step-detail" role="status">
      <h4 class="sd-title">{{ t('siteApply.failedStep', { step: siteState.stepLabel(firstFailed.name) }) }}</h4>
      <p v-if="firstFailed.detail" class="sd-detail mono">{{ firstFailed.detail }}</p>
      <p v-if="hintFor(firstFailed.name)" class="sd-hint">{{ hintFor(firstFailed.name) }}</p>
      <p v-if="failedCount > 1" class="sd-more">{{ t('siteApply.moreFailed', { n: failedCount - 1 }, failedCount - 1) }}</p>
    </div>

    <div v-if="compact" class="ap-compact-actions">
      <el-button size="small" type="primary" :loading="busy" @click="reapply">{{ t('siteApply.applyAgain') }}</el-button>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Check, Clock, Close, Remove } from '@element-plus/icons-vue'
import { useSiteApplyStore } from '../../stores/siteApply'
import { useSiteStateStore } from '../../stores/siteState'
import type { SiteApplyStepDto } from '../../api/daemon'
import { errorMessage } from '../../utils/errors'

/**
 * Shows what the last apply of a site did, step by step (certificate,
 * php.ini, vhost, PHP, web-server reload, hosts file, tunnel), and lets the
 * user re-run it after fixing a failed step. Fed by the daemon's
 * `site:applied` event and its remembered last result, so it reflects saves,
 * reapply-all and startup re-apply as well as the button here.
 *
 * The detail page shows one line while all is well and opens the steps on a
 * failure; `compact` is the always-open form used under a table row.
 */
const props = defineProps<{ domain: string; compact?: boolean }>()
const { t, te } = useI18n()
const store = useSiteApplyStore()
const siteState = useSiteStateStore()

const record = computed(() => store.results[props.domain])
const busy = computed(() => store.applying.has(props.domain))
const failedCount = computed(() => record.value?.steps.filter(s => s.status === 'failed').length ?? 0)
const firstFailed = computed(() => record.value?.steps.find(s => s.status === 'failed') ?? null)
const showSteps = ref(false)
const stepsOpen = computed(() => props.compact || failedCount.value > 0 || showSteps.value)

// The daemon skips the reload when Apache is down: the config is written, the site just does not answer yet.
const reloadSkippedForApache = computed(() =>
  !!record.value?.steps.some(s => s.name === 'reload' && s.status === 'skipped' && /not running/i.test(s.detail ?? '')))
const waitingForApache = computed(() =>
  !failedCount.value && reloadSkippedForApache.value && siteState.webServer !== 'running')

const tone = computed<'ok' | 'failed' | 'waiting' | 'none'>(() => {
  if (!record.value) return 'none'
  if (failedCount.value) return 'failed'
  return waitingForApache.value ? 'waiting' : 'ok'
})

const headline = computed(() => {
  switch (tone.value) {
    case 'failed': return t('siteApply.someFailed', { n: failedCount.value }, failedCount.value)
    case 'waiting': return t(siteState.webServer === 'missing' ? 'siteApply.waitingMissing' : 'siteApply.waitingStopped')
    case 'ok': return t('siteApply.allOk')
    default: return t('siteApply.notYet')
  }
})

function hintFor(name: string): string | null {
  const key = `siteApply.hints.${name}`
  return te(key) ? t(key) : null
}

// The daemon's skip reasons are English log text; known ones are translated.
function skipReason(step: SiteApplyStepDto): string {
  if (!step.detail) return ''
  if (/not running/i.test(step.detail)) return t('siteApply.skip.apacheDown')
  if (/reloaded once/i.test(step.detail)) return t('siteApply.skip.batch')
  if (/SKIP_HOSTS/i.test(step.detail)) return t('siteApply.skip.hosts')
  return step.detail
}

// Reactive clock for relative(): Date.now() alone is not reactive, so
// "applied just now" never aged while the page stayed open.
const now = ref(Date.now())
const nowTimer = setInterval(() => { now.value = Date.now() }, 30_000)
onBeforeUnmount(() => clearInterval(nowTimer))

function relative(at: number): string {
  const min = Math.max(0, Math.floor((now.value - at) / 60_000))
  if (min < 1) return t('common.justNow')
  if (min < 60) return t('sites.card.minutesAgo', { n: min })
  const h = Math.floor(min / 60)
  return h < 24 ? t('sites.card.hoursAgo', { n: h }) : t('sites.card.daysAgo', { n: Math.floor(h / 24) })
}
const whenText = computed(() => (record.value ? relative(record.value.at) : ''))

async function reapply() {
  try {
    const result = await store.apply(props.domain)
    if (result.success) ElMessage.success(t('siteApply.appliedOk'))
    else {
      const n = result.steps.filter(s => s.status === 'failed').length
      ElMessage.warning(t('siteApply.someFailed', { n }, n))
    }
  } catch (e) {
    ElMessage.error(t('toast.applyFailed', { err: errorMessage(e) }))
  }
}
</script>

<style scoped>
.apply-pipeline {
  display: grid;
  gap: var(--wdc-space-3);
}
.ap-header {
  display: flex;
  align-items: center;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
  padding: var(--wdc-space-2-5) var(--wdc-space-4);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-lg);
  background: var(--wdc-surface);
}
.ap-icon--ok { color: var(--wdc-status-running); }
.ap-icon--failed { color: var(--wdc-status-error); }
.ap-icon--waiting,
.ap-icon--none { color: var(--wdc-text-3); }
.ap-summary {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: var(--wdc-space-1) var(--wdc-space-3);
  flex: 1;
  min-width: 0;
}
.ap-headline { font-family: var(--wdc-font-display); font-weight: 600; font-size: 0.93rem; }
.ap-when { font-size: 0.82rem; color: var(--wdc-text-2); }
.ap-actions { display: flex; align-items: center; gap: var(--wdc-space-2); }
.ap-actions :deep(.el-button + .el-button) { margin-left: 0; }
.ap-compact-actions { display: flex; }

/* Ruler: a 2px rail; each step owns one segment of it. */
.ruler {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(var(--ap-steps, 7), minmax(0, 1fr));
}
.step {
  position: relative;
  display: grid;
  gap: var(--wdc-space-1);
  align-content: start;
  padding: var(--wdc-space-4) var(--wdc-space-2) 0 0;
  border-top: 2px solid var(--wdc-border-strong);
  font-size: 0.82rem;
  min-width: 0;
}
.step--ok { border-top-color: var(--wdc-status-running); }
.step--failed { border-top-color: var(--wdc-status-error); }
.tick {
  position: absolute;
  top: -2px;
  left: 0;
  width: 2px;
  height: var(--wdc-space-3-5);
  background: var(--wdc-border-strong);
}
.step--ok .tick { background: var(--wdc-status-running); }
.step--failed .tick { background: var(--wdc-status-error); width: 4px; }
.step-name {
  font-family: var(--wdc-font-display);
  font-weight: 600;
  font-size: 0.93rem;
  color: var(--wdc-text);
  overflow-wrap: anywhere;
}
.step--skipped .step-name { color: var(--wdc-text-2); }
.step--failed .step-name { color: var(--wdc-status-error); }
.step-status {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  color: var(--wdc-text-2);
}
.step--ok .step-status .el-icon { color: var(--wdc-status-running); }
.step--failed .step-status { color: var(--wdc-status-error); }
.step-reason { color: var(--wdc-text-3); font-size: 0.78rem; line-height: 1.35; }

.step-detail {
  display: grid;
  gap: var(--wdc-space-2);
  padding: var(--wdc-space-4);
  border-radius: var(--wdc-radius-lg);
  background: var(--wdc-surface-2);
  max-width: 72ch;
}
.sd-title { font-size: 0.93rem; }
.sd-detail { font-size: 0.78rem; color: var(--wdc-text); overflow-wrap: anywhere; }
.sd-hint { color: var(--wdc-text-2); font-size: 0.87rem; }
.sd-more { color: var(--wdc-text-3); font-size: 0.82rem; }

/* Narrow: the rail turns vertical. */
@media (max-width: 60rem) {
  .ruler { grid-template-columns: minmax(0, 1fr); border-left: 2px solid var(--wdc-border-strong); }
  .step { border-top: 0; padding: var(--wdc-space-2) 0 var(--wdc-space-2) var(--wdc-space-4); }
  .tick { top: var(--wdc-space-3-5); left: -2px; width: var(--wdc-space-3); height: 2px; }
  .step--failed .tick { width: var(--wdc-space-3); height: 4px; }
}
</style>
