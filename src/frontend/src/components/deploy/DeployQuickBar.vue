<template>
  <!--
    Phase 7.5+++ Deploy detail redesign — quick-deploy inline form.
    Sits above the host grid as a one-row toolbar so an operator can:
      • pick a branch override (defaults to host's branch when blank)
      • toggle a pre-deploy DB snapshot
      • fire a deploy at one host or all hosts in a single click
    Skips the confirm modal — used when the deploy is "obvious"
    (dev fixture, retry of a known-good release). The full HostCard
    Deploy button still routes through DeployConfirmModal for the
    explicit-confirmation path.
  -->
  <div class="quick-bar" role="region" :aria-label="t('deploy.quickBar.aria')">
    <div class="quick-bar-row">
      <span class="quick-label">{{ t('deploy.quickBar.label') }}</span>

      <el-select
        v-model="targetHost"
        :placeholder="t('deploy.quickBar.targetPlaceholder')"
        size="default"
        style="width: 200px"
        :aria-label="t('deploy.quickBar.targetAria')"
      >
        <el-option v-if="hosts.length > 1" :label="t('deploy.quickBar.allHosts')" value="__all__" />
        <el-option v-for="h in hosts" :key="h" :label="h" :value="h" />
      </el-select>

      <el-input
        v-model="branch"
        :placeholder="t('deploy.quickBar.branchPlaceholder')"
        size="default"
        style="width: 160px"
        clearable
        :aria-label="t('deploy.quickBar.branchAria')"
      />

      <el-checkbox v-model="snapshot" size="default">
        {{ t('deploy.quickBar.withSnapshot') }}
      </el-checkbox>

      <el-button
        :disabled="!targetHost || targetHost === '__all__' || busy || previewBusy"
        :loading="previewBusy"
        @click="onPreview"
      >
        {{ t('deploy.quickBar.preview') }}
      </el-button>

      <el-button
        type="primary"
        :disabled="!targetHost || busy"
        :loading="busy"
        @click="onFire"
      >
        {{ buttonLabel }}
      </el-button>

      <span class="quick-hint muted">{{ t('deploy.quickBar.hint') }}</span>
    </div>

    <!-- Phase 7.5+++ — preview modal: shows the resolved deploy plan
         from the daemon's dryRun:true endpoint. Read-only summary;
         operator clicks Deploy in the toolbar to actually commit. -->
    <el-dialog
      v-model="previewOpen"
      :title="t('deploy.quickBar.previewTitle')"
      width="540px"
      destroy-on-close
    >
      <div v-if="previewPlan" class="preview-plan">
        <!-- Phase 7.5+++ — stale-source warning. Daemon flags this true
             when source mtime <= last successful deploy.startedAt for this
             host, i.e. you'd publish a release identical to the one
             that's already live. Usually a finger slip. -->
        <el-alert
          v-if="previewPlan.sourceUnchangedSinceLastDeploy === true"
          type="warning"
          :closable="false"
          show-icon
          :title="t('deploy.quickBar.plan.staleSourceWarn')"
          class="stale-source-alert"
        />
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
          <span class="plan-val">
            <span>{{ formatRelative(previewPlan.sourceLastModified) }}</span>
            <span class="muted plan-iso-hint">· {{ previewPlan.sourceLastModified.slice(0, 19).replace('T', ' ') }}</span>
          </span>
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
        <el-button type="primary" @click="onPreviewConfirm">
          {{ t('deploy.quickBar.previewConfirm') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'
import { dryRunDeploy, type DryRunDeployResult } from '../../api/deploy'

const { t } = useI18n()
const props = defineProps<{
  domain: string
  hosts: string[]
}>()

const store = useDeployStore()
const targetHost = ref<string>('')
const branch = ref<string>('')
const snapshot = ref<boolean>(false)
const busy = ref<boolean>(false)
const previewBusy = ref<boolean>(false)
const previewOpen = ref<boolean>(false)
const previewPlan = ref<DryRunDeployResult | null>(null)

async function onPreview(): Promise<void> {
  if (!targetHost.value || targetHost.value === '__all__') return
  previewBusy.value = true
  try {
    previewPlan.value = await dryRunDeploy(props.domain, targetHost.value, {
      branch: branch.value || undefined,
    })
    previewOpen.value = true
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.quickBar.previewFailed'))
  } finally {
    previewBusy.value = false
  }
}

async function onPreviewConfirm(): Promise<void> {
  previewOpen.value = false
  await onFire()
}

// Phase 7.5+++ — relative-time formatter for sourceLastModified. Reuses
// the deploy.confirmModal.age* keys (already cs/en parity-tested).
function formatRelative(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime()
  if (Number.isNaN(ms)) return iso
  if (ms < 60_000) return t('deploy.confirmModal.ageJustNow', { n: Math.max(0, Math.floor(ms / 1000)) })
  if (ms < 3_600_000) return t('deploy.confirmModal.ageMinutes', { n: Math.floor(ms / 60_000) })
  if (ms < 86_400_000) return t('deploy.confirmModal.ageHours', { n: Math.floor(ms / 3_600_000) })
  return t('deploy.confirmModal.ageDays', { n: Math.floor(ms / 86_400_000) })
}

const buttonLabel = computed(() => {
  if (targetHost.value === '__all__') {
    return t('deploy.quickBar.deployAll', { n: props.hosts.length })
  }
  return t('deploy.quickBar.deployOne')
})

async function onFire(): Promise<void> {
  if (!targetHost.value) return
  busy.value = true
  try {
    if (targetHost.value === '__all__') {
      const id = await store.startGroupDeploy(props.domain, props.hosts)
      ElMessage.success(t('deploy.quickBar.startedGroup', { id: id.slice(0, 8) }))
    } else {
      // Single-host deploy. Bypass modal by calling the store directly.
      // localPaths come from settings (Phase 7.5+++ daemon fallback)
      // when not supplied here — so the fast path stays one click.
      await store.startDeploy(props.domain, targetHost.value, {
        snapshot: snapshot.value
          ? { include: true, retentionDays: 30 }
          : undefined,
        backendOptions: branch.value ? { branch: branch.value } : undefined,
      })
      ElMessage.success(t('deploy.quickBar.started'))
    }
    branch.value = ''
    snapshot.value = false
  } catch (e) {
    ElMessage.error((e as Error).message || t('deploy.quickBar.failed'))
  } finally {
    busy.value = false
  }
}
</script>

<style scoped>
.quick-bar {
  padding: 12px 16px;
  background: var(--el-fill-color-lighter);
  border: 1px dashed var(--el-border-color-lighter);
  border-radius: 6px;
}
.quick-bar-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}
.quick-label {
  font-weight: 600;
  font-size: 13px;
}
.quick-hint {
  font-size: 12px;
  margin-left: auto;
}
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
.plan-iso-hint {
  margin-left: 8px;
  font-size: 11px;
  font-family: var(--el-font-family-monospace, ui-monospace, monospace);
}
.stale-source-alert {
  margin-bottom: 4px;
}
</style>
