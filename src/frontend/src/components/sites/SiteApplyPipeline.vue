<template>
  <section class="apply-pipeline" :aria-labelledby="`ap-${domain}`">
    <header class="ap-header">
      <div class="ap-title">
        <h3 :id="`ap-${domain}`" class="ap-heading">{{ t('siteApply.title') }}</h3>
        <span class="ap-meta">
          <template v-if="record">
            {{ record.success ? t('siteApply.allOk') : t('siteApply.someFailed', { n: failedCount }, failedCount) }}
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

    <!-- The ruler: one segment per step, coloured by its result, so the
         place where an apply stopped is visible at a glance. -->
    <ol
      v-if="record && record.steps.length"
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
        <span class="step-name">{{ stepLabel(step.name) }}</span>
        <span class="step-status">
          <el-icon :size="12" aria-hidden="true">
            <Check v-if="step.status === 'ok'" />
            <Remove v-else-if="step.status === 'skipped'" />
            <Close v-else />
          </el-icon>
          {{ t(`siteApply.status.${step.status}`) }}
        </span>
      </li>
    </ol>

    <!-- What failed and what to do about it, for the first failed step. -->
    <div v-if="firstFailed" class="step-detail" role="status">
      <h4 class="sd-title">{{ t('siteApply.failedStep', { step: stepLabel(firstFailed.name) }) }}</h4>
      <p v-if="firstFailed.detail" class="sd-detail mono">{{ firstFailed.detail }}</p>
      <p v-if="hintFor(firstFailed.name)" class="sd-hint">{{ hintFor(firstFailed.name) }}</p>
      <p v-if="failedCount > 1" class="sd-more">{{ t('siteApply.moreFailed', { n: failedCount - 1 }, failedCount - 1) }}</p>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Check, Close, Remove } from '@element-plus/icons-vue'
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
const firstFailed = computed(() => record.value?.steps.find(s => s.status === 'failed') ?? null)

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
  gap: var(--wdc-space-4);
}
.ap-header {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--wdc-space-3);
  flex-wrap: wrap;
}
.ap-title { display: grid; gap: var(--wdc-space-1); min-width: 0; }
.ap-heading { font-size: 1.07rem; }
.ap-meta { font-size: 0.87rem; color: var(--wdc-text-2); }

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
.step--skipped .step-name { color: var(--wdc-text-3); }
.step--failed .step-name { color: var(--wdc-status-error); }
.step-status {
  display: inline-flex;
  align-items: center;
  gap: var(--wdc-space-1);
  color: var(--wdc-text-2);
}
.step--ok .step-status .el-icon { color: var(--wdc-status-running); }
.step--failed .step-status { color: var(--wdc-status-error); }

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
