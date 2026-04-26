<template>
  <div class="waterfall" role="list">
    <template v-for="(step, idx) in steps" :key="idx">
      <!-- Sticky PONR banner — appears immediately before the symlink_switch
           step, anchors the cliff between reversible and irreversible work.
           role=alert + aria-live=assertive so screen readers announce it as
           critical (per v2 a11y fix). -->
      <div
        v-if="idx === pointOfNoReturnIdx && pointOfNoReturnIdx >= 0"
        class="ponr-banner"
        :class="{ 'ponr-banner--alert': showPonrAlert, 'ponr-banner--cruising': !showPonrAlert }"
        role="alert"
        aria-live="assertive"
        aria-atomic="true"
      >
        <el-icon><WarningFilled /></el-icon>
        <span class="ponr-label mono">
          {{ t('deploy.waterfall.ponrLabel') }}
        </span>
      </div>

      <div
        :class="['step', `step--${step.state}`, { 'step--current': idx === currentIdx }]"
        role="listitem"
      >
        <span class="step-icon" :aria-label="t('deploy.waterfall.stepAria', { label: step.label, state: step.state })">
          <el-icon v-if="step.state === 'running'" class="is-loading"><Loading /></el-icon>
          <el-icon v-else-if="step.state === 'done'"><Check /></el-icon>
          <el-icon v-else-if="step.state === 'failed'"><CloseBold /></el-icon>
          <el-icon v-else-if="step.state === 'skipped'"><Minus /></el-icon>
          <span v-else class="step-pending-dot" />
        </span>
        <span class="step-label">{{ step.label }}</span>
        <!-- Text state mirrors the icon — never rely on color alone (v2 a11y) -->
        <span class="step-status mono">{{ step.state.toUpperCase() }}</span>
        <span v-if="step.elapsedMs != null" class="step-duration mono">{{ formatMs(step.elapsedMs) }}</span>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { Check, CloseBold, Loading, Minus, WarningFilled } from '@element-plus/icons-vue'

const { t } = useI18n()

export interface WaterfallStep {
  label: string
  state: 'pending' | 'running' | 'done' | 'failed' | 'skipped'
  elapsedMs?: number | null
}

const props = withDefaults(defineProps<{
  steps: WaterfallStep[]
  currentIdx?: number
  /** Index of the step IMMEDIATELY AFTER which the PONR banner appears. -1 = no banner. */
  pointOfNoReturnIdx?: number
  /** True after the symlink switch has succeeded — banner upgrades from
   *  cruising (subtle) to alert (full red). v2 ui-ux audit decision: don't
   *  burn out users by always showing the same intensity banner. */
  showPonrAlert?: boolean
}>(), {
  currentIdx: -1,
  pointOfNoReturnIdx: -1,
  showPonrAlert: false,
})

function formatMs(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  return `${(ms / 1000).toFixed(1)}s`
}
</script>

<style scoped>
.waterfall {
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-family: var(--el-font-family);
}
.step {
  display: grid;
  grid-template-columns: 24px 1fr auto auto;
  align-items: center;
  gap: 10px;
  padding: 6px 12px;
  border-left: 3px solid transparent;
  transition: border-color 0.15s, background 0.15s;
}
.step--pending  { border-left-color: var(--el-border-color); color: var(--el-text-color-secondary); }
.step--running  { border-left-color: var(--el-color-primary); background: var(--el-color-primary-light-9); }
.step--done     { border-left-color: var(--el-color-success); }
.step--failed   { border-left-color: var(--el-color-danger); background: var(--el-color-danger-light-9); color: var(--el-color-danger); }
.step--skipped  { border-left-color: var(--el-border-color-light); color: var(--el-text-color-disabled); text-decoration: line-through; }

.step-icon { display: inline-flex; justify-content: center; }
.step-pending-dot {
  display: inline-block; width: 8px; height: 8px; border-radius: 50%;
  background: var(--el-border-color);
}
.step-status {
  font-size: 0.7em; letter-spacing: 0.05em;
  padding: 1px 6px; border-radius: 3px;
  background: var(--el-fill-color-light);
}
.step-duration { font-size: 0.85em; color: var(--el-text-color-secondary); }

.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }

.ponr-banner {
  position: sticky; top: 0; z-index: 5;
  display: flex; align-items: center; gap: 10px;
  margin: 8px 0; padding: 8px 14px;
  border-radius: 4px;
  font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
  font-size: 0.78rem; font-weight: 700; letter-spacing: 0.04em;
}
.ponr-banner--cruising {
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color);
  border-left: 3px solid var(--el-color-warning);
  color: var(--el-text-color-regular);
}
.ponr-banner--alert {
  background: var(--el-color-danger-light-9);
  border: 1px solid var(--el-color-danger);
  border-left: 4px solid var(--el-color-danger);
  color: var(--el-color-danger);
  animation: ponr-attention 0.6s ease-out 1;
}
@keyframes ponr-attention {
  0%, 100% { box-shadow: 0 0 0 0 transparent; }
  50% { box-shadow: 0 0 0 6px var(--el-color-danger-light-7); }
}
@media (prefers-reduced-motion: reduce) {
  .ponr-banner--alert { animation: none; outline: 2px solid var(--el-color-danger); outline-offset: 2px; }
}
</style>
