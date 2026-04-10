<!--
  Implements the "Validating... ✓ Passed" UX pattern.
  The parent calls startValidation() → daemon validates → setResult(true/false, error?).
  After 2s auto-display, emits 'confirmed' so parent can apply the change.
-->
<template>
  <transition name="fade" mode="out-in">
    <div v-if="state.phase !== 'idle'" class="validation-badge" :class="[`phase-${state.phase}`]">
      <el-icon v-if="state.phase === 'validating'" class="spin"><Loading /></el-icon>
      <el-icon v-else-if="state.phase === 'passed'"><CircleCheck /></el-icon>
      <el-icon v-else-if="state.phase === 'failed'"><CircleClose /></el-icon>

      <span class="message">{{ state.message }}</span>

      <el-button
        v-if="state.phase === 'failed'"
        size="small"
        type="warning"
        plain
        class="revert-btn"
        @click="emit('revert')"
      >Revert</el-button>
    </div>
  </transition>
</template>

<script setup lang="ts">
import { reactive, watch } from 'vue'
import { Loading, CircleCheck, CircleClose } from '@element-plus/icons-vue'
import type { ValidationState } from '../../api/types'

const emit = defineEmits<{
  confirmed: []
  revert: []
}>()

const state = reactive<ValidationState>({
  phase: 'idle',
  message: '',
})

let autoCloseTimer: ReturnType<typeof setTimeout> | null = null

function startValidation() {
  if (autoCloseTimer) clearTimeout(autoCloseTimer)
  state.phase = 'validating'
  state.message = 'Validating configuration...'
  state.error = undefined
}

function setResult(passed: boolean, error?: string) {
  if (passed) {
    state.phase = 'passed'
    state.message = 'Configuration valid'
    autoCloseTimer = setTimeout(() => {
      state.phase = 'idle'
      emit('confirmed')
    }, 2000)
  } else {
    state.phase = 'failed'
    state.message = error ?? 'Validation failed'
  }
}

function reset() {
  if (autoCloseTimer) clearTimeout(autoCloseTimer)
  state.phase = 'idle'
  state.message = ''
}

defineExpose({ startValidation, setResult, reset })
</script>

<style scoped>
.validation-badge {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-radius: 6px;
  font-size: 0.85rem;
  font-weight: 500;
}

.phase-validating {
  background: var(--el-color-warning-light-9);
  color: var(--el-color-warning);
}
.phase-passed {
  background: var(--el-color-success-light-9);
  color: var(--el-color-success);
}
.phase-failed {
  background: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
}

.spin { animation: spin 1s linear infinite; }
@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }

.revert-btn { margin-left: 8px; }

.fade-enter-active, .fade-leave-active { transition: opacity 0.2s; }
.fade-enter-from, .fade-leave-to { opacity: 0; }
</style>
