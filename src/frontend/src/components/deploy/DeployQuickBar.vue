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
      <DryRunPlanView :plan="previewPlan" />
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
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useDeployStore } from '../../stores/deploy'
import { dryRunDeploy, type DryRunDeployResult } from '../../api/deploy'
import DryRunPlanView from './DryRunPlanView.vue'

const { t } = useI18n()
const props = defineProps<{
  domain: string
  hosts: string[]
}>()

const store = useDeployStore()
// Iter 72 — pre-select target host so operator doesn't need to click
// the dropdown first. Single-host config (the common dev case) → that
// host. Multi-host → "__all__" so operator can fan out in one click.
// Operator can still re-pick from dropdown.
const targetHost = ref<string>(
  props.hosts.length === 1 ? props.hosts[0]
  : props.hosts.length > 1 ? '__all__'
  : ''
)
// Keep the auto-selection healthy if the host list changes mid-session
// (e.g. operator adds a host in Settings without page reload).
watch(() => props.hosts, (now) => {
  if (!targetHost.value || (targetHost.value !== '__all__' && !now.includes(targetHost.value))) {
    targetHost.value = now.length === 1 ? now[0] : now.length > 1 ? '__all__' : ''
  }
})
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
</style>
