<template>
  <el-drawer
    v-model="visible"
    :title="t('help.wizard.title')"
    direction="rtl"
    size="420px"
    :close-on-click-modal="true"
    :close-on-press-escape="true"
    @close="onClose"
  >
    <div class="gsw-wrap" @keydown="onKeydown" tabindex="-1" ref="wrapRef">
      <p class="gsw-subtitle">{{ t('help.wizard.subtitle') }}</p>

      <el-steps :active="activeStep" finish-status="success" direction="vertical" class="gsw-steps">
        <el-step
          v-for="(step, i) in steps"
          :key="step.key"
          :title="t(`help.wizard.steps.${step.key}.title`)"
        />
      </el-steps>

      <div class="gsw-content">
        <div class="gsw-step-indicator">{{ t('help.wizard.step', { current: activeStep + 1, total: steps.length }) }}</div>

        <h3 class="gsw-step-title">{{ t(`help.wizard.steps.${currentStep.key}.title`) }}</h3>

        <div
          class="gsw-step-desc"
          v-html="t(`help.wizard.steps.${currentStep.key}.desc`)"
        />

        <div class="gsw-tip">
          <span class="gsw-tip-icon">i</span>
          <span v-html="t(`help.wizard.steps.${currentStep.key}.tip`)" />
        </div>
      </div>
    </div>

    <template #footer>
      <div class="gsw-footer">
        <el-button text size="small" @click="skip">{{ t('help.wizard.skip') }}</el-button>
        <div class="gsw-nav">
          <el-button v-if="activeStep > 0" @click="activeStep--">{{ t('help.wizard.back') }}</el-button>
          <el-button v-if="activeStep < steps.length - 1" type="primary" @click="activeStep++">
            {{ t('help.wizard.next') }}
          </el-button>
          <el-button v-else type="primary" @click="finish">
            {{ t('help.wizard.finish') }}
          </el-button>
        </div>
      </div>
    </template>
  </el-drawer>
</template>

<script setup lang="ts">
import { ref, computed, nextTick } from 'vue'
import { useI18n } from 'vue-i18n'

const STORAGE_KEY = 'wdc_seenGettingStarted'

const { t } = useI18n()

const visible = ref(false)
const activeStep = ref(0)
const wrapRef = ref<HTMLElement | null>(null)

const steps = [
  { key: 'site' },
  { key: 'ssl' },
  { key: 'browser' },
  { key: 'database' },
  { key: 'mcp' },
] as const

const currentStep = computed(() => steps[activeStep.value])

function open() {
  activeStep.value = 0
  visible.value = true
  nextTick(() => wrapRef.value?.focus())
}

function close() {
  visible.value = false
}

function finish() {
  localStorage.setItem(STORAGE_KEY, '1')
  close()
}

function skip() {
  localStorage.setItem(STORAGE_KEY, '1')
  close()
}

function onClose() {
  localStorage.setItem(STORAGE_KEY, '1')
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'ArrowRight' || (e.key === 'Enter' && activeStep.value < steps.length - 1)) {
    activeStep.value = Math.min(activeStep.value + 1, steps.length - 1)
  } else if (e.key === 'ArrowLeft') {
    activeStep.value = Math.max(activeStep.value - 1, 0)
  }
}

/** Returns true if user has already seen the wizard. */
function hasSeen(): boolean {
  return localStorage.getItem(STORAGE_KEY) === '1'
}

defineExpose({ open, close, hasSeen })
</script>

<style scoped>
.gsw-wrap {
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-4-5);
  padding: var(--wdc-space-1) 0 0;
  outline: none;
}

.gsw-subtitle {
  font-size: 0.88rem;
  color: var(--wdc-text-2);
  margin: 0;
  line-height: 1.5;
}

.gsw-steps {
  --el-color-primary: var(--wdc-accent);
}

.gsw-content {
  background: var(--wdc-surface);
  border: 1px solid var(--wdc-border);
  border-radius: var(--wdc-radius-sm);
  padding: var(--wdc-space-4-5) var(--wdc-space-4-5);
  display: flex;
  flex-direction: column;
  gap: var(--wdc-space-3-5);
}

.gsw-step-indicator {
  font-size: 0.82rem;
  font-weight: 500;
  color: var(--wdc-accent);
  font-family: var(--wdc-font-display);
}

.gsw-step-title {
  font-size: 1.05rem;
  font-weight: 700;
  color: var(--wdc-text);
  margin: 0;
}

.gsw-step-desc {
  font-size: 0.88rem;
  color: var(--wdc-text-2);
  line-height: 1.6;
}

.gsw-step-desc :deep(strong) { color: var(--wdc-text); font-weight: 600; }
.gsw-step-desc :deep(code) {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  padding: 1px var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.82rem;
  color: var(--wdc-accent);
}
.gsw-step-desc :deep(kbd) {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  padding: var(--wdc-space-0-5) var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.8rem;
  color: var(--wdc-text);
  box-shadow: 0 1px 0 var(--wdc-border-strong);
}

.gsw-tip {
  display: flex;
  gap: var(--wdc-space-2);
  align-items: flex-start;
  font-size: 0.82rem;
  color: var(--wdc-text-3);
  line-height: 1.5;
  padding-top: var(--wdc-space-1);
  border-top: 1px solid var(--wdc-border);
}

.gsw-tip-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  border: 1px solid var(--wdc-border);
  font-size: 0.7rem;
  font-style: italic;
  font-weight: 700;
  color: var(--wdc-text-3);
  flex-shrink: 0;
  margin-top: 1px;
}

.gsw-tip :deep(kbd) {
  background: var(--wdc-surface-2);
  border: 1px solid var(--wdc-border);
  padding: 1px var(--wdc-space-1-5);
  border-radius: var(--wdc-radius-sm);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.75rem;
  color: var(--wdc-text-2);
  box-shadow: 0 1px 0 var(--wdc-border-strong);
}

.gsw-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.gsw-nav {
  display: flex;
  gap: var(--wdc-space-2);
}
</style>
