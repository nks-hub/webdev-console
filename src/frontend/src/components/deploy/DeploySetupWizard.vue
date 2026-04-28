<template>
  <div class="wizard">
    <el-steps :active="activeStep" finish-status="success" simple>
      <el-step :title="t('deploySettings.wizard.step1Title')" />
      <el-step :title="t('deploySettings.wizard.step2Title')" />
      <el-step :title="t('deploySettings.wizard.step3Title')" />
    </el-steps>

    <div class="wizard-body">
      <!-- Step 0: host details -->
      <div v-if="activeStep === 0" class="wizard-step">
        <el-form label-position="top" :model="form" size="default">
          <el-form-item :label="t('deploySettings.wizard.host')" required>
            <el-input v-model="form.host" placeholder="production" />
            <div class="field-hint">{{ t('deploySettings.wizard.hostHint') }}</div>
          </el-form-item>
          <el-form-item :label="t('deploySettings.wizard.hostnameField')" required>
            <el-input v-model="form.hostname" placeholder="deploy.example.com" />
            <div class="field-hint">{{ t('deploySettings.wizard.hostnameHint') }}</div>
          </el-form-item>
          <el-form-item :label="t('deploySettings.wizard.userField')">
            <el-input v-model="form.user" placeholder="deploy" />
          </el-form-item>
          <el-form-item :label="t('deploySettings.wizard.deployPath')" required>
            <el-input v-model="form.deployPath" placeholder="/var/www/myapp" />
            <div class="field-hint">{{ t('deploySettings.wizard.deployPathHint') }}</div>
          </el-form-item>
        </el-form>
      </div>

      <!-- Step 1: strategy -->
      <div v-if="activeStep === 1" class="wizard-step">
        <el-form label-position="top" :model="form" size="default">
          <el-form-item :label="t('deploySettings.wizard.branch')">
            <el-input v-model="form.branch" placeholder="main" />
          </el-form-item>
          <el-form-item :label="t('deploySettings.wizard.healthCheckUrl')">
            <el-input v-model="form.healthCheckUrl" placeholder="https://myapp.example.com/_health" />
            <div class="field-hint">{{ t('deploySettings.wizard.healthCheckHint') }}</div>
          </el-form-item>
          <el-form-item :label="t('deploySettings.wizard.keepReleases')">
            <el-input-number v-model="form.keepReleases" :min="2" :max="20" />
          </el-form-item>
          <el-form-item>
            <el-checkbox v-model="form.autoRollback">{{ t('deploySettings.wizard.autoRollback') }}</el-checkbox>
          </el-form-item>
        </el-form>
      </div>

      <!-- Step 2: verify -->
      <div v-if="activeStep === 2" class="wizard-step">
        <el-alert
          type="info"
          :closable="false"
          show-icon
          :title="t('deploySettings.wizard.reviewTitle')"
          :description="t('deploySettings.wizard.reviewBody')"
        />
        <el-descriptions :column="1" border class="wizard-summary">
          <el-descriptions-item :label="t('deploySettings.wizard.summary.target')">{{ form.host }}</el-descriptions-item>
          <el-descriptions-item :label="t('deploySettings.wizard.summary.sshHost')">{{ form.user }}@{{ form.hostname }}</el-descriptions-item>
          <el-descriptions-item :label="t('deploySettings.wizard.summary.remotePath')"><span class="mono">{{ form.deployPath }}</span></el-descriptions-item>
          <el-descriptions-item :label="t('deploySettings.wizard.summary.branch')">{{ form.branch || 'main' }}</el-descriptions-item>
          <el-descriptions-item :label="t('deploySettings.wizard.summary.healthCheck')">{{ form.healthCheckUrl || t('deploySettings.wizard.summary.healthCheckNone') }}</el-descriptions-item>
          <el-descriptions-item :label="t('deploySettings.wizard.summary.keepReleases')">{{ form.keepReleases }}</el-descriptions-item>
        </el-descriptions>
      </div>
    </div>

    <div class="wizard-actions">
      <el-button v-if="activeStep > 0" plain @click="prev">{{ t('deploySettings.wizard.back') }}</el-button>
      <el-button @click="$emit('cancel')">{{ t('deploySettings.wizard.cancel') }}</el-button>
      <el-button v-if="activeStep < 2" type="primary" :disabled="!canAdvance" @click="next">{{ t('deploySettings.wizard.next') }}</el-button>
      <el-button v-else type="primary" @click="finish">{{ t('deploySettings.wizard.finish') }}</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
defineProps<{ domain: string }>()
const emit = defineEmits<{
  done: [config: typeof form]
  cancel: []
}>()

const activeStep = ref(0)
const form = reactive({
  host: 'production',
  hostname: '',
  user: 'deploy',
  deployPath: '',
  branch: 'main',
  healthCheckUrl: '',
  keepReleases: 5,
  autoRollback: true,
})

const canAdvance = computed(() => {
  if (activeStep.value === 0) return form.host && form.hostname && form.deployPath
  return true
})

function next(): void { if (activeStep.value < 2) activeStep.value++ }
function prev(): void { if (activeStep.value > 0) activeStep.value-- }
function finish(): void { emit('done', form) }
</script>

<style scoped>
.wizard { display: flex; flex-direction: column; gap: 20px; }
.wizard-body { padding: 16px 0; min-height: 220px; }
.wizard-step { max-width: 540px; }
.wizard-summary { margin-top: 12px; }
.wizard-actions { display: flex; gap: 8px; justify-content: flex-end; }
.mono { font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace; }
.field-hint { font-size: 12px; color: var(--el-text-color-secondary); margin-top: 4px; line-height: 1.45; }
</style>
