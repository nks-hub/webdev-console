<template>
  <!--
    Phase 7.5+++ — Dry-run plan renderer extracted from DeployQuickBar +
    DeployConfirmModal so both surfaces share one source of truth. Adding
    a new plan field means one edit here instead of two.

    Renders all DryRunDeployResult fields with the established UX:
      • stale-source warning alert at the top
      • plan rows (label / value pairs)
      • relative-time formatting for sourceLastModified
      • mono code styling for paths/release ids
  -->
  <div v-if="plan" class="preview-plan">
    <el-alert
      v-if="plan.sourceUnchangedSinceLastDeploy === true"
      type="warning"
      :closable="false"
      show-icon
      :title="t('deploy.quickBar.plan.staleSourceWarn')"
      class="stale-source-alert"
    />
    <!-- Phase 7.5+++ — always-confirm hint. When the deploy kind is in
         mcp.always_confirm_kinds, the operator will see the GUI banner
         even with active grants. Surface that here so they're not
         surprised after clicking Deploy. -->
    <el-alert
      v-if="plan.alwaysConfirmKind === true"
      type="info"
      :closable="false"
      show-icon
      :title="t('deploy.quickBar.plan.alwaysConfirmHint')"
      class="stale-source-alert"
    />
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.wouldRelease') }}</span>
      <span class="plan-val mono">{{ plan.wouldRelease }}</span>
    </div>
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.copyFrom') }}</span>
      <span class="plan-val mono">{{ plan.wouldCopyFrom }}</span>
    </div>
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.extractTo') }}</span>
      <span class="plan-val mono">{{ plan.wouldExtractTo }}</span>
    </div>
    <div v-if="plan.branch" class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.branch') }}</span>
      <span class="plan-val mono">{{ plan.branch }}</span>
    </div>
    <div v-if="plan.sourceLastModified" class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.sourceLastModified') }}</span>
      <span class="plan-val">
        <span>{{ formatRelative(plan.sourceLastModified) }}</span>
        <span class="muted plan-iso-hint">· {{ plan.sourceLastModified.slice(0, 19).replace('T', ' ') }}</span>
      </span>
    </div>
    <div v-if="plan.currentRelease" class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.currentRelease') }}</span>
      <span class="plan-val mono">{{ plan.currentRelease }}</span>
    </div>
    <div v-if="plan.wouldSwapCurrentFrom" class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.previousRelease') }}</span>
      <span class="plan-val mono">{{ plan.wouldSwapCurrentFrom }}</span>
    </div>
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.shared') }}</span>
      <span class="plan-val">
        <el-tag v-for="d in plan.sharedDirs" :key="`d-${d}`" size="small" effect="plain" class="plan-tag">{{ d }}/</el-tag>
        <el-tag v-for="f in plan.sharedFiles" :key="`f-${f}`" size="small" effect="plain" class="plan-tag">{{ f }}</el-tag>
        <span v-if="plan.sharedDirs.length === 0 && plan.sharedFiles.length === 0" class="muted">—</span>
      </span>
    </div>
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.retention') }}</span>
      <span class="plan-val">
        {{ t('deploy.quickBar.plan.retentionValue', {
          keep: plan.keepReleases,
          existing: plan.existingReleaseCount,
          prune: plan.wouldPruneCount,
        }) }}
      </span>
    </div>
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.hooks') }}</span>
      <span class="plan-val">
        <el-tag v-for="(n, evt) in plan.hooksWillFire" :key="evt" size="small" effect="plain" class="plan-tag">{{ evt }} ×{{ n }}</el-tag>
        <span v-if="Object.keys(plan.hooksWillFire).length === 0" class="muted">{{ t('deploy.quickBar.plan.noHooks') }}</span>
      </span>
    </div>
    <div v-if="plan.healthCheckUrl" class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.healthCheck') }}</span>
      <span class="plan-val mono">{{ plan.healthCheckUrl }}</span>
    </div>
    <div class="plan-row">
      <span class="plan-key">{{ t('deploy.quickBar.plan.notifications') }}</span>
      <span class="plan-val">{{ plan.slackEnabled
        ? t('deploy.quickBar.plan.slackEnabled')
        : t('deploy.quickBar.plan.slackDisabled') }}</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { DryRunDeployResult } from '../../api/deploy'

const { t } = useI18n()
defineProps<{ plan: DryRunDeployResult | null }>()

// Reuse the deploy.confirmModal.age* keys (already cs/en parity-tested
// across the app). Falls back to raw ISO if Date.parse fails.
function formatRelative(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime()
  if (Number.isNaN(ms)) return iso
  if (ms < 60_000) return t('deploy.confirmModal.ageJustNow', { n: Math.max(0, Math.floor(ms / 1000)) })
  if (ms < 3_600_000) return t('deploy.confirmModal.ageMinutes', { n: Math.floor(ms / 60_000) })
  if (ms < 86_400_000) return t('deploy.confirmModal.ageHours', { n: Math.floor(ms / 3_600_000) })
  return t('deploy.confirmModal.ageDays', { n: Math.floor(ms / 86_400_000) })
}
</script>

<style scoped>
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
.plan-iso-hint {
  margin-left: 8px;
  font-size: 11px;
  font-family: var(--el-font-family-monospace, ui-monospace, monospace);
}
.stale-source-alert {
  margin-bottom: 4px;
}
.muted { color: var(--el-text-color-secondary); }
</style>
