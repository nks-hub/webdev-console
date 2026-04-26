<template>
  <el-dialog
    v-model="visible"
    title="Confirm deploy"
    :close-on-press-escape="true"
    :close-on-click-modal="false"
    width="540px"
    @open="onOpen"
  >
    <div class="confirm">
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        :title="`You are about to deploy to ${domain} → ${host}`"
        description="This will publish your latest commit. Pre-deploy tests run first; after the symlink switch the deploy cannot be cancelled — only rolled back."
      />

      <div class="confirm-section">
        <label :for="inputId" class="confirm-label">
          Type the host name <strong>{{ host }}</strong> to confirm:
        </label>
        <el-input
          :id="inputId"
          ref="inputRef"
          v-model="typed"
          :placeholder="host"
          autocomplete="off"
          spellcheck="false"
          @keydown.enter="tryStartCountdown"
        />
        <small v-if="typed && !match" class="confirm-mismatch">
          Doesn't match — type "{{ host }}" exactly.
        </small>
      </div>

      <!-- Phase 6.12c — pre-deploy snapshot opt-in. Defaults ON for
           production-tagged hosts (heuristic: name contains 'prod') so
           the safer default is taken when the user is most likely to
           want it. Operator can override either way. -->
      <div class="confirm-section">
        <el-checkbox
          v-model="snapshotOptIn"
          :aria-describedby="snapshotHintId"
        >
          Snapshot database before deploy
        </el-checkbox>
        <small :id="snapshotHintId" class="confirm-hint">
          Creates a gzipped DB dump at <code>~/.wdc/backups/pre-deploy/</code>
          before the deploy starts. Snapshot failure aborts the deploy
          (fail-fast). Restorable via the snapshot list in the Settings tab.
        </small>
      </div>

      <!-- 2s countdown ring (NOT 2s hold-gesture, per v2 a11y BLOCKER #1). Once
           text matches and user activates the button, a 2-second countdown
           runs; user can cancel by clicking again or pressing Esc. Keyboard
           reachable, no sustained input required. -->
      <div class="confirm-actions">
        <el-button @click="onCancel">Cancel</el-button>
        <el-button
          type="danger"
          :disabled="!match || isDeploying"
          @click="onActivate"
          :aria-pressed="countdownActive"
        >
          <span v-if="!countdownActive">Deploy now</span>
          <span v-else>{{ countdownRemaining }}s — release to abort</span>
        </el-button>
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { ElInput } from 'element-plus'

const props = defineProps<{
  modelValue: boolean
  domain: string
  host: string
}>()

const emit = defineEmits<{
  'update:modelValue': [open: boolean]
  /**
   * Emitted only after user types match AND the 2s countdown elapses.
   * Phase 6.12c: payload now includes the snapshot opt-in flag so the
   * caller can decide whether to attach DeploySnapshotOptions to the
   * StartDeployBody.
   */
  confirmed: [opts: { snapshot: boolean }]
}>()

const visible = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

const typed = ref('')
const match = computed(() => typed.value.trim() === props.host)

const inputRef = ref<InstanceType<typeof ElInput> | null>(null)
const inputId = `confirm-input-${Math.random().toString(36).slice(2, 8)}`
const snapshotHintId = `confirm-snapshot-hint-${Math.random().toString(36).slice(2, 8)}`

// Phase 6.12c — snapshot opt-in. Heuristic default: ON for production-
// tagged hosts (matches HostCard's `isProduction` check), OFF otherwise.
const isProductionHost = computed(() =>
  props.host.toLowerCase().includes('prod') || props.host.toLowerCase() === 'production')
const snapshotOptIn = ref(false)

const countdownActive = ref(false)
const countdownRemaining = ref(2)
const isDeploying = ref(false)
let countdownTimer: ReturnType<typeof setInterval> | null = null

function onOpen(): void {
  typed.value = ''
  countdownActive.value = false
  countdownRemaining.value = 2
  isDeploying.value = false
  // Reset snapshot toggle to the host-aware default each time the modal
  // opens — operator's per-deploy choice doesn't persist across opens.
  snapshotOptIn.value = isProductionHost.value
  // Autofocus the input on open so keyboard users land directly on it.
  nextTick(() => inputRef.value?.focus())
}

function tryStartCountdown(): void {
  if (match.value && !countdownActive.value && !isDeploying.value) onActivate()
}

function onActivate(): void {
  if (countdownActive.value) {
    // Second click during countdown = abort
    abortCountdown()
    return
  }
  countdownActive.value = true
  countdownRemaining.value = 2
  countdownTimer = setInterval(() => {
    countdownRemaining.value--
    if (countdownRemaining.value <= 0) {
      clearInterval(countdownTimer!)
      countdownTimer = null
      countdownActive.value = false
      isDeploying.value = true
      emit('confirmed', { snapshot: snapshotOptIn.value })
      visible.value = false
    }
  }, 1000)
}

function abortCountdown(): void {
  if (countdownTimer) {
    clearInterval(countdownTimer)
    countdownTimer = null
  }
  countdownActive.value = false
  countdownRemaining.value = 2
}

function onCancel(): void {
  abortCountdown()
  visible.value = false
}

watch(() => props.modelValue, (v) => { if (!v) abortCountdown() })
</script>

<style scoped>
.confirm { display: flex; flex-direction: column; gap: 16px; }
.confirm-section { display: flex; flex-direction: column; gap: 6px; }
.confirm-label { font-size: 13px; color: var(--el-text-color-regular); }
.confirm-mismatch { color: var(--el-color-danger); font-size: 12px; }
.confirm-hint { color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.4; }
.confirm-hint code { font-family: ui-monospace, monospace; font-size: 11px; padding: 0 3px; background: var(--el-fill-color-light); border-radius: 2px; }
.confirm-actions { display: flex; gap: 8px; justify-content: flex-end; }
</style>
