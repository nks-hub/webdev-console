<template>
  <el-dialog
    v-model="visible"
    :title="t('deploy.confirmModal.title')"
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
        :title="t('deploy.confirmModal.alertTitle', { domain, host })"
        :description="t('deploy.confirmModal.alertBody')"
      />

      <!-- Phase 7.5+++ — context panel. Surfaces what the operator is
           ABOUT to override (last release + when it shipped). When the
           previous deploy was very recent we escalate to a yellow warn
           since back-to-back deploys are usually an accidental
           double-click rather than intentional. -->
      <div v-if="lastDeploy" class="confirm-context"
           :class="{ 'warn-recent': isRecentDeploy }">
        <div class="confirm-context-row">
          <span class="muted">{{ t('deploy.confirmModal.lastReleaseLabel') }}</span>
          <code class="mono">{{ lastDeploy.releaseId || '—' }}</code>
        </div>
        <div class="confirm-context-row">
          <span class="muted">{{ t('deploy.confirmModal.lastDeployedLabel') }}</span>
          <span class="mono">{{ relativeAge }}</span>
          <el-tag :type="lastDeploy.success ? 'success' : 'danger'" size="small" effect="plain">
            {{ lastDeploy.success
                ? t('deploy.confirmModal.lastDeployOk')
                : t('deploy.confirmModal.lastDeployFailed') }}
          </el-tag>
        </div>
        <div v-if="isRecentDeploy" class="confirm-context-warn">
          ⚠ {{ t('deploy.confirmModal.recentDeployWarn',
                 { seconds: secondsSinceLast }) }}
        </div>
      </div>
      <div v-else class="confirm-context confirm-context-empty">
        <span class="muted">{{ t('deploy.confirmModal.firstEverDeploy') }}</span>
      </div>

      <div class="confirm-section">
        <label :for="inputId" class="confirm-label">
          {{ t('deploy.confirmModal.typeHostLabel') }} <strong>{{ host }}</strong> {{ t('deploy.confirmModal.typeHostSuffix') }}
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
          {{ t('deploy.confirmModal.mismatch', { host }) }}
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
          {{ t('deploy.confirmModal.snapshotLabel') }}
        </el-checkbox>
        <small :id="snapshotHintId" class="confirm-hint">
          {{ t('deploy.confirmModal.snapshotHint') }}
        </small>
      </div>

      <!-- 2s countdown ring (NOT 2s hold-gesture, per v2 a11y BLOCKER #1). Once
           text matches and user activates the button, a 2-second countdown
           runs; user can cancel by clicking again or pressing Esc. Keyboard
           reachable, no sustained input required. -->
      <div class="confirm-actions">
        <el-button @click="onCancel">{{ t('deploy.confirmModal.cancel') }}</el-button>
        <el-button
          type="danger"
          :disabled="!match || isDeploying"
          @click="onActivate"
          :aria-pressed="countdownActive"
        >
          <span v-if="!countdownActive">{{ t('deploy.confirmModal.deployNow') }}</span>
          <span v-else>{{ t('deploy.confirmModal.countdown', { seconds: countdownRemaining }) }}</span>
        </el-button>
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ElInput } from 'element-plus'

const { t } = useI18n()

const props = defineProps<{
  modelValue: boolean
  domain: string
  host: string
  /**
   * Phase 7.5+++ — most recent deploy for this host (passed by parent
   * from history). Powers the context panel + recent-deploy warning.
   * Null/undefined when no prior deploy exists (first-ever deploy).
   */
  lastDeploy?: import('../../api/deploy').DeployHistoryEntryDto | null
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

// Phase 7.5+++ — context panel helpers. Recompute on each open via
// the reactive `now` ref nudged in onOpen so a stale modal doesn't
// show "5 seconds ago" if it was opened a minute later.
const now = ref(Date.now())
const secondsSinceLast = computed(() => {
  if (!props.lastDeploy) return Number.POSITIVE_INFINITY
  try {
    return Math.max(0, Math.floor((now.value - new Date(props.lastDeploy.startedAt).getTime()) / 1000))
  } catch { return Number.POSITIVE_INFINITY }
})
const isRecentDeploy = computed(() => secondsSinceLast.value < 60)
const relativeAge = computed(() => {
  const s = secondsSinceLast.value
  if (!Number.isFinite(s)) return ''
  if (s < 60) return t('deploy.confirmModal.ageJustNow', { n: s })
  if (s < 3600) return t('deploy.confirmModal.ageMinutes', { n: Math.floor(s / 60) })
  if (s < 86400) return t('deploy.confirmModal.ageHours', { n: Math.floor(s / 3600) })
  return t('deploy.confirmModal.ageDays', { n: Math.floor(s / 86400) })
})

// Hook the existing onOpen flow to refresh `now` so the relative time
// is fresh every time the modal pops.
watch(() => props.modelValue, (v) => { if (v) now.value = Date.now() })
</script>

<style scoped>
.confirm { display: flex; flex-direction: column; gap: 16px; }
.confirm-section { display: flex; flex-direction: column; gap: 6px; }
.confirm-label { font-size: 13px; color: var(--el-text-color-regular); }
.confirm-mismatch { color: var(--el-color-danger); font-size: 12px; }
.confirm-hint { color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.4; }
.confirm-hint code { font-family: ui-monospace, monospace; font-size: 11px; padding: 0 3px; background: var(--el-fill-color-light); border-radius: 2px; }
.confirm-actions { display: flex; gap: 8px; justify-content: flex-end; }
.confirm-context {
  display: flex; flex-direction: column; gap: 4px;
  padding: 8px 12px; font-size: 13px;
  background: var(--el-fill-color-light);
  border-radius: 4px;
  border-left: 3px solid var(--el-color-info-light-3);
}
.confirm-context.warn-recent {
  border-left-color: var(--el-color-warning);
  background: var(--el-color-warning-light-9);
}
.confirm-context-empty { font-style: italic; }
.confirm-context-row { display: flex; align-items: center; gap: 8px; }
.confirm-context-warn {
  margin-top: 4px; font-size: 12px;
  color: var(--el-color-warning-dark-2);
  font-weight: 500;
}
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
</style>
