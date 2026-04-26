<template>
  <div class="wizard">
    <el-steps :active="activeStep" finish-status="success" simple>
      <el-step title="Host details" />
      <el-step title="Strategy" />
      <el-step title="Verify" />
    </el-steps>

    <div class="wizard-body">
      <!-- Step 0: host details -->
      <div v-if="activeStep === 0" class="wizard-step">
        <el-form label-position="top" :model="form" size="default">
          <el-form-item label="Target name (used in URL)" required>
            <el-input v-model="form.host" placeholder="production" />
          </el-form-item>
          <el-form-item label="SSH host" required>
            <el-input v-model="form.hostname" placeholder="deploy.example.com" />
          </el-form-item>
          <el-form-item label="SSH user">
            <el-input v-model="form.user" placeholder="deploy" />
          </el-form-item>
          <el-form-item label="Remote deploy path" required>
            <el-input v-model="form.deployPath" placeholder="/var/www/myapp" />
          </el-form-item>
        </el-form>
      </div>

      <!-- Step 1: strategy -->
      <div v-if="activeStep === 1" class="wizard-step">
        <el-form label-position="top" :model="form" size="default">
          <el-form-item label="Branch">
            <el-input v-model="form.branch" placeholder="main" />
          </el-form-item>
          <el-form-item label="Health check URL">
            <el-input v-model="form.healthCheckUrl" placeholder="https://myapp.example.com/_health" />
          </el-form-item>
          <el-form-item label="Keep N old releases">
            <el-input-number v-model="form.keepReleases" :min="2" :max="20" />
          </el-form-item>
          <el-form-item>
            <el-checkbox v-model="form.autoRollback">Auto-rollback on health check failure</el-checkbox>
          </el-form-item>
        </el-form>
      </div>

      <!-- Step 2: verify -->
      <div v-if="activeStep === 2" class="wizard-step">
        <el-alert
          type="info"
          :closable="false"
          show-icon
          title="Review and finish"
          description="The wizard will write deploy.neon (and deploy.local.neon for any secrets you supplied). The file lives in your project root and is committable to git."
        />
        <el-descriptions :column="1" border class="wizard-summary">
          <el-descriptions-item label="Target">{{ form.host }}</el-descriptions-item>
          <el-descriptions-item label="SSH host">{{ form.user }}@{{ form.hostname }}</el-descriptions-item>
          <el-descriptions-item label="Remote path"><span class="mono">{{ form.deployPath }}</span></el-descriptions-item>
          <el-descriptions-item label="Branch">{{ form.branch || 'main' }}</el-descriptions-item>
          <el-descriptions-item label="Health check">{{ form.healthCheckUrl || 'none' }}</el-descriptions-item>
          <el-descriptions-item label="Keep releases">{{ form.keepReleases }}</el-descriptions-item>
        </el-descriptions>
      </div>
    </div>

    <div class="wizard-actions">
      <el-button v-if="activeStep > 0" plain @click="prev">Back</el-button>
      <el-button @click="$emit('cancel')">Cancel</el-button>
      <el-button v-if="activeStep < 2" type="primary" :disabled="!canAdvance" @click="next">Next</el-button>
      <el-button v-else type="primary" @click="finish">Finish setup</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'

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
</style>
