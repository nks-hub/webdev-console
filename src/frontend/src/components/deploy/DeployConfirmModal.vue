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
          :loading="previewBusy"
          :disabled="isDeploying"
          @click="onPreview"
        >
          {{ t('deploy.confirmModal.preview') }}
        </el-button>
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

    <!-- Phase 7.5+++ — dry-run plan preview, available from inside the
         explicit-confirmation flow so the operator can sanity-check the
         resolved release/source/target/hooks before clicking Deploy. -->
    <el-dialog
      v-model="previewOpen"
      :title="t('deploy.confirmModal.previewTitle')"
      width="540px"
      destroy-on-close
      append-to-body
    >
      <div v-if="previewPlan" class="preview-plan">
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.wouldRelease') }}</span>
          <span class="plan-val mono">{{ previewPlan.wouldRelease }}</span>
        </div>
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.copyFrom') }}</span>
          <span class="plan-val mono">{{ previewPlan.wouldCopyFrom }}</span>
        </div>
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.extractTo') }}</span>
          <span class="plan-val mono">{{ previewPlan.wouldExtractTo }}</span>
        </div>
        <div v-if="previewPlan.branch" class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.branch') }}</span>
          <span class="plan-val mono">{{ previewPlan.branch }}</span>
        </div>
        <div v-if="previewPlan.sourceLastModified" class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.sourceLastModified') }}</span>
          <span class="plan-val mono">{{ previewPlan.sourceLastModified }}</span>
        </div>
        <div v-if="previewPlan.currentRelease" class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.currentRelease') }}</span>
          <span class="plan-val mono">{{ previewPlan.currentRelease }}</span>
        </div>
        <div v-if="previewPlan.wouldSwapCurrentFrom" class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.previousRelease') }}</span>
          <span class="plan-val mono">{{ previewPlan.wouldSwapCurrentFrom }}</span>
        </div>
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.shared') }}</span>
          <span class="plan-val">
            <el-tag v-for="d in previewPlan.sharedDirs" :key="`d-${d}`" size="small" effect="plain" class="plan-tag">{{ d }}/</el-tag>
            <el-tag v-for="f in previewPlan.sharedFiles" :key="`f-${f}`" size="small" effect="plain" class="plan-tag">{{ f }}</el-tag>
            <span v-if="previewPlan.sharedDirs.length === 0 && previewPlan.sharedFiles.length === 0" class="muted">—</span>
          </span>
        </div>
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.retention') }}</span>
          <span class="plan-val">
            {{ t('deploy.quickBar.plan.retentionValue', {
              keep: previewPlan.keepReleases,
              existing: previewPlan.existingReleaseCount,
              prune: previewPlan.wouldPruneCount,
            }) }}
          </span>
        </div>
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.hooks') }}</span>
          <span class="plan-val">
            <el-tag v-for="(n, evt) in previewPlan.hooksWillFire" :key="evt" size="small" effect="plain" class="plan-tag">{{ evt }} ×{{ n }}</el-tag>
            <span v-if="Object.keys(previewPlan.hooksWillFire).length === 0" class="muted">{{ t('deploy.quickBar.plan.noHooks') }}</span>
          </span>
        </div>
        <div v-if="previewPlan.healthCheckUrl" class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.healthCheck') }}</span>
          <span class="plan-val mono">{{ previewPlan.healthCheckUrl }}</span>
        </div>
        <div class="plan-row">
          <span class="plan-key">{{ t('deploy.quickBar.plan.notifications') }}</span>
          <span class="plan-val">{{ previewPlan.slackEnabled
            ? t('deploy.quickBar.plan.slackEnabled')
            : t('deploy.quickBar.plan.slackDisabled') }}</span>
        </div>
      </div>
      <template #footer>
        <el-button @click="previewOpen = false">{{ t('deploy.quickBar.previewClose') }}</el-button>
      </template>
    </el-dialog>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, type ElInput } from 'element-plus'
import { dryRunDeploy, type DryRunDeployResult } from '../../api/deploy'

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

// Phase 7.5+++ — preview state. Fetched lazily when operator clicks
// Preview from inside the confirm modal so the explicit-confirmation
// path also gets visibility into what the daemon would actually do.
const previewBusy = ref<boolean>(false)
const previewOpen = ref<boolean>(false)
const previewPlan = ref<DryRunDeployResult | null>(null)

async function onPreview(): Promise<void> {
  previewBusy.value = true
  try {
    previewPlan.value = await dryRunDeploy(props.domain, props.host)
    previewOpen.value = true
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.quickBar.previewFailed'))
  } finally {
    previewBusy.value = false
  }
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
.muted { color: var(--el-text-color-secondary); }

.preview-plan {
  display: flex;
  flex-direction: column;
  gap: 10px;
  font-size: 13px;
}
.plan-row {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  padding: 6px 0;
  border-bottom: 1px solid var(--el-border-color-lighter);
}
.plan-row:last-child { border-bottom: none; }
.plan-key {
  flex: 0 0 150px;
  font-weight: 600;
  color: var(--el-text-color-regular);
}
.plan-val {
  flex: 1 1 auto;
  word-break: break-all;
}
.plan-val.mono {
  font-family: var(--el-font-family-monospace, ui-monospace, monospace);
  font-size: 12px;
}
.plan-tag {
  margin-right: 4px;
  margin-bottom: 4px;
}
</style>
